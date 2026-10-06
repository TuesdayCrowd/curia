using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using CsCheck;
using Xunit;

namespace Curia.Canon.Tests.Json;

/// <summary>
/// R10.67 (errata G17): how a reader writes a span it did not compose. Each row's expected bytes are
/// spelled here from the code point, independently of <see cref="SpanText"/>: a backslash, <c>u</c>
/// and four lowercase hexadecimal digits per UTF-16 code unit for every character of general category
/// Cc, Cf, Zl or Zp and every surrogate without its pair, and every other character as it is.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class SpanTextTests
{
    [Theory]
    [InlineData(0x0000, "0000")]
    [InlineData(0x0007, "0007")]
    [InlineData(0x000B, "000b")]
    [InlineData(0x000C, "000c")]
    [InlineData(0x000D, "000d")]
    [InlineData(0x001B, "001b")]
    [InlineData(0x007F, "007f")]
    [InlineData(0x0085, "0085")]
    [InlineData(0x009B, "009b")]
    [InlineData(0x009C, "009c")]
    [InlineData(0x009D, "009d")]
    [InlineData(0x00AD, "00ad")]
    [InlineData(0x061C, "061c")]
    [InlineData(0x200B, "200b")]
    [InlineData(0x200D, "200d")]
    [InlineData(0x200E, "200e")]
    [InlineData(0x202E, "202e")]
    [InlineData(0x2066, "2066")]
    [InlineData(0x2028, "2028")]
    [InlineData(0x2029, "2029")]
    [InlineData(0xFEFF, "feff")]
    [InlineData(0xE0041, "db40,dc41")]
    public void R10_67_EachCharacterOfTheSetIsWrittenAsItsEscape(int codePoint, string units)
    {
        ArgumentNullException.ThrowIfNull(units);
        var text = "a" + char.ConvertFromUtf32(codePoint) + "b";
        var expected = "a" + string.Concat(units.Split(',').Select(u => "\\u" + u)) + "b";

        Assert.Equal(expected, SpanText.Block(text));
        Assert.Equal(expected, SpanText.Line(text));
    }

    [Theory]
    [InlineData(0x000A)]
    [InlineData(0x0009)]
    [InlineData(0x0020)]
    [InlineData(0x0022)]
    [InlineData(0x005C)]
    [InlineData(0x00E9)]
    [InlineData(0x0430)]
    [InlineData(0x0301)]
    [InlineData(0xFE0F)]
    [InlineData(0x3164)]
    [InlineData(0x2065)]
    [InlineData(0xE000)]
    [InlineData(0x1F600)]
    public void R10_67_EveryOtherCharacterIsWrittenAsItIs(int codePoint)
    {
        var text = "a" + char.ConvertFromUtf32(codePoint) + "b";

        Assert.Equal(text, SpanText.Block(text));
        if (codePoint is 0x000A or 0x0009)
            Assert.Equal("a" + "\\u" + codePoint.ToString("x4", CultureInfo.InvariantCulture) + "b", SpanText.Line(text));
        else
            Assert.Equal(text, SpanText.Line(text));
    }

    [Fact]
    public void R10_67_LineEscapesTheLineFeedAndTheTabAndBlockKeepsThem()
    {
        const string text = "a\nb\tc";

        Assert.Equal("a" + "\\u" + "000a" + "b" + "\\u" + "0009" + "c", SpanText.Line(text));
        Assert.Equal(text, SpanText.Block(text));
    }

    [Fact]
    public void R10_67_ASurrogateWithoutItsPairIsWrittenAsItsEscape()
    {
        Assert.Equal("a\\u" + "d800b", SpanText.Block("a" + (char)0xD800 + "b"));
        Assert.Equal("a\\u" + "dc41b", SpanText.Block("a" + (char)0xDC41 + "b"));
        Assert.Equal("a\\u" + "d800", SpanText.Block("a" + (char)0xD800));
    }

    /// <summary>
    /// Over generated text holding no backslash: each output reads back as its input through a decoder
    /// written here, holds no character of the set (for <see cref="SpanText.Block"/>, none but line feed
    /// and tab) and no surrogate without its pair, and grows by exactly five units per escape, so each
    /// escape replaced exactly one code unit. Each generated class is counted, so the fact fails over a
    /// generator that stopped producing one.
    /// </summary>
    [Fact]
    public void R10_67_NothingOfTheSetSurvivesAndEveryEscapeReadsBack()
    {
        long controls = 0, lone = 0, astral = 0;

        GenText.Sample(text =>
        {
            if (text.Any(char.IsControl)) Interlocked.Increment(ref controls);
            if (CanonicalJson.HasUnpairedSurrogate(text)) Interlocked.Increment(ref lone);
            else if (text.Any(char.IsSurrogate)) Interlocked.Increment(ref astral);

            return Holds(text, SpanText.Block(text), keepLayout: true)
                && Holds(text, SpanText.Line(text), keepLayout: false);
        }, iter: 5_000, print: DisplayLiteral.Of);

        Assert.True(
            controls > 0 && lone > 0 && astral > 0,
            $"the generator missed a class: controls={controls} lone-halves={lone} astral={astral}");
    }

    private static bool Holds(string text, string output, bool keepLayout) =>
        string.Equals(Decode(output), text, StringComparison.Ordinal)
        && HoldsNothingOfTheSet(output, keepLayout)
        && output.Length == text.Length + (5 * output.Count(c => c == '\\'));

    private static bool HoldsNothingOfTheSet(string output, bool keepLayout)
    {
        for (var i = 0; i < output.Length;)
        {
            if (Rune.DecodeFromUtf16(output.AsSpan(i), out var rune, out var consumed) != System.Buffers.OperationStatus.Done)
                return false;

            var layout = keepLayout && (rune.Value is 0x0A or 0x09);
            var category = Rune.GetUnicodeCategory(rune);
            if (!layout && category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                return false;

            i += consumed;
        }

        return true;
    }

    /// <summary>Every backslash, <c>u</c> and four lowercase hexadecimal digits, read as that code unit.</summary>
    private static string Decode(string output)
    {
        var decoded = new StringBuilder(output.Length);
        for (var i = 0; i < output.Length; i++)
        {
            if (output[i] == '\\'
                && i + 5 < output.Length
                && output[i + 1] == 'u'
                && output.AsSpan(i + 2, 4).ToString().All(h => h is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                decoded.Append((char)int.Parse(output.AsSpan(i + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
                i += 5;
            }
            else
            {
                decoded.Append(output[i]);
            }
        }

        return decoded.ToString();
    }

    /// <summary>
    /// One piece of a generated string: any code unit but a backslash, a lone half, a scalar beyond the
    /// BMP, a line feed, a tab or a carriage return, or a plain character from the space to the bracket.
    /// </summary>
    private static readonly Gen<string> GenPiece = Gen.Frequency(
        (6, Gen.Char[char.MinValue, char.MaxValue].Where(u => u != '\\').Select(u => u.ToString())),
        (2, Gen.Char[(char)0xD800, (char)0xDFFF].Select(u => u.ToString())),
        (2, Gen.Int[0x10000, 0x10FFFF].Select(char.ConvertFromUtf32)),
        (1, Gen.OneOfConst("\n", "\t", "\r")),
        (3, Gen.Char[' ', '['].Select(u => u.ToString())));

    private static readonly Gen<string> GenText = GenPiece.Array[0, 12].Select(pieces => string.Concat(pieces));
}
