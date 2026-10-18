using System.Collections.Immutable;
using Curia.Client;

namespace Curia.Client.Cli;

/// <summary>What the process knows about its terminal and its environment, so the decision to draw is a function of facts.</summary>
internal sealed record TerminalFacts(
    bool InputRedirected,
    bool OutputRedirected,
    bool IsWindows,
    IReadOnlyDictionary<string, string> Environment)
{
    /// <summary>The variables the decision reads.</summary>
    private static readonly string[] Variables = ["TERM", "LC_ALL", "LC_CTYPE", "LANG", "CURIA_BROWSE"];

    internal static TerminalFacts OfProcess()
    {
        var (input, output) = Output.Redirected();
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in Variables)
        {
            if (System.Environment.GetEnvironmentVariable(name) is { } value) environment[name] = value;
        }

        return new TerminalFacts(input, output, OperatingSystem.IsWindows(), environment);
    }

    internal string? Variable(string name) => Environment.TryGetValue(name, out var value) && value.Length > 0 ? value : null;
}

/// <summary>
/// Whether to draw, and why not. <paramref name="Asked"/> is true when the caller asked for the line form (<c>--plain</c>,
/// <c>CURIA_BROWSE=plain</c>), which needs no sentence on standard error.
/// </summary>
internal readonly record struct ScreenDecision(bool Draws, string Reason, bool Asked);

/// <summary>
/// <c>curia browse</c> (errata G22): a screen for a person at a terminal, and for every other caller the matching verb's
/// bytes and exit code. This type decides which, and runs the line form; <see cref="BrowseSession"/> is the screen.
/// </summary>
internal static class BrowseCommand
{
    internal const string Usage =
        "error: usage: curia browse (board <board> | thread <root-id> | read <post-id>) [--forum <url>] [--plain | --screen]";

    /// <summary>The terminals the screen draws on without being told to (<c>--screen</c>): a prefix before a dash, or the name itself.</summary>
    private static readonly string[] TermNames = ["xterm", "screen", "tmux", "alacritty", "foot", "wezterm"];

    private static readonly string[] TermPrefixes = ["xterm-", "screen-", "tmux-", "rxvt-unicode"];

    /// <summary>
    /// R10.73: both ends terminals in a UTF-8 locale, <c>TERM</c> on the list or <c>--screen</c>, not Windows, no
    /// <c>--plain</c>, <c>CURIA_BROWSE</c> not <c>plain</c>, and no option the screen cannot honour. The reason is the first
    /// that applies, in the order a person would fix them.
    /// </summary>
    internal static ScreenDecision DrawsAScreen(Args args, TerminalFacts facts)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(facts);

        if (args.Has("plain")) return new ScreenDecision(false, "--plain asked for the line form", Asked: true);
        if (string.Equals(facts.Variable("CURIA_BROWSE"), "plain", StringComparison.Ordinal))
            return new ScreenDecision(false, "CURIA_BROWSE=plain asked for the line form", Asked: true);
        if (facts.IsWindows) return new ScreenDecision(false, "the platform is Windows, where there is no screen", Asked: false);
        if (args.Has("agent"))
            return new ScreenDecision(false, "--agent names an identity and runs the foreign-act watch, and the screen holds neither", Asked: false);
        if (args.Has("marking"))
            return new ScreenDecision(false, "--marking selects a transformation the screen does not apply", Asked: false);
        if (facts.InputRedirected) return new ScreenDecision(false, "standard input is not a terminal", Asked: false);
        if (facts.OutputRedirected) return new ScreenDecision(false, "standard output is not a terminal", Asked: false);
        if (!IsUtf8(facts)) return new ScreenDecision(false, "the locale is not UTF-8 (LC_ALL, then LC_CTYPE, then LANG)", Asked: false);

        if (!args.Has("screen") && !AllowedTerm(facts.Variable("TERM")))
        {
            return new ScreenDecision(
                false,
                facts.Variable("TERM") is null
                    ? "TERM is not set (pass --screen to draw anyway)"
                    : "TERM is not one this screen draws on (pass --screen to draw anyway)",
                Asked: false);
        }

        return new ScreenDecision(true, "a terminal in a UTF-8 locale", Asked: false);
    }

    /// <summary>The first of LC_ALL, LC_CTYPE and LANG that is set names the codeset.</summary>
    private static bool IsUtf8(TerminalFacts facts)
    {
        var locale = facts.Variable("LC_ALL") ?? facts.Variable("LC_CTYPE") ?? facts.Variable("LANG");
        return locale is not null
            && (locale.Contains("UTF-8", StringComparison.OrdinalIgnoreCase) || locale.Contains("UTF8", StringComparison.OrdinalIgnoreCase));
    }

    private static bool AllowedTerm(string? term) =>
        term is not null
        && (TermNames.Contains(term, StringComparer.Ordinal) || TermPrefixes.Any(prefix => term.StartsWith(prefix, StringComparison.Ordinal)));

    /// <summary>
    /// Runs <c>browse</c>. The line form is the matching verb's own handler, reached through <paramref name="lineForm"/> with
    /// the arguments the verb would have been given, so its stdout and exit code are the verb's and not a copy of them.
    /// </summary>
    internal static async Task<int> RunAsync(
        string[] argv, Args args, Uri forum, TerminalFacts facts, Func<string, Args, CancellationToken, Task<int>> lineForm, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(argv);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(lineForm);

        if (args.Positional.Length != 2 || args.Positional[0] is not ("board" or "thread" or "read"))
            return Output.Fail(Usage, ExitCode.Usage);

        var verb = args.Positional[0];
        var decision = DrawsAScreen(args, facts);
        if (!decision.Draws)
        {
            if (!decision.Asked) Output.Note($"browse: line form, not a screen: {new OwnText(decision.Reason)}");
            return await lineForm(verb, Args.Parse(LineFormArguments(argv, verb), 1), ct).ConfigureAwait(false);
        }

        var kind = verb switch
        {
            "board" => ViewKind.Board,
            "thread" => ViewKind.Thread,
            _ => ViewKind.Read,
        };

        using var http = Program.HttpFor(forum);
        var session = new BrowseSession(new ForumBrowseSource(new ForumClient(http, forum)));
        return await session.RunAsync(new ViewSource(kind, args.Positional[1])).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>browse board b --forum f</c> as the verb takes it: <c>board b --titles --forum f</c>. The switches of <c>browse</c> are
    /// dropped; every other token goes on exactly as given, so a value the verb reads is the one the caller wrote.
    /// </summary>
    internal static string[] LineFormArguments(string[] argv, string verb)
    {
        var line = new List<string>();
        for (var i = 1; i < argv.Length; i++)
        {
            var token = argv[i];
            if (token is "--plain" or "--screen") continue;

            line.Add(token);
            if (token.StartsWith("--", StringComparison.Ordinal) && !token.Contains('=', StringComparison.Ordinal) && i + 1 < argv.Length)
            {
                line.Add(argv[++i]);
            }
        }

        if (verb == "board") line.Add("--titles");
        return [.. line];
    }
}
