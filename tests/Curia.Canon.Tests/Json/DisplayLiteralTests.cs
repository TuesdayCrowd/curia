using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Canon.Tests.Vectors;
using CsCheck;
using Xunit;

namespace Curia.Canon.Tests.Json;

/// <summary>
/// R10.64 (errata G17): the display literal, against <c>conformance/display/</c> and against
/// generated strings. The vectors pin the bytes both readers must print; the properties pin what
/// the vectors cannot enumerate -- that no output of any input holds a character a reader's output
/// could be laid out by, and that two inputs never print alike.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class DisplayLiteralTests
{
    private static readonly IReadOnlyList<DisplayVector> Vectors = DisplayVectorLoader.Load();

    public static TheoryData<string> VectorNames()
    {
        var data = new TheoryData<string>();
        foreach (var vector in Vectors) data.Add(vector.Name);
        return data;
    }

    /// <summary>
    /// The runner's half of R6.45: the family is not directory-shaped, so
    /// <see cref="ConformanceIndexTests"/> cannot see whether anything here loads it. This does.
    /// </summary>
    [Fact]
    public void R6_45_ThisRunnerLoadsEveryDisplayVectorTheIndexDeclares()
    {
        Assert.NotEmpty(Vectors);
        Assert.Equal(DisplayVectorLoader.DeclaredCount(), Vectors.Count);
        Assert.All(Vectors, v => Assert.Equal("R10.64", v.Requirement));
    }

    [Theory]
    [MemberData(nameof(VectorNames))]
    public void R10_64_EveryDisplayVectorPrintsAsPublished(string name)
    {
        var vector = Vectors.Single(v => v.Name == name);

        Assert.Equal(vector.Expected, DisplayLiteral.Of(vector.Input));
    }

    /// <summary>
    /// The one input the corpus cannot carry: half a surrogate pair, which Rust's string type cannot
    /// hold. It is an escape of its own, and the character after it is unaffected.
    /// </summary>
    [Fact]
    public void R10_64_AnUnpairedSurrogateIsAnEscapeOfItsOwn()
    {
        Assert.Equal("\"a\\ud800b\"", DisplayLiteral.Of("a" + (char)0xD800 + "b"));
        Assert.Equal("\"\\udc00\"", DisplayLiteral.Of(((char)0xDC00).ToString()));
    }

    /// <summary>An absent value is written outside quotes, so no literal can be mistaken for it.</summary>
    [Fact]
    public void R10_64_AnAbsentValueIsNotALiteral()
    {
        Assert.Equal("(none)", DisplayLiteral.Of(null));
        Assert.Equal("\"(none)\"", DisplayLiteral.Of("(none)"));
    }

    /// <summary>
    /// For every generated string, the literal is printable ASCII between two quotes, and reading it
    /// back gives the string: so no output can begin a line or reorder one, and two different values
    /// never print alike. Read back two ways -- by System.Text.Json, which knows nothing of this
    /// rule, for every well-formed string, and by <see cref="Decode"/> for every string -- and each
    /// generated class is counted, so the fact fails over a generator that stopped producing one.
    /// </summary>
    [Fact]
    public void R10_64_EveryLiteralIsPrintableAsciiAndReadsBackAsItsValue()
    {
        long controls = 0, surrogates = 0, astral = 0, quotes = 0;

        GenText.Sample(text =>
        {
            var literal = DisplayLiteral.Of(text);

            if (text.Any(char.IsControl)) Interlocked.Increment(ref controls);
            if (CanonicalJson.HasUnpairedSurrogate(text)) Interlocked.Increment(ref surrogates);
            else if (text.Any(char.IsSurrogate)) Interlocked.Increment(ref astral);
            if (text.Contains('"', StringComparison.Ordinal)) Interlocked.Increment(ref quotes);

            var printable = literal.Length >= 2
                && literal[0] == '"'
                && literal[^1] == '"'
                && literal.All(unit => unit is >= ' ' and <= '~');

            var readBack = string.Equals(Decode(literal), text, StringComparison.Ordinal)
                && (CanonicalJson.HasUnpairedSurrogate(text)
                    || string.Equals(JsonSerializer.Deserialize<string>(literal), text, StringComparison.Ordinal));

            return printable && readBack;
        }, iter: 5_000, print: Render);

        Assert.True(
            controls > 0 && surrogates > 0 && astral > 0 && quotes > 0,
            $"the generator missed a class: controls={controls} unpaired-surrogates={surrogates} astral={astral} quotes={quotes}");
    }

    /// <summary>
    /// R10.66 (errata G17): a literal reads back as its value -- every vector's expected bytes as its
    /// input, and every generated well-formed string's literal as the string -- so a reader can take
    /// its own output as input. A generated string holding a surrogate without its pair is the other
    /// side: its literal is refused, since no name can hold one and the next hop would send U+FFFD.
    /// </summary>
    [Fact]
    public void R10_66_EveryLiteralReadsBackAsItsValue()
    {
        Assert.All(Vectors, v => Assert.True(
            DisplayLiteral.TryRead(v.Expected, out var value) && string.Equals(value, v.Input, StringComparison.Ordinal),
            v.Name));

        long wellFormed = 0, unpaired = 0;
        GenText.Sample(
            text =>
            {
                if (CanonicalJson.HasUnpairedSurrogate(text))
                {
                    Interlocked.Increment(ref unpaired);
                    return !DisplayLiteral.TryRead(DisplayLiteral.Of(text), out _);
                }

                Interlocked.Increment(ref wellFormed);
                return DisplayLiteral.TryRead(DisplayLiteral.Of(text), out var value) && string.Equals(value, text, StringComparison.Ordinal);
            },
            iter: 5_000,
            print: Render);

        Assert.True(wellFormed > 0 && unpaired > 0, $"the generator missed a side: well-formed={wellFormed} unpaired={unpaired}");
    }

    /// <summary>
    /// Only the literal a reader prints is read: one spelling per value, so a literal altered on its
    /// way back -- an escape in capitals, a printable character escaped, JSON's other escapes, a bare
    /// quote, a character left unescaped -- is refused rather than read as some value. So is the
    /// literal of a surrogate without its pair, which no name on the Forum can hold (R6.15).
    /// </summary>
    [Fact]
    public void R10_66_OnlyTheLiteralAReaderPrintsIsRead()
    {
        string?[] others =
        [
            null, "", "\"", "a", "\"a", "a\"", "\"a\"b\"", "(none)",
            "\"caf\\u" + "00E9\"", "\"\\u" + "0041\"", "\"\\n\"", "\"\\/\"", "\"\\u" + "00e\"",
            "\"caf" + (char)0xE9 + "\"", "\"a" + (char)0x0A + "b\"",
            "\"b\\u" + "d800\"", "\"\\u" + "dc00a\"",
        ];

        foreach (var other in others)
            Assert.False(DisplayLiteral.TryRead(other, out _), other is null ? "(null)" : Render(other));
    }

    /// <summary>
    /// One piece of a generated string: any code unit, a lone half, a scalar beyond the BMP, a quote
    /// or a backslash, a line break, or a plain letter.
    /// </summary>
    private static readonly Gen<string> GenPiece = Gen.Frequency(
        (6, Gen.Char[char.MinValue, char.MaxValue].Select(unit => unit.ToString())),
        (2, Gen.Char[(char)0xD800, (char)0xDFFF].Select(unit => unit.ToString())),
        (2, Gen.Int[0x10000, 0x10FFFF].Select(char.ConvertFromUtf32)),
        (1, Gen.OneOfConst("\"", "\\", "\n", "\u0085")),
        (3, Gen.Char[' ', '~'].Select(unit => unit.ToString())));

    private static readonly Gen<string> GenText = GenPiece.Array[0, 12].Select(pieces => string.Concat(pieces));

    /// <summary>
    /// The inverse of the rule as R10.64 states it, written from the text rather than from
    /// <see cref="DisplayLiteral"/>: a quote, then characters standing for themselves, a backslash
    /// before a quote or a backslash, or <c>\u</c> and four hex digits, then a quote.
    /// </summary>
    private static string? Decode(string literal)
    {
        if (literal.Length < 2 || literal[0] != '"' || literal[^1] != '"') return null;

        var text = new System.Text.StringBuilder();
        for (var i = 1; i < literal.Length - 1; i++)
        {
            if (literal[i] != '\\')
            {
                text.Append(literal[i]);
                continue;
            }

            if (i + 1 >= literal.Length - 1) return null;
            var next = literal[++i];
            if (next is '"' or '\\')
            {
                text.Append(next);
                continue;
            }

            if (next != 'u' || i + 4 >= literal.Length - 1) return null;
            text.Append((char)Convert.ToInt32(literal.Substring(i + 1, 4), 16));
            i += 4;
        }

        return text.ToString();
    }

    /// <summary>A failing example, spelled as code units, so a report never carries the character itself.</summary>
    private static string Render(string text) =>
        string.Join(' ', text.Select(unit => "U+" + ((int)unit).ToString("X4", System.Globalization.CultureInfo.InvariantCulture)));
}
