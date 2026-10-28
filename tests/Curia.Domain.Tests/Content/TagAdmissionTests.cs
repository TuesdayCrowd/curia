using System.Diagnostics.CodeAnalysis;
using Curia.Canon.Json;
using Curia.Domain.Content;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Domain.Tests.Content;

/// <summary>
/// R8.73 (errata G43): a submission whose <c>tags</c> holds a tag the <c>tags</c> filter cannot name (empty, a comma, edge white space
/// or U+0000) is refused <c>curia/content/tag-unfilterable</c>, naming the zero-based index of every such tag and never a value. The
/// rule is <see cref="PostEnvelope.AdmissionRefusal"/>'s, the function that applies R8.64's rules to a submission, and not
/// <see cref="PostEnvelope.Read"/>'s: <see cref="PostEnvelope.Read"/> is applied to every stored post and a post that fails it is
/// dropped on replay (R11.31).
///
/// <para><b>Oracles.</b> The refusing type is parsed from the errata. The tags are built at run time, so no file carries a raw control
/// character. Every refusal has its control, a list of tags the filter can name, which must stay admitted.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class TagAdmissionTests
{
    private static readonly string Digest = "sha256:" + new string('b', 64);

    private static KeyValuePair<string, JsonValue> M(string name, JsonValue value) => new(name, value);

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
        if (PostKinds.RequiresPrev(kind)) members.Add(M("prev", new JsonValue.String(Digest)));
        return members;
    }

    private static PostEnvelope ReadOk(PostKind kind, params string[] tags)
    {
        var members = Valid(kind);
        members.Add(M("tags", new JsonValue.Array([.. tags.Select(t => (JsonValue)new JsonValue.String(t))])));
        var read = PostEnvelope.Read(new JsonValue.Object([.. members]));
        Assert.True(read.TryGetValue(out var envelope, out var error), error?.Type);
        return envelope!;
    }

    /// <summary>The five kinds that carry tags and are submitted through <c>AdmissionRefusal</c>.</summary>
    private static readonly PostKind[] Kinds = [PostKind.Question, PostKind.Answer, PostKind.Comment, PostKind.Finding, PostKind.Revision];

    /// <summary>Each case is a list of tags and the detail it must be refused with. Built at run time so no raw control character is in a file.</summary>
    private static readonly (string Name, string[] Tags, string Detail)[] Refused =
    [
        ("comma", ["a", "b,c"], "tags[1]"),
        ("empty", ["a", ""], "tags[1]"),
        ("leading-space", ["a", " x"], "tags[1]"),
        ("trailing-space", ["a", "x "], "tags[1]"),
        ("trailing-tab", ["a", "x" + (char)9], "tags[1]"),
        ("nul", ["a", "b" + (char)0 + "c"], "tags[1]"),
    ];

    public static TheoryData<PostKind, string> KindsAndCases()
    {
        var data = new TheoryData<PostKind, string>();
        foreach (var kind in Kinds)
            foreach (var row in Refused)
                data.Add(kind, row.Name);
        return data;
    }

    // ---- PJ21 -----------------------------------------------------------------------------------

    /// <summary>PJ21: every class the filter cannot name is refused with the type R8.73 names and the index of the tag, on every kind.</summary>
    [Theory]
    [MemberData(nameof(KindsAndCases))]
    public void R8_73_AnUnfilterableTagIsRefusedWithItsIndex(PostKind kind, string name)
    {
        var row = Refused.Single(r => r.Name == name);
        var envelope = ReadOk(kind, row.Tags);

        var refusal = PostEnvelope.AdmissionRefusal(envelope);

        Assert.NotNull(refusal);
        Assert.Equal(PublishedSmallWords.TagUnfilterableType(), refusal.Type);
        Assert.Equal(row.Detail, refusal.Detail);
    }

    /// <summary>PJ21's control: tags the filter can name are admitted on every kind (none of the five refuses an empty list or a plain one).</summary>
    [Theory]
    [InlineData(PostKind.Question)]
    [InlineData(PostKind.Answer)]
    [InlineData(PostKind.Comment)]
    [InlineData(PostKind.Finding)]
    [InlineData(PostKind.Revision)]
    public void R8_73_TagsTheFilterCanNameAreAdmitted(PostKind kind)
    {
        Assert.Null(PostEnvelope.AdmissionRefusal(ReadOk(kind, "a", "b c", "d-e")));
        Assert.Null(PostEnvelope.AdmissionRefusal(ReadOk(kind)));
    }

    // ---- PJ22 -----------------------------------------------------------------------------------

    /// <summary>PJ22: every tag that cannot be named is named by index, in order; the control returns null.</summary>
    [Fact]
    public void R8_73_EveryIndexIsNamedInOrder()
    {
        var control = ReadOk(PostKind.Question, "a", "b c", "d-e", "a&b", "a+b", "a%2Cb", "#a");
        Assert.Null(PostEnvelope.AdmissionRefusal(control));
        Assert.Null(PostEnvelope.AdmissionRefusal(ReadOk(PostKind.Question)));

        var commas = PostEnvelope.AdmissionRefusal(ReadOk(PostKind.Question, "a,b", "c", "d,e"));
        Assert.NotNull(commas);
        Assert.Equal("tags[0], tags[2]", commas.Detail);

        var mixed = PostEnvelope.AdmissionRefusal(ReadOk(PostKind.Question, "", "c", " d"));
        Assert.NotNull(mixed);
        Assert.Equal("tags[0], tags[2]", mixed.Detail);
    }

    // ---- PJ23 -----------------------------------------------------------------------------------

    /// <summary>PJ23: the refusal holds no value: not the tag's text, not its comma, not its white space (R6.40).</summary>
    [Theory]
    [InlineData("secret,token-value", "secret")]
    [InlineData("secret,token-value", "token-value")]
    [InlineData("  padded-secret  ", "padded-secret")]
    public void R8_73_NoValueIsInTheRefusal(string tag, string fragment)
    {
        var refusal = PostEnvelope.AdmissionRefusal(ReadOk(PostKind.Question, tag));

        Assert.NotNull(refusal);
        var everything = refusal.Type + "\n" + refusal.Title + "\n" + refusal.Detail;
        Assert.DoesNotContain(fragment, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(tag, everything, StringComparison.Ordinal);
    }

    // ---- PJ24 (Read half) -----------------------------------------------------------------------

    /// <summary>
    /// PJ24: <see cref="PostEnvelope.Read"/> still admits a comma-tagged envelope, because it is applied to every stored post
    /// (R11.31). Green first; held by FPJ17, which puts the rule in <c>Read</c> and so fails <see cref="ReadOk"/>. The replay half is
    /// <c>StoredCommaTagReplayTests</c> in the Application tests.
    /// </summary>
    [Fact]
    public void R8_73_ReadStillAcceptsAnUnfilterableTag()
    {
        foreach (var row in Refused)
        {
            var envelope = ReadOk(PostKind.Question, row.Tags);
            Assert.Equal(row.Tags.Length, envelope.Tags.Length);
        }
    }

    /// <summary>
    /// The seam's own fact: the helper reads the eight entries and every value it returns is the count the entry names (FPJ40 drops
    /// an anchor and this goes red by name). Green first.
    /// </summary>
    [Fact]
    public void R8_73_TheErrataParsesToTheValuesTheProbesUse()
    {
        PublishedSmallWords.AssertAnchorsPresent();
        Assert.Equal("curia/content/tag-unfilterable", PublishedSmallWords.TagUnfilterableType());
        Assert.Equal(400, PublishedSmallWords.TagUnfilterableStatus());
        Assert.Equal("curia/inbox/invalid-limit", PublishedSmallWords.InboxLimitType());
        Assert.Equal("curia/search/invalid-limit", PublishedSmallWords.SearchLimitType());
        Assert.Equal(7, PublishedSmallWords.TagPhrases().Length);
        Assert.Equal("an act open only to the owner of the resource", PublishedSmallWords.ExitFourPhrase());
        Assert.Equal("--cursor", PublishedSmallWords.BlankCursorFlag());
    }
}
