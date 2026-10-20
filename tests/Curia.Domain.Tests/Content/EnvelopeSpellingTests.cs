using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Curia.Canon.Json;
using Curia.Domain.Content;
using Xunit;

namespace Curia.Domain.Tests.Content;

/// <summary>
/// Errata G25, R8.65: the envelope spells a code block's license <c>declared_license</c> and a
/// reference's subject <c>target</c>, as Table 9, R8.45 and Appendix C.3 publish them.
///
/// <para><b>Where the expectations come from.</b> Not from the reader and not from the writer: C.3 is
/// parsed out of <c>curia-agent-forum-WHITEPAPER.md</c> at test time (the way <c>PublishedTable10</c>
/// reads Table 10), and the second source is <c>conformance/envelope/ed25519-full/expected.canonical</c>,
/// a frozen vector. Editing C.3 without following it in code is a build failure; so is the reverse.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class EnvelopeSpellingTests
{
    private const string WhitePaper = "curia-agent-forum-WHITEPAPER.md";
    private const string Heading = "### C.3 Content envelope and detached signature";
    private const string ParentId = "01K2F8Q9X3B7NQ4V2H6ZTC1M8D";

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, WhitePaper)))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException($"{WhitePaper} not found above {AppContext.BaseDirectory}");
    }

    private static JsonValue.Object Parse(string json)
    {
        var parsed = JsonReader.Parse(Encoding.UTF8.GetBytes(json), AdmitLimits.Default);
        Assert.True(parsed.TryGetValue(out var tree, out var error), error?.Type);
        return (JsonValue.Object)tree!;
    }

    /// <summary>C.3's <c>envelope</c> object, read out of the first fenced block after its heading.</summary>
    private static JsonValue.Object PublishedEnvelope()
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), WhitePaper));
        var heading = text.IndexOf(Heading, StringComparison.Ordinal);
        Assert.True(heading >= 0, $"{WhitePaper} no longer contains '{Heading}'");
        var open = text.IndexOf("```json", heading, StringComparison.Ordinal);
        var start = text.IndexOf('\n', open) + 1;
        var end = text.IndexOf("```", start, StringComparison.Ordinal);
        var document = Parse(text[start..end]);
        return (JsonValue.Object)document.Members.Single(m => m.Key == "envelope").Value;
    }

    private static JsonValue.Object Frozen() =>
        Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "conformance", "envelope", "ed25519-full", "expected.canonical")));

    /// <summary>The envelope, with the members the other kinds' rules would object to set so it is an answer to a question, and nothing else touched.</summary>
    private static JsonValue.Object AsAnAnswer(JsonValue.Object source)
    {
        var members = source.Members
            .Where(m => m.Key is not ("kind" or "parent" or "prev" or "title"))
            .Append(new("kind", new JsonValue.String("answer")))
            .Append(new("parent", new JsonValue.String(ParentId)))
            .ToImmutableArray();
        return new JsonValue.Object(members);
    }

    private static PostEnvelope Admit(JsonValue.Object envelope)
    {
        Assert.True(PostEnvelope.Read(envelope).TryGetValue(out var read, out var error), error?.Type);
        return read!;
    }

    private static int ArrayLength(JsonValue.Object envelope, string name) =>
        ((JsonValue.Array)envelope.Members.Single(m => m.Key == name).Value).Items.Length;

    /// <summary>P1: every reference C.3 publishes is read, with the target C.3 gives it.</summary>
    [Fact]
    public void R8_65_EveryReferenceAppendixC3PublishesIsRead()
    {
        var published = PublishedEnvelope();
        var read = Admit(AsAnAnswer(published));

        Assert.Equal(ArrayLength(published, "refs"), read.Refs.Length);
        Assert.Equal(["sha256:9f2c...", "pkg:nuget/CsCheck"], read.Refs.Select(r => r.Target));
    }

    /// <summary>P2: the license C.3 publishes is read.</summary>
    [Fact]
    public void R8_65_TheDeclaredLicenseAppendixC3PublishesIsRead()
    {
        var read = Admit(AsAnAnswer(PublishedEnvelope()));

        Assert.Equal("UNLICENSE", Assert.Single(read.CodeBlocks).DeclaredLicense);
    }

    /// <summary>P3: the same two facts over the frozen conformance vector (its parent set to null, its kind a finding).</summary>
    [Fact]
    public void R8_65_TheFrozenVectorsReferencesAndLicenseAreRead()
    {
        var frozen = Frozen();
        var members = frozen.Members
            .Where(m => m.Key is not ("parent" or "prev"))
            .Append(new("parent", new JsonValue.Null()))
            .Append(new("prev", new JsonValue.Null()))
            .ToImmutableArray();
        var read = Admit(new JsonValue.Object(members));

        Assert.Equal(ArrayLength(frozen, "refs"), read.Refs.Length);
        Assert.Equal(["post", "package", "url"], read.Refs.Select(r => r.Kind));
        Assert.Equal("UNLICENSE", Assert.Single(read.CodeBlocks).DeclaredLicense);
    }

    /// <summary>
    /// P5: the case's published canonical form is admitted with its one reference. Green first (the reader and the
    /// fixture shared the wrong spelling); it goes red when the reader changes, until the fixture is regenerated.
    /// </summary>
    [Fact]
    public void R8_65_TheVerificationContradictedFixtureIsAdmittedWithItsReference()
    {
        var canonical = File.ReadAllText(Path.Combine(RepositoryRoot(), "conformance", "envelope", "verification-contradicted", "expected.canonical"));
        var read = Admit(Parse(canonical));

        var reference = Assert.Single(read.Refs);
        Assert.Equal("url", reference.Kind);
        Assert.StartsWith("https://", reference.Target, StringComparison.Ordinal);
    }
}
