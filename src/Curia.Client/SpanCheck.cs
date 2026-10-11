using System.Buffers;
using System.Globalization;
using System.Text;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Domain.Serving;

namespace Curia.Client;

/// <summary>
/// R10.71 (errata G19): a reference reader shows as content only what the signature it checked
/// covers.
///
/// <para><b>The span is compared, not re-rendered.</b> The Forum's marking is removed by R10.14's
/// inverse (<see cref="Datamarking.StripDatamarking"/>) under a token held to one private-use
/// character, and what remains is compared with the inner text of
/// <see cref="Datamarking.Delimit"/> over the covered bytes. Where the token goes is the Forum's
/// choice and not a normative format, so a reader that re-rendered and compared whole spans would
/// fail every post the day a Forum chose another granularity.</para>
///
/// <para><b>The covered bytes, not the served string.</b> The signature covers the served form parsed
/// under ADMIT's limits and re-canonicalized with NFC, exactly as <see cref="SignatureCheck.Verify"/>
/// computes it. A served form that is not byte-identical to those bytes is a mismatch before any span
/// is compared: an equivalent spelling is still not the bytes the verdict is about.</para>
/// </summary>
public static class SpanCheck
{
    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// The bytes the signature covers, as text: the served form parsed under
    /// <see cref="AdmitLimits.Default"/> and re-canonicalized with NFC -- the two calls
    /// <see cref="SignatureCheck.Verify"/> makes -- or <see langword="null"/> when either fails.
    /// </summary>
    public static string? CoveredForm(ProvenancePost post)
    {
        ArgumentNullException.ThrowIfNull(post);

        if (!JsonReader.Parse(Encoding.UTF8.GetBytes(post.Canonical), AdmitLimits.Default).TryGetValue(out var tree, out _)
            || !CanonicalJson.CanonicalizeWithNfc(tree!).TryGetValue(out var canonical, out _))
            return null;

        try
        {
            return Strict.GetString(canonical.Span);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the served span renders the covered bytes: the served form is those bytes, the span
    /// passed R10.63's delimiter check, a datamarking token is one private-use character, and the
    /// span's inner text with that token stripped is, ordinal UTF-16, the inner text of
    /// <see cref="Datamarking.Delimit"/> over the covered form.
    /// </summary>
    public static bool RendersCanonical(ProvenancePost post)
    {
        ArgumentNullException.ThrowIfNull(post);

        var covered = CoveredForm(post);
        if (covered is null
            || !string.Equals(covered, post.Canonical, StringComparison.Ordinal)
            || !FrameBuilder.IsDelimitedSpan(post.Rendered))
            return false;

        var inner = Inner(post.Rendered);
        if (post.Provenance.Marking == MarkingMode.Datamark)
        {
            if (!IsAcceptableToken(post.Provenance.MarkingToken)) return false;
            inner = Datamarking.StripDatamarking(inner, post.Provenance.MarkingToken!);
        }

        return string.Equals(inner, Inner(Datamarking.Delimit(covered)), StringComparison.Ordinal);
    }

    /// <summary>The text between a delimited span's delimiter lines.</summary>
    private static string Inner(string delimited)
    {
        var open = Datamarking.OpenDelimiter.Length + 1;
        var close = Datamarking.CloseDelimiter.Length + 1;
        return delimited.Substring(open, delimited.Length - open - close);
    }

    /// <summary>
    /// This client's rendering of the covered bytes, under the served marking and the served token
    /// when it is acceptable (the default otherwise); <see langword="null"/> when there are no covered
    /// bytes, so there is nothing to render as content. Never the served <c>canonical</c>: that string
    /// is the Forum's, not the signature's.
    /// </summary>
    public static string? Rendering(ProvenancePost post)
    {
        ArgumentNullException.ThrowIfNull(post);

        var covered = CoveredForm(post);
        var marking = post.Provenance.Marking;
        var token = IsAcceptableToken(post.Provenance.MarkingToken) ? post.Provenance.MarkingToken! : Datamarking.DefaultControlToken;
        return covered is null ? null : Datamarking.Render(covered, marking, token);
    }

    /// <summary>
    /// Whether <paramref name="token"/> is exactly one scalar value of category Co. Stripping trusts
    /// the token, so a Forum naming a sentence as its token, inserted before every word, would
    /// otherwise pass the comparison.
    /// </summary>
    public static bool IsAcceptableToken(string? token) =>
        token is { Length: > 0 }
        && Rune.DecodeFromUtf16(token, out var rune, out var consumed) == OperationStatus.Done
        && consumed == token.Length
        && Rune.GetUnicodeCategory(rune) == UnicodeCategory.PrivateUse;
}
