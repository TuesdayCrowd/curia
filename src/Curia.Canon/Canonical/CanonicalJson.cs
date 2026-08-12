using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Curia.Canon.Envelope;
using Curia.Canon.Json;
using Curia.Domain.Primitives;

namespace Curia.Canon.Canonical;

/// <summary>
/// RFC 8785 (JSON Canonicalization Scheme) canonicalization.
///
/// <see cref="Canonicalize"/> is pure JCS: it normalizes nothing, ever. It is the
/// conformance target for the vendored RFC-author vectors under conformance/rfc8785/,
/// two of which ("unicode", "weird") specifically exist to prove JCS does not touch
/// Unicode normalization — an NFD string round-trips unchanged, and U+FB33 (on
/// Unicode's composition exclusion list) is not recomposed.
///
/// <see cref="CanonicalizeWithNfc"/> is the Cūria profile: RFC 8785 plus mandatory
/// Unicode NFC normalization of every object key and string value, applied as a step
/// inside the canonicalization function (R6.9) — never as a separate pass over stored
/// content, which §6.4's no-mutation invariant forbids. Signing and verification SHALL
/// use <see cref="CanonicalizeWithNfc"/>, never the bare <see cref="Canonicalize"/>.
///
/// These are deliberately two distinct, non-overloaded names rather than one function
/// with a flag: R6.9 is irreconcilable with bare RFC 8785 conformance on adversarial
/// input (proven by the two vendored vectors above), so a caller must not be able to
/// pick the wrong semantics by accident. <see cref="CanonicalizeWithNfc"/> normalizes
/// the tree and delegates to <see cref="Canonicalize"/> — one writer, two entry points —
/// so the two can never disagree byte-for-byte on already-NFC input except by a defect.
/// </summary>
public static class CanonicalJson
{
    /// <summary>Pinned per R6.34; changes only with an envelope schema version bump.</summary>
    public const string UnicodeVersion = "16.0";

    /// <summary>
    /// Pure RFC 8785. Normalizes nothing. See the type-level remarks.
    ///
    /// Warning: performs no Unicode normalization (R6.9). Do not call this on envelope
    /// content -- <see cref="EnvelopeDocument.Root"/> being public makes
    /// <c>Canonicalize(doc.Root)</c> a one-line way to reach this function instead of
    /// <see cref="CanonicalizeEnvelope"/>, silently skipping the NFC step signing and
    /// verification depend on. <see cref="CanonicalizeEnvelope"/> is the entry point for
    /// anything that will be signed or verified.
    /// </summary>
    public static Result<CanonicalBytes> Canonicalize(JsonValue value)
    {
        var sb = new StringBuilder();
        Write(value, sb);
        return Result<CanonicalBytes>.Ok(new CanonicalBytes(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>The Cūria profile (R6.9). See the type-level remarks.</summary>
    public static Result<CanonicalBytes> CanonicalizeWithNfc(JsonValue value) =>
        Canonicalize(NormalizeToNfc(value));

    /// <summary>
    /// Canonicalizes an <see cref="EnvelopeDocument"/> for signing, verification, and
    /// digesting. Always the Cūria profile (R6.9) — an envelope is exactly the signed
    /// content R6.9 governs — never the bare RFC 8785 <see cref="Canonicalize"/>. Named
    /// distinctly rather than added as a <c>Canonicalize(EnvelopeDocument)</c> overload:
    /// an overload sharing the "Canonicalize" name would let a caller reach the NFC
    /// profile by typing the same short name used for the pure-RFC-8785 function on a
    /// plain <see cref="JsonValue"/>, reintroducing by the back door exactly the
    /// wrong-semantics-by-accident hazard <see cref="CanonicalizeWithNfc"/>'s distinct
    /// name exists to prevent (see the type-level remarks).
    /// </summary>
    public static Result<CanonicalBytes> CanonicalizeEnvelope(EnvelopeDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return CanonicalizeWithNfc(doc.Root);
    }

    /// <summary>
    /// Rebuilds the tree with every object key and string value NFC-normalized. A new
    /// tree rather than a mutation, because <see cref="JsonValue"/> is immutable and this
    /// runs on every canonicalize call rather than touching stored content (§6.4).
    /// </summary>
    private static JsonValue NormalizeToNfc(JsonValue value)
    {
        switch (value)
        {
            case JsonValue.Object o:
                return new JsonValue.Object(o.Members
                    .Select(m => new KeyValuePair<string, JsonValue>(
                        m.Key.Normalize(NormalizationForm.FormC), NormalizeToNfc(m.Value)))
                    .ToImmutableArray());
            case JsonValue.Array a:
                return new JsonValue.Array(a.Items.Select(NormalizeToNfc).ToImmutableArray());
            case JsonValue.String s:
                return new JsonValue.String(s.Value.Normalize(NormalizationForm.FormC));
            case JsonValue.Number n:
                return n;
            case JsonValue.Bool b:
                return b;
            case JsonValue.Null n:
                return n;
            default:
                // Unreachable: JsonValue is closed to this assembly (CS-11). A new case
                // added there without updating this switch fails loudly here rather than
                // silently dropping the case's content from a signed document.
                throw new ArgumentOutOfRangeException(nameof(value), value, "Unhandled JsonValue case");
        }
    }

    private static void Write(JsonValue value, StringBuilder sb)
    {
        switch (value)
        {
            case JsonValue.Object o:
                sb.Append('{');
                var ordered = o.Members
                    .OrderBy(m => m.Key, Utf16Ordinal.Comparer)
                    .ToArray();
                for (var i = 0; i < ordered.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    WriteString(ordered[i].Key, sb);
                    sb.Append(':');
                    Write(ordered[i].Value, sb);
                }
                sb.Append('}');
                break;

            case JsonValue.Array a:
                sb.Append('[');
                for (var i = 0; i < a.Items.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    Write(a.Items[i], sb);        // array order is preserved (R6.8)
                }
                sb.Append(']');
                break;

            case JsonValue.String s: WriteString(s.Value, sb); break;
            case JsonValue.Number n: sb.Append(JsonNumber.Serialize(n.Value)); break;
            case JsonValue.Bool b:   sb.Append(b.Value ? "true" : "false"); break;
            case JsonValue.Null:     sb.Append("null"); break;
        }
    }

    /// <summary>RFC 8785 §3.2.2.2 string escaping: minimal, with control characters escaped.</summary>
    private static void WriteString(string s, StringBuilder sb)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}");
                    else sb.Append(c);           // everything else literal UTF-8
                    break;
            }
        }
        sb.Append('"');
    }
}
