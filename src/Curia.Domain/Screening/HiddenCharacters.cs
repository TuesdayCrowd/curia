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
/// <para><b>Deliberately left out:</b> U+061C (Arabic letter mark) and U+2061–U+2064 (the invisible
/// mathematical operators) are the same class of character, but nothing here has measured them
/// against the benign corpus. Adding one is a detector version bump (R10.10) and a measurement,
/// not an edit to this list.</para>
/// </summary>
public static class HiddenCharacters
{
    /// <summary>Whether <paramref name="c"/> is one of the hidden characters this class names.</summary>
    public static bool Contains(char c) => c switch
    {
        '\u00AD' => true,                    // soft hyphen
        >= '\u200B' and <= '\u200F' => true, // zero-width space, ZWNJ, ZWJ, LRM, RLM
        >= '\u202A' and <= '\u202E' => true, // bidi embeddings and overrides
        '\u2060' => true,                    // word joiner
        >= '\u2066' and <= '\u2069' => true, // bidi isolates
        '\uFEFF' => true,                    // zero-width no-break space (BOM)
        _ => false,
    };
}
