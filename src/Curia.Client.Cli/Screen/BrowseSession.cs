using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Curia.Client;

namespace Curia.Client.Cli;

/// <summary>
/// The screen as a process runs it (R10.73). One loop, one writer:
/// <list type="bullet">
/// <item>keys are read on a thread of their own and posted to a channel, and so are the signals the screen can catch
/// (SIGWINCH, SIGTERM, SIGHUP, SIGQUIT, SIGINT, SIGTSTP, SIGCONT), registered only here, only while drawing; neither writes
/// to the terminal, and the loop reads one event at a time;</item>
/// <item>the terminal is given back once, from <c>finally</c>, on every path: <c>q</c>, Ctrl-C, Ctrl-\, a signal, a fault.
/// <see cref="Output.Draw"/> refuses to draw after it;</item>
/// <item>Ctrl-C is a key while the screen is up, so it quits through the same path; Ctrl-Z does not suspend (the terminal would
/// be left as the screen left it) and says so on the status row;</item>
/// <item>death by SIGTERM or SIGHUP exits <c>128 + n</c>, after the terminal is given back; every other exit is the ledger's
/// (R10.74); a fault is reported quoted, after the terminal is given back, and joins the ledger as a local error.</item>
/// </list>
/// The loop never waits on the terminal: it waits on the channel, so a signal is answered whether or not a key is pending.
/// It keeps waiting on it while a load is in flight (spec 14.6 item 6): <c>q</c>, Ctrl-C, Ctrl-\\, SIGINT and SIGQUIT cancel the
/// load and leave with the ledger's code, SIGTERM and SIGHUP end it as ever, and any other key is discarded, so a hung Forum
/// cannot make a screen that does not answer <c>q</c>.
/// </summary>
internal sealed class BrowseSession(IBrowseSource source)
{
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan KeyPoll = TimeSpan.FromMilliseconds(15);

    /// <summary>The signals the screen answers, as the screen names them: what each asks of the loop, apart from the platform's number.</summary>
    private enum SignalKind
    {
        Resize,
        Continue,
        Stop,
        Terminate,
        Hangup,
        Interrupt,
        Quit,
    }

    internal const string LoadingSentence = "loading from the Forum: q cancels";

    internal const string SuspendSentence = "suspending is not supported here: the terminal would be left as the screen left it. q quits";

