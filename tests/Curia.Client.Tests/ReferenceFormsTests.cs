using System.Diagnostics.CodeAnalysis;
using Curia.Client;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// Errata G25, R8.65, decision 3: the one classifier the reference client uses for a reference named on a
/// command line, <c>answer</c>, <c>comment</c>, <c>reproduce</c> and <c>contradict</c> alike.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ReferenceFormsTests
{
    private static readonly string Digest = "sha256:" + new string('a', 64);

    [Fact]
    public void R8_65_ADigestIsAPostReference()
    {
        Assert.True(ReferenceForms.Parse(Digest).TryGetValue(out var reference, out var error), error?.Detail);
        Assert.Equal("post", reference!.Kind);
        Assert.Equal(Digest, reference.Target);
        Assert.Null(reference.Version);
    }

    [Fact]
    public void R8_65_APackageCoordinateIsAPackageReferenceWithNoVersion()
    {
        Assert.True(ReferenceForms.Parse("pkg:nuget/CsCheck@4.8.0").TryGetValue(out var reference, out var error), error?.Detail);
        Assert.Equal("package", reference!.Kind);
        Assert.Equal("pkg:nuget/CsCheck@4.8.0", reference.Target);
        Assert.Null(reference.Version);
    }

    [Theory]
    [InlineData("https://x")]
    [InlineData("http://x")]
    [InlineData("https://example.test/a?b=c")]
    public void R8_65_AnHttpAddressIsAUrlReference(string address)
    {
        Assert.True(ReferenceForms.Parse(address).TryGetValue(out var reference, out var error), error?.Detail);
        Assert.Equal("url", reference!.Kind);
        Assert.Equal(address, reference.Target);
    }

    [Theory]
    [InlineData("ftp://x")]
    [InlineData("file:///etc/passwd")]
    [InlineData("sha256:ABC")]
    [InlineData("sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("pkg:")]
    [InlineData("https://")]
    [InlineData("http://")]
    [InlineData("word")]
    [InlineData("")]
    public void R8_65_AnythingElseIsRefusedNamingTheThreeForms(string given)
    {
        Assert.False(ReferenceForms.Parse(given).TryGetValue(out _, out var error));
        Assert.Contains("sha256:", error!.Detail, StringComparison.Ordinal);
        Assert.Contains("pkg:", error.Detail, StringComparison.Ordinal);
        Assert.Contains("https://", error.Detail, StringComparison.Ordinal);
    }
}
