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
    public static Result<CanonicalBytes> CanonicalizeWithNfc(JsonValue value)
    {
        var normalized = NormalizeToNfc(value);
        return normalized.TryGetValue(out var tree, out var error)
            ? Canonicalize(tree)
            : Result<CanonicalBytes>.Fail(error!);
    }

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
    ///
    /// Fallible for two reasons, both caught here because this is where the normalized
    /// tree is built -- the only place either condition first exists to detect:
    ///
    /// <list type="bullet">
    /// <item>Normalizing an object's member names can make two distinct raw wire keys
    /// equal (e.g. precomposed "café" vs. "cafe" + combining acute, U+0301) --
    /// <see cref="NormalizeObject"/> rejects the collision rather than silently emitting
    /// a canonical object with two members sharing one key, which would not be valid
    /// I-JSON and would let two distinct wire documents share one canonical digest and
    /// signature (a non-repudiation defect).</item>
    /// <item><c>string.Normalize(NormalizationForm.FormC)</c> throws
    /// <see cref="ArgumentException"/> on some inputs .NET's ICU-backed implementation
    /// treats as invalid code points (observed for U+FFFE, a Unicode noncharacter read
    /// as a reversed byte-order mark). ADMIT rejects noncharacters before a
    /// <see cref="JsonValue"/> exists to reach this function on any real call path, but
    /// CS-10 requires domain fallibility to be a value even so -- see
    /// <see cref="NormalizeString"/>.</item>
    /// </list>
    /// </summary>
    private static Result<JsonValue> NormalizeToNfc(JsonValue value)
    {
        switch (value)
        {
            case JsonValue.Object o:
                return NormalizeObject(o);
            case JsonValue.Array a:
                return NormalizeArray(a);
            case JsonValue.String s:
                return NormalizeString(s.Value).Map(n => (JsonValue)new JsonValue.String(n));
            case JsonValue.Number n:
                return Result<JsonValue>.Ok(n);
            case JsonValue.Bool b:
                return Result<JsonValue>.Ok(b);
            case JsonValue.Null n:
                return Result<JsonValue>.Ok(n);
            default:
                // Unreachable: JsonValue is closed to this assembly (CS-11). A new case
                // added there without updating this switch fails loudly here rather than
                // silently dropping the case's content from a signed document.
                throw new ArgumentOutOfRangeException(nameof(value), value, "Unhandled JsonValue case");
        }
    }

    /// <summary>
    /// Normalizes one object's own member list. Two linear passes over the *same*
    /// member list, not one combined scan, so the outcome is independent of member
    /// order (mirrors curia-testis's <c>nfc.rs</c>, whose own fix history records why a
    /// single combined pass is wrong: it makes the reported slug depend on which
    /// collision the scan happens to reach first, and the corpus pins exact slugs).
    ///
    /// Pass 1 rejects a raw, byte-identical duplicate member name with the same
    /// <c>curia/admit/duplicate-key</c> predicate ADMIT itself uses -- this is the
    /// identical defect, just noticed by a caller that reached this function without
    /// ADMIT having run first, and a verifier should report the same slug for the same
    /// defect regardless of which layer noticed it. Pass 1 runs to completion, over
    /// every member, before pass 2 computes a single normalized name, which is what
    /// makes a raw duplicate always win over an NFC-created collision in the same
    /// object -- regardless of which pair appears earlier -- rather than whichever
    /// defect the scan happens to reach first.
    ///
    /// Pass 2 normalizes every remaining (by definition raw-unique) member name and
    /// value, rejecting with the distinct <c>curia/canon/duplicate-normalized-key</c>
    /// predicate when two raw-distinct names normalize to the same string. The check is
    /// scoped to this one object's member list, not the whole document: RFC 8785
    /// §3.2.3 ordering and duplicate-freedom are properties of one member list, so two
    /// equal normalized names in different objects (siblings or otherwise) are fine.
    /// </summary>
    private static Result<JsonValue> NormalizeObject(JsonValue.Object o)
    {
        var rawSeen = new HashSet<string>(o.Members.Length, StringComparer.Ordinal);
        foreach (var member in o.Members)
        {
            if (!rawSeen.Add(member.Key))
                return Result<JsonValue>.Fail(CanonErrors.DuplicateKey(member.Key));
        }

        var members = ImmutableArray.CreateBuilder<KeyValuePair<string, JsonValue>>(o.Members.Length);
        var normalizedSeen = new HashSet<string>(o.Members.Length, StringComparer.Ordinal);
        foreach (var member in o.Members)
        {
            var keyResult = NormalizeString(member.Key);
            if (!keyResult.TryGetValue(out var normalizedKey, out var keyError))
                return Result<JsonValue>.Fail(keyError!);

            if (!normalizedSeen.Add(normalizedKey))
                return Result<JsonValue>.Fail(CanonErrors.DuplicateNormalizedKey(normalizedKey));

            var valueResult = NormalizeToNfc(member.Value);
            if (!valueResult.TryGetValue(out var normalizedValue, out var valueError))
                return Result<JsonValue>.Fail(valueError!);

            members.Add(new KeyValuePair<string, JsonValue>(normalizedKey, normalizedValue));
        }

        return Result<JsonValue>.Ok(new JsonValue.Object(members.MoveToImmutable()));
    }

    /// <summary>Normalizes every element of an array; order is preserved (R6.8).</summary>
    private static Result<JsonValue> NormalizeArray(JsonValue.Array a)
    {
        var items = ImmutableArray.CreateBuilder<JsonValue>(a.Items.Length);
        foreach (var item in a.Items)
        {
            var itemResult = NormalizeToNfc(item);
            if (!itemResult.TryGetValue(out var normalizedItem, out var error))
                return Result<JsonValue>.Fail(error!);
            items.Add(normalizedItem);
        }

        return Result<JsonValue>.Ok(new JsonValue.Array(items.MoveToImmutable()));
    }

    /// <summary>
    /// NFC-normalizes one string, catching the <see cref="ArgumentException"/>
    /// <c>string.Normalize(NormalizationForm.FormC)</c> throws on some inputs (CS-10:
    /// domain fallibility must be a value, never an unhandled exception). ADMIT rejects
    /// the one input class this is known to be reachable on (Unicode noncharacters --
    /// see the caller's remarks) before wire content ever becomes a
    /// <see cref="JsonValue"/>, but this function's contract does not get to assume its
    /// caller ran ADMIT first, so the catch stays regardless.
    /// </summary>
    private static Result<string> NormalizeString(string s)
    {
        try
        {
            return Result<string>.Ok(s.Normalize(NormalizationForm.FormC));
        }
        catch (ArgumentException ex)
        {
            return Result<string>.Fail(CanonErrors.NormalizationFailed(ex.Message));
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
