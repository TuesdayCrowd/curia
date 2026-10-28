using System.Diagnostics.CodeAnalysis;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain.Content;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R8.74 (errata G43): a reference classified as a URL is read without regard to the case of its scheme, as RFC 3986 reads it, and is kept
/// as the author typed it; R8.67's other conditions stand. <c>HTTPS://example.org/a</c> was refused by both reference doors with "A reference
/// is a post digest, a package coordinate or a URL" while the Forum, which has no URL check, would have stored it.
///
/// <para><b>Oracles.</b> The signed member is read back from the canonical form by <see cref="JsonReader"/> (a reader other than the
/// classifier), so a classifier that folded the target would fail; the refusals are the rows R8.67 already refuses, in the upper case too.
/// Control characters are built at run time.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ReferenceFormsCaseTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("curia-refcase-tests-").FullName;
    private readonly EnrolledAgent _agent;

    private static readonly DateTimeOffset When = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    public ReferenceFormsCaseTests()
    {
        Assert.True(new ProfileStore(_root)
            .Create("alice", "https://agents.example/alice", "alice-1", new Uri("http://localhost:5199"))
            .TryGetValue(out var agent, out _));
        _agent = agent!;
    }

    public void Dispose()
    {
        _agent.Dispose();
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    // ---- PJ15 -----------------------------------------------------------------------------------

    /// <summary>PJ15: any case of <c>http</c> or <c>https</c> is a URL, and the target is the input's bytes.</summary>
    [Theory]
    [InlineData("HTTPS://example.org/a")]
    [InlineData("Http://a.b")]
    [InlineData("hTTp://a.b/c?d=e#f")]
    public void R8_74_AnyCaseOfHttpOrHttpsIsAUrl(string given)
    {
        var parsed = ReferenceForms.Parse(given);

        Assert.True(parsed.TryGetValue(out var reference, out var error), error?.Detail);
        Assert.Equal("url", reference!.Kind);
        Assert.Equal(given, reference.Target);
        Assert.Null(reference.Version);
    }

    // ---- PJ16 -----------------------------------------------------------------------------------

    /// <summary>PJ16, the control: what is not a URL stays refused in the upper case too. Green first; held by FPJ13 and FPJ14.</summary>
    public static TheoryData<string> NotUrls() => new()
    {
        "ftp://x",
        "FTP://x",
        "HTTPS:/x",
        "https//x",
        "HTTPS://",
        "HTTPS://a b",
        "HTTPS://a" + (char)9 + "b",
        "javascript:alert(1)",
        "JAVASCRIPT:alert(1)",
        "HTTP ://x",
        "HTTPS://a" + (char)7,
    };

    [Theory]
    [MemberData(nameof(NotUrls))]
    public void R8_74_WhatIsNotAUrlStaysRefused(string given)
    {
        ArgumentNullException.ThrowIfNull(given);

        Assert.False(ReferenceForms.Parse(given).TryGetValue(out _, out _));
    }

    // ---- PJ17 -----------------------------------------------------------------------------------

    /// <summary>PJ17: the signed member is the author's bytes: <c>kind</c> is <c>url</c> and <c>target</c> is not folded.</summary>
    [Fact]
    public void R8_74_TheSignedMemberIsTheAuthorsBytes()
    {
        const string Typed = "HTTPS://example.org/a";
        var parsed = ReferenceForms.Parse(Typed);
        Assert.True(parsed.TryGetValue(out var reference, out var parseError), parseError?.Detail);

        var draft = new PostDraft
        {
            Kind = PostKind.Comment,
            Board = "b",
            Body = "a comment",
            Parent = "01J00000000000000000000000",
            Refs = [reference!],
        };
        Assert.True(SubmissionBuilder.Build(_agent, draft, When).TryGetValue(out var signed, out var error), error?.Title);

        Assert.True(JsonReader.Parse(signed!.Canonical.Span, AdmitLimits.Default).TryGetValue(out var tree, out _));
        var refs = Assert.IsType<JsonValue.Array>(((JsonValue.Object)tree!).Members.Single(m => m.Key == "refs").Value);
        var first = Assert.IsType<JsonValue.Object>(Assert.Single(refs.Items));
        Assert.Equal("url", ((JsonValue.String)first.Members.Single(m => m.Key == "kind").Value).Value);
        Assert.Equal(Typed, ((JsonValue.String)first.Members.Single(m => m.Key == "target").Value).Value);
    }
}
