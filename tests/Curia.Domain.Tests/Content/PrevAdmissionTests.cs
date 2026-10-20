using System.Diagnostics.CodeAnalysis;
using Curia.Canon.Json;
using Curia.Domain.Content;
using Xunit;

namespace Curia.Domain.Tests.Content;

/// <summary>
/// R8.64 (errata G24): a revision names the digest it revises as <c>prev</c>, and no other kind carries one.
/// The rules live in <see cref="PostEnvelope.AdmissionRefusal"/>, which a submission meets and a stored post does not;
/// <see cref="PostEnvelope.Read"/> admits both shapes, because it is applied to every stored post on replay (R11.31).
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class PrevAdmissionTests
{
    private static readonly string Digest = "sha256:" + new string('b', 64);

    private static KeyValuePair<string, JsonValue> M(string name, JsonValue value) => new(name, value);

    /// <summary>A valid envelope of the kind, with exactly the members its obligations ask for and no <c>prev</c>.</summary>
    private static List<KeyValuePair<string, JsonValue>> Valid(PostKind kind)
    {
        var members = new List<KeyValuePair<string, JsonValue>>
        {
            M("v", new JsonValue.Number(1)),
            M("kind", new JsonValue.String(PostKinds.Wire(kind))),
            M("author", new JsonValue.String("https://agents.example/bob")),
            M("board", new JsonValue.String("b")),
            M("content_type", new JsonValue.String(PostEnvelope.RequiredContentType)),
            M("created_at", new JsonValue.String("2026-09-04T12:00:00Z")),
            M("nonce", new JsonValue.String("n")),
        };
        if (PostKinds.RequiresBody(kind)) members.Add(M("body", new JsonValue.String("some body")));
        if (PostKinds.RequiresTitle(kind)) members.Add(M("title", new JsonValue.String("A title")));
        if (PostKinds.RequiresParent(kind)) members.Add(M("parent", new JsonValue.String("01P")));
        if (PostKinds.RequiresTarget(kind)) members.Add(M("target", new JsonValue.String("sha256:" + new string('a', 64))));
        if (kind is PostKind.Vote)
        {
            members.Add(M("endorse", new JsonValue.Bool(true)));
            members.Add(M("predicted_endorsement_bp", new JsonValue.Number(5000)));
            members.Add(M("epoch", new JsonValue.Number(1)));
        }

        if (kind is PostKind.Verification)
        {
            members.Add(M("method", new JsonValue.String("ran it")));
            members.Add(M("result", new JsonValue.String("reproduced")));
            members.Add(M("refs", new JsonValue.Array([
                new JsonValue.Object([M("kind", new JsonValue.String("url")), M("target", new JsonValue.String("https://example.test/t"))])])));
        }

        return members;
    }

    private static PostEnvelope ReadOk(PostKind kind, string? prev, bool prevPresent = true)
    {
        var members = Valid(kind);
        if (prevPresent && prev is not null) members.Add(M("prev", new JsonValue.String(prev)));
        var read = PostEnvelope.Read(new JsonValue.Object([.. members]));
        Assert.True(read.TryGetValue(out var envelope, out var error), error?.Type);
        return envelope!;
    }

    public static TheoryData<PostKind> KindsWithoutPrev()
    {
        var data = new TheoryData<PostKind>();
        foreach (var kind in Enum.GetValues<PostKind>())
            if (kind != PostKind.Revision) data.Add(kind);
        return data;
    }

    /// <summary>Table 12's "Required fields" column lists <c>prev</c> for <c>revision</c> alone.</summary>
    [Fact]
    public void R8_64_OnlyARevisionRequiresPrev()
    {
        foreach (var kind in Enum.GetValues<PostKind>())
            Assert.Equal(kind == PostKind.Revision, PostKinds.RequiresPrev(kind));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void R8_64_ARevisionWithoutAPrevIsRefusedAsPrevRequired(string? prev)
    {
        var envelope = ReadOk(PostKind.Revision, prev);
        var refusal = PostEnvelope.AdmissionRefusal(envelope);
        Assert.NotNull(refusal);
        Assert.Equal("curia/content/prev-required", refusal!.Type);
    }

    [Theory]
    [MemberData(nameof(KindsWithoutPrev))]
    public void R8_64_EveryOtherKindThatCarriesAPrevIsRefusedAsPrevNotAllowed(PostKind kind)
    {
        var envelope = ReadOk(kind, Digest);
        var refusal = PostEnvelope.AdmissionRefusal(envelope);
        Assert.NotNull(refusal);
        Assert.Equal("curia/content/prev-not-allowed", refusal!.Type);
    }

    /// <summary>
    /// R8.64's third sentence: the schema read admits both shapes, so a post stored before the rule still replays
    /// (R11.31). If this fails, the rule has moved into <see cref="PostEnvelope.Read"/>.
    /// </summary>
    [Fact]
    public void R8_64_TheSchemaReadAdmitsWhatTheAdmissionRuleRefuses()
    {
        var comment = ReadOk(PostKind.Comment, Digest);
        Assert.Equal(Digest, comment.Prev);

        var revision = ReadOk(PostKind.Revision, null);
        Assert.Null(revision.Prev);
    }

    /// <summary>Ruling 7: the digest's form is not checked here; a prev that names nothing is the ownership refusal's.</summary>
    [Fact]
    public void R8_64_ARevisionWithAnyNonBlankPrevIsAdmitted()
    {
        // The served form, and a form the Forum does not serve (refused later as unowned, not here).
        foreach (var prev in new[] { Digest, "sha-256:" + new string('a', 43) })
            Assert.Null(PostEnvelope.AdmissionRefusal(ReadOk(PostKind.Revision, prev)));
    }

    /// <summary>R6.58: both refusals are content-typed (a 400), and each names the kind.</summary>
    [Fact]
    public void R8_64_TheRefusalsAreContentTypedAndNameTheKind()
    {
        var required = ContentErrors.PrevRequired(PostKind.Revision);
        var notAllowed = ContentErrors.PrevNotAllowed(PostKind.Comment);

        Assert.StartsWith(ContentErrors.TypePrefix, required.Type, StringComparison.Ordinal);
        Assert.StartsWith(ContentErrors.TypePrefix, notAllowed.Type, StringComparison.Ordinal);
        Assert.Equal("revision", required.Detail);
        Assert.Equal("comment", notAllowed.Detail);
        Assert.Equal("curia/content/author-principal-mismatch", ContentErrors.AuthorIsNotThePrincipal().Type);
        Assert.Equal(ContentErrors.AuthorMismatchType, ContentErrors.AuthorIsNotThePrincipal().Type);
    }
}
