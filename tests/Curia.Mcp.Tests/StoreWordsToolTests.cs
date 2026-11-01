using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Client;
using Curia.Domain.Primitives;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// ULG26, ULG27, ULG30, ULG31 (errata G52, R11.72) at the tools: the write tools that send a post relay <c>503 curia/log/unreadable</c>,
/// <c>503 curia/store/unavailable</c> and the token endpoint's <c>temporarily_unavailable</c> in the words <c>curia</c> prints for them, as an error that names the
/// cause; none retries; every write tool's description carries the repeat sentence; and <c>curia-mcp</c> is the version the entry names. Every word is read from
/// the errata's <c>R11.72 words</c> (<see cref="PublishedStoreFaults"/>), folded for whitespace. <c>curia_resolve</c> and <c>curia_flag</c> share the relay of the
/// four post tools (<c>WriteTools.Refused</c>) but the stub refuses only <c>POST /v1/posts</c>; their descriptions are held by ULG30.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class StoreWordsToolTests : IDisposable
{
    private readonly List<IDisposable> _owned = [];
    private readonly List<string> _homes = [];

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        foreach (var home in _homes) Directory.Delete(home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Fold(string text) => Whitespace().Replace(text, " ");

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    private static string Text(CallToolResult result) => string.Join('\n', result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private ForumTools AliceTools(StubLog log)
    {
        Assert.True(log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        var home = Directory.CreateTempSubdirectory("curia-mcp-g52-").FullName;
        _homes.Add(home);
        _owned.Add(agent!);
        var session = new ForumSession(log.Client(), agent!, log.Store, TimeProvider.System);
        return new ForumTools(log.Client(), MarkingMode.Datamark, new HeadStore(home), new ForumWriter(agent!, session, TimeProvider.System));
    }

    public static TheoryData<string> PostTools() => new() { "curia_ask", "curia_answer", "curia_comment", "curia_revise" };

    private static async Task<string> CallAsync(StubLog log, string tool, ForumTools tools)
    {
        try
        {
            var result = tool switch
            {
                "curia_ask" => await tools.AskAsync("b", "A title?", "A body.", null, null, Ct),
                "curia_answer" => await tools.AnswerAsync(StubLog.PostId, "An answer.", null, Ct),
                "curia_comment" => await tools.CommentAsync(StubLog.PostId, "A comment.", null, Ct),
                "curia_revise" => await tools.ReviseAsync(StubLog.PostId, "A corrected body.", null, null, null, Ct),
                _ => throw new InvalidOperationException($"this fact does not know how to call {tool}"),
            };
            return (result.IsError == true ? "error: " : "not an error: ") + Text(result);
        }
        catch (ModelContextProtocol.McpException refused)
        {
            return "error: " + refused.Message;
        }
    }

    private sealed record Refused(string Name, StubIngestRefusal? Post, bool Token, string Title, string Type, string Sentence);

    private static Refused[] Cases() =>
    [
        new("log", StubIngestRefusal.LogUnreadable, false, PublishedStoreFaults.LogUnreadableTitle, PublishedStoreFaults.LogUnreadableType, PublishedStoreFaults.LogUnreadableSentence),
        new("store", StubIngestRefusal.StoreUnavailable, false, PublishedStoreFaults.StoreUnavailableTitle, PublishedStoreFaults.StoreUnavailableType, PublishedStoreFaults.StoreUnavailableSentence),
        new("token", null, true, PublishedStoreFaults.TokenCode, PublishedStoreFaults.TokenCode, PublishedStoreFaults.TokenSentence),
    ];

    private static string TokenRefusalBody() =>
        $"{{\"error\":\"{PublishedStoreFaults.TokenCode}\",\"error_description\":\"{PublishedStoreFaults.TokenDescription}\"}}";

    private async Task<string> RelayedAsync(string tool, Refused c)
    {
        using var log = new StubLog();
        log.RefusesSubmissionsAs = c.Post;
        if (c.Token) log.TokenRefusalJson = TokenRefusalBody();
        return Fold(await CallAsync(log, tool, AliceTools(log)));
    }

    /// <summary>
    /// ULG26. Each post-sending tool relays each of the three refusals as an error that carries the title and type and then the published sentence, and nothing
    /// worded as a tier: the same definition <c>curia</c> prints from.
    /// </summary>
    [Theory]
    [MemberData(nameof(PostTools))]
    public async Task R11_72_ULG26_EveryPostToolRelaysTheThreeRefusalsInThePublishedWords(string tool)
    {
        var problems = new List<string>();
        foreach (var c in Cases())
        {
            var relayed = await RelayedAsync(tool, c);
            var sentence = Fold(c.Sentence);
            var at = relayed.IndexOf(sentence, StringComparison.Ordinal);
            var type = relayed.IndexOf(c.Type, StringComparison.Ordinal);

            if (!relayed.StartsWith("error: ", StringComparison.Ordinal)) problems.Add($"{c.Name}: not relayed as an error");
            if (type < 0 && !c.Token) problems.Add($"{c.Name}: does not name {c.Type}");
            if (at < 0) problems.Add($"{c.Name}: lacks \"{sentence}\"");
            else if (type >= 0 && !c.Token && at < type) problems.Add($"{c.Name}: the sentence comes before the type");
            if (relayed.Contains("trust tier", StringComparison.OrdinalIgnoreCase)) problems.Add($"{c.Name}: worded with a tier");
            if (c.Name == "log" && relayed.Contains("may or may not", StringComparison.Ordinal)) problems.Add("log: says a write may or may not have been recorded");
        }

        Assert.True(problems.Count == 0, $"{tool}: {string.Join("; ", problems)}");
    }

    /// <summary>ULG27, a control. No tool retries a refusal by itself: a refused call makes as many writes (and token requests) as a served one.</summary>
    [Theory]
    [MemberData(nameof(PostTools))]
    public async Task R11_72_ULG27_NoToolRetriesAFaultByItself(string tool)
    {
        async Task<(int Writes, int Tokens)> MadeAsync(Refused? c)
        {
            using var log = new StubLog();
            if (c is not null)
            {
                log.RefusesSubmissionsAs = c.Post;
                if (c.Token) log.TokenRefusalJson = TokenRefusalBody();
            }

            _ = await CallAsync(log, tool, AliceTools(log));
            return (log.Requests.Count(r => r.StartsWith("POST /v1/posts", StringComparison.Ordinal) && !r.Contains("/batch", StringComparison.Ordinal)),
                log.Requests.Count(r => r.StartsWith("POST /oauth/token", StringComparison.Ordinal)));
        }

        var served = await MadeAsync(null);
        var problems = new List<string>();
        foreach (var c in Cases())
        {
            var (writes, tokens) = await MadeAsync(c);
            if (c.Token ? tokens != 1 : writes != served.Writes) problems.Add($"{c.Name}: {writes} writes and {tokens} token requests, a served call makes {served.Writes} and {served.Tokens}");
        }

        Assert.True(problems.Count == 0, $"{tool}: {string.Join("; ", problems)}");
    }

    // ----- the act line (ruling 22, R11.72 act words): ULG56 and ULG57 -----

    [GeneratedRegex(@"^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixedDigest();

    /// <summary>The words an act line begins with: the published act words up to the digest.</summary>
    private static string ActLead() => Fold(PublishedStoreFaults.ActSentence[..PublishedStoreFaults.ActSentence.IndexOf("<digest>", StringComparison.Ordinal)]);

    /// <summary>
    /// ULG56. Each tool that sends a post relays a <c>store/unavailable</c> refusal with the act line: the error text holds the published act words and the digest
    /// recomputed from the envelope the stub received (never the one <c>SubmissionBuilder</c> returned), in the <c>sha256:</c> spelling.
    /// </summary>
    [Theory]
    [MemberData(nameof(PostTools))]
    public async Task R11_72_ULG56_EveryPostToolRelaysTheActLineAfterAStoreFault(string tool)
    {
        using var log = new StubLog { RefusesSubmissionsAs = StubIngestRefusal.StoreUnavailable };
        var relayed = Fold(await CallAsync(log, tool, AliceTools(log)));
        // A DPoP nonce challenge makes a client send the same act twice: what is counted is the acts, by the digest recomputed from each body.
        var sent = log.WrittenBodies.Where(ReceivedAct.IsSubmission).Select(ReceivedAct.DigestOf).Distinct(StringComparer.Ordinal).ToList();

        var problems = new List<string>();
        if (!relayed.StartsWith("error: ", StringComparison.Ordinal)) problems.Add("not relayed as an error");
        if (sent.Count != 1) problems.Add($"the stub received {sent.Count} different acts, not one");
        var digest = sent.Count == 0 ? null : sent[^1];
        if (digest is null || !PrefixedDigest().IsMatch(digest)) problems.Add($"the recomputed digest is not in the sha256: spelling: {digest}");
        else if (!relayed.Contains(Fold(PublishedStoreFaults.ActLine(digest)), StringComparison.Ordinal)) problems.Add($"lacks \"{Fold(PublishedStoreFaults.ActLine(digest))}\"");

        Assert.True(problems.Count == 0, $"{tool}: {string.Join("; ", problems)}. Relayed: \"{relayed}\"");
    }

    public static TheoryData<string> NoActCases() => new() { "log-unreadable", "token", "gate-unavailable", "resolve", "flag" };

    /// <summary>
    /// ULG57, a control (red under FULG48), at the tools. No act line after <c>curia/log/unreadable</c>, the token's <c>temporarily_unavailable</c> or
    /// <c>curia/budget/gate-unavailable</c> to a post, and none after <c>curia_resolve</c> or <c>curia_flag</c> answered <c>store/unavailable</c>, which carry the
    /// store sentence.
    /// </summary>
    [Theory]
    [MemberData(nameof(NoActCases))]
    public async Task R11_72_ULG57_NoToolPrintsAnActLineWhereThereIsNoActToAskAbout(string name)
    {
        using var log = new StubLog();
        var carriesStoreSentence = false;
        switch (name)
        {
            case "log-unreadable": log.RefusesSubmissionsAs = StubIngestRefusal.LogUnreadable; break;
            case "token": log.TokenRefusalJson = TokenRefusalBody(); break;
            case "gate-unavailable": log.RefusesSubmissionsAs = StubIngestRefusal.GateUnavailable; break;
            default:
                log.RefusesSubmissionsAs = StubIngestRefusal.StoreUnavailable;
                carriesStoreSentence = true;
                break;
        }

        var tools = AliceTools(log);
        string text;
        try
        {
            var result = name switch
            {
                "resolve" => await tools.ResolveAsync(StubLog.PostId, Ct),
                "flag" => await tools.FlagAsync(StubLog.PostId, "spam", "this reads as spam", Ct),
                _ => await tools.AskAsync("b", "A title?", "A body.", null, null, Ct),
            };
            text = "not an error: " + Text(result);
        }
        catch (ModelContextProtocol.McpException refused)
        {
            text = "error: " + refused.Message;
        }

        text = Fold(text);
        var problems = new List<string>();
        if (text.Contains(ActLead(), StringComparison.Ordinal)) problems.Add("prints an act line");
        if (carriesStoreSentence && !text.Contains(Fold(PublishedStoreFaults.StoreUnavailableSentence), StringComparison.Ordinal)) problems.Add("lacks the store sentence");
        if (!text.StartsWith("error: ", StringComparison.Ordinal)) problems.Add("not relayed as an error");

        Assert.True(problems.Count == 0, $"{name}: {string.Join("; ", problems)}. Relayed: \"{text}\"");
    }

    /// <summary>
    /// ULG30, the tools' half. The description of every write tool carries the repeat sentence. The tools are the catalogue's own list of tools that write in the
    /// agent's name (<c>ToolCatalogue.WriteToolNames</c>), not a list kept here, and the count is written after, so a write tool added without the sentence is red.
    /// </summary>
    [Fact]
    public void R11_72_ULG30_TheDescriptionOfEveryWriteToolCarriesTheRepeatSentence()
    {
        using var log = new StubLog();
        var tools = AliceTools(log);
        var names = ToolCatalogue.WriteToolNames(tools);
        var descriptions = ToolCatalogue.Build(tools).ToDictionary(t => t.ProtocolTool.Name, t => Fold(t.ProtocolTool.Description ?? string.Empty), StringComparer.Ordinal);
        var sentence = Fold(PublishedStoreFaults.RepeatSentence);
        var missing = names.Where(n => !descriptions.TryGetValue(n, out var d) || !d.Contains(sentence, StringComparison.Ordinal)).ToArray();
        var expected = $"the repeat sentence is in the description of 6 write tools ({string.Join(' ', names.Order(StringComparer.Ordinal))})";

        var actual = names.Count == 6 && missing.Length == 0
            ? expected
            : $"{names.Count} write tools ({string.Join(' ', names.Order(StringComparer.Ordinal))}); the sentence is absent from the description of {missing.Length} ({string.Join(' ', missing)})";
        if (!string.Equals(expected, actual, StringComparison.Ordinal)) throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    /// <summary>
    /// ULG31. The version: the one parser of a version out of an errata paragraph, <c>PublishedReadersLastWords.Version()</c>, reads R11.72's (re-pointed from
    /// R11.70's by this commit, so that the facts that read it are red until the version moves), the catalogue reports it, and the README says it. The six facts that
    /// read the parser and the hand-written twin move in the commit that bumps; this fact is the version's own.
    /// </summary>
    [Fact]
    public void R11_72_ULG31_TheVersionIs093ThroughTheOneParserAndTheReadmeSaysIt()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curia.sln"))) dir = dir.Parent;
        var readme = File.ReadAllText(Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Curia.sln not found"), "README.md"));
        var version = PublishedReadersLastWords.Version();

        var expected = $"the parser reads 0.9.4; the catalogue reports 0.9.4; the README says `curia-mcp` is 0.9.4 and `curia-mcp` 0.9.4.";
        var actual = $"the parser reads {version}; the catalogue reports {ToolCatalogue.Version}; the README says "
            + $"{(readme.Contains($"`curia-mcp` is {version}", StringComparison.Ordinal) ? $"`curia-mcp` is {version}" : "not `curia-mcp` is " + version)} and "
            + $"{(readme.Contains($"`curia-mcp` {version}.", StringComparison.Ordinal) ? $"`curia-mcp` {version}." : "not `curia-mcp` " + version + ".")}";
        if (!string.Equals(expected, actual, StringComparison.Ordinal)) throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }
}
