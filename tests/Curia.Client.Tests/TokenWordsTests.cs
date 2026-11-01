using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Client.Cli;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// WAF24 to WAF27 (errata G53, R11.74 (c)) at the command: the real <c>curia</c> in a child process against a Forum on a loopback socket that refuses the token
/// endpoint with <c>503 temporarily_unavailable</c>. The sentence is read from the errata's <c>R11.74 words</c> (<see cref="PublishedClientWords"/>); the claim it
/// makes (the call was refused before anything was sent to be recorded) is checked against the requests the stub counted, which a verb's own list states
/// beside the verb, and not against the sentence's reading of itself. <c>curia-mcp</c>'s half is in <c>Curia.Mcp.Tests</c> (<c>TokenWordsToolTests</c>).
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("console")]
public sealed partial class TokenWordsTests
{
    private static string Fold(string text) => PublishedClientWords.Fold(text);

    private static string TokenRefusalBody() =>
        $"{{\"error\":\"{PublishedStoreFaults.TokenCode}\",\"error_description\":\"{PublishedStoreFaults.TokenDescription}\"}}";

    private const string Token = "POST /oauth/token";

    private static string Parent => $"GET /v1/posts/{StubLog.PostId}";

    /// <summary>
    /// The verbs that need a token, each with the requests the stub must have counted for a call the token refused (M6): the token request alone, or, for the
    /// verbs that read the parent first, a read of the parent, any further reads, and then the token request. Written here by verb, not derived from a run.
    /// </summary>
    public static TheoryData<string> Verbs() => new() { "ask", "comment", "resolve", "flag", "budget", "inbox", "answer", "revision" };

    private static string[] ArgsFor(string verb) => verb switch
    {
        "ask" => ["ask", "--agent", "alice", "--board", "b", "--title", "A title?", "--body", "A body."],
        "comment" => ["comment", "--agent", "alice", "--board", "b", "--parent", StubLog.PostId, "--body", "A comment."],
        "answer" => ["answer", "--agent", "alice", "--board", "b", "--parent", StubLog.PostId, "--body", "An answer."],
        "revision" => ["revision", "--agent", "alice", "--board", "b", "--parent", StubLog.PostId, "--body", "A corrected body."],
        "resolve" => ["resolve", StubLog.AnswerPostId, "--agent", "alice"],
        "flag" => ["flag", StubLog.PostId, "--agent", "alice", "--kind", "spam", "--rationale", "a reason"],
        "budget" => ["budget", "--agent", "alice"],
        "inbox" => ["inbox", "--agent", "alice"],
        _ => throw new InvalidOperationException($"this fact does not know the verb {verb}"),
    };

    private sealed record Run(int Exit, string Text, List<string> Requests);

    private static async Task<Run> RunAsync(string verb)
    {
        using var log = new StubLog { TokenRefusalJson = TokenRefusalBody() };
        using var listener = new StubLogListener(log);
        var run = await CliHome.RunInAsync(log.Store.Root, [.. ArgsFor(verb), "--forum", listener.Address.ToString()]);
        return new Run(run.Exit, Fold(run.Stdout + " " + run.Stderr), [.. log.Requests]);
    }