    internal async Task<int> RunAsync(ViewSource first)
    {
        var state = new BrowseState(source);

        using (var load = new CancellationTokenSource(LoadTimeout))
        {
            var refusal = await state.ApplyAsync(new BrowseTransition.Open(first), CurrentSize(), load.Token).ConfigureAwait(false);
            if (refusal is not null) return Output.Fail(refusal);
        }

        if (state.Current is BrowseView.Board { Posts.IsEmpty: true }) return Program.EmptyBoard(first.Id);

        return await RunScreenAsync(state).ConfigureAwait(false);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "R10.73: every exception is caught so the terminal is given back and the fault reported quoted; the exit joins the ledger as a local error.")]
    private static async Task<int> RunScreenAsync(BrowseState state)
    {
        // The command declines to draw on Windows (R10.73); this makes the signals below unreachable there as well.
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("a screen is not drawn on Windows");

        (PosixSignal Signal, SignalKind Kind)[] signals =
        [
            (PosixSignal.SIGWINCH, SignalKind.Resize), (PosixSignal.SIGCONT, SignalKind.Continue), (PosixSignal.SIGTSTP, SignalKind.Stop),
            (PosixSignal.SIGTERM, SignalKind.Terminate), (PosixSignal.SIGHUP, SignalKind.Hangup),
            (PosixSignal.SIGINT, SignalKind.Interrupt), (PosixSignal.SIGQUIT, SignalKind.Quit),
        ];

        var events = Channel.CreateUnbounded<SessionEvent>(new UnboundedChannelOptions { SingleReader = true });
        using var terminating = new CancellationTokenSource();
        using var stopReading = new CancellationTokenSource();
        var registrations = new List<PosixSignalRegistration>();
        var entered = false;
        var code = 0;
        Exception? fault = null;

        try
        {
            foreach (var (signal, kind) in signals)
            {
                registrations.Add(PosixSignalRegistration.Create(signal, context =>
                {
                    context.Cancel = true;
                    if (kind is SignalKind.Terminate or SignalKind.Hangup) terminating.Cancel();
                    events.Writer.TryWrite(new SessionEvent.Signalled(kind));
                }));
            }

            Output.TreatControlCAsInput(true);
            new Thread(() => ReadKeys(events.Writer, stopReading.Token)) { IsBackground = true, Name = "curia-browse-keys" }.Start();

            entered = true;
            Output.Draw(ScreenWriter.Enter());
            code = await LoopAsync(state, new EventQueue(events.Reader), terminating.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            fault = ex;
        }
        finally
        {
            await stopReading.CancelAsync().ConfigureAwait(false);
            foreach (var registration in registrations) registration.Dispose();

            // Once, whatever happened above. A terminal that has hung up cannot be written to, and has nothing to give back.
            if (entered)
            {
                try
                {
                    Output.Draw(ScreenWriter.Leave());
                }
                catch (IOException)
                {
                }
            }
        }

        if (fault is null) return code;

        Output.Error(new FrameBuilder().Line($"error: the screen stopped: {fault.GetType().Name}: {fault.Message}"));
        state.Ledger.AddRefusal(ExitCode.Local);
        return state.Ledger.Exit;
    }

    private static async Task<int> LoopAsync(BrowseState state, EventQueue events, CancellationToken terminating)
    {
        while (true)
        {
            var size = CurrentSize();
            Output.Draw(ScreenWriter.Serialize(state.Render(size), size));

            var next = await events.NextAsync().ConfigureAwait(false);
            var done = await next.Match(
                key => HandleKeyAsync(state, key.Info, size, events, terminating),
                signal => Task.FromResult(HandleSignal(state, signal.Kind))).ConfigureAwait(false);
            if (done is { } exit) return exit;
        }
    }

    /// <summary>What a signal does: a window change or a continue redraws, a stop asks for no suspending, the rest end the session.</summary>
    private static int? HandleSignal(BrowseState state, SignalKind kind) => kind switch
    {
        SignalKind.Terminate => 128 + 15,
        SignalKind.Hangup => 128 + 1,
        SignalKind.Interrupt or SignalKind.Quit => state.Ledger.Exit,
        SignalKind.Stop => Say(state, SuspendSentence),
        SignalKind.Resize or SignalKind.Continue => null,
        _ => throw new InvalidOperationException("a signal the screen registered no meaning for"),
    };

    private static int? Say(BrowseState state, [ConstantExpected] string sentence)
    {
        state.Say(new OwnText(sentence));
        return null;
    }

    private static async Task<int?> HandleKeyAsync(BrowseState state, ConsoleKeyInfo key, ScreenSize size, EventQueue events, CancellationToken terminating)
    {
        var meaning = Interpret(key, size);
        if (meaning.Quit) return state.Ledger.Exit;

        if (meaning.Suspend) return Say(state, SuspendSentence);

        if (meaning.Transition is not { } transition) return null;

        using var load = CancellationTokenSource.CreateLinkedTokenSource(terminating);
        load.CancelAfter(LoadTimeout);
        try
        {
            // The status row says a load is in flight before it starts, so that nothing reads the state while it is changing.
            if (transition.Rule is OutcomeRule.Counted)
            {
                state.Say(new OwnText(LoadingSentence));
                Output.Draw(ScreenWriter.Serialize(state.Render(size), size));
            }

            var applying = state.ApplyAsync(transition, size, load.Token);
            while (!applying.IsCompleted)
            {
                var heard = events.Peek();
                if (await Task.WhenAny(applying, heard).ConfigureAwait(false) == applying) break;

                events.Take();
                if (LeaveDuringLoad(state, await heard.ConfigureAwait(false), size) is { } exit)
                {
                    await load.CancelAsync().ConfigureAwait(false);
                    _ = applying.ContinueWith(static (task, _) => task.Exception, null, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    return exit;
                }
            }

            if (await applying.ConfigureAwait(false) is { } refusal)
                state.Say(new OwnText("the Forum refused the load: " + refusal.Summary));
        }
        catch (OperationCanceledException) when (!terminating.IsCancellationRequested)
        {
            state.Ledger.AddRefusal(ExitCode.ForumFault);
            state.Say(new OwnText("the load timed out"));
        }
        catch (OperationCanceledException)
        {
            // Terminating: the signal that cancelled the load is the next event.
        }

        return null;
    }

    /// <summary>What an event during a load asks of the session: leave with a code, or nothing (a key is discarded, a window change waits).</summary>
    private static int? LeaveDuringLoad(BrowseState state, SessionEvent heard, ScreenSize size) => heard.Match<int?>(
        key => Interpret(key.Info, size).Quit ? state.Ledger.Exit : null,
        signal => signal.Kind switch
        {
            SignalKind.Terminate => 128 + 15,
            SignalKind.Hangup => 128 + 1,
            SignalKind.Interrupt or SignalKind.Quit => state.Ledger.Exit,
            SignalKind.Resize or SignalKind.Continue or SignalKind.Stop => null,
            _ => throw new InvalidOperationException("a signal the screen registered no meaning for"),
        });

    private readonly record struct Meaning(bool Quit, bool Suspend, BrowseTransition? Transition);

    /// <summary>
    /// A key as the screen reads it. Ctrl-C and Ctrl-\ arrive as keys when the terminal's interrupt characters are not
    /// handled by the line discipline, and as signals when they are; the signals are handled above, so both quit.
    /// </summary>
    private static Meaning Interpret(ConsoleKeyInfo key, ScreenSize size)
    {
        var page = Math.Max(1, size.Height - 6);

        // The runtime reads Ctrl-\ (0x1C) as Control and the key 4, with no character; Ctrl-C and Ctrl-Z carry theirs.
        var control = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        if (key.KeyChar is '\u0003' or '\u001c' or 'q' or 'Q' || (control && key.Key is ConsoleKey.C or ConsoleKey.D4 or ConsoleKey.Oem5))
            return new Meaning(Quit: true, Suspend: false, null);
        if (key.KeyChar == '\u001a' || (control && key.Key == ConsoleKey.Z)) return new Meaning(Quit: false, Suspend: true, null);

        var transition = ByKey(key.Key, page) ?? ByCharacter(key.KeyChar, page);
        return new Meaning(Quit: false, Suspend: false, transition);
    }

    private static BrowseTransition? ByKey(ConsoleKey key, int page)
    {
        if (key == ConsoleKey.UpArrow) return new BrowseTransition.Scroll(-1, Relative: true);
        if (key == ConsoleKey.DownArrow) return new BrowseTransition.Scroll(1, Relative: true);
        if (key == ConsoleKey.PageUp) return new BrowseTransition.Scroll(-page, Relative: true);
        if (key is ConsoleKey.PageDown or ConsoleKey.Spacebar) return new BrowseTransition.Scroll(page, Relative: true);
        if (key == ConsoleKey.Home) return new BrowseTransition.Scroll(0, Relative: false);
        if (key == ConsoleKey.End) return new BrowseTransition.Scroll(int.MaxValue / 2, Relative: false);
        if (key == ConsoleKey.Tab) return new BrowseTransition.NextPassage();
        if (key == ConsoleKey.Enter) return new BrowseTransition.OpenFromIndex();
        return key == ConsoleKey.Backspace ? new BrowseTransition.Back() : null;
    }

    private static BrowseTransition? ByCharacter(char character, int page) => character switch
    {
        'k' => new BrowseTransition.Scroll(-1, Relative: true),
        'j' => new BrowseTransition.Scroll(1, Relative: true),
        ' ' => new BrowseTransition.Scroll(page, Relative: true),
        '\t' => new BrowseTransition.NextPassage(),
        '\r' or '\n' => new BrowseTransition.OpenFromIndex(),
        '\u007f' or '\b' => new BrowseTransition.Back(),
        'r' => new BrowseTransition.Reload(),
        '!' => new BrowseTransition.ShowCommand(),
        '?' => new BrowseTransition.ShowHelp(),
        _ => null,
    };

    private static ScreenSize CurrentSize()
    {
        var size = Output.Size();
        return new ScreenSize(size.Width > 0 ? size.Width : 80, size.Height > 0 ? size.Height : 24);
    }

    /// <summary>Reads keys until told to stop, polling rather than blocking so that it can be told.</summary>
    private static void ReadKeys(ChannelWriter<SessionEvent> writer, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                if (Output.ReadKeyAvailable()) writer.TryWrite(new SessionEvent.Key(Output.ReadKey()));
                else Thread.Sleep(KeyPoll);
            }
        }
        catch (InvalidOperationException)
        {
            // Input went away with the terminal; SIGHUP says so.
        }
        catch (IOException)
        {
        }
    }

    /// <summary>
    /// The channel the loop reads, with one read outstanding at most: an event looked at while a load was in flight and not
    /// taken is the next the loop reads, and none is lost.
    /// </summary>
    private sealed class EventQueue(ChannelReader<SessionEvent> reader)
    {
        private Task<SessionEvent>? _pending;

        internal Task<SessionEvent> Peek() => _pending ??= reader.ReadAsync().AsTask();

        internal void Take() => _pending = null;

        internal async Task<SessionEvent> NextAsync()
        {
            var next = await Peek().ConfigureAwait(false);
            Take();
            return next;
        }
    }

    /// <summary>What reaches the loop: a key, or a signal. A closed hierarchy with an explicit <see cref="Match{T}"/> (CS-11).</summary>
    private abstract record SessionEvent
    {
        private SessionEvent()
        {
        }

        internal T Match<T>(Func<Key, T> key, Func<Signalled, T> signal) => this switch
        {
            Key k => key(k),
            Signalled s => signal(s),
            _ => throw new InvalidOperationException("an event is a key or a signal"),
        };

        internal sealed record Key(ConsoleKeyInfo Info) : SessionEvent;

        internal sealed record Signalled(SignalKind Kind) : SessionEvent;
    }
}
