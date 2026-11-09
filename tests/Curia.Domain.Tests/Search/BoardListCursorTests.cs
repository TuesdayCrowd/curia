using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using CsCheck;
using Curia.Domain.Primitives;
using Curia.Domain.Search;
using Xunit;

namespace Curia.Domain.Tests.Search;

/// <summary>
/// R9.58 (errata G63), LOB3: <see cref="BoardListCursor"/> is <c>b&lt;bound&gt;:&lt;offset&gt;</c> in canonical numerals and base64, read strictly. <see cref="BoardListCursor.Decode"/> accepts the
/// one spelling <see cref="BoardListCursor.Encode"/> writes and refuses every other, another route's cursor included, as <c>curia/board-list/cursor-malformed</c>, echoing nothing.
///
/// <para><b>Oracle.</b> The rows are the test's own hand list of spellings a lenient decoder would read (the same shape as <c>SearchCursorDecodeTests</c>'s) and the values the property
/// builds; nothing is read from the decoder to decide what it should accept. The accepting rows come first and each refusing row has its control: the same decoder returns the cursor
/// <c>Encode</c> wrote. The type string is the errata's (<see cref="Curia.Tests.Shared.PublishedBoardListWords.Types"/>), not the constant's.
/// Predicted red on the seam: every accepting row (<c>Decode</c> refuses all, <c>Ok(b1:0)</c> expected, <c>curia/board-list/cursor-malformed</c> actual); every refusing row is a green control.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class BoardListCursorTests
{
    private static string Base64(string text) => Convert.ToBase64String(Encoding.ASCII.GetBytes(text));

    private static string Outcome(string? encoded) =>
        BoardListCursor.Decode(encoded).Match(
            c => c is null ? "Ok(null)" : string.Create(CultureInfo.InvariantCulture, $"Ok(b{c.CorpusBound}:{c.Offset})"),
            e => e.Type);

    /// <summary>Spellings that are the one the encoder writes, with the position each names.</summary>
    public static TheoryData<string, string> Accepted => new()
    {
        { Base64("b1:0"), "Ok(b1:0)" },
        { Base64("b0:0"), "Ok(b0:0)" },
        { Base64("b120:50"), "Ok(b120:50)" },
        { Base64("b9223372036854775807:2147483647"), "Ok(b9223372036854775807:2147483647)" },
    };

    [Theory]
    [MemberData(nameof(Accepted))]
    public void R9_58_LOB3_DecodeReadsTheOneSpellingEncodeWrites(string encoded, string expected)
    {
        Assert.Equal(expected, Outcome(encoded));
    }

    public static TheoryData<string> Malformed => new()
    {
        "@@@",
        "not-base64!!",
        Base64("b"),
        Base64("b1"),
        Base64("b:0"),
        Base64("b1:"),
        Base64("1:0"),
        Base64("B1:0"),
        Base64("b+1:0"),
        Base64("b-1:0"),
        Base64("b1:-1"),
        Base64("b01:0"),
        Base64("b1:00"),
        Base64("b 1:0"),
        Base64("b1: 0"),
        Base64("b1:0 "),
        Base64(" b1:0"),
        Base64("b1:0:0"),
        Base64("b1:0:0123456789abcdef"),
        Base64("b99999999999999999999:0"),
        Base64("b1:99999999999999999999"),
        Base64("c1:0:0123456789abcdef"),
        Base64("1:2"),
        "garbage",
    };

    /// <summary>LOB3's refusals, each with its control in the same row: the encoder's own cursor reads, so a decoder that refuses everything is red on the control and not green here.</summary>
    [Theory]
    [MemberData(nameof(Malformed))]
    public void R9_58_LOB3_DecodeRefusesEverySpellingEncodeDoesNotWriteAndTheControlReads(string encoded)
    {
        var type = Curia.Tests.Shared.PublishedBoardListWords.Types()[3];
        var control = Outcome(new BoardListCursor(7, 3).Encode());

        Assert.True(control == "Ok(b7:3)" && Outcome(encoded) == type, $"control: {control} (expected Ok(b7:3)); refusal of the row: {Outcome(encoded)} (expected {type})");
    }

    /// <summary>The refusal echoes nothing: neither the cursor nor the text it decodes to appears in the type, title or detail.</summary>
    [Theory]
    [MemberData(nameof(Malformed))]
    public void R9_58_LOB3_TheRefusalEchoesNothingOfTheCursor(string encoded)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        var error = BoardListCursor.Decode(encoded).Match<Error?>(_ => null, e => e);

        Assert.NotNull(error);
        var text = error.Type + error.Title + error.Detail;
        Assert.DoesNotContain(encoded, text, StringComparison.Ordinal);

        var buffer = new byte[encoded.Length];
        if (Convert.TryFromBase64String(encoded, buffer, out var written) && written >= 3)
            Assert.DoesNotContain(Encoding.ASCII.GetString(buffer, 0, written), text, StringComparison.Ordinal);
    }

    /// <summary>An absent or blank cursor is the first page (a refusal of a present blank one is the route's, R9.58): <c>Ok(null)</c>.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void R9_58_LOB3_AnAbsentCursorIsTheFirstPage_Control(string? encoded)
    {
        Assert.Equal("Ok(null)", Outcome(encoded));
    }

    /// <summary>
    /// The round trip as a property over the bounds and offsets the route can mint: zero, small, and the largest. Each class named is asserted reached (<c>NOT REACHED</c> otherwise);
    /// the sampler fills shared counters, so it runs on one thread.
    /// </summary>
    [Fact]
    public void R9_58_LOB3_EncodeThenDecodeReturnsTheSamePosition()
    {
        var reached = new Dictionary<string, int> { ["zero"] = 0, ["small"] = 0, ["large"] = 0 };
        var gen = Gen.OneOf(
            Gen.Const((0L, 0)),
            Gen.Select(Gen.Long[1, 100_000], Gen.Int[0, 1_000]),
            Gen.Select(Gen.Long[long.MaxValue - 1000, long.MaxValue], Gen.Int[int.MaxValue - 1000, int.MaxValue]));

        gen.Sample(
            pair =>
            {
                var (bound, offset) = pair;
                reached[bound == 0 ? "zero" : bound < 1_000_000 ? "small" : "large"]++;
                Assert.Equal(string.Create(CultureInfo.InvariantCulture, $"Ok(b{bound}:{offset})"), Outcome(new BoardListCursor(bound, offset).Encode()));
            },
            iter: 300,
            threads: 1);

        var missing = reached.Where(r => r.Value == 0).Select(r => r.Key).ToArray();
        Assert.True(missing.Length == 0, "NOT REACHED: " + string.Join(", ", missing));
    }
}
