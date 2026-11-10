using System.Diagnostics.CodeAnalysis;
using Curia.Infrastructure.Embedder.Tokenization;
using Xunit;

namespace Curia.Infrastructure.Tests.Embedder;

/// <summary>
/// Errata G65 (D10a), Amendment 2 ruling 44 (SEM50), a control, model-free: a non-spacing mark is removed after decomposition wherever it stands, so marks after a base, at the start of a
/// text, after a special token and after a dropped format character change no id. The oracle is that rule and not the implementation: each text is compared with the same text without its
/// marks. Green on the tree as it stands; its value is its falsification row (FSEM40, <c>FormD</c> to <c>FormC</c>), which Task 6 records.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class TokenizerMarkTests
{
    private static readonly int[] MarkCodePoints = [0x0301, 0x0300, 0x0327, 0x0323];

    private static string Marks(int count) => string.Concat(Enumerable.Range(0, count).Select(i => Cp.Of(MarkCodePoints[i % MarkCodePoints.Length])));

    private static int[] Ids(string text) => [.. new WordPieceTokenizer(EmbedderRig.Vocabulary).Tokenize(text, 510).ContentIds];

    /// <summary>
    /// SEM50. For a base word, a precomposed letter (alone and with marks after it), the start of a text, a special token and a soft hyphen (a dropped format character), a run of 1, 8 and 2,000 marks
    /// (past the 1,024 a cluster is cut at) leaves the ids of the mark-free text; a precomposed letter is the mark-free word's own letter (the oracle for it is the ASCII spelling, not the tokenizer); and the
    /// mark-free ids are not empty, so the equality is not between two empty lists. Why the precomposed cases: the tokenizer flushes an ASCII character on its own, so an ASCII base never meets its marks
    /// in normalisation and <c>FormD</c> and <c>FormC</c> cannot differ on it; only a base outside ASCII does (Task 6's FSEM40 left this probe green for that reason; Amendment 3 ruling 54).
    /// </summary>
    [Fact]
    public void R9_63_SEM50_NonSpacingMarksChangeNoId()
    {
        var softHyphen = Cp.Of(0x00AD);
        var precomposed = Cp.Of(0x00E9);
        var cases = new (string Name, Func<string, string> WithMarks, string Without)[]
        {
            ("a base word", m => "cafe" + m, "cafe"),
            ("a precomposed letter", _ => "caf" + precomposed, "cafe"),
            ("a precomposed letter and marks after it", m => "caf" + precomposed + m, "cafe"),
            ("the start of a text", m => m + "x", "x"),
            ("a special token", m => "[CLS]" + m + "x", "[CLS]x"),
            ("a dropped format character", m => "x" + softHyphen + m + "y", "x" + softHyphen + "y"),
        };

        foreach (var (name, withMarks, without) in cases)
        {
            var expected = Ids(without);
            Assert.NotEmpty(expected);
            if (without == "cafe") Assert.Equal([EmbedderRig.IdOf("cafe")], expected);
            foreach (var count in new[] { 1, 8, 2000 })
            {
                var actual = Ids(withMarks(Marks(count)));
                Assert.True(expected.SequenceEqual(actual), $"{count} marks after {name} changed the ids: [{string.Join(' ', expected)}] became [{string.Join(' ', actual)}]");
            }
        }
    }
}
