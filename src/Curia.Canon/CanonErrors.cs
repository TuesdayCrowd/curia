using Curia.Domain.Primitives;

namespace Curia.Canon;

/// <summary>RFC 9457 problem-type slugs. Every rejection names the rule it enforces.</summary>
public static class CanonErrors
{
    public static Error InvalidUtf8() => new("curia/admit/invalid-utf8", "Input is not well-formed UTF-8");
    public static Error NulByte() => new("curia/admit/nul-byte", "Input contains a raw NUL byte");
    public static Error RawControlCharacter() => new("curia/admit/raw-control-character", "Input contains an unescaped C0 control byte (other than NUL) inside a JSON string");
    public static Error UnpairedSurrogate() => new("curia/admit/unpaired-surrogate", "Input contains an unpaired surrogate");
    public static Error Noncharacter() => new("curia/admit/noncharacter", "Input contains a Unicode noncharacter (Unicode 16.0 section 23.7: permanently reserved, not for interchange)");
    public static Error DuplicateKey(string key) => new("curia/admit/duplicate-key", "Duplicate object key", key);
    public static Error DepthExceeded(int max) => new("curia/admit/depth-exceeded", "Nesting depth exceeded", $"max {max}");
    public static Error SizeExceeded(int max) => new("curia/admit/size-exceeded", "Payload too large", $"max {max} bytes");
    public static Error MembersExceeded(int max) => new("curia/admit/members-exceeded", "Too many object members", $"max {max}");
    public static Error StringTooLong(int max) => new("curia/admit/string-too-long", "String too long", $"max {max} bytes");
    public static Error NonFiniteNumber() => new("curia/admit/non-finite-number", "Number literal overflows a double to a non-finite value");
    public static Error Malformed(string detail) => new("curia/admit/malformed-json", "Malformed JSON", detail);
    public static Error NonIntegerNumber() => new("curia/admit/non-integer-number", "Numeric values must be integers (R6.33 rev. 2: applies to every number ADMIT parses, not only envelope fields)");
    public static Error UnsafeInteger() => new("curia/admit/unsafe-integer", "Integer outside the I-JSON safe range (R6.33 rev. 2: applies to every number ADMIT parses, not only envelope fields)");
    public static Error MissingEnvelope() => new("curia/admit/missing-envelope", "Submission has no envelope object");
    public static Error MissingSignature() => new("curia/admit/missing-signature", "Submission has no detached signature");

    // Curia.Canon layer (post-ADMIT canonicalization, R6.9): conditions that only exist
    // once a tree is being normalized, not visible to ADMIT's raw-wire-bytes checks.
    // `DuplicateKey` above is deliberately reused (not re-slugged) when
    // CanonicalJson.CanonicalizeWithNfc finds two byte-identical raw member names --
    // it is the same defect ADMIT's own duplicate-key check exists to catch, just
    // noticed by a caller that reached this layer without ADMIT having run first
    // (mirrors curia-testis's nfc.rs, which reuses its own admit slug for the same
    // reason: a verifier should report the same predicate for the same defect
    // regardless of which layer noticed it).
    public static Error DuplicateNormalizedKey(string key) => new(
        "curia/canon/duplicate-normalized-key",
        "Two distinct object member names normalize (NFC) to the same string",
        key);
    public static Error NormalizationFailed(string detail) => new(
        "curia/canon/normalization-failed",
        "Unicode NFC normalization failed for a string in the document (R6.9)",
        detail);
}
