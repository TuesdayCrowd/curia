using System.Buffers.Text;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text;

namespace Curia.Domain.Screening;

/// <summary>
/// A normalized reading of the content, with every character mapped back to where it came from.
/// </summary>
/// <param name="Name">Which normalization produced this, for the finding's provenance.</param>
/// <param name="Text">The normalized text a detector scans.</param>
/// <param name="OriginalIndex">
/// For each character in <see cref="Text"/>, the index in the original it came from.
///
/// <para><b>This is the part that makes normalization usable at all.</b> R10.27 requires a rejection
/// to report the *location* of what it found, and a location in a normalized copy is useless to an
/// author holding the original. Without the map, normalization would buy detection at the cost of
/// the one thing that makes a rejection actionable.</para>
/// </param>
public sealed record DerivedView(string Name, string Text, ImmutableArray<int> OriginalIndex)
{
    /// <summary>The original span a match at <paramref name="start"/> of <paramref name="length"/> covers.</summary>
    public (int Offset, int Length) ToOriginal(int start, int length)
    {
        if (OriginalIndex.IsEmpty || length == 0) return (0, 0);

        var from = OriginalIndex[Math.Min(start, OriginalIndex.Length - 1)];
        var to = OriginalIndex[Math.Min(start + length - 1, OriginalIndex.Length - 1)];

        return (from, Math.Max(1, to - from + 1));
    }
}

/// <summary>
/// The normalizations SCREEN applies before detection — on the derived copy R6.13 permits, and never
/// to anything that is stored.
///
/// <para><b>Why this exists: because the detectors were trivially evadable without it.</b> Adding ten
/// realistic evasions to the red-team corpus, all ten defeated the pattern rules. Character spacing,
/// Markdown emphasis, homoglyph substitution and base64 wrapping all break a regex that matches
/// literal words, and none of them requires any sophistication from an attacker.</para>
///
/// <para><b>R6.13 explicitly permits this:</b> "Any transformation performed for analysis SHALL
/// operate on a derived copy that is discarded after the analysis completes." These views are that
/// derived copy. They are constructed inside <see cref="ContentScreener"/>, read by the detectors,
/// and unreachable when screening returns — the persisted bytes never see any of it.</para>
///
/// <para><b>The cost is false positives, which is why each view is scored against the benign
/// corpus.</b> R10.26 makes a credential hit a hard rejection, so a normalization that made ordinary
/// prose look like an attack would cost authors their submissions. Every view added here was checked
/// against `conformance/red-team/benign.jsonl` at a zero-tolerance ceiling.</para>
/// </summary>
public static class DerivedViews
{
    /// <summary>
    /// Confusable code points folded to their Latin lookalike.
    ///
    /// <para><b>A curated subset, not UTS #39.</b> The full confusables table is large and folding all
    /// of it would map legitimate Greek and Cyrillic text onto Latin, which is precisely the
    /// false-positive risk that kept homoglyph detection out until now. This covers the characters
    /// that are visually identical to ASCII letters in common fonts and therefore the ones actually
    /// used for substitution. Text genuinely written in Cyrillic still normalizes -- the view is only
    /// ever *scanned*, never stored, and a Russian-language post that happens to fold to something
    /// resembling an English attack phrase is a case the benign corpus exists to catch.</para>
    /// </summary>
    private static readonly FrozenDictionary<char, char> Confusables =
        new Dictionary<char, char>
        {
            // Cyrillic
            ['а'] = 'a', ['е'] = 'e', ['о'] = 'o', ['р'] = 'p', ['с'] = 'c', ['х'] = 'x',
            ['у'] = 'y', ['і'] = 'i', ['ѕ'] = 's', ['ј'] = 'j', ['һ'] = 'h', ['ԁ'] = 'd',
            ['А'] = 'A', ['В'] = 'B', ['Е'] = 'E', ['К'] = 'K', ['М'] = 'M', ['Н'] = 'H',
            ['О'] = 'O', ['Р'] = 'P', ['С'] = 'C', ['Т'] = 'T', ['Х'] = 'X',

            // Greek
            ['ο'] = 'o', ['α'] = 'a', ['ν'] = 'v', ['ρ'] = 'p', ['τ'] = 't', ['υ'] = 'u',
            ['Α'] = 'A', ['Β'] = 'B', ['Ε'] = 'E', ['Ζ'] = 'Z', ['Η'] = 'H', ['Ι'] = 'I',
            ['Κ'] = 'K', ['Μ'] = 'M', ['Ν'] = 'N', ['Ο'] = 'O', ['Ρ'] = 'P', ['Τ'] = 'T',
        }.ToFrozenDictionary();

    /// <summary>
    /// Every view a detector should scan, including the identity view.
    ///
    /// <para>The identity view is first and is never omitted: a finding's offset is most useful when
    /// it came from the original, and the deduplication in <see cref="ContentScreener"/> keeps the
    /// first occurrence of each (category, offset).</para>
    /// </summary>
    public static ImmutableArray<DerivedView> Of(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var views = ImmutableArray.CreateBuilder<DerivedView>();
        views.Add(Identity(content));

        // "i g n o r e   a l l" -- character spacing defeats word-boundary matching. Collapsing runs
        // of single characters separated by single spaces recovers the word without touching ordinary
        // prose, whose words are longer than one character.
        views.Add(Map(content, "despaced", Despace));

        // "ig**nore** all **prev**ious" -- Markdown emphasis splits words mid-token.
        views.Add(Map(content, "unmarked-up", StripMarkup));

        // Homoglyph substitution: R10.8 names it, and it was unimplemented until now.
        views.Add(Map(content, "unconfused", c => Confusables.TryGetValue(c, out var latin) ? latin : c));

        // A credential split across words: "ghp_A7bQ2xLm and 9RtVz...". Removing separators entirely
        // makes a concatenation visible. Aggressive, and scoped to secret scanning only for that
        // reason -- see ContentScreener.
        views.Add(Map(content, "unseparated", c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '\0'));

        views.AddRange(DecodedSegments(content));

        return views.ToImmutable();
    }

