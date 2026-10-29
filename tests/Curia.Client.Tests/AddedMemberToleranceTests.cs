using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Nodes;
using JsonValue = Curia.Canon.Json.JsonValue;
using Curia.Canon.Json;
using Curia.Client;
using Curia.Domain.Primitives;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// PR 3 (errata G47, plan ruling 18), CHN53, a control (green on the unmodified tree, Task 0's M11): every client reader tolerates a member it does not know
/// and reads the document as it reads it without. It is how this entry adds <c>post_served</c> and <c>marking</c> without breaking an older client, and
/// the facts that refuse a wrong-typed member (CHN35, CHN52, CHN59) are the other half of the same decision. This fact covers the post receipt, a post and
/// its <c>provenance</c>, and a thread; the flag receipt and the contract are covered beside their own facts (<see cref="FlagReceiptWordsTests"/>,
/// <see cref="ContractMarkingTests"/>), and <c>curia-mcp</c>'s half is <c>AddedMemberToleranceToolTests</c>.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AddedMemberToleranceTests : IDisposable
{
    private readonly StubLog _log = new();

    public void Dispose()
    {
        _log.Dispose();
        GC.SuppressFinalize(this);
    }

    private static JsonValue.Object Parse(string json)
    {
        var parsed = JsonReader.Parse(Encoding.UTF8.GetBytes(json), ClientJson.Limits);
        Assert.True(parsed.TryGetValue(out var value, out var error), error?.Type);
        return Assert.IsType<JsonValue.Object>(value);
    }

    /// <summary>A post document with <c>extra_probe</c> added to the post and to its provenance envelope.</summary>
    private static string WithExtras(string postJson)
    {
        var node = JsonNode.Parse(postJson)!.AsObject();
        node["extra_probe"] = new JsonObject { ["nested"] = new JsonArray(1, 2) };
        node["provenance"]!.AsObject()["extra_probe"] = "x";
        return node.ToJsonString();
    }

    [Fact]
    public void R10_88_CHN53_AnUnknownMemberOfAPostReceiptIsTolerated()
    {
        var plain = ForumDocuments.ReadReceipt(Parse(StubLog.ReceiptJson("sha256:" + new string('a', 64))));
        var extra = ForumDocuments.ReadReceipt(Parse(StubLog.ReceiptJson("sha256:" + new string('a', 64)).Replace("{", "{\"extra_probe\":[true],", StringComparison.Ordinal)));

        Assert.True(plain.TryGetValue(out var a, out var e1), e1?.Type);
        Assert.True(extra.TryGetValue(out var b, out var e2), e2?.Type);
        Assert.Equal(a!.PostId, b!.PostId);
        Assert.Equal(a.Digest, b.Digest);
    }

    [Fact]
    public async Task R10_88_CHN53_AnUnknownMemberOfAPostOrItsProvenanceIsTolerated()
    {
        var ct = TestContext.Current.CancellationToken;
        var plain = await _log.Client().GetPostAsync(StubLog.PostId, MarkingMode.None, ct);
        _log.ServedPosts[StubLog.PostId] = WithExtras(_log.PostJson());
        var extra = await _log.Client().GetPostAsync(StubLog.PostId, MarkingMode.None, ct);

        Assert.True(plain.TryGetValue(out var a, out var r1), r1?.Summary);
        Assert.True(extra.TryGetValue(out var b, out var r2), r2?.Summary);
        Assert.Equal(new Passage(a!, new SignatureVerdict(true, "alice-1", "ok", "d")).Render(), new Passage(b!, new SignatureVerdict(true, "alice-1", "ok", "d")).Render());
    }

    [Fact]
    public async Task R10_88_CHN53_AnUnknownMemberOfAThreadsPostsIsTolerated()
    {
        var ct = TestContext.Current.CancellationToken;
        _log.ServedThreads[StubLog.PostId] = "[" + _log.PostJson() + "]";
        var plain = await _log.Client().GetThreadAsync(StubLog.PostId, MarkingMode.None, ct);
        _log.ServedThreads[StubLog.PostId] = "[" + WithExtras(_log.PostJson()) + "]";
        var extra = await _log.Client().GetThreadAsync(StubLog.PostId, MarkingMode.None, ct);

        Assert.True(plain.TryGetValue(out var a, out var r1), r1?.Summary);
        Assert.True(extra.TryGetValue(out var b, out var r2), r2?.Summary);
        Assert.Equal(a!.Select(p => p.PostId), b!.Select(p => p.PostId));
    }
}
