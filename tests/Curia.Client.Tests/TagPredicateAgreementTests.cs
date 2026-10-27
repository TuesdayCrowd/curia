using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Curia.Canon.Json;
using Curia.Domain.Content;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R8.73 (errata G43): the predicate the write side applies to a submission's tags (<see cref="PostEnvelope.AdmissionRefusal"/>) is the one the
/// client applies to a filter (<see cref="ForumClient.UnfilterableTag"/>), written once. A tag admitted at one end and refused at the other is
/// the defect: the Forum stored a comma tag the filter could not name, and the client refused to search for it.
///
/// <para><b>Oracles.</b> Each spelling carries the verdict the test's own table gives it (empty, a comma, edge white space or U+0000 is
/// refused; everything else is not), and both ends are compared with the table and not with each other alone. The spellings are built at run
/// time, so no raw control character is in a file. FPJ43 puts U+0000 on one end only.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class TagPredicateAgreementTests
{
    private static KeyValuePair<string, JsonValue> M(string name, JsonValue value) => new(name, value);

    /// <summary>The spellings, each with whether the filter can name it: false means refused. 40 rows, asserted before use.</summary>
    private static readonly (string Tag, bool Nameable)[] Spellings =
    [
        ("x", true), ("X", true), ("a b", true), ("a-b", true), ("a&b", true), ("a+b", true), ("a%2Cb", true), ("#a", true),
        ("jcs", true), ("a:b", true), ("a/b", true), ("é", true),
        ("e" + (char)0x0301, true), // NFD: the canonicalizer repairs it; the route reads it in NFC (R9.45)
        (new string('t', 200), true),
        ("a" + (char)0x200B + "b", true), // zero width space inside; not white space to Trim
        ((char)0x200B + "a", true), // zero width space at an edge; Trim leaves it
        ("a" + (char)0xFF0C + "b", true), // fullwidth comma is not a comma
        ("a" + (char)0x201A + "b", true), // single low-9 quote is not a comma
        ("", false), (" ", false), (",", false), (",,", false),
        ("a,b", false), ("a,", false), (",a", false), ("a, b", false), ("a ,b", false),
        (" x", false), ("x ", false), (" x ", false),
        ("x" + (char)9, false), ((char)9 + "x", false), ("x" + (char)10, false), ("x" + (char)13, false),
        ("x" + (char)0xA0, false), ((char)0xA0 + "x", false), ("x" + (char)0x2003, false), ("x" + (char)0x85, false),
        ("b" + (char)0 + "c", false), (((char)0).ToString(), false),
    ];

    public static TheoryData<int> Rows()
    {
        Assert.Equal(40, Spellings.Length);
        var data = new TheoryData<int>();
        for (var i = 0; i < Spellings.Length; i++) data.Add(i);
        return data;
    }

    private static PostEnvelope QuestionWith(string tag)
    {
        var members = new List<KeyValuePair<string, JsonValue>>
        {
            M("v", new JsonValue.Number(1)),
            M("kind", new JsonValue.String("question")),
            M("author", new JsonValue.String("https://agents.example/bob")),
            M("board", new JsonValue.String("b")),
            M("content_type", new JsonValue.String(PostEnvelope.RequiredContentType)),
            M("created_at", new JsonValue.String("2026-09-04T12:00:00Z")),
            M("nonce", new JsonValue.String("n")),
            M("body", new JsonValue.String("some body")),
            M("title", new JsonValue.String("A title")),
            M("tags", new JsonValue.Array([new JsonValue.String(tag)])),
        };
        var read = PostEnvelope.Read(new JsonValue.Object([.. members]));
        Assert.True(read.TryGetValue(out var envelope, out var error), error?.Type);
        return envelope!;
    }

    /// <summary>PJ45: for each of 40 spellings the write side and the read side give the table's verdict, so neither admits what the other refuses.</summary>
    [Theory]
    [MemberData(nameof(Rows))]
    public void R8_73_TheWritePredicateAndTheReadPredicateAreOne(int row)
    {
        var (tag, nameable) = Spellings[row];

        var writeRefuses = PostEnvelope.AdmissionRefusal(QuestionWith(tag)) is not null;
        var readRefuses = ForumClient.UnfilterableTag([tag]) is not null;

        Assert.Equal(!nameable, readRefuses);
        Assert.Equal(!nameable, writeRefuses);
    }

    /// <summary>The control: an empty list of tags is refused by neither end, and a list of nameable tags by neither.</summary>
    [Fact]
    public void R8_73_NoTagsAndNameableTagsAreRefusedByNeitherEnd()
    {
        Assert.Null(ForumClient.UnfilterableTag(ImmutableArray<string>.Empty));
        Assert.Null(ForumClient.UnfilterableTag(default));
        Assert.Null(ForumClient.UnfilterableTag(["x", "a b", "é"]));
        Assert.Null(PostEnvelope.AdmissionRefusal(QuestionWith("x")));
    }
}