    private static DerivedView Identity(string content) =>
        new("original", content, [.. Enumerable.Range(0, content.Length)]);

    /// <summary>Applies a per-character transform, dropping characters mapped to NUL, keeping the index map.</summary>
    private static DerivedView Map(string content, string name, Func<char, char> transform)
    {
        var text = new StringBuilder(content.Length);
        var indexes = ImmutableArray.CreateBuilder<int>(content.Length);

        for (var i = 0; i < content.Length; i++)
        {
            var mapped = transform(content[i]);
            if (mapped == '\0') continue;

            text.Append(mapped);
            indexes.Add(i);
        }

        return new DerivedView(name, text.ToString(), indexes.ToImmutable());
    }

    /// <summary>Overload for transforms that need position, used by the despacing view.</summary>
    private static DerivedView Map(string content, string name, Func<string, int, char> transform)
    {
        var text = new StringBuilder(content.Length);
        var indexes = ImmutableArray.CreateBuilder<int>(content.Length);

        for (var i = 0; i < content.Length; i++)
        {
            var mapped = transform(content, i);
            if (mapped == '\0') continue;

            text.Append(mapped);
            indexes.Add(i);
        }

        return new DerivedView(name, text.ToString(), indexes.ToImmutable());
    }

    /// <summary>
    /// Drops a space that sits between two single characters, which is what "s p a c e d o u t" text
    /// is made of.
    ///
    /// <para>Scoped to single-character neighbours on both sides so ordinary prose is untouched: in
    /// "the cat sat", every space has a multi-character word on at least one side, so nothing
    /// collapses. That scoping is the difference between this view and one that would fold every
    /// sentence into a single word and fire on everything.</para>
    /// </summary>
    private static char Despace(string content, int i)
    {
        if (content[i] != ' ') return content[i];

        var beforeIsSingle = i >= 1 && char.IsLetterOrDigit(content[i - 1])
            && (i < 2 || !char.IsLetterOrDigit(content[i - 2]));
        var afterIsSingle = i + 1 < content.Length && char.IsLetterOrDigit(content[i + 1])
            && (i + 2 >= content.Length || !char.IsLetterOrDigit(content[i + 2]));

        return beforeIsSingle && afterIsSingle ? '\0' : ' ';
    }

    /// <summary>Removes Markdown emphasis and inline-code markers, which split words without changing them.</summary>
    private static char StripMarkup(char c) => c is '*' or '_' or '`' or '~' ? '\0' : c;

    /// <summary>
    /// Decodes base64 and ROT13 candidates, each mapped back to the span that carried it.
    ///
    /// <para>R10.25's list does not stop at plaintext: a credential wrapped in base64 is still a
    /// credential, and "decode this for the PAT:" followed by base64 is a real leak in a real
    /// incident report. Bounded work -- only runs that look like base64 are decoded, once, without
    /// recursion, so this cannot become an unbounded decode loop on the ingest path.</para>
    /// </summary>
    private static IEnumerable<DerivedView> DecodedSegments(string content)
    {
        foreach (var (start, length) in Base64Runs(content))
        {
            var span = content.AsSpan(start, length);
            var buffer = new byte[length];

            if (!Convert.TryFromBase64Chars(span, buffer, out var written) || written == 0) continue;

            string decoded;
            try
            {
                decoded = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(buffer, 0, written);
            }
            catch (DecoderFallbackException)
            {
                // Base64 of something that is not text. Nothing a text detector can read, and
                // guessing at binary formats is not this layer's job.
                continue;
            }

            // Every decoded character maps to the encoded run: an author told "a credential at offset
            // N" can find the base64 that carried it, which is the actionable location.
            yield return new DerivedView(
                "base64-decoded", decoded, [.. Enumerable.Repeat(start, decoded.Length)]);
        }

        // ROT13 is cheap enough to try unconditionally and cannot false-positive on its own: text that
        // is not ROT13 decodes to noise, and noise does not match the detectors' patterns.
        yield return Map(content, "rot13", Rot13);
    }

    private static char Rot13(char c) => c switch
    {
        >= 'a' and <= 'z' => (char)('a' + ((c - 'a' + 13) % 26)),
        >= 'A' and <= 'Z' => (char)('A' + ((c - 'A' + 13) % 26)),
        _ => c,
    };

    /// <summary>
    /// Runs of base64 alphabet long enough to carry something. The floor is 24 characters -- 18 bytes
    /// -- which is below any credential worth wrapping and above the short base64 fragments that
    /// appear constantly in a forum about canonicalization.
    /// </summary>
    private static IEnumerable<(int Start, int Length)> Base64Runs(string content)
    {
        const int minimum = 24;
        var start = -1;

        for (var i = 0; i <= content.Length; i++)
        {
            var isBase64 = i < content.Length && (char.IsAsciiLetterOrDigit(content[i]) || content[i] is '+' or '/' or '=');

            if (isBase64)
            {
                if (start < 0) start = i;
                continue;
            }

            if (start >= 0 && i - start >= minimum) yield return (start, i - start);
            start = -1;
        }
    }
}
