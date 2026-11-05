namespace Curia.Domain.Screening;

/// <summary>
/// R10.96 (errata G60): whether the password a connection-string rule matched is, whole, a reference to a secret and not a value.
/// Code over the text a pattern already matched; no <c>Regex</c>, and each question reads the matched text once, so screening stays
/// linear (R10.69). <see cref="SecretScanner.Scan"/> asks it in every view it reads (the original, the unmarked-up view and the rest),
/// and the line-joined view does not read the rules.
///
/// <para>A name is stated on what survives the deletion of its underscores, because the unmarked-up view deletes <c>_</c>:
/// <c>${A_}</c>, <c>%X_%</c>, <c>$_1X</c> and <c>{{_}}</c> are not references.</para>
/// </summary>
internal static class ReferencePassword
{
    /// <summary>Whether <paramref name="password"/>, the value a rule matched, is a reference R10.96 names, followed by nothing or closing characters only.</summary>
    internal static bool IsReference(ReadOnlySpan<char> password)
    {
        if (password.Length == 0) return false;

        switch (password[0])
        {
            case '$':
                if (password.Length > 1 && password[1] == '{')
                    return SigilName(password[2..], out var afterBrace) && afterBrace.Length > 0 && afterBrace[0] == '}' && AllClosing(afterBrace[1..]);
                return SigilName(password[1..], out var afterSigil) && AllClosing(afterSigil);

            case '%':
                return SigilName(password[1..], out var afterPercent) && afterPercent.Length > 0 && afterPercent[0] == '%' && AllClosing(afterPercent[1..]);

            case '<':
                return PlaceholderName(password[1..], out var afterAngle) && afterAngle.Length > 0 && afterAngle[0] == '>' && AllClosing(afterAngle[1..]);

            case '{':
                if (password.Length < 2 || password[1] != '{') return false;
                return PlaceholderName(password[2..], out var afterBraces) && afterBraces.Length > 1 && afterBraces[0] == '}' && afterBraces[1] == '}' && AllClosing(afterBraces[2..]);

            case '*':
                var stars = 0;
                while (stars < password.Length && password[stars] == '*') stars++;
                return stars >= 4 && AllClosing(password[stars..]);

            default:
                return false;
        }
    }

    /// <summary>
    /// Whether a URI match that ends at <paramref name="end"/> (the index after its <c>@</c>) may be cleared: no further <c>@</c> follows before the next
    /// <c>/</c>, <c>?</c>, <c>#</c> or blank (R10.96). A parser that splits the userinfo at its last <c>@</c> reads the first reference as the front of the password.
    /// </summary>
    internal static bool ClearsUri(string text, int end)
    {
        ArgumentNullException.ThrowIfNull(text);
        for (var i = end; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '@') return false;
            if (c == '/' || c == '?' || c == '#' || char.IsWhiteSpace(c)) return true;
        }
        return true;
    }

    /// <summary>
    /// The password of a URI match (<c>scheme://user:password@</c>): between the first colon after <c>://</c> and the final <c>@</c>. Both classes exclude
    /// <c>:</c>, so that colon is the only one. Empty where the text is not of that shape.
    /// </summary>
    internal static ReadOnlySpan<char> UriPassword(ReadOnlySpan<char> match)
    {
        var scheme = match.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0 || match.Length == 0 || match[^1] != '@') return default;
        var rest = match[(scheme + 3)..];
        var colon = rest.IndexOf(':');
        if (colon < 0) return default;
        return rest[(colon + 1)..^1];
    }

    /// <summary>The value after the first <c>=</c> of a keyword match and its blanks (<c>password = x</c>), or empty.</summary>
    internal static ReadOnlySpan<char> KeywordValue(ReadOnlySpan<char> match)
    {
        var equals = match.IndexOf('=');
        if (equals < 0) return default;
        var value = match[(equals + 1)..];
        var start = 0;
        while (start < value.Length && char.IsWhiteSpace(value[start])) start++;
        return value[start..];
    }

    /// <summary>R10.96, <c>pwd</c> alone: a value that begins a drive letter, a colon and <c>\</c> or <c>/</c> is a path and not a password.</summary>
    internal static bool StartsDrivePath(ReadOnlySpan<char> value) =>
        value.Length >= 3 && IsAsciiLetter(value[0]) && value[1] == ':' && (value[2] == '\\' || value[2] == '/');

    // A NAME of the sigil forms: upper-case letters, digits and underscores, a letter first once underscores are passed, at least two characters that are not underscores.
    private static bool SigilName(ReadOnlySpan<char> text, out ReadOnlySpan<char> rest)
    {
        var length = 0;
        while (length < text.Length && (IsUpper(text[length]) || IsDigit(text[length]) || text[length] == '_')) length++;
        rest = text[length..];
        return NameShape(text[..length], minimumNonUnderscore: 2, upperOnly: true);
    }

    // A name of the bracketed forms: ASCII letters, digits, underscores, hyphens and periods, a letter first once underscores are passed, at least one character that is not an underscore.
    private static bool PlaceholderName(ReadOnlySpan<char> text, out ReadOnlySpan<char> rest)
    {
        var length = 0;
        while (length < text.Length && (IsAsciiLetter(text[length]) || IsDigit(text[length]) || text[length] is '_' or '-' or '.')) length++;
        rest = text[length..];
        return NameShape(text[..length], minimumNonUnderscore: 1, upperOnly: false);
    }

    private static bool NameShape(ReadOnlySpan<char> name, int minimumNonUnderscore, bool upperOnly)
    {
        var first = 0;
        while (first < name.Length && name[first] == '_') first++;
        if (first == name.Length) return false;
        if (!(upperOnly ? IsUpper(name[first]) : IsAsciiLetter(name[first]))) return false;
        return name.Length - CountUnderscores(name) >= minimumNonUnderscore;
    }

    private static int CountUnderscores(ReadOnlySpan<char> name)
    {
        var count = 0;
        foreach (var c in name)
            if (c == '_') count++;
        return count;
    }

    // R10.96: what prose and Markdown put against a word, none of which a name holds; `*`, `~` and the backtick are the characters the unmarked-up view deletes.
    private static bool AllClosing(ReadOnlySpan<char> rest)
    {
        foreach (var c in rest)
            if (c is not ('`' or '*' or '~' or '.' or ',' or ')' or ']' or '}' or '>' or '!' or '?')) return false;
        return true;
    }

    private static bool IsUpper(char c) => c is >= 'A' and <= 'Z';
    private static bool IsAsciiLetter(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
    private static bool IsDigit(char c) => c is >= '0' and <= '9';
}
