using System.Collections.Immutable;
using Curia.Client;
using Curia.Domain.Serving;

namespace Curia.Client.Cli;

/// <summary>What loading a view's posts answered: the verified passages, or the Forum's refusal.</summary>
internal sealed record LoadResult
{
    private LoadResult()
    {
    }

    internal ImmutableArray<Passage> Passages { get; private init; } = [];

    internal Refusal? Refusal { get; private init; }

    internal static LoadResult Loaded(ImmutableArray<Passage> passages) => new() { Passages = passages };

    internal static LoadResult Refused(Refusal refusal) => new() { Refusal = refusal };
}

/// <summary>Where a screen's posts come from: fetched and verified as the line form verifies them.</summary>
internal interface IBrowseSource
{
    Task<LoadResult> LoadAsync(ViewSource source, CancellationToken ct);
}

/// <summary>
/// The screen's session, with no terminal in it: the stack of views the person has opened, the ledger of outcomes they have
/// displayed (R10.74), and the note on the status row. A key becomes a <see cref="BrowseTransition"/>, and
/// <see cref="ApplyAsync"/> is the one place a transition acts, so the rule of spec 8.2 is applied in one place: a
/// transition that loads a post counts it, and a transition whose row says it loads nothing and does is a defect that
/// throws rather than a count that is silently missing.
/// </summary>
internal sealed class BrowseState(IBrowseSource source)
{
    private readonly List<BrowseView> _views = [];

    internal OutcomeLedger Ledger { get; } = new();

    /// <summary>The view on show. Valid once the first <see cref="BrowseTransition.Open"/> has been applied.</summary>
    internal BrowseView Current => _views[^1];

    internal bool HasView => _views.Count > 0;

    /// <summary>A sentence of this client's on the status row, until the next key.</summary>
    internal OwnText? Note { get; private set; }

    /// <summary>Puts a sentence on the status row, as a key that does nothing else does.</summary>
    internal void Say(OwnText note) => Note = note;

    /// <summary>
    /// Applies <paramref name="transition"/>. Returns the Forum's refusal when a load was refused, after counting its code in
    /// the ledger and leaving the views as they were.
    /// </summary>
    internal async Task<Refusal?> ApplyAsync(BrowseTransition transition, ScreenSize size, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(transition);

        Note = null;
        var loaded = await transition.Match(
            open => LoadAsync(open.Source, replace: false, ct),
            _ => Task.FromResult(OpenFromIndex(size)),
            _ => Task.FromResult(GoBack()),
            _ => Current.Source is { } where ? LoadAsync(where, replace: true, ct) : Task.FromResult<LoadResult?>(null),
            scroll => Task.FromResult(ScrollBy(scroll, size)),
            _ => Task.FromResult(NextPassage(size)),
            _ => Task.FromResult(ShowHelp()),
            _ => Task.FromResult(ShowCommand(size))).ConfigureAwait(false);

        if (loaded is null) return null;

        if (transition.Rule is not OutcomeRule.Counted)
            throw new InvalidOperationException($"{transition.GetType().Name} loaded posts, and spec 8.2's row for it counts none: its outcomes would be missing from the exit code");

        if (loaded.Refusal is { } refusal)
        {
            Ledger.AddRefusal(ExitCode.For(refusal));
            return refusal;
        }

        Ledger.Add(loaded.Passages.Select(passage => passage.Outcome));
        return null;
    }

    /// <summary>The current view as a screen at <paramref name="size"/>, its status row carrying the note when there is one.</summary>
    internal Screen Render(ScreenSize size)
    {
        var screen = Layout(Current, size);

        // The window stays where the screen snapped it, but only when it was drawn: a window too small for content has no position.
        if (screen.DrawsContent && screen.Offset != Current.Offset) _views[^1] = Current.WithOffset(screen.Offset);

        return Note is { } note ? screen.WithNote(note, size.Width) : screen;
    }

    private static Screen Layout(BrowseView view, ScreenSize size) => view.Match(
        board => Screen.ForBoard(board.Posts, size, board.Offset),
        thread => Screen.ForPassages(thread.Posts, size, thread.Offset),
        read => Screen.ForPassages(read.Posts, size, read.Offset),
        _ => Screen.ForHelp(HelpLines, size));

