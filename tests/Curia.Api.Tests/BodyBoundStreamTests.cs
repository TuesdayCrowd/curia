using System.Diagnostics.CodeAnalysis;
using CsCheck;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// BND10 (errata G84, R6.68 (rev.)): the counting stream the middleware lays over a request body, driven without a host. For any bound, any total length and any sequence of read sizes the stream
/// delivers at most the bound and throws the host's own 413 exactly when the total passes it, on the read that would have delivered the byte after the bound; a body at the bound is delivered whole.
/// BND3's figure is held beside it: the code's bound equals the white paper's R6.39 number plus one.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class BodyBoundStreamTests
{
    private static (long Delivered, int? ThrownStatus) Drain(Stream stream, int[] readSizes)
    {
        long delivered = 0;
        var at = 0;
        while (true)
        {
            var buffer = new byte[readSizes[at++ % readSizes.Length]];
            int read;
            try
            {
                read = stream.ReadAsync(buffer.AsMemory()).AsTask().GetAwaiter().GetResult();
            }
            catch (BadHttpRequestException e)
            {
                return (delivered, e.StatusCode);
            }

            if (read == 0) return (delivered, null);
            delivered += read;
        }
    }

    [Fact]
    public void R6_68_BND10_TheStreamDeliversAtMostTheBoundAndThrowsExactlyWhenTheTotalPassesIt()
    {
        var gen = Gen.Select(Gen.Int[0, 48], Gen.Int[0, 140], Gen.Int[1, 23].List[1, 30]);
        gen.Sample(
            (cap, total, reads) =>
            {
                var stream = new BodyBoundStream(new MemoryStream(new byte[total]), cap);
                var (delivered, thrown) = Drain(stream, [.. reads]);
                if (total <= cap)
                {
                    Assert.Null(thrown);
                    Assert.Equal(total, delivered);
                }
                else
                {
                    Assert.Equal(StatusCodes.Status413PayloadTooLarge, thrown);
                    Assert.Equal(cap, delivered);
                }
            },
            iter: 500);
    }

    [Fact]
    public void R6_68_BND10_TheBoundIsTheWhitePapersSubmissionSizePlusOne() =>
        Assert.Equal(Curia.Tests.Shared.PublishedRequestBound.Cap(), RequestBodyBound.Cap);
}
