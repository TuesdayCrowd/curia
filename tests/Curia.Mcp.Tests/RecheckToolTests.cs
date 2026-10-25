using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Mcp;
using Curia.Tests.Shared;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// Errata G35 (R11.49) at the adapter: <c>curia_recheck</c> turns the digests an agent holds into post ids and standings, on the Forum's word
/// checked by computation, and returns no word an author wrote. The oracles are the test's own: the stub's list of what it served
/// (<see cref="StubLog.BatchRequests"/>, the posts and canonical text it chose), and the digest the test computed from that text with the BCL.
/// A negative probe has a control that must pass.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class RecheckToolTests : IDisposable
{
    private const string Marker = "ZQ9AUTHOR";
    private const string NotSent = "NOT SENT";
    private const string LyingId = "01ZQPB11LYING000000000000001";

    private readonly StubLog _log = new();
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-recheck-").FullName;

    public void Dispose()
    {
        _log.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private ForumTools Tools() => new(_log.Client(), MarkingMode.None, new HeadStore(_home));

    private async Task<McpProtocolSession> SessionAsync(bool identity)
    {
        if (!identity) return await McpProtocolSession.StartAsync(Tools(), null);

        Assert.True(_log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        var writer = new ForumWriter(agent!, new ForumSession(_log.Client(), agent!, _log.Store, TimeProvider.System), TimeProvider.System);
        return await McpProtocolSession.StartAsync(new ForumTools(_log.Client(), MarkingMode.None, new HeadStore(_home), writer), writer.Agent.Profile.AgentId, agent);
    }

    private static string Canon(string title, string body = "A body.", string tags = "[]")
    {
        var text = "{\"author\":\"urn:curia:agent:alice\",\"board\":\"b\",\"body\":" + Quote(body)
            + ",\"code_blocks\":[],\"content_type\":\"agent-authored/untrusted\",\"created_at\":\"2026-01-01T00:00:00.0000000+00:00\",\"kind\":\"question\","
            + "\"nonce\":\"n0\",\"refs\":[],\"tags\":" + tags + ",\"title\":" + Quote(title) + ",\"v\":1}";

        // Non-vacuity of the oracle: the text is in the order JCS writes, or its hash is not its canonical form's.
        using var document = JsonDocument.Parse(text);
        var names = document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.True(names.SequenceEqual(names.Order(StringComparer.Ordinal)), "the fixture's canonical text is not in ordinal member order");
        return text;
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value);

    private static string Sha(string canonical) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

    private static string TextOf(CallToolResult result) =>
        string.Join('\n', result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    /// <summary>The stub's own post, as the Forum answers its digest: <see cref="StubLog.PostId"/>, whose signed canonical form hashes to <see cref="StubLog.Submission"/>'s digest.</summary>
    private string HonestDigest => _log.Submission.PrefixedDigest;

    [Fact]
    public async Task R11_49_TheToolIsRegisteredAnonymouslyAndTakesDigests()
    {
        foreach (var identity in new[] { false, true })
        {
            await using var session = await SessionAsync(identity);
            var tools = await session.ListAsync();
            Assert.NotEmpty(tools);

            var tool = Assert.Single(tools, t => t.Name == "curia_recheck");
            var schema = tool.InputSchema;
            var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToArray();
            var properties = schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();
            Assert.Equal(["digests"], required);
            Assert.Equal(["digests"], properties);
            Assert.Equal("array", ((JsonElement)schema.GetProperty("properties").GetProperty("digests")).GetProperty("type").ToString());
            Assert.True(tool.Annotations?.ReadOnlyHint, "curia_recheck is not marked read-only");
        }
    }

    [Fact]
    public async Task R11_49_ACurrentDigestIsResolvedToItsPostId()
    {
        await using var session = await SessionAsync(identity: false);

        var answer = await session.CallAsync("curia_recheck", new() { ["digests"] = new[] { HonestDigest } });

        Assert.False(answer.IsError, answer.Text);
        Assert.Contains(StubLog.PostId, answer.Text, StringComparison.Ordinal);
        var received = Assert.Single(_log.BatchRequests);
        Assert.Equal([HonestDigest], received.Select(d => d!).ToArray());
        Assert.Single(_log.Requests, r => r == "POST /v1/posts/batch");
    }

    [Fact]
    public async Task R11_49_ADigestTheForumAnswersForADifferentPostIsFAILED()
    {
        var asked = Sha(Canon("The post cited"));
        var lyingCanonical = Canon("Another post entirely");
        var actual = Sha(lyingCanonical);
        Assert.NotEqual(asked, actual);

        // The lying Forum echoes the digest it was asked and serves another post's canonical form under it, so a comparison of the echo passes it.
        _log.BatchAnswer = digests =>
        [
            StubLog.BatchItem(digests[0], "current", postJson: _log.BatchPostJson(LyingId, lyingCanonical, digests[0]!)),
            _log.BatchItemFor(digests[1]),
        ];

        var result = await Tools().RecheckAsync([asked, HonestDigest], TestContext.Current.CancellationToken);
        var text = TextOf(result);

        Assert.True(result.IsError, text);
        var failed = Assert.Single(text.Split('\n'), l => l.Contains("FAILED", StringComparison.Ordinal));
        Assert.Contains(actual, failed, StringComparison.Ordinal);
        Assert.DoesNotContain(LyingId, text, StringComparison.Ordinal);

        // The honest item in the same call still prints its id.
        Assert.Contains(StubLog.PostId, text, StringComparison.Ordinal);

        // Control: the honest item alone is not an error.
        _log.BatchAnswer = null;
        var control = await Tools().RecheckAsync([HonestDigest], TestContext.Current.CancellationToken);
        Assert.False(control.IsError, TextOf(control));
        Assert.Contains(StubLog.PostId, TextOf(control), StringComparison.Ordinal);
    }

    [Fact]
    public async Task R11_49_TheNamedPostIsNotARead()
    {
        var tools = Tools();
        var named = TextOf(await tools.RecheckAsync([HonestDigest], TestContext.Current.CancellationToken));
        Assert.Contains(StubLog.PostId, named, StringComparison.Ordinal);

        var verified = TextOf(await tools.VerifyAsync(StubLog.PostId, null, TestContext.Current.CancellationToken));

        Assert.Contains("WARNING: no read in this session", verified, StringComparison.Ordinal);

        // Control: after a read of the same post, the same call verifies that read.
        Assert.NotEmpty((await tools.ReadAsync(StubLog.PostId, TestContext.Current.CancellationToken)).Content);
        var afterRead = TextOf(await tools.VerifyAsync(StubLog.PostId, null, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("WARNING: no read in this session", afterRead, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R11_49_TheClosingLineSaysNoSignatureAndNoInclusionWasChecked()
    {
        var phrases = PublishedRecheck.R1149ClosingPhrases();
        Assert.Equal(3, phrases.Length);

        var text = TextOf(await Tools().RecheckAsync([HonestDigest], TestContext.Current.CancellationToken));

        var last = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
        foreach (var phrase in phrases)
            Assert.Contains(phrase, last, StringComparison.Ordinal);
    }

    public static TheoryData<string, string?[], string> Refused() => new()
    {
        { "empty", [], "digests" },
        { "null item", [null], "digests[0]" },
        { "blank item", ["  "], "digests[0]" },
        { "empty second item", ["sha256:" + "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90", ""], "digests[1]" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task R11_49_BadArgumentsAreRefusedByNameAndNothingIsSent(string label, string?[] digests, string name)
    {
        await using var session = await SessionAsync(identity: false);
        Assert.NotEmpty(label);

        var result = await session.CallAsync("curia_recheck", new() { ["digests"] = digests });

        Assert.True(
            result.IsError && result.Text.StartsWith(NotSent, StringComparison.Ordinal) && result.Text.Contains(name, StringComparison.Ordinal),
            $"{label}: digests were not refused by name:\n{result.Text}");
        Assert.DoesNotContain("POST /v1/posts/batch", _log.Requests);
    }

    [Fact]
    public async Task R11_49_TheForumsCapRefusalIsRelayedAndTheCapIsNotGuessed()
    {
        _log.BatchCap = 7;
        var digests = Enumerable.Range(0, 65).Select(i => "sha256:" + i.ToString("x", System.Globalization.CultureInfo.InvariantCulture).PadLeft(64, '0')).ToArray();

        var refusal = await Assert.ThrowsAsync<McpException>(() => Tools().RecheckAsync(digests, TestContext.Current.CancellationToken));

        Assert.Contains("cap=7", refusal.Message, StringComparison.Ordinal);

        // No local cap: all sixty-five reached the stub.
        Assert.Equal(65, Assert.Single(_log.BatchRequests).Length);
    }

    [Fact]
    public async Task R11_49_NoWordOfAPostReachesTheResult()
    {
        var canonical = Canon($"Title {Marker}", $"Body {Marker}", $"[\"{Marker}\"]");
        var digest = Sha(canonical);
        _log.BatchAnswer = _ => [StubLog.BatchItem(digest, "current", postJson: _log.BatchPostJson(StubLog.PostId, canonical, digest, rendered: $"Rendered {Marker}"))];

        var result = await Tools().RecheckAsync([digest], TestContext.Current.CancellationToken);

        // Non-vacuity: the tool answered with the post it was served, and the marker is in what the stub served.
        Assert.Contains(StubLog.PostId, TextOf(result), StringComparison.Ordinal);
        Assert.Contains(Marker, canonical, StringComparison.Ordinal);
        Assert.All(result.Content, block => Assert.DoesNotContain(Marker, (block as TextContentBlock)?.Text ?? string.Empty, StringComparison.Ordinal));
        Assert.All(result.Content, block => Assert.IsType<TextContentBlock>(block));
    }
}
