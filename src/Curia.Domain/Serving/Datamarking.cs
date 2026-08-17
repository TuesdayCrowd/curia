using System.Text;

namespace Curia.Domain.Serving;

/// <summary>
/// R10.12's datamarking and R10.19's delimiting: the two output transformations, applied at the
/// serving boundary and <b>never written back</b>.
///
/// <para><b>Why this type takes and returns strings and touches no store.</b> R6.12 requires the
/// persisted bytes to be byte-identical to the verified ones. Every function here is a pure
/// transformation of a string a caller already holds, with no reference to a repository, an event, or
/// a canonical form. There is no expression in this API that could write anything anywhere -- the
/// invariant is kept by the type's shape rather than by remembering not to.</para>
///
/// <para><b>R10.14 and R10.19 both hinge on escaping.</b> "SHALL be escaped if it occurs within the
/// content itself" -- because content that contains the control token can otherwise forge the
/// boundary between marked and unmarked spans, or make its own text appear to be the Forum's. This is
/// the same discipline as parameterized SQL, and R10.19 says so explicitly; it fails the same way
/// when skipped, which is to say silently and in the attacker's favour.</para>
/// </summary>
public static class Datamarking
{
    /// <summary>
    /// The default control token: U+E000, the first Unicode private-use code point.
    ///
    /// <para>Private-use is the right neighbourhood: no legitimate text assigns it meaning, so its
    /// presence in content is itself anomalous, and it survives UTF-8 round-trips that would mangle a
    /// control character. R10.14 makes it configurable because a model that has learned to ignore one
    /// token has not learned to ignore another.</para>
    /// </summary>
    public const string DefaultControlToken = "";

    /// <summary>The delimiters R10.19 requires for text renderings, escaped if they occur in content.</summary>
    public const string OpenDelimiter = "<<<CURIA-UNTRUSTED-BEGIN>>>";

    public const string CloseDelimiter = "<<<CURIA-UNTRUSTED-END>>>";

    /// <summary>
    /// Interleaves <paramref name="controlToken"/> through the content, escaping any occurrence of it
    /// that was already there.
    ///
    /// <para>Interleaved per <i>whitespace-separated word</i> rather than per character. Per character
    /// multiplies the token count by the content length for no additional signal -- the marking exists
    /// so a model can see which span is data, and word granularity conveys that at a fraction of the
    /// tokens. Per line would be too coarse: a single-line injection would carry one marker and read
    /// as almost unmarked.</para>
    /// </summary>
    public static string Datamark(string content, string controlToken = DefaultControlToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(controlToken);

        // R10.14: escape the token if the content already contains it. Doubling it is the escape --
        // a reader stripping the marking collapses doubles back to singles, and content that tried
        // to inject a bare token cannot make one appear.
        var escaped = content.Replace(controlToken, controlToken + controlToken, StringComparison.Ordinal);

        var builder = new StringBuilder(escaped.Length + (escaped.Length / 4));
        var atWordStart = true;

        foreach (var c in escaped)
        {
            if (char.IsWhiteSpace(c))
            {
                builder.Append(c);
                atWordStart = true;
                continue;
            }

            if (atWordStart)
            {
                builder.Append(controlToken);
                atWordStart = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// R10.19: wraps content in delimiters, escaping any occurrence of either delimiter inside it.
    ///
    /// <para>Content that contains the closing delimiter could otherwise make the untrusted span
    /// appear to end early, so that everything after it reads as the Forum's own words. Escaping is
    /// what stops that, and it is exactly the SQL-injection shape R10.19 names.</para>
    /// </summary>
    public static string Delimit(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var escaped = content
            .Replace(OpenDelimiter, EscapeOf(OpenDelimiter), StringComparison.Ordinal)
            .Replace(CloseDelimiter, EscapeOf(CloseDelimiter), StringComparison.Ordinal);

        return $"{OpenDelimiter}\n{escaped}\n{CloseDelimiter}";
    }

    /// <summary>
    /// Applies the marking a reader asked for, and the delimiting R10.19 requires regardless.
    ///
    /// <para>Delimiting is not optional at any marking level. R10.18 makes the envelope
    /// "structurally inseparable from the content in every representation, including plain-text and
    /// Markdown renderings" -- and in a text rendering the delimiters *are* that structure. A caller
    /// choosing <see cref="MarkingMode.None"/> is choosing not to interleave a token, not choosing to
    /// receive an unmarked blob.</para>
    /// </summary>
    public static string Render(string content, MarkingMode mode, string controlToken = DefaultControlToken) =>
        mode switch
        {
            MarkingMode.Datamark => Delimit(Datamark(content, controlToken)),
            MarkingMode.DelimitersOnly => Delimit(content),
            MarkingMode.None => Delimit(content),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Not a marking mode"),
        };

    /// <summary>
    /// Removes datamarking, for a client stripping it after its model has consumed the marked form
    /// (R10.14).
    ///
    /// <para>Collapses doubled tokens back to singles first-class rather than as a special case: a
    /// stripper that removed every token would delete content that legitimately contained one, which
    /// is the escaping bug in reverse.</para>
    /// </summary>
    public static string StripDatamarking(string marked, string controlToken = DefaultControlToken)
    {
        ArgumentNullException.ThrowIfNull(marked);
        ArgumentException.ThrowIfNullOrEmpty(controlToken);

        var builder = new StringBuilder(marked.Length);
        var i = 0;

        while (i < marked.Length)
        {
            if (Matches(marked, i, controlToken))
            {
                // A doubled token was an escaped literal: emit one and skip both.
                if (Matches(marked, i + controlToken.Length, controlToken))
                {
                    builder.Append(controlToken);
                    i += controlToken.Length * 2;
                    continue;
                }

                i += controlToken.Length;
                continue;
            }

            builder.Append(marked[i]);
            i++;
        }

        return builder.ToString();
    }

    private static bool Matches(string text, int at, string token) =>
        at + token.Length <= text.Length
        && text.AsSpan(at, token.Length).SequenceEqual(token);

    /// <summary>
    /// The escaped form of a delimiter: the same string with a zero-width-space-free marker inserted,
    /// so it is visibly not the delimiter and cannot be collapsed back into it by trimming.
    /// </summary>
    private static string EscapeOf(string delimiter) =>
        delimiter.Replace(">>>", "-ESCAPED>>>", StringComparison.Ordinal);
}
