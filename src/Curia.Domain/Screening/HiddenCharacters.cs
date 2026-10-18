using System.Text;

namespace Curia.Domain.Screening;

/// <summary>
/// The invisible characters SCREEN treats as hidden: R10.8's "zero-width characters ... unusual
/// Unicode direction marks", and the soft hyphen.
///
/// <para><b>One predicate, three callers, because they ask the same question.</b>
/// <see cref="InjectionDetector"/> annotates each of these as <see cref="RiskCategory.HiddenText"/>,
/// and the line-joined view in <see cref="DerivedViews"/> deletes them before it rejoins a wrapped
/// credential. A character that hides text from a reader is the same character that splits a key
/// without the author seeing the split -- a renderer leaves a soft hyphen or a zero-width break at a
/// wrap, and a verbatim copy keeps it (§10.8). Two lists would drift: the view would delete what the
/// detector no longer reports, or the reverse, and a key split by the character one list lacks
/// would be admitted with no annotation at all, which is what U+2060 was until this class existed
/// (register D17). The third caller is the moderation writer's reason guard (R10.62), which drops
/// these from its derived copies so a raiser split by one is still recognised; public for that reason,
/// since the guard lives in the application layer.</para>
///
/// <para><b>Deliberately left out:</b> the ranges the errata's R10.78 declines (variation selectors,
/// the combining grapheme joiner, the Hangul fillers and the rest) have an ordinary use no closed list
/// covers. Each is recorded as a known evasion, and its ordinary-use sample is in the benign corpus.
/// Adding one is a detector version bump (R10.10) and a measurement, not an edit to this list.</para>
/// </summary>
public static class HiddenCharacters
{
    /// <summary>Whether <paramref name="rune"/> is one of the hidden characters this class names (R10.76). Over a scalar value, never a code unit.</summary>
    public static bool Contains(Rune rune) => rune.Value switch
    {
        0x00AD => true,                      // soft hyphen
        0x061C => true,                      // Arabic letter mark (Bidi_Control, with U+200E, U+200F, U+202A to U+202E, U+2066 to U+2069)
        >= 0x200B and <= 0x200F => true,     // zero-width space, ZWNJ, ZWJ, LRM, RLM
        >= 0x202A and <= 0x202E => true,     // bidi embeddings and overrides
        >= 0x2060 and <= 0x206F => true,     // word joiner, invisible operators, U+2065 (reserved), isolates, deprecated format characters
        0xFEFF => true,                      // zero-width no-break space (BOM)
        >= 0xE0000 and <= 0xE007F => true,   // the tag block
        _ => false,
    };
}
