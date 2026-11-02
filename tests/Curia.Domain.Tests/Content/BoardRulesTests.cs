using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Curia.Domain.Content;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Domain.Tests.Content;

/// <summary>
/// R8.85 (errata G56): <see cref="BoardRules.Classes"/>, the one predicate for "is this board a name", by hand-written table (NAM1), for
/// totality and linear cost (NAM9) and for culture independence (NAM10).
///
/// <para><b>Oracles.</b> Each row's expected class set is written here by hand from the clause quoted beside it (ruling 13), never by calling
/// the predicate; the ten slugs and their order are parsed from R8.85 words (<see cref="PublishedBoardWords.Classes"/>, count asserted) and
/// the table's literals are checked against them, so a slug the errata renames turns the table red by name. Every spelling is built at run
/// time (no raw control character, noncharacter or lone surrogate is in a file). A qualifying row is a control and is green on the seam
/// (the seam accepts every board); every refused row is red by assertion.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class BoardRulesTests
{
    private static string U(int scalar) => BoardSpellings.U(scalar);

    private static string Rep(string unit, int count) => BoardSpellings.Rep(unit, count);

    private static readonly string[] None = [];

    private static (string Name, string Board, string[] Expected)[] Table() => BoardSpellings.Table();

    public static TheoryData<int> Rows()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Table().Length; i++) data.Add(i);
        return data;
    }

    /// <summary>NAM1's words check: the table's slugs are the ten of R8.85 words, in the published order, and no expectation is out of that order.</summary>
    [Fact]
    public void R8_85_NAM1_TheTableUsesThePublishedSlugsInThePublishedOrder()
    {
        var published = PublishedBoardWords.Classes();
        Assert.Equal(
            ["not-nfc", "length", "control", "format", "separator", "private-use", "unassigned", "reserved", "dot-segment", "not-lower-case"],
            published);

        foreach (var (name, _, expected) in Table())
        {
            var positions = expected.Select(c => Array.IndexOf(published, c)).ToArray();
            Assert.True(positions.All(p => p >= 0), $"{name}: a class the errata does not publish");
            Assert.True(positions.SequenceEqual(positions.Order()), $"{name}: the expectation is not in the published order");
        }

        // Finding 3: no class is an edge class. The table's edge rows (" a", "a ") expect separator, and nothing else.
        Assert.DoesNotContain(published, c => c.Contains("edge", StringComparison.Ordinal));
    }

    /// <summary>NAM1: for each of the table's spellings the predicate gives the classes written beside it, in the published order.</summary>
    [Theory]
    [MemberData(nameof(Rows))]
    public void R8_85_NAM1_TheTableGivesTheWrittenClasses(int row)
    {
        var (name, board, expected) = Table()[row];

        var actual = BoardRules.Classes(board);

        Assert.True(expected.SequenceEqual(actual), $"{name}: expected [{string.Join(", ", expected)}], was [{string.Join(", ", actual)}]");
    }

    /// <summary>The table (56 spellings; the plan said about 70): its size is asserted before it is used, so a row deleted turns this red.</summary>
    [Fact]
    public void R8_85_NAM1_TheTableHasItsRows()
    {
        var rows = Table();
        Assert.Equal(rows.Length, rows.Select(r => r.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(56, rows.Length);
        Assert.Equal(16, rows.Count(r => r.Expected.Length == 0));
    }

    // ---- NAM9 -------------------------------------------------------------------------------------

    /// <summary>
    /// NAM9: the predicate is total. The two values <c>Normalize</c> and <c>IsNormalized</c> throw on (U+FFFE and a lone surrogate) yield
    /// <c>unassigned</c> and no <c>not-nfc</c> (ruling 22); the noncharacters that do not throw are <c>unassigned</c> too.
    /// </summary>
    [Theory]
    [InlineData(0xFFFE)]
    [InlineData(0xD800)]
    [InlineData(0xDBFF)]
    [InlineData(0xDC00)]
    [InlineData(0xDFFF)]
    [InlineData(0xFDD0)]
    [InlineData(0xFDEF)]
    [InlineData(0xFFFF)]
    public void R8_85_NAM9_AnUnassignedUnitOnItsOwnYieldsUnassignedAndNeverThrows(int unit)
    {
        var board = ((char)unit).ToString();

        var actual = BoardRules.Classes(board);

        Assert.Equal(["unassigned"], actual);
    }

    /// <summary>NAM9: U+10FFFF is a scalar value and a noncharacter; it neither throws nor reports not-nfc.</summary>
    [Fact]
    public void R8_85_NAM9_TheLastScalarIsUnassignedAndNeverThrows() =>
        Assert.Equal(["unassigned"], BoardRules.Classes(U(0x10FFFF)));

    /// <summary>NAM9: a high surrogate followed by a non-low one is two lone units, still one answer and no throw.</summary>
    [Fact]
    public void R8_85_NAM9_AHighSurrogateBeforeALetterIsUnassigned() =>
        Assert.Equal(["unassigned"], BoardRules.Classes("a" + (char)0xD800 + "b"));

    /// <summary>
    /// NAM9: 262,144 characters (the string cap) and 262,144 bytes of 4-byte scalars give <c>length</c> and nothing else, and a string holding
    /// every scalar value returns classes without throwing; each in under a bound a quadratic scan misses by orders of magnitude (the
    /// linear scan over 262,144 characters took 7 ms, M3; the bound is two seconds, written here after that measurement).
    /// </summary>
    [Fact]
    public void R8_85_NAM9_TheLargestBoardsAreAnsweredInLinearTime()
    {
        var wide = Rep("a", 262_144);
        var astral = Rep(U(0x1F642), 65_536);
        var every = new System.Text.StringBuilder();
        for (var scalar = 0; scalar <= 0x10FFFF; scalar++)
            if (scalar is < 0xD800 or > 0xDFFF) every.Append(char.ConvertFromUtf32(scalar));

        var clock = Stopwatch.StartNew();
        var wideClasses = BoardRules.Classes(wide);
        var astralClasses = BoardRules.Classes(astral);
        var everyClasses = BoardRules.Classes(every.ToString());
        clock.Stop();

        Assert.Equal(["length"], wideClasses);
        Assert.Equal(["length"], astralClasses);
        Assert.Contains("length", everyClasses);
        Assert.Contains("unassigned", everyClasses);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"three large boards took {clock.Elapsed.TotalMilliseconds:F0} ms");
    }

    /// <summary>NAM9: a 262,144-scalar-wide run of one class stays one entry per class (a class is named once, not once per character).</summary>
    [Fact]
    public void R8_85_NAM9_AClassIsNamedOnceHoweverManyCharactersFailIt() =>
        Assert.Equal(["control"], BoardRules.Classes(Rep("\n", 64)));

    // ---- NAM10 ------------------------------------------------------------------------------------

    /// <summary>
    /// NAM10's table, measured by M3 on .NET 10.0.14 (Unicode 16.0): the verdicts under the invariant culture, written by hand. <c>I</c> is refused,
    /// <c>i</c> admitted, U+0130 admitted (the invariant form leaves it unchanged), U+0131 admitted, Sigma refused, final sigma admitted, U+01C5 and
    /// U+1E9E refused.
    /// </summary>
    private static (string Board, string[] Expected)[] CultureTable() =>
    [
        ("I", ["not-lower-case"]),
        ("i", None),
        (U(0x130), None),
        (U(0x131), None),
        (U(0x3A3), ["not-lower-case"]),
        (U(0x3C2), None),
        (U(0x1C5), ["not-lower-case"]),
        (U(0x1E9E), ["not-lower-case"]),
    ];

    /// <summary>NAM10: under tr-TR and under the invariant culture the verdicts are the same and are the table's.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("tr-TR")]
    public void R8_85_NAM10_TheVerdictsDoNotDependOnTheCulture(string culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            Assert.Equal(culture, CultureInfo.CurrentCulture.Name);
            if (culture.Length > 0)
                Assert.Equal(U(0x131), "I".ToLower(CultureInfo.CurrentCulture)); // the premise: tr-TR really lower-cases I to dotless i

            foreach (var (board, expected) in CultureTable())
            {
                var actual = BoardRules.Classes(board);
                Assert.True(expected.SequenceEqual(actual), $"culture '{culture}', U+{char.ConvertToUtf32(board, 0):X4}: expected [{string.Join(", ", expected)}], was [{string.Join(", ", actual)}]");
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }
}
