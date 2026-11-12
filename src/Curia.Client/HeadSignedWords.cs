using System.Globalization;

namespace Curia.Client;

/// <summary>
/// Errata G67, R11.94: the words a verification past the latest signed head says about when that head was signed. Pure: the head's own signed timestamp and
/// the instant the check began in, the client's own rendering out, and never the signed string itself. Both clients (<c>curia verify</c> and <c>curia_verify</c>)
/// read the one definition. The forms are the ones R11.94 words publishes; the tests read them from the errata and never from here.
///
/// <para>The timestamp is accepted as <c>yyyy-MM-ddTHH:mm:ss</c>, zero to seven fractional digits and <c>Z</c>, and as nothing else: the tool writes six digits and
/// a stub head none, and the host parser's own bound is seven. Anything else (an offset, a lower-case <c>z</c>, a leap second, a date alone, white space, a hostile
/// string) is not a time this client can read and is stated as <c>NOT STATED</c>. The instant is rendered UTC to the millisecond, truncated and not rounded; the
/// age is computed from the full-precision value and truncated to whole seconds.</para>
/// </summary>
internal static class HeadSignedWords
{
    private const string NotStated = "signed at NOT STATED (the head's timestamp is not a time this client can read)";

    private static readonly string[] Formats =
    [
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.f'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.ff'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.ffff'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.fffff'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
    ];

    /// <summary>The <c>&lt;signed&gt;</c> clause of R11.94 words, for the head timestamp <paramref name="headTimestamp"/> and the instant the check began.</summary>
    internal static string Signed(string? headTimestamp, DateTimeOffset checkBegan)
    {
        if (!TryRead(headTimestamp, out var signed)) return NotStated;

        var ticks = signed.UtcTicks;
        var instant = new DateTime(ticks - (ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc)
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        var age = checkBegan.UtcTicks - ticks;
        if (age < 0)
            return $"signed at {instant}, after this check began (this client's clock and the signer's differ)";
        if (age < TimeSpan.TicksPerSecond)
            return $"signed at {instant}, less than a second before this check began";

        return $"signed at {instant}, {(age / TimeSpan.TicksPerSecond).ToString(CultureInfo.InvariantCulture)} s before this check began";
    }

    /// <summary>The sentence <c>That head was &lt;signed&gt;.</c> the inclusion line holds between its first sentence and its second.</summary>
    internal static string InclusionSentence(string? headTimestamp, DateTimeOffset checkBegan) =>
        "That head was " + Signed(headTimestamp, checkBegan) + ".";

    /// <summary>
    /// The closing line of a verification whose post lies past the latest head and in which no check failed: R11.94 words, whose ending past the head is
    /// <c>Another check could not be made: read the lines above.</c> where <paramref name="anotherCheckCouldNotBeMade"/> and not otherwise.
    /// </summary>
    internal static string Closing(long treeSize, string? headTimestamp, DateTimeOffset checkBegan, bool anotherCheckCouldNotBeMade) =>
        "NOT ESTABLISHED. Nothing failed: the post is newer than the latest signed head (tree size "
        + treeSize.ToString(CultureInfo.InvariantCulture)
        + ", " + Signed(headTimestamp, checkBegan)
        + "), so its inclusion could not be checked, and the key and lifecycle checks, which need it, were not run. Verify again after the operator signs a head."
        + (anotherCheckCouldNotBeMade ? " Another check could not be made: read the lines above." : string.Empty);

    private static bool TryRead(string? timestamp, out DateTimeOffset signed)
    {
        signed = default;
        if (timestamp is null || !HasShape(timestamp)) return false;

        return DateTimeOffset.TryParseExact(
            timestamp, Formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out signed);
    }

    /// <summary>
    /// <c>yyyy-MM-ddTHH:mm:ss</c>, an optional <c>.</c> and one to seven digits, and <c>Z</c>, in ASCII digits and the exact separators, so that nothing the parser's own
    /// leniency would admit (a lower-case <c>t</c> or <c>z</c>, white space, a Unicode digit) is read as a time. Written out and not a pattern: shipped code holds its patterns to
    /// the linear engine (R10.69), and this is a length and a character class.
    /// </summary>
    private static bool HasShape(string s)
    {
        if (s.Length != 20 && (s.Length < 22 || s.Length > 28)) return false;

        for (var i = 0; i < 19; i++)
        {
            var ok = i switch
            {
                4 or 7 => s[i] == '-',
                10 => s[i] == 'T',
                13 or 16 => s[i] == ':',
                _ => char.IsAsciiDigit(s[i]),
            };
            if (!ok) return false;
        }

        if (s[^1] != 'Z') return false;
        if (s.Length == 20) return s[19] == 'Z';

        return s[19] == '.' && s.AsSpan(20, s.Length - 21).IndexOfAnyExceptInRange('0', '9') < 0;
    }
}