    /// <summary>
    /// WAF24. The token's words, and what was sent. Each verb refused for its token prints the published sentence and exits 8, and the stub's request list for the
    /// call is the list written beside the verb: no write but the token's was sent, the verbs that read a parent first made that read, and the one token request is
    /// the call's last.
    /// </summary>
    [Theory]
    [MemberData(nameof(Verbs))]
    public async Task R11_74_WAF24_ATokenRefusalIsWordedAsTheSessionsAndNothingWasSentToBeRecorded(string verb)
    {
        var run = await RunAsync(verb);
        var problems = new List<string>();

        var sentence = Fold(PublishedClientWords.TokenSentence);
        if (run.Exit != ExitCode.ForumFault) problems.Add($"exit {run.Exit}, not {ExitCode.ForumFault}");
        if (!run.Text.Contains(sentence, StringComparison.Ordinal)) problems.Add($"lacks \"{sentence}\"");

        var readsParent = verb is "answer" or "revision";
        var writes = run.Requests.Where(r => !r.StartsWith("GET ", StringComparison.Ordinal)).ToList();
        if (writes.Count != 1 || writes[0] != Token) problems.Add($"requests that are not a GET: {string.Join(", ", writes)}; expected the token's alone");
        if (readsParent)
        {
            if (run.Requests.FirstOrDefault() != Parent) problems.Add($"the first request was {run.Requests.FirstOrDefault() ?? "none"}, not the read of the parent");
            if (run.Requests.LastOrDefault() != Token) problems.Add($"the last request was {run.Requests.LastOrDefault() ?? "none"}, not the token's");
            if (verb == "answer" && !run.Requests.SequenceEqual([Parent, Token])) problems.Add($"requests {string.Join(", ", run.Requests)}; expected the parent and the token");
        }
        else if (!run.Requests.SequenceEqual([Token])) problems.Add($"requests {string.Join(", ", run.Requests)}; expected the token's alone");

        Assert.True(problems.Count == 0, $"{verb}: {string.Join("; ", problems)}. Printed: \"{run.Text}\"");
    }

