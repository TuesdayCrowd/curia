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

    /// <summary>
    /// R8.67 (errata G29): a URL parses as an absolute <c>http</c> or <c>https</c> URI and holds no whitespace or control
    /// character. Each row carries whether the published sentence admits it; <c>System.Uri</c> (RFC 3986's absolute-URI
    /// rule, a different call path from <c>ReferenceForms</c>) is asked too, and the row must agree with it.
    /// </summary>
    public static TheoryData<string, bool> UrlRows()
    {
        var bell = ((char)7).ToString();
        return new TheoryData<string, bool>
        {
            { "https://a b", false },
            { "http://x\ty", false },
            { "https://x" + bell, false },
            { "http://", false },
            { "https:///", false },
            { "http:relative", false },
            { "https://a.example/x?y=1#z", true },
        };
    }

    [Theory]
    [MemberData(nameof(UrlRows))]
    public void R8_67_AUrlIsAnAbsoluteHttpUriWithoutWhitespaceOrControls(string given, bool admitted)
    {
        ArgumentNullException.ThrowIfNull(given);
        var oracle = Uri.TryCreate(given, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && given.StartsWith(uri.Scheme + "://", StringComparison.Ordinal)
            && !given.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));
        Assert.Equal(admitted, oracle);

        var parsed = ReferenceForms.Parse(given);
        Assert.True(admitted == parsed.TryGetValue(out var reference, out var error), $"'{given}' was {(admitted ? "refused" : "classified as a url")}");
        if (admitted) Assert.Equal("url", reference!.Kind);
        else Assert.Contains("https://", error!.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// R11.48 (errata G34): <c>ParseAll</c> walks the whole list. All valid: every reference, in order. Otherwise the error's detail
    /// begins with the zero-based index of every item refused, in order, and echoes no value. The oracle is the list this fact built.
    /// </summary>
    [Fact]
    public void R11_48_ParseAllNamesTheIndicesAndReturnsEveryReferenceInOrderOtherwise()
    {
        string[] valid = [Digest, "pkg:nuget/CsCheck@4.8.0", "https://example.test/a"];
        Assert.True(ReferenceForms.ParseAll(valid).TryGetValue(out var all, out var allError), allError?.Detail);
        Assert.Equal(3, all.Length);
        Assert.Equal(valid, all.Select(r => r.Target));
        Assert.Equal(["post", "package", "url"], all.Select(r => r.Kind));

        Assert.True(ReferenceForms.ParseAll([]).TryGetValue(out var none, out var noneError), noneError?.Detail);
        Assert.Empty(none);

        Assert.False(ReferenceForms.ParseAll([Digest, "ZQ7BAD", Digest, "ZQ8BAD"]).TryGetValue(out _, out var error));
        Assert.StartsWith("refs[1], refs[3]", error!.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("ZQ7BAD", error.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("ZQ8BAD", error.Detail, StringComparison.Ordinal);
        Assert.Contains("sha256:", error.Detail, StringComparison.Ordinal);
    }
}
