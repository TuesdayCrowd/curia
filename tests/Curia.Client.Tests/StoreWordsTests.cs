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

    // ----- the act line (ruling 22, R11.72 act words): ULG55 and ULG57 -----

    /// <summary>The five verbs that sign an envelope and send it to <c>POST /v1/posts</c>: the four posts and one of the signals (they share the path).</summary>
    public static TheoryData<string> SigningVerbs() => new() { "ask", "answer", "comment", "revision", "endorse" };

    private static string[] SigningArgs(string verb) => verb switch
    {
        "ask" => ["ask", "--agent", "alice", "--board", "b", "--title", "A title?", "--body", "A body."],
        "answer" => ["answer", "--agent", "alice", "--board", "b", "--parent", StubLog.PostId, "--body", "An answer."],
        "comment" => ["comment", "--agent", "alice", "--board", "b", "--parent", StubLog.PostId, "--body", "A comment."],
        "revision" => ["revision", "--agent", "alice", "--board", "b", "--parent", StubLog.PostId, "--body", "A corrected body."],
        "endorse" => ["endorse", "sha256:" + new string('a', 64), "--agent", "alice", "--board", "b"],
        _ => throw new InvalidOperationException($"this fact does not know the verb {verb}"),
    };

    /// <summary>What a run of a verb against the stub printed, the submissions the stub received, and the digest of the last, recomputed from what it received.</summary>
    private sealed record Signed(int Exit, string Text, int Submissions, string? Digest);

    /// <summary>Runs <paramref name="args"/> in the stub's profile store against the stub on a socket, the stub answering every write as <paramref name="refusal"/>.</summary>
    private static async Task<Signed> RunAgainstStubAsync(StubIngestRefusal? refusal, string? tokenRefusal, params string[] args)
    {
        using var log = new StubLog { RefusesSubmissionsAs = refusal, TokenRefusalJson = tokenRefusal };
        using var listener = new StubLogListener(log);
        var run = await CliHome.RunInAsync(log.Store.Root, [.. args, "--forum", listener.Address.ToString()]);
        var sent = log.WrittenBodies.Where(ReceivedAct.IsSubmission).ToList();
        // The DPoP nonce challenge makes the client send the same act twice: what is counted is the acts, by the digest recomputed from each body.
        var acts = sent.Select(ReceivedAct.DigestOf).Distinct(StringComparer.Ordinal).ToList();
        return new Signed(run.Exit, Fold(run.Stdout + " " + run.Stderr), acts.Count, acts.Count == 0 ? null : acts[^1]);
    }

    [GeneratedRegex(@"^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixedDigest();

    /// <summary>The words an act line begins with: the published act words up to the digest. Nothing is printed with them but an act line.</summary>
    private static string ActLead() => Fold(PublishedStoreFaults.ActSentence[..PublishedStoreFaults.ActSentence.IndexOf("<digest>", StringComparison.Ordinal)]);

    /// <summary>
    /// ULG55. <c>curia</c> prints the act line after a <c>503</c> <c>curia/store/unavailable</c> of a signed post: for each verb that signs an envelope, the
    /// output holds the published act words with a digest that equals SHA-256 over the canonical form recomputed from the envelope the stub received (never
    /// the value <c>SubmissionBuilder</c> returned), in the <c>sha256:</c> spelling <c>recheck</c> takes, and the store sentence.
    /// </summary>
    [Theory]
    [MemberData(nameof(SigningVerbs))]
    public async Task R11_72_ULG55_CuriaPrintsTheActLineAfterAStoreFaultOfASignedPost(string verb)
    {
        var run = await RunAgainstStubAsync(StubIngestRefusal.StoreUnavailable, null, SigningArgs(verb));

        var problems = new List<string>();
        if (run.Exit != ExitCode.ForumFault) problems.Add($"exit {run.Exit}, not {ExitCode.ForumFault}");
        if (run.Submissions != 1) problems.Add($"the stub received {run.Submissions} different acts, not one");
        if (run.Digest is null || !PrefixedDigest().IsMatch(run.Digest)) problems.Add($"the recomputed digest is not in the sha256: spelling: {run.Digest}");
        else if (!run.Text.Contains(Fold(PublishedStoreFaults.ActLine(run.Digest)), StringComparison.Ordinal)) problems.Add($"lacks \"{Fold(PublishedStoreFaults.ActLine(run.Digest))}\"");
        if (!run.Text.Contains(Fold(PublishedStoreFaults.StoreUnavailableSentence), StringComparison.Ordinal)) problems.Add("lacks the store sentence");

        Assert.True(problems.Count == 0, $"{verb}: {string.Join("; ", problems)}. Printed: \"{run.Text}\"");
    }

    /// <summary>The act line out of a printed output (already folded): from "The act sent:" to the end, where the client prints it last.</summary>
    private static string ActLineOf(string text)
    {
        var start = text.IndexOf("The act sent:", StringComparison.Ordinal);
        return start < 0 ? string.Empty : text[start..];
    }

    /// <summary>
    /// WAF36 (errata G53, ruling 18, R11.73). The act line and the store sentence do not claim each other's work: from a real <c>503 curia/store/unavailable</c> of an
    /// <c>ask</c>, the printed act line says the Forum's <c>recheck</c> answers as of the moment it is asked, carries no wait and no instruction to send, and the store
    /// sentence beside it still carries the wait.
    /// </summary>
    [Fact]
    public async Task R11_73_WAF36_TheActLineSaysAsOfTheMomentItIsAskedAndCarriesNoWaitWhileTheStoreSentenceDoes()
    {
        var run = await RunAgainstStubAsync(StubIngestRefusal.StoreUnavailable, null, SigningArgs("ask"));
        var act = ActLineOf(run.Text);

        var problems = new List<string>();
        if (act.Length == 0) problems.Add("no act line was printed");
        if (!act.Contains("as of the moment it is asked", StringComparison.Ordinal)) problems.Add("the act line lacks \"as of the moment it is asked\"");
        if (act.Contains(PublishedClientWords.Wait, StringComparison.Ordinal) || act.Contains("seconds", StringComparison.Ordinal)) problems.Add("the act line carries a wait");
        if (act.Contains("send", StringComparison.Ordinal)) problems.Add("the act line carries an instruction to send");
        if (!Fold(run.Text).Contains(PublishedClientWords.Wait, StringComparison.Ordinal)) problems.Add("the store sentence beside it does not carry the wait");

        Assert.True(problems.Count == 0, $"{string.Join("; ", problems)}. Printed: \"{run.Text}\"");
    }

    public static TheoryData<string> PostVerbs() => new() { "ask", "answer", "comment", "revision" };

    /// <summary>
    /// ULG61 (R11.72, R11.64). <c>curia</c> makes no second submission after a store fault: for each of the four posts, the stub receives as many submission
    /// POSTs when it answers <c>store/unavailable</c> as it does when it serves the call (the DPoP nonce challenge makes a served call more than one, so the
    /// count is the fact's own baseline and never a constant), the refusal makes no more, and the nonce-challenge POSTs carry one act.
    /// </summary>
    [Theory]
    [MemberData(nameof(PostVerbs))]
    public async Task R11_72_ULG61_CuriaMakesNoSecondSubmissionAfterAStoreFault(string verb)
    {
        async Task<int> CountAsync(StubIngestRefusal? refusal)
        {
            using var log = new StubLog { RefusesSubmissionsAs = refusal };
            using var listener = new StubLogListener(log);
            await CliHome.RunInAsync(log.Store.Root, [.. SigningArgs(verb), "--forum", listener.Address.ToString()]);
            return log.WrittenBodies.Count(ReceivedAct.IsSubmission);
        }

        var served = await CountAsync(null);
        var refused = await CountAsync(StubIngestRefusal.StoreUnavailable);

        Assert.True(served >= 1 && refused == served,
            $"{verb}: a served call makes {served} submission POSTs and a call refused as store/unavailable makes {refused}; the CLI must make no more after the refusal");
    }

    public static TheoryData<string> NoActCases() => new() { "log-unreadable", "token", "gate-unavailable", "resolve", "flag", "read" };

    /// <summary>
    /// ULG57, a control (red under FULG48). No act line where there is no act to ask about: none after <c>curia/log/unreadable</c>, after the token's
    /// <c>temporarily_unavailable</c> or after <c>curia/budget/gate-unavailable</c> to a post, none after <c>resolve</c> or <c>flag</c> answered
    /// <c>store/unavailable</c> (which carry the store sentence), and none for a <c>store/unavailable</c> of a read (which carries it too).
    /// </summary>
    [Theory]
    [MemberData(nameof(NoActCases))]
    public async Task R11_72_ULG57_NoActLineWhereThereIsNoActToAskAbout(string name)
    {
        var ask = SigningArgs("ask");
        string text;
        var carriesStoreSentence = false;
        switch (name)
        {
            case "log-unreadable":
                text = (await RunAgainstStubAsync(StubIngestRefusal.LogUnreadable, null, ask)).Text;
                break;
            case "token":
                text = (await RunAgainstStubAsync(null, $"{{\"error\":\"{PublishedStoreFaults.TokenCode}\",\"error_description\":\"{PublishedStoreFaults.TokenDescription}\"}}", ask)).Text;
                break;
            case "gate-unavailable":
                text = (await RunAgainstStubAsync(StubIngestRefusal.GateUnavailable, null, ask)).Text;
                break;
            case "resolve":
                text = (await RunAgainstStubAsync(StubIngestRefusal.StoreUnavailable, null, "resolve", StubLog.PostId, "--agent", "alice")).Text;
                carriesStoreSentence = true;
                break;
            case "flag":
                text = (await RunAgainstStubAsync(StubIngestRefusal.StoreUnavailable, null, "flag", StubLog.PostId, "--kind", "spam", "--rationale", "this reads as spam", "--agent", "alice")).Text;
                carriesStoreSentence = true;
                break;
            default:
                text = (await RunAsync(Cases().Single(x => x.Name == "store"))).Text;
                carriesStoreSentence = true;
                break;
        }

        var problems = new List<string>();
        if (text.Contains(ActLead(), StringComparison.Ordinal)) problems.Add("prints an act line");
        if (carriesStoreSentence && !text.Contains(Fold(PublishedStoreFaults.StoreUnavailableSentence), StringComparison.Ordinal)) problems.Add("lacks the store sentence");
        if (text.Length == 0) problems.Add("printed nothing");

        Assert.True(problems.Count == 0, $"{name}: {string.Join("; ", problems)}. Printed: \"{text}\"");
    }

    [GeneratedRegex("\"(?<verb>[a-z]+)\" => await (?:PostAsync|SignalAsync|ResolveAsync|FlagAsync)\\(", RegexOptions.CultureInvariant)]
    private static partial Regex Writers();
}