    private async Task<LoadResult?> LoadAsync(ViewSource where, bool replace, CancellationToken ct)
    {
        var result = await source.LoadAsync(where, ct).ConfigureAwait(false);
        if (result.Refusal is not null) return result;

        var view = where.Kind switch
        {
            ViewKind.Board => (BrowseView)new BrowseView.Board(where.Id, result.Passages),
            ViewKind.Thread => new BrowseView.Thread(where.Id, result.Passages),
            ViewKind.Read => new BrowseView.Read(where.Id, result.Passages),
            _ => throw new InvalidOperationException("a view source is a board, a thread or a post"),
        };

        if (replace && _views.Count > 0) _views[^1] = view.WithOffset(Current.Offset);
        else _views.Add(view);

        return result;
    }

    private LoadResult? OpenFromIndex(ScreenSize size)
    {
        if (Current is not BrowseView.Board board || board.Posts.IsEmpty) return null;

        var passage = board.Posts[PassageAt(board, size)];
        _views.Add(new BrowseView.Read(passage.Post.PostId, [passage]));
        return null;
    }

    private LoadResult? GoBack()
    {
        if (_views.Count > 1) _views.RemoveAt(_views.Count - 1);
        return null;
    }

    private LoadResult? ScrollBy(BrowseTransition.Scroll scroll, ScreenSize size)
    {
        var screen = Layout(Current, size);
        var target = scroll.Relative ? screen.Offset + scroll.Amount : scroll.Amount;
        if (scroll.Relative) target = screen.Settle(target, towardsEnd: scroll.Amount > 0);

        // A pager stops with its last row at the bottom (spec 14.3), not with one row on show. A window already past that, by a
        // Tab to a short last passage, is not moved up by a key that scrolls down.
        var ceiling = scroll.Relative ? Math.Max(screen.MaxOffset, screen.Offset) : screen.MaxOffset;
        _views[^1] = Current.WithOffset(Math.Clamp(target, 0, ceiling));
        return null;
    }

    private LoadResult? NextPassage(ScreenSize size)
    {
        var screen = Layout(Current, size);
        if (screen.PassageStarts.IsDefaultOrEmpty) return null;

        var next = screen.PassageStarts.FirstOrDefault(start => start > screen.Offset, defaultValue: screen.PassageStarts[0]);
        _views[^1] = Current.WithOffset(next);
        return null;
    }

    private LoadResult? ShowHelp()
    {
        if (Current is BrowseView.Help) _views.RemoveAt(_views.Count - 1);
        else _views.Add(new BrowseView.Help());
        return null;
    }

    private LoadResult? ShowCommand(ScreenSize size)
    {
        if (Current.Passages.IsEmpty)
        {
            Note = new OwnText("there is no post in view to name a command for");
            return null;
        }

        // The topmost passage: the one the window's first row is in.
        var passage = Current.Passages[PassageAt(Current, size)];
        Note = new OwnText(new FrameBuilder().Append($"the equivalent command, which also checks inclusion and the key's lifecycle: {Hints.Verify(passage.Post.PostId)}").ToString());
        return null;
    }

    /// <summary>The index of the passage (or index row) the window's first row is in.</summary>
    private static int PassageAt(BrowseView view, ScreenSize size)
    {
        var screen = Layout(view, size);
        var starts = screen.PassageStarts;
        if (starts.IsDefaultOrEmpty) return 0;

        var at = starts.ToList().FindLastIndex(start => start <= screen.Offset);
        return Math.Max(0, at);
    }

    private static readonly string[] HelpLines =
    [
        "curia browse: keys",
        "  up, down, j, k   scroll a row          page up, page down, space   scroll a page          home, end   top and bottom",
        "  tab              next post             enter   open the post an index row names          backspace   back",
        "  r                fetch the posts again           !   the equivalent curia command          ?   this help          q   quit",
        "  ctrl-c, ctrl-\\   quit                  ctrl-z   not supported here: suspending would leave the terminal as the screen left it",
        "The exit code is the worst outcome of every post shown, as curia read, thread and board --titles give it: 6 a check did not hold, 8 it could not be run, 9 disputed, then 4 or 5 a load was refused, 0 otherwise.",
        "A window too short for the full header gets a compact one, which leaves out the digest and kid row and the owner and server_ts of the post (curia read shows them); it still states everything R10.74 lists. Residual: a combining mark on a layout space draws as it would on any base.",
        "A post's content is laid out from the bytes its signature covers, and is shown only where the signature held. curia verify checks more: inclusion, consistency, the key's binding and lifecycle.",
        "If the terminal is left in a bad state, run: reset    (or: tput rmcup; tput smam; tput cnorm)",
        "press backspace or ? to leave this help",
    ];
}
