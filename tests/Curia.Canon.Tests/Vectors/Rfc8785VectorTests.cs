using System.Text;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Xunit;

namespace Curia.Canon.Tests.Vectors;

public sealed class Rfc8785VectorTests
{
    [Theory]
    [InlineData("arrays")]
    [InlineData("french")]
    [InlineData("structures")]
    [InlineData("unicode")]
    [InlineData("values")]
    [InlineData("weird")]
    public void OfficialVectorFromTheRfcAuthor(string name)
    {
        var root = Path.Combine(VectorLoader.ConformanceRoot, "rfc8785");
        var input = File.ReadAllBytes(Path.Combine(root, $"input-{name}.json"));
        var expected = File.ReadAllBytes(Path.Combine(root, $"output-{name}.json"));

        // R6.41: pure RFC 8785 conformance must hold for documents ADMIT's own caps would
        // refuse (input-values.json's 4.5, 0.002, 1e-27, 1e+30 among them) -- this is the
        // pure-canonicalization side of the split, so it parses via ParseUnrestricted, never
        // the ADMIT-gated Parse. See JsonReader.ParseUnrestricted's remarks and errata E4.
        var parsed = JsonReader.ParseUnrestricted(input)
            .Match(v => v, e => throw new Xunit.Sdk.XunitException($"parse: {e.Type}"));
        var actual = CanonicalJson.Canonicalize(parsed)
            .Match(b => b.ToArray(), e => throw new Xunit.Sdk.XunitException($"canonicalize: {e.Type}"));

        Assert.Equal(Encoding.UTF8.GetString(expected).TrimEnd('\n'), Encoding.UTF8.GetString(actual));
    }
}
