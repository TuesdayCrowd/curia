using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Curia.Domain.Search;
using Xunit;

namespace Curia.Domain.Tests.Search;

/// <summary>
/// R9.38 (errata G37): the inbox's cursor is read strictly. <see cref="SearchCursor.Decode"/> accepts the one spelling
/// <see cref="SearchCursor.Encode"/> writes, and refuses every other as <c>curia/inbox/cursor-malformed</c>, echoing nothing.
///
/// <para><b>Oracle.</b> The rows are the test's own hand list of spellings the old decoder (<c>int.TryParse</c>,
/// <c>long.TryParse</c>) read as positions or as a first page; the round trips are values the test builds. Nothing is read
/// from the decoder to decide what it should accept. A negative row has its control: the same decoder returns the cursor
/// <see cref="SearchCursor.Encode"/> wrote.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class SearchCursorDecodeTests
{
    private const string MalformedType = "curia/inbox/cursor-malformed";

    private static string Base64(string text) => Convert.ToBase64String(Encoding.ASCII.GetBytes(text));

    /// <summary>The cursor <paramref name="encoded"/> decodes to, or the problem type it is refused with, as one string a failing assertion prints whole.</summary>
    private static string Outcome(string? encoded) =>
        SearchCursor.Decode(encoded).Match(
            c => c is null ? "Ok(null)" : string.Create(CultureInfo.InvariantCulture, $"Ok({c.AfterScore}:{c.AfterSequence})"),
            e => e.Type);

    public static TheoryData<string> Malformed => new()
    {
        "@@@",
        "not-base64!!",
        Base64("1"),
        Base64("a:b"),
        Base64("1:2:3"),
        Base64("+1:2"),
        Base64(" 1:2"),
        Base64("01:2"),
        Base64("-1:2"),
        Base64("1:-2"),
        Base64("1: 2"),
        Base64("1:2 "),
        Base64("99999999999:1"),
        Base64("1:99999999999999999999"),
        Base64(":2"),
        Base64("1:"),
        Base64("c6:1:0123456789abcdef"),
        "garbage",
        "bm90LWEtY3Vyc29y",
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public void R9_38_DecodeRefusesEverySpellingEncodeDoesNotWrite(string encoded)
    {
        Assert.Equal(MalformedType, Outcome(encoded));
    }

    [Theory]
    [MemberData(nameof(Malformed))]
    public void R9_38_TheRefusalEchoesNothingOfTheCursor(string encoded)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        var error = SearchCursor.Decode(encoded).Match<Curia.Domain.Primitives.Error?>(_ => null, e => e);

        Assert.NotNull(error);
        var text = error.Type + error.Title + error.Detail;
        Assert.DoesNotContain(encoded, text, StringComparison.Ordinal);

        // The text the cursor decodes to, where it is base64 at all and long enough to be told from the refusal's own words: a refusal that quotes it back is an echo too.
        var buffer = new byte[encoded.Length];
        if (Convert.TryFromBase64String(encoded, buffer, out var written) && written >= 3)
        {
            var decoded = Encoding.ASCII.GetString(buffer, 0, written);
            Assert.DoesNotContain(decoded, text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(0, 0L)]
    [InlineData(0, 1L)]
    [InlineData(5, 17L)]
    [InlineData(int.MaxValue, long.MaxValue)]
    public void R9_38_ACursorEncodeWroteRoundTrips(int score, long sequence)
    {
        var cursor = new SearchCursor(score, sequence);

        Assert.Equal(string.Create(CultureInfo.InvariantCulture, $"Ok({score}:{sequence})"), Outcome(cursor.Encode()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void R9_38_AnAbsentEmptyOrBlankCursorIsAFirstPage(string? encoded)
    {
        Assert.Equal("Ok(null)", Outcome(encoded));
    }
}
