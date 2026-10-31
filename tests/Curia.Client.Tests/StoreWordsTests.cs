using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Client.Cli;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// ULG25, ULG27 to ULG30 (errata G52, R11.72) at the command: the real <c>curia</c> run in a child process against a Forum on a loopback socket
/// (<see cref="ListenerForum"/>) that answers <c>503</c> with each of the three refusals. Every sentence is read from the errata's <c>R11.72 words</c>
/// (<see cref="PublishedStoreFaults"/>), folded for whitespace, and matched in the printed text in the order the entry gives it: the refusal's title and type, then
/// the sentence. <c>curia-mcp</c>'s half is in <c>Curia.Mcp.Tests</c> (<c>StoreWordsToolTests</c>).
/// </summary>
[Collection("console")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class StoreWordsTests
{
    private static string Fold(string text) => Whitespace().Replace(text, " ");

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    /// <summary>The three refusals, each as the Forum would send it and as the entry words it.</summary>
    private sealed record Case(string Name, string Title, string Type, string Sentence, bool IsToken);

    private static Case[] Cases() =>
    [
        new("log", PublishedStoreFaults.LogUnreadableTitle, PublishedStoreFaults.LogUnreadableType, PublishedStoreFaults.LogUnreadableSentence, false),
        new("store", PublishedStoreFaults.StoreUnavailableTitle, PublishedStoreFaults.StoreUnavailableType, PublishedStoreFaults.StoreUnavailableSentence, false),
        new("token", PublishedStoreFaults.TokenCode, PublishedStoreFaults.TokenCode, PublishedStoreFaults.TokenSentence, true),
    ];

    public static TheoryData<string> Names() => new() { "log", "store", "token" };

    /// <summary>Runs <c>curia budget</c> (a read that mints a token first) against a Forum that refuses as <paramref name="c"/>; returns the run and the requests it made.</summary>
    private static async Task<(int Exit, string Text, int Reads, int Tokens)> RunAsync(Case c)
    {
        using var forum = new ListenerForum();
        if (c.IsToken)
        {
            forum.Token = () => (PublishedStoreFaults.TokenStatus, $"{{\"error\":\"{PublishedStoreFaults.TokenCode}\",\"error_description\":\"{PublishedStoreFaults.TokenDescription}\"}}");
        }
        else
        {
            forum.Answer = (_, _) => (PublishedStoreFaults.UnavailableStatus, $"{{\"type\":\"{c.Type}\",\"title\":\"{c.Title}\",\"detail\":null}}");
        }

        using var home = new CliHome(forum.Address);
        var run = await home.RunAsync("budget", "--agent", "alice", "--forum", forum.Address.ToString());
        return (run.Exit, Fold(run.Stdout + " " + run.Stderr), forum.Calls("GET /v1/budget").Count, forum.Calls("POST /oauth/token").Count);
    }

    private static string WordsProblems(Case c, string text)
    {
        var problems = new List<string>();
        var sentence = Fold(c.Sentence);
        var title = text.IndexOf(Fold(c.Title), StringComparison.Ordinal);
        var type = text.IndexOf(c.Type, StringComparison.Ordinal);
        var at = text.IndexOf(sentence, StringComparison.Ordinal);
        if (!c.IsToken && (title < 0 || type < 0)) problems.Add($"lacks the title and type (\"{c.Title}\", {c.Type})");
        if (at < 0) problems.Add($"lacks \"{sentence}\"");
        else if (!c.IsToken && title >= 0 && type >= 0 && at < Math.Max(title, type)) problems.Add("the sentence comes before the title and type");
        return string.Join("; ", problems);
    }

    /// <summary>
    /// ULG25. <c>curia</c> words the three refusals: each refusal's text contains its published sentence after the title and type (the token's: after its code),
    /// and the exit is the Forum-fault code, 8.
    /// </summary>
    [Theory]
    [MemberData(nameof(Names))]
    public async Task R11_72_ULG25_CuriaWordsEachOfTheThreeRefusalsInThePublishedWords(string name)
    {
        var c = Cases().Single(x => x.Name == name);
        var run = await RunAsync(c);

        var problems = new List<string>();
        if (run.Exit != ExitCode.ForumFault) problems.Add($"exit {run.Exit}, not {ExitCode.ForumFault}");
        var words = WordsProblems(c, run.Text);
        if (words.Length > 0) problems.Add(words);

        Assert.True(problems.Count == 0, $"{name}: {string.Join("; ", problems)}. Printed: \"{run.Text}\"");
    }

    /// <summary>ULG27, a control. <c>curia</c> never retries: exactly one request reaches the Forum per call, for each of the three refusals.</summary>
    [Theory]
    [MemberData(nameof(Names))]
    public async Task R11_72_ULG27_CuriaSendsExactlyOneRequestForEachRefusal(string name)
    {
        var c = Cases().Single(x => x.Name == name);
        var run = await RunAsync(c);

        var (reads, tokens) = c.IsToken ? (0, 1) : (1, 1);
        Assert.True((run.Reads, run.Tokens) == (reads, tokens), $"{name}: expected {reads} read(s) and {tokens} token request(s), actual {run.Reads} and {run.Tokens}");
    }

    /// <summary>
    /// ULG28. The two 503 types are worded differently: the log's text does not say a write may or may not have been recorded; the store's does; and neither is
    /// the generic server-fault line (the title and type alone).
    /// </summary>
    [Fact]
    public async Task R11_72_ULG28_TheTwoTypesAreWordedDifferentlyAndNeitherIsTheGenericLine()
    {
        var log = await RunAsync(Cases().Single(x => x.Name == "log"));
        var store = await RunAsync(Cases().Single(x => x.Name == "store"));
        var problems = new List<string>();

        if (log.Text.Contains("may or may not", StringComparison.Ordinal)) problems.Add("the log's text says a write may or may not have been recorded");
        if (!store.Text.Contains("may or may not have been recorded", StringComparison.Ordinal)) problems.Add("the store's text lacks \"may or may not have been recorded\"");
        if (log.Text == store.Text) problems.Add("both texts are equal");
        foreach (var (name, c, run) in new[] { ("log", Cases()[0], log), ("store", Cases()[1], store) })
        {
            if (!run.Text.Contains(Fold(c.Sentence), StringComparison.Ordinal)) problems.Add($"the {name} text is the generic server-fault line: \"{run.Text}\"");
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>ULG29. The token's words reach <c>curia ask</c>'s output when <c>/oauth/token</c> answers <c>temporarily_unavailable</c> (the session's token call).</summary>
    [Fact]
    public async Task R11_72_ULG29_TheTokensWordsReachAnAsk()
    {
        using var forum = new ListenerForum
        {
            Token = () => (PublishedStoreFaults.TokenStatus, $"{{\"error\":\"{PublishedStoreFaults.TokenCode}\",\"error_description\":\"{PublishedStoreFaults.TokenDescription}\"}}"),
        };
        using var home = new CliHome(forum.Address);

        var run = await home.RunAsync("ask", "--agent", "alice", "--board", "b", "--title", "A title", "--body", "A body", "--forum", forum.Address.ToString());
        var text = Fold(run.Stdout + " " + run.Stderr);

        var problems = new List<string>();
        if (run.Exit != ExitCode.ForumFault) problems.Add($"exit {run.Exit}, not {ExitCode.ForumFault}");
        if (!text.Contains(Fold(PublishedStoreFaults.TokenSentence), StringComparison.Ordinal)) problems.Add($"lacks \"{Fold(PublishedStoreFaults.TokenSentence)}\"");

        Assert.True(problems.Count == 0, $"{string.Join("; ", problems)}. Printed: \"{text}\"");
    }

    /// <summary>
    /// ULG30, the CLI's half. The help of every verb that writes carries the repeat sentence. The verbs are derived from the dispatch in <c>Program.cs</c> (each
    /// whose arm calls <c>PostAsync</c>, <c>SignalAsync</c>, <c>ResolveAsync</c> or <c>FlagAsync</c>), not listed, and the count is written after, so a writing verb
    /// added without the sentence is red. (<c>rotate</c> and <c>retire</c> write lifecycle acts that are guarded and exit 4; their words are not this entry's.)
    /// </summary>
    [Fact]
    public void R11_72_ULG30_TheHelpOfEveryVerbThatWritesCarriesTheRepeatSentence()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curia.sln"))) dir = dir.Parent;
        var program = File.ReadAllText(Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Curia.sln not found"), "src", "Curia.Client.Cli", "Program.cs"));

        var verbs = Writers().Matches(program).Select(m => m.Groups["verb"].Value).Order(StringComparer.Ordinal).ToArray();
        var sentence = Fold(PublishedStoreFaults.RepeatSentence);
        var missing = verbs.Where(v => !Fold(Help.For(v) ?? string.Empty).Contains(sentence, StringComparison.Ordinal)).ToArray();
        var expected = $"the repeat sentence is in the help of 10 verbs ({string.Join(' ', verbs)})";

        var actual = verbs.Length == 10 && missing.Length == 0
            ? expected
            : $"{verbs.Length} verbs write ({string.Join(' ', verbs)}); the sentence is absent from the help of {missing.Length} ({string.Join(' ', missing)})";
        if (!string.Equals(expected, actual, StringComparison.Ordinal)) throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    [GeneratedRegex("\"(?<verb>[a-z]+)\" => await (?:PostAsync|SignalAsync|ResolveAsync|FlagAsync)\\(", RegexOptions.CultureInvariant)]
    private static partial Regex Writers();
}
