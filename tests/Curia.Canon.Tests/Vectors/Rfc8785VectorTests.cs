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

        var parsed = JsonReader.Parse(input, AdmitLimits.Default)
            .Match(v => v, e => throw new Xunit.Sdk.XunitException($"parse: {e.Type}"));
        var actual = CanonicalJson.Canonicalize(parsed)
            .Match(b => b.ToArray(), e => throw new Xunit.Sdk.XunitException($"canonicalize: {e.Type}"));

        Assert.Equal(Encoding.UTF8.GetString(expected).TrimEnd('\n'), Encoding.UTF8.GetString(actual));
    }
}
