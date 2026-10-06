using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Curia.AuthN.Jwt;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.AuthN.Tests;

/// <summary>
/// R11.33 at the one parse every compact JWS shares (Task 11's fix review). <c>JsonDocument.Parse</c>
/// accepts an escaped unpaired surrogate, and <c>JsonElement.GetString()</c> then throws
/// <see cref="InvalidOperationException"/> on it, so an access token whose header <c>alg</c> or
/// <c>kid</c> held one answered 500 on every route behind authentication, before any key was
/// resolved. <see cref="CompactJws"/> now refuses such a segment as malformed, header and payload
/// alike, so no reader downstream is handed a string it cannot decode.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs (R11.33) they pin verbatim, mirroring " +
        "Curia.Architecture.Tests.LayeringTests' CS-6/CS-7 precedent.")]
public sealed class CompactJwsStringTests
{
    /// <summary>
    /// Each row holds an escaped unpaired surrogate as JSON text -- a high or a low half alone, one
    /// between other characters, a member name, a nested object's member, an array's element. Each
    /// is sent as both the header and the payload. A throw fails the test by itself.
    /// </summary>
    [Theory]
    [InlineData("{\"alg\":\"\\ud800\"}")]
    [InlineData("{\"alg\":\"\\udc00\"}")]
    [InlineData("{\"alg\":\"a\\ud800b\"}")]
    [InlineData("{\"\\ud800\":\"x\"}")]
    [InlineData("{\"jwk\":{\"kty\":\"OKP\",\"crv\":\"\\udc00\"}}")]
    [InlineData("{\"aud\":[\"\\ud800\"]}")]
    public void R11_33_ASegmentHoldingAStringThatIsNotUtf16IsMalformedNotThrown(string json)
    {
        Assert.True(CompactJws.Split(Segment(json) + "." + Segment(json) + ".AA").TryGetValue(out var parts, out var splitError), splitError?.Detail);

        var header = CompactJws.DecodeHeader(parts!);
        var payload = CompactJws.ParsePayload(parts!, root => Result<string>.Ok(CompactJws.ReadString(root, "alg")));

        Assert.False(header.TryGetValue(out _, out var headerError));
        Assert.Equal(AuthNErrors.Malformed("").Type, headerError!.Type);
        Assert.False(payload.TryGetValue(out _, out var payloadError));
        Assert.Equal(AuthNErrors.Malformed("").Type, payloadError!.Type);
    }

    /// <summary>
    /// The accepting side: an escaped surrogate pair is one code point, and it decodes. Without this
    /// fact, nothing could tell a correct check from one that refuses every escape.
    /// </summary>
    [Fact]
    public void R11_33_ASurrogatePairStillDecodes()
    {
        var json = "{\"alg\":\"\\ud83d\\ude00\",\"kid\":\"k\"}";
        Assert.True(CompactJws.Split(Segment(json) + "." + Segment(json) + ".AA").TryGetValue(out var parts, out var splitError), splitError?.Detail);

        var header = CompactJws.DecodeHeader(parts!);

        Assert.True(header.TryGetValue(out var decoded, out var error), error?.Detail);
        Assert.Equal(char.ConvertFromUtf32(0x1F600), decoded!.Alg);
        Assert.Equal("k", decoded.Kid);
    }

    private static string Segment(string json) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));
}