    /// <summary>
    /// WAF24's Forum-side leg (M6, with a stub that answers the parent's read <c>503 curia/log/unreadable</c> as a Forum whose log is unreadable does): the
    /// verbs that read the parent first and need it print <c>curia/log/unreadable</c>'s sentence and not the token's, and <c>answer</c>, whose read of the parent is
    /// advisory, goes on to the token and prints the token's. Two sentences by verb, each true of its own call.
    /// </summary>
    [Fact]
    public async Task R11_74_WAF24_AVerbWhoseParentReadFailsFirstPrintsTheLogsSentenceAndNotTheTokens()
    {
        var problems = new List<string>();
        var tokenSentence = Fold(PublishedClientWords.TokenSentence);
        var logSentence = Fold(PublishedStoreFaults.LogUnreadableSentence);

        foreach (var (verb, expectsToken) in new[] { ("revision", false), ("answer", true) })
        {
            using var forum = new ListenerForum
            {
                Answer = (_, _) => (PublishedStoreFaults.UnavailableStatus, $"{{\"type\":\"{PublishedStoreFaults.LogUnreadableType}\",\"title\":\"{PublishedStoreFaults.LogUnreadableTitle}\",\"detail\":null}}"),
                Token = () => (PublishedStoreFaults.TokenStatus, TokenRefusalBody()),
            };
            using var home = new CliHome(forum.Address);
            var run = await home.RunAsync([.. ArgsFor(verb), "--forum", forum.Address.ToString()]);
            var text = Fold(run.Stdout + " " + run.Stderr);

            if (expectsToken && !text.Contains(tokenSentence, StringComparison.Ordinal)) problems.Add($"{verb}: lacks the token's sentence");
            if (!expectsToken && text.Contains(tokenSentence, StringComparison.Ordinal)) problems.Add($"{verb}: prints the token's sentence over an unreadable parent");
            if (!expectsToken && !text.Contains(logSentence, StringComparison.Ordinal)) problems.Add($"{verb}: lacks the log's sentence \"{logSentence}\"");
            if (!expectsToken && forum.Calls(Token).Count != 0) problems.Add($"{verb}: reached the token endpoint after its parent could not be read");
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>WAF25, a control (green on the seam tree, ULG27's twin; red under FWAF20). The CLI never retries a refused token: exactly one token request per call.</summary>
    [Theory]
    [InlineData("ask")]
    [InlineData("answer")]
    [InlineData("budget")]
    public async Task R11_74_WAF25_CuriaSendsExactlyOneTokenRequestForARefusedToken(string verb)
    {
        var run = await RunAsync(verb);
        var tokens = run.Requests.Count(r => r == Token);
        Assert.True(tokens == 1, $"{verb}: {tokens} token requests were made, expected 1. Requests: {string.Join(", ", run.Requests)}");
    }

    // The token line is printed as a display literal (in quotes), whatever it says.
    [GeneratedRegex(@"token\s+""none cached""", RegexOptions.CultureInvariant)]
    private static partial Regex NoneCached();

    [GeneratedRegex(@"token\s+""valid for \d+s""", RegexOptions.CultureInvariant)]
    private static partial Regex ValidFor();

    [GeneratedRegex(@"token\s+""[^""]*""", RegexOptions.CultureInvariant)]
    private static partial Regex TokenLine();

    /// <summary>
    /// WAF26. <c>whoami</c> says, in three states, what the token cache holds and why, and makes no call: a cached token whose expiry has passed says it has expired and
    /// that a new one is requested at the next call (the published line); none cached says so; a valid one says for how long. The stub counts no request.
    /// </summary>
    [Fact]
    public async Task R11_74_WAF26_WhoamiSaysWhyACachedTokenLooksExpiredAndMakesNoCall()
    {
        using var forum = new ListenerForum();
        using var home = new CliHome(forum.Address);
        var store = new ProfileStore(home.Path);
        var problems = new List<string>();

        string Line(string stdout) => TokenLine().Match(Fold(stdout)) is { Success: true } m ? m.Value : "(no token line)";

        var none = Line((await home.RunAsync("whoami", "--agent", "alice")).Stdout);
        if (!NoneCached().IsMatch(none)) problems.Add($"none cached: no \"token none cached\" line in \"{none}\"");

        store.WriteToken("alice", new CachedToken("a-token", DateTimeOffset.UtcNow.AddMinutes(-10), null));
        var expired = Line((await home.RunAsync("whoami", "--agent", "alice")).Stdout);
        var published = Fold(PublishedClientWords.WhoamiExpired);
        if (!expired.Contains($"token \"{published}\"", StringComparison.Ordinal)) problems.Add($"expired: lacks \"token \"{published}\"\"; printed \"{expired}\"");

        store.WriteToken("alice", new CachedToken("a-token", DateTimeOffset.UtcNow.AddMinutes(10), null));
        var valid = Line((await home.RunAsync("whoami", "--agent", "alice")).Stdout);
        if (!ValidFor().IsMatch(valid)) problems.Add($"valid: no \"token valid for <n>s\" line in \"{valid}\"");

        if (!forum.Requests.IsEmpty) problems.Add($"whoami made {forum.Requests.Count} request(s): {string.Join(", ", forum.Requests)}");

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// WAF27. A scan: no client word says "Nothing was minted", "nothing you sent was read" or a bare <c>expired</c> for a token, in the sources of
    /// <c>Curia.Client</c>, <c>Curia.Client.Cli</c> and <c>Curia.Mcp</c> (where every sentence, help and description is written or composed).
    /// </summary>
    [Fact]
    public void R11_74_WAF27_NoWordOfTheClientsSaysNothingWasMintedOrABareExpired()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curia.sln"))) dir = dir.Parent;
        var root = dir?.FullName ?? throw new InvalidOperationException("Curia.sln not found");

        string[] banned = ["Nothing was minted", "nothing you sent was read", "\"expired\""];
        var found = new List<string>();
        foreach (var project in new[] { "src/Curia.Client", "src/Curia.Client.Cli", "src/Curia.Mcp" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
                var text = File.ReadAllText(file);
                foreach (var word in banned.Where(w => text.Contains(w, StringComparison.OrdinalIgnoreCase)))
                    found.Add($"{Path.GetRelativePath(root, file)}: {word}");
            }
        }

        Assert.True(found.Count == 0, string.Join("; ", found));
    }
}
