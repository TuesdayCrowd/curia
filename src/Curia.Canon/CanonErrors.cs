using Curia.Domain.Primitives;

namespace Curia.Canon;

/// <summary>RFC 9457 problem-type slugs. Every rejection names the rule it enforces.</summary>
public static class CanonErrors
{
    public static Error InvalidUtf8() => new("curia/admit/invalid-utf8", "Input is not well-formed UTF-8");
    public static Error NulByte() => new("curia/admit/nul-byte", "Input contains a raw NUL byte");
    public static Error UnpairedSurrogate() => new("curia/admit/unpaired-surrogate", "Input contains an unpaired surrogate");
    public static Error Noncharacter() => new("curia/admit/noncharacter", "Input contains a Unicode noncharacter (Unicode 16.0 section 23.7: permanently reserved, not for interchange)");
    public static Error DuplicateKey(string key) => new("curia/admit/duplicate-key", "Duplicate object key", key);
    public static Error DepthExceeded(int max) => new("curia/admit/depth-exceeded", "Nesting depth exceeded", $"max {max}");
    public static Error SizeExceeded(int max) => new("curia/admit/size-exceeded", "Payload too large", $"max {max} bytes");
    public static Error MembersExceeded(int max) => new("curia/admit/members-exceeded", "Too many object members", $"max {max}");
    public static Error StringTooLong(int max) => new("curia/admit/string-too-long", "String too long", $"max {max} bytes");
    public static Error NonFiniteNumber() => new("curia/admit/non-finite-number", "Number literal overflows a double to a non-finite value");
    public static Error Malformed(string detail) => new("curia/admit/malformed", "Malformed JSON", detail);
    public static Error NonIntegerNumber() => new("curia/admit/non-integer-number", "Envelope numerics must be integers (R6.33)");
    public static Error UnsafeInteger() => new("curia/admit/unsafe-integer", "Integer outside the I-JSON safe range (R6.33)");
    public static Error MissingEnvelope() => new("curia/admit/missing-envelope", "Submission has no envelope object");
    public static Error MissingSignature() => new("curia/admit/missing-signature", "Submission has no detached signature");
}
