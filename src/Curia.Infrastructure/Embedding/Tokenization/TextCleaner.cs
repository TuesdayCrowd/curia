using System.Globalization;
using System.Text;

namespace Curia.Infrastructure.Embedder.Tokenization;

/// <summary>
/// The character rules of BERT's text preparation (errata G65, Amendment 1 ruling 22), written in this repository to the behaviour of Hugging Face's tokenizer and held to it by the
/// committed reference ids (SEM29) and the boot canaries (SEM4). No code is copied from Hugging Face, Google or Microsoft. The Unicode tables are the host's (<see cref="Rune"/>,
/// <see cref="string.Normalize(NormalizationForm)"/>), which is why the identity names the rules and not the tables (non-property (i) of the entry).
/// </summary>
internal static class TextCleaner
{
    /// <summary>The longest run of combining marks normalised as one piece; a longer run is cut here, which only a text no one wrote meets, and which bounds the reordering work.</summary>
    internal const int MaxCluster = 1024;

    /// <summary>NUL, U+FFFD and every control, format and private-use character is dropped; tab, line feed and carriage return are white space and are kept. An unassigned character is kept.</summary>
    internal static bool IsDropped(Rune rune)
    {
        var v = rune.Value;
        if (v is 0 or 0xFFFD) return true;
        if (v is '\t' or '\n' or '\r') return false;
        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.PrivateUse or UnicodeCategory.Surrogate;
    }

    /// <summary>The CJK ideograph ranges BERT puts a space around.</summary>
    internal static bool IsChinese(int v) =>
        (v >= 0x4E00 && v <= 0x9FFF) || (v >= 0x3400 && v <= 0x4DBF) || (v >= 0x20000 && v <= 0x2A6DF) || (v >= 0x2A700 && v <= 0x2B73F) ||
        (v >= 0x2B740 && v <= 0x2B81F) || (v >= 0x2B820 && v <= 0x2CEAF) || (v >= 0xF900 && v <= 0xFAFF) || (v >= 0x2F800 && v <= 0x2FA1F);

    /// <summary>ASCII punctuation and symbols, and any character of a Unicode punctuation category: each is a word of its own.</summary>
    internal static bool IsPunctuation(Rune rune)
    {
        var v = rune.Value;
        if ((v >= 33 && v <= 47) || (v >= 58 && v <= 64) || (v >= 91 && v <= 96) || (v >= 123 && v <= 126)) return true;
        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation
            or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation;
    }

    /// <summary>A character that attaches to the one before it, so that the two are normalised together.</summary>
    internal static bool IsMark(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    /// <summary>A noncharacter, which <see cref="string.Normalize(NormalizationForm)"/> will not take, so it is passed through as it is.</summary>
    internal static bool IsNoncharacter(int v) => (v & 0xFFFE) == 0xFFFE || (v >= 0xFDD0 && v <= 0xFDEF);

    /// <summary>
    /// Decomposes <paramref name="cluster"/> (a character and the marks that follow it), removes the non-spacing marks and lower-cases what is left, appending the result to <paramref name="output"/>.
    /// Hugging Face does the same in this order over the whole text: decompose, strip, then lower-case.
    /// </summary>
    internal static void AppendNormalised(StringBuilder cluster, Action<Rune> output)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(output);

        var text = cluster.ToString();
        if (IsAscii(text))
        {
            foreach (var c in text) output(new Rune(char.ToLowerInvariant(c)));
            return;
        }

        var first = Rune.GetRuneAt(text, 0);
        if (IsNoncharacter(first.Value))
        {
            output(first);
            text = text[first.Utf16SequenceLength..];
            if (text.Length == 0) return;
        }

        foreach (var rune in text.Normalize(NormalizationForm.FormD).EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.NonSpacingMark) continue;
            output(Rune.ToLowerInvariant(rune));
        }
    }

    private static bool IsAscii(string text)
    {
        foreach (var c in text)
            if (c >= 0x80) return false;
        return true;
    }
}
