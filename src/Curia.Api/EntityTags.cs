namespace Curia.Api;

/// <summary>
/// R9.11: conditional requests "keyed to the content digest". The entity tag of a served post is
/// its digest, quoted as a strong validator, and <c>If-None-Match</c> is compared the way RFC 9110
/// §13.1.2 says to -- weakly, so <c>W/"…"</c> from a cautious client library still matches.
///
/// <para><b>Why the digest and not a hash of the response.</b> The response varies with the marking
/// query and with the envelope's live fields (owner verification, verification level), but the
/// thing a citing agent is asking about is the content it cited, and that is identified by the
/// digest the author's signature covers. The marking is part of the URL, so a datamarked and an
/// unmarked read are different resources with the same tag, which is the correct HTTP shape: each
/// URL's representation is a function of the digest alone.</para>
/// </summary>
internal static class EntityTags
{
    /// <summary>The strong entity tag for a digest: the digest, quoted.</summary>
    public static string For(string digest) => "\"" + digest + "\"";

    /// <summary>
    /// Whether an <c>If-None-Match</c> header names <paramref name="digest"/>. <c>*</c> matches any
    /// current representation, per RFC 9110; a list is any-of; weak prefixes are ignored; an
    /// absent or empty header matches nothing.
    /// </summary>
    public static bool Matches(string? ifNoneMatch, string digest)
    {
        if (string.IsNullOrWhiteSpace(ifNoneMatch)) return false;

        foreach (var raw in ifNoneMatch.Split(','))
        {
            var tag = raw.Trim();
            if (tag == "*") return true;

            if (tag.StartsWith("W/", StringComparison.Ordinal)) tag = tag[2..];
            if (tag.Length >= 2 && tag[0] == '"' && tag[^1] == '"') tag = tag[1..^1];

            if (string.Equals(tag, digest, StringComparison.Ordinal)) return true;
        }

        return false;
    }
}
