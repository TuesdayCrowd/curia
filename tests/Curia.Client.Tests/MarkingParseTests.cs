using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// A served post's <c>provenance.marking</c> is one of the enum's own names, exactly, or the response is
/// malformed. <see cref="Enum.TryParse{TEnum}(string?, out TEnum)"/> also accepts a number and a
/// comma-joined list, yielding a value <see cref="MarkingMode"/> does not define; R10.71's
/// <see cref="SpanCheck.Rendering"/> hands the marking to <see cref="Datamarking.Render"/>, which throws
/// on such a value, so a lenient parse let a Forum crash every reader.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class MarkingParseTests
{
    /// <summary>
    /// The case-sensitive <see cref="Enum.TryParse{TEnum}(string?, out TEnum)"/> already refused
    /// <c>none</c> and <c>datamark</c>; the number, sign, comma-joined and padded spellings are the ones
    /// it let through (FA28's target).
    /// </summary>
    [Theory]
    [InlineData("3")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("DelimitersOnly, Datamark")]
    [InlineData("none")]
    [InlineData("datamark")]
    [InlineData(" None")]
    [InlineData("None ")]
    public async Task R10_71_AMarkingOutsideItsThreeNamesIsMalformed(string marking)
    {
        using var log = new StubLog();
        var served = JsonNode.Parse(log.PostJson())!.AsObject();
        served["provenance"]!["marking"] = marking;

        var result = await ReadAsync(served);

        Assert.False(result.TryGetValue(out _, out var refusal), $"the marking {marking} was accepted");
        Assert.Equal(RefusalKind.Malformed, refusal!.Kind);
        Assert.Contains("provenance.marking is not a marking mode", refusal.Error.Detail, StringComparison.Ordinal);
    }

    /// <summary>The names the enum defines, so a fourth mode added in <c>Curia.Domain</c> is held here the day it is added.</summary>
    public static TheoryData<string> MarkingNames() => [.. Enum.GetNames<MarkingMode>()];

    /// <summary>Each of the enum's names parses to its value, and a passage under it renders.</summary>
    [Theory]
    [MemberData(nameof(MarkingNames))]
    public async Task R10_71_EachOfTheThreeMarkingNamesParses(string name)
    {
        var mode = Enum.Parse<MarkingMode>(name);
        using var log = new StubLog();
        var served = JsonNode.Parse(log.PostJson())!.AsObject();
        served["provenance"]!["marking"] = name;
        if (mode == MarkingMode.Datamark)
            served["provenance"]!["marking_token"] = Datamarking.DefaultControlToken;
        served["rendered"] = Datamarking.Render((string)served["canonical"]!, mode);

        var result = await ReadAsync(served);

        Assert.True(result.TryGetValue(out var post, out var refusal), refusal?.Summary);
        Assert.Equal(mode, post!.Provenance.Marking);
        var rendered = new Passage(post, SignatureCheck.Verify(post, [])).Render();
        Assert.Contains("post      ", rendered, StringComparison.Ordinal);
    }

    private static async Task<ForumResult<ProvenancePost>> ReadAsync(JsonObject served)
    {
        using var handler = new FixedHandler(served.ToJsonString());
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://forum.test") };
        var client = new ForumClient(http, new Uri("http://forum.test"));
        return await client.GetPostAsync(StubLog.PostId, MarkingMode.None, CancellationToken.None);
    }

    private sealed class FixedHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
