using System.Diagnostics.CodeAnalysis;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// WAF24 and WAF25 (errata G53, R11.74 (c)) at the tools: each tool that needs a token, refused for it (<c>503 temporarily_unavailable</c> at the token endpoint of
/// a stub Forum that serves the parent post), answers with the published sentence as an error, and the stub's request list for the call is the list written
/// beside the tool: the token request alone, or, for the tools that read a parent first, the read of the parent and then the token request, and no write but the
/// token's. The sentence is read from the errata's <c>R11.74 words</c> (<see cref="PublishedClientWords"/>). <c>curia</c>'s half is in <c>Curia.Client.Tests</c>.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class TokenWordsToolTests : IDisposable
{
    private const string Token = "POST /oauth/token";

    private readonly List<IDisposable> _owned = [];
    private readonly List<string> _homes = [];

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        foreach (var home in _homes) Directory.Delete(home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Fold(string text) => PublishedClientWords.Fold(text);

    private static string Text(CallToolResult result) => string.Join('\n', result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private ForumTools AliceTools(StubLog log)
    {
        Assert.True(log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        var home = Directory.CreateTempSubdirectory("curia-mcp-g53-").FullName;
        _homes.Add(home);
        _owned.Add(agent!);
        var session = new ForumSession(log.Client(), agent!, log.Store, TimeProvider.System);
        return new ForumTools(log.Client(), MarkingMode.Datamark, new HeadStore(home), new ForumWriter(agent!, session, TimeProvider.System));
    }

    /// <summary>Every tool that needs a token, with the requests the stub must have counted for a call the token refused: written here by tool.</summary>
    public static TheoryData<string> Tools() => new()
    {
        "curia_budget", "curia_ask", "curia_resolve", "curia_flag", "curia_answer", "curia_comment", "curia_revise",
    };

    private static string TokenRefusalBody() =>
        $"{{\"error\":\"{PublishedStoreFaults.TokenCode}\",\"error_description\":\"{PublishedStoreFaults.TokenDescription}\"}}";

    private static async Task<string> CallAsync(string tool, ForumTools tools)
    {
        try
        {
            var result = tool switch
            {
                "curia_budget" => await tools.BudgetAsync(Ct),
                "curia_ask" => await tools.AskAsync("b", "A title?", "A body.", null, null, Ct),
                "curia_resolve" => await tools.ResolveAsync(StubLog.AnswerPostId, Ct),
                "curia_flag" => await tools.FlagAsync(StubLog.PostId, "spam", "a reason", Ct),
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

    private async Task<(string Text, List<string> Requests)> RunAsync(string tool)
    {
        using var log = new StubLog { TokenRefusalJson = TokenRefusalBody() };
        var text = Fold(await CallAsync(tool, AliceTools(log)));
        return (text, [.. log.Requests]);
    }

    /// <summary>
    /// WAF24. A token refusal is worded as the session's and nothing was sent to be recorded: the tool answers an error holding the published sentence, and the
    /// requests the stub counted are the tool's own list, with no request that is not a GET but the token's.
    /// </summary>
    [Theory]
    [MemberData(nameof(Tools))]
    public async Task R11_74_WAF24_ATokenRefusalIsWordedAsTheSessionsAndNothingWasSentToBeRecorded(string tool)
    {
        var (text, requests) = await RunAsync(tool);
        var problems = new List<string>();
        var sentence = Fold(PublishedClientWords.TokenSentence);

        if (!text.StartsWith("error: ", StringComparison.Ordinal)) problems.Add("not relayed as an error");
        if (!text.Contains(sentence, StringComparison.Ordinal)) problems.Add($"lacks \"{sentence}\"");

        var writes = requests.Where(r => !r.StartsWith("GET ", StringComparison.Ordinal)).ToList();
        if (writes.Count != 1 || writes[0] != Token) problems.Add($"requests that are not a GET: {string.Join(", ", writes)}; expected the token's alone");

        var readsParent = tool is "curia_answer" or "curia_comment" or "curia_revise";
        var parent = $"GET /v1/posts/{StubLog.PostId}";
        if (readsParent)
        {
            if (requests.FirstOrDefault() != parent) problems.Add($"the first request was {requests.FirstOrDefault() ?? "none"}, not the read of the parent");
            if (requests.LastOrDefault() != Token) problems.Add($"the last request was {requests.LastOrDefault() ?? "none"}, not the token's");
        }
        else if (!requests.SequenceEqual([Token])) problems.Add($"requests {string.Join(", ", requests)}; expected the token's alone");

        Assert.True(problems.Count == 0, $"{tool}: {string.Join("; ", problems)}. Relayed: \"{text}\"");
    }

    /// <summary>WAF25, a control (green on the seam tree, ULG27's twin; red under FWAF20's second form). No tool retries a refused token: exactly one token request per call.</summary>
    [Theory]
    [MemberData(nameof(Tools))]
    public async Task R11_74_WAF25_NoToolSendsMoreThanOneTokenRequestForARefusedToken(string tool)
    {
        var (_, requests) = await RunAsync(tool);
        var tokens = requests.Count(r => r == Token);
        Assert.True(tokens == 1, $"{tool}: {tokens} token requests were made, expected 1. Requests: {string.Join(", ", requests)}");
    }
}
