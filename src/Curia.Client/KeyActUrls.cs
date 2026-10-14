using System.Text;

namespace Curia.Client;

/// <summary>
/// The two key-act routes' paths and the DPoP target a proof for them names (errata G21, spec sections 4.7 and
/// 4.12). One place, so the path a request is sent to and the <c>htu</c> its proof is signed over cannot drift.
///
/// <para><b>Why the proof's target is not the sent URL.</b> The host builds the target it compares a proof's
/// <c>htu</c> with from its decoded path: every escape decoded once, left to right, except <c>%2F</c> and
/// <c>%5C</c>, which stay as written, then the framework's own re-escape of the result. An identifier is a
/// URL, so it holds slashes, and it is sent escaped (a segment cannot hold one); an identifier holding a
/// literal <c>%2F</c> is sent as <c>%252F</c>, which the host reads as <c>%2F</c> and so as the same text
/// as a sent <c>%2F</c>. The routes compare the identity they read with the token's subject (403
/// <c>curia/keys/not-own</c>), so the escaped and the double-escaped slash name different identities, and a
/// proof signed over the sent URL would fail against whichever the host reads differently.</para>
/// </summary>
internal static class KeyActUrls
{
    /// <summary>
    /// A value as one path segment: an escape for everything but the colon, which the router reads as itself.
    /// A slash is escaped, since a segment cannot hold one, and a percent sign is escaped, so a literal
    /// <c>%2F</c> goes as <c>%252F</c>.
    /// </summary>
    internal static string Segment(string value) =>
        Uri.EscapeDataString(value).Replace("%3A", ":", StringComparison.Ordinal);

    internal static string RotatePath(string agentId) => $"/v1/agents/{Segment(agentId)}/keys";

    internal static string RetirePath(string agentId, string kid) => $"{RotatePath(agentId)}/{Segment(kid)}/retire";

    /// <summary>
    /// The target a proof for <paramref name="sent"/> is signed over: the URL as the host builds it from its
    /// path, as the class summary says.
    /// </summary>
    internal static string ProofTarget(Uri sent)
    {
        ArgumentNullException.ThrowIfNull(sent);

        var path = sent.AbsolutePath;
        var bytes = new List<byte>(path.Length);
        for (var i = 0; i < path.Length;)
        {
            if (path[i] == '%' && i + 2 < path.Length && Uri.IsHexDigit(path[i + 1]) && Uri.IsHexDigit(path[i + 2]))
            {
                var hex = path.Substring(i + 1, 2);
                if (string.Equals(hex, "2F", StringComparison.OrdinalIgnoreCase) || string.Equals(hex, "5C", StringComparison.OrdinalIgnoreCase))
                    bytes.AddRange(Encoding.ASCII.GetBytes("%" + hex));
                else
                    bytes.Add(Convert.ToByte(hex, 16));

                i += 3;
            }
            else
            {
                bytes.Add((byte)path[i]); // AbsolutePath is ASCII
                i++;
            }
        }

        return HeadStore.Origin(sent) + ReEscape(Encoding.UTF8.GetString([.. bytes]));
    }

    /// <summary>
    /// The framework's re-escape of a decoded path: an escape already in it stays, a character outside the
    /// path set (unreserved, sub-delims, <c>:</c>, <c>@</c>, <c>/</c>) is escaped as its UTF-8 bytes in upper-case hex,
    /// and a <c>%</c> that does not begin an escape is escaped itself.
    /// </summary>
    private static string ReEscape(string decoded)
    {
        var escaped = new StringBuilder(decoded.Length);
        for (var i = 0; i < decoded.Length;)
        {
            var c = decoded[i];
            if (c == '%' && i + 2 < decoded.Length && Uri.IsHexDigit(decoded[i + 1]) && Uri.IsHexDigit(decoded[i + 2]))
            {
                escaped.Append(decoded, i, 3);
                i += 3;
            }
            else if (IsPathChar(c))
            {
                escaped.Append(c);
                i++;
            }
            else
            {
                Rune.DecodeFromUtf16(decoded.AsSpan(i), out var rune, out var consumed);
                foreach (var b in Encoding.UTF8.GetBytes(rune.ToString()))
                    escaped.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));

                i += consumed;
            }
        }

        return escaped.ToString();
    }

    private static bool IsPathChar(char c) =>
        c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9'
            or '-' or '.' or '_' or '~'
            or '!' or '$' or '&' or '\'' or '(' or ')' or '*' or '+' or ',' or ';' or '='
            or ':' or '@' or '/';
}
