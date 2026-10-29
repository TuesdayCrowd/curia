using System.Diagnostics.CodeAnalysis;
using System.Text;
using CsCheck;
using Curia.Domain.Serving;
using Xunit;

namespace Curia.Domain.Tests.Serving;

/// <summary>
/// PR 3 (errata G47, R10.90), CHN44, a control: the rule the Reader Contract will state is true of the Forum. R10.90's sentence says the Forum's own mark
/// is one token at the start of a word, every occurrence of the token in an author's text is written twice, so a run at a word's start is odd (one is
/// the Forum's) and a run elsewhere is even (all the author's), and a reader removes one token at each word start and collapses each remaining pair.
/// <see cref="Decode"/> is written from that sentence and not from <see cref="Datamarking.StripDatamarking"/>, so a change to the mark's escaping that the
/// sentence does not say fails here and not by agreeing with itself. Green on the unmodified tree (Task 0's M11 measured 13 of 13 agreements); red under
/// FCHN50 (a mark that is not escaped) and FCHN51 (a mark at every character).
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class MarkingRuleRoundTripTests
{
    private const string Token = Datamarking.DefaultControlToken;

    /// <summary>The runs of <paramref name="token"/> in <paramref name="marked"/>: (start index, length, whether the run starts a word).</summary>
    private static List<(int Start, int Length, bool AtWordStart)> Runs(string marked, string token)
    {
        var runs = new List<(int, int, bool)>();
        var i = 0;
        while (i < marked.Length)
        {
            if (string.CompareOrdinal(marked, i, token, 0, token.Length) != 0)
            {
                i++;
                continue;
            }

            var start = i;
            while (i < marked.Length && string.CompareOrdinal(marked, i, token, 0, token.Length) == 0) i += token.Length;
            runs.Add((start, (i - start) / token.Length, start == 0 || char.IsWhiteSpace(marked[start - 1])));
        }

        return runs;
    }

    /// <summary>R10.90's decode: remove one token at each word start, then collapse each remaining pair of tokens to one.</summary>
    internal static string Decode(string marked, string token)
    {
        var builder = new StringBuilder();
        var cursor = 0;
        foreach (var (start, length, atWordStart) in Runs(marked, token))
        {
            builder.Append(marked, cursor, start - cursor);
            var remaining = atWordStart ? length - 1 : length;
            Assert.True(remaining % 2 == 0, $"a run of {length} at index {start} leaves {remaining} after the Forum's mark, which does not collapse in pairs");
            for (var k = 0; k < remaining / 2; k++) builder.Append(token);
            cursor = start + length * token.Length;
        }

        builder.Append(marked, cursor, marked.Length - cursor);
        return builder.ToString();
    }

    private static readonly Gen<string> Content =
        Gen.OneOfConst(Token, "a", "ab", " ", "\n", "  ").List[0, 14].Select(parts => string.Concat(parts));

    /// <summary>CHN44. Decoding the marked content by R10.90's sentence returns the content, and every run at a word start is odd and every other run even.</summary>
    [Fact]
    public void R10_90_CHN44_TheRuleTheContractWillStateIsTrueOfDatamark()
    {
        Content.Sample(
            content =>
            {
                var marked = Datamarking.Datamark(content);
                Assert.Equal(content, Decode(marked, Token));
                foreach (var (start, length, atWordStart) in Runs(marked, Token))
                    Assert.True(atWordStart ? length % 2 == 1 : length % 2 == 0, $"a run of {length} at index {start} (word start: {atWordStart}) in {marked.Length} characters");
            },
            iter: 500);
    }

    /// <summary>CHN44's named cases: the token alone in runs of 1 to 5, at the start, mid-word, after a newline, at the end, and empty.</summary>
    [Fact]
    public void R10_90_CHN44_TheNamedCasesRoundTrip()
    {
        var cases = new List<string> { string.Empty };
        for (var n = 1; n <= 5; n++)
        {
            var run = string.Concat(Enumerable.Repeat(Token, n));
            cases.Add(run);
            cases.Add($"{run}word");
            cases.Add($"word{run}word");
            cases.Add($"word\n{run}");
            cases.Add($"word {run}\nmore{run}");
        }

        foreach (var content in cases)
            Assert.Equal(content, Decode(Datamarking.Datamark(content), Token));
    }
}
