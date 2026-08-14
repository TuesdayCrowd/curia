using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Unicode;          // Utf8.IsValid
using Curia.Domain.Primitives;

namespace Curia.Canon.Json;

/// <summary>Caps frozen by R15.1. See spec §5.1.</summary>
public sealed record AdmitLimits(int MaxBytes, int MaxDepth, int MaxMembersPerObject, int MaxStringBytes)
{
    public static readonly AdmitLimits Default = new(
        MaxBytes: 1_048_576,
        MaxDepth: 32,
        MaxMembersPerObject: 1_024,
        MaxStringBytes: 262_144);
}

/// <summary>
/// Two parse paths from bytes to a <see cref="JsonValue"/>, sharing one Utf8JsonReader walk
/// (<see cref="ParseCore"/>) but distinct at the public API surface (R6.41):
///
/// <list type="bullet">
/// <item><see cref="Parse"/> is ADMIT phase ① (§6.4): reject or pass, never repair. Enforces
/// R6.39's four size-shaped caps (nesting depth, member count, submission size, string
/// length), Unicode noncharacter rejection, and R6.33 (rev. 2)'s numeric bound -- every
/// number ADMIT parses, at any depth, in any document (errata E4).</item>
/// <item><see cref="ParseUnrestricted"/> is R6.41: no ADMIT policy cap. Canonicalization
/// uses this path -- see its own remarks for why a document ADMIT would refuse can still
/// need a canonical form.</item>
/// </list>
///
/// A hand-rolled Utf8JsonReader walk rather than JsonSerializer.Deserialize either way,
/// because duplicate-key rejection and (on the ADMIT side) the size and depth caps must
/// apply before any object exists.
/// </summary>
public static class JsonReader
{
    /// <summary>
    /// 2^53 - 1: the largest integer an IEEE-754 double represents exactly. R6.33 (rev.)'s
    /// explicit symmetric bound; R6.33 (rev. 2) makes this ADMIT-generic -- every number
    /// ADMIT parses, at any depth, in any document, not only fields that will become part
    /// of an envelope's signed schema (errata E4). Never enforced by
    /// <see cref="ParseUnrestricted"/>: RFC 8785 defines a canonical output for 4.5, 1e+30,
    /// and 123456789012345680000 alike, and moving this check into a parse path
    /// canonicalization depends on breaks the RFC author's own conformance vectors and four
    /// of the numbers/ vectors -- see errata E4's "Prerequisite, demonstrated" and R6.41.
    /// </summary>
    private const long SafeMaxInteger = 9_007_199_254_740_991; // 2^53 - 1

    public static Result<JsonValue> Parse(ReadOnlySpan<byte> utf8, AdmitLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        if (utf8.Length > limits.MaxBytes)
            return Result<JsonValue>.Fail(CanonErrors.SizeExceeded(limits.MaxBytes));

        return ParseCore(utf8, Policy.Admit(limits));
    }

    /// <summary>
    /// R6.41: "An implementation SHALL provide a path from input bytes to a parsed document
    /// that applies no ADMIT policy cap, distinct from the ADMIT phase itself. Canonicalization
    /// SHALL use that path." This is that path -- the conformance suite (and the differential
    /// harness in tools/Curia.Differential) call it to build the <see cref="JsonValue"/> tree
    /// fed to <c>CanonicalJson.Canonicalize</c>/<c>CanonicalizeWithNfc</c>, because RFC 8785
    /// defines a canonical form for documents R6.39's caps or R6.33's numeric bound would
    /// refuse: the RFC author's own rfc8785/input-values.json (4.5, 0.002, 1e-27, 1e+30) and
    /// four of the numbers/ vectors (large-exact-expansion's 123456789012345680000 among
    /// them) are exactly such documents. Attempting to route canonicalization through
    /// <see cref="Parse"/> instead was tried and measured -- it fails those five vectors --
    /// before this method existed; see errata E4's "Prerequisite, demonstrated" and E2.
    ///
    /// "No policy cap" is deliberately narrower than "no checks at all" (R6.38, errata E2):
    /// RFC 8785 defines no canonical output for invalid UTF-8, an unpaired UTF-16 surrogate,
    /// a raw duplicate object member name, or a number literal that overflows a double to a
    /// non-finite value -- these are well-definedness violations, not policy, and this path
    /// rejects all four exactly as <see cref="Parse"/> does, with the identical slug (a
    /// verifier should report the same predicate for the same defect regardless of which
    /// entry point noticed it -- the same reasoning errata E1 gives for
    /// <c>CanonicalJson</c> reusing <c>curia/admit/duplicate-key</c>). Only R6.39's four
    /// size-shaped caps, Unicode noncharacters (a valid Unicode scalar value ADMIT excludes
    /// as policy, not one RFC 8785 or NFC leaves undefined -- Unicode §23.7), and R6.33's
    /// numeric bound are skipped.
    ///
    /// No <see cref="AdmitLimits"/> parameter exists on this method's signature, deliberately.
    /// R6.41 asks for a path "distinct from the ADMIT phase itself", and that distinction has
    /// to be visible at every call site, not only in an argument value a caller could get
    /// wrong. Passing some maximally-permissive <see cref="AdmitLimits"/> to <see cref="Parse"/>
    /// would satisfy the letter of "unrestricted" while leaving every call site looking
    /// identical to an ADMIT call -- the two concepts would differ only in an argument's
    /// runtime value, invisible to a reviewer or the compiler, not in a type. A second,
    /// distinctly named, distinctly shaped method makes the choice a call site makes visible
    /// by inspection (grep the method name) instead, the same posture CS-15 takes for the
    /// event store's write surface being restricted to <c>Persist</c>'s adapter alone.
    ///
    /// One structural bound still applies: Utf8JsonReader's own default MaxDepth (64), left
    /// unset below rather than derived from any <see cref="AdmitLimits"/>. That is not one of
    /// R6.39's caps (32) -- it is an implementation reality every practical JSON parser has,
    /// because unbounded recursion through this method's own Utf8JsonReader walk is a real
    /// stack-overflow hazard, not a Cūria policy choice, and 64 is comfortably above anything
    /// the published conformance corpus or R6.38's own example (a 33-level document) needs to
    /// canonicalize successfully. A document deeper than 64 levels fails here with
    /// <c>curia/admit/malformed-json</c> (Utf8JsonReader's own exception, caught below) rather than
    /// a dedicated slug; no requirement or vector asks for one.
    /// </summary>
    public static Result<JsonValue> ParseUnrestricted(ReadOnlySpan<byte> utf8) =>
        ParseCore(utf8, Policy.Unrestricted);

    private static Result<JsonValue> ParseCore(ReadOnlySpan<byte> utf8, Policy policy)
    {
        if (utf8.IndexOf((byte)0) >= 0)
            return Result<JsonValue>.Fail(CanonErrors.NulByte());

        if (Utf8.IsValid(utf8) is false)
            return Result<JsonValue>.Fail(CanonErrors.InvalidUtf8());

        var options = new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            // Deliberately above policy.Caps?.MaxDepth under ADMIT (see ReadValue's remarks):
            // our own depth: parameter in ReadValue is the sole authority for
            // curia/admit/depth-exceeded, not the reader's own counter -- if this were set to
            // exactly caps.MaxDepth, the reader's own internal counter would trip first on
            // every legitimate depth-cap violation, forcing the catch clause below to recover
            // the specific slug by sniffing the exception message, which is unreliable (see
            // that comment for the full reasoning). Under ParseUnrestricted, policy.Caps is
            // null and this is left at Utf8JsonReader's own default (64) -- see
            // ParseUnrestricted's own remarks for why the platform default, not a Cūria policy
            // value, is the right bound for a path R6.41 requires to carry none.
            MaxDepth = policy.Caps is { } admitCaps ? admitCaps.MaxDepth + 2 : 0,
        };

        var reader = new Utf8JsonReader(utf8, options);
        try
        {
            if (!reader.Read())
                return Result<JsonValue>.Fail(CanonErrors.Malformed("empty input"));

            var result = ReadValue(ref reader, policy, depth: 1);
            if (!result.IsOk)
                return result;

            return reader.Read()
                ? Result<JsonValue>.Fail(CanonErrors.Malformed("trailing content after top-level value"))
                : result;
        }
        catch (JsonException ex)
        {
            // With the ADMIT-side headroom, our own depth check always wins the race for a
            // real depth-cap violation, so under Parse, anything that reaches here is a
            // genuine structural failure (truncated input, invalid syntax) rather than the
            // depth cap -- no substring inspection needed or wanted. Under ParseUnrestricted,
            // this is also where a document deeper than Utf8JsonReader's own 64-level default
            // surfaces (see that method's remarks).
            return Result<JsonValue>.Fail(CanonErrors.Malformed(ex.Message));
        }
    }

    private static Result<JsonValue> ReadValue(ref Utf8JsonReader reader, Policy policy, int depth)
    {
        // Under ADMIT (policy.Caps set), this switch is the sole authority for
        // curia/admit/depth-exceeded -- see ParseCore's MaxDepth comment for why the reader's
        // own JsonReaderOptions.MaxDepth is set above policy.Caps.MaxDepth rather than equal to
        // it. Under ParseUnrestricted (policy.Caps null), no depth check ever fires here; R6.39's
        // 32-container cap is exactly the policy R6.41 says this path must not re-enforce.
        //
        // The check applies only to containers (StartObject/StartArray), never to leaf
        // values: depth counts levels of nesting, and a leaf sitting inside the
        // policy.Caps.MaxDepth-th container is not itself an additional level of nesting.
        // Applying the check uniformly to every token (the earlier, buggy shape of this
        // method) rejected a scalar nested inside exactly policy.Caps.MaxDepth containers --
        // checked at depth policy.Caps.MaxDepth + 1 -- making the effective accepted maximum
        // policy.Caps.MaxDepth - 1 levels of content instead of policy.Caps.MaxDepth. See
        // conformance/admit-reject/over-nested/meta.json: "33 levels exceeds the depth cap of
        // 32" -- 32 must be accepted, 33 rejected.
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                return policy.Caps is { } capsO && depth > capsO.MaxDepth
                    ? Result<JsonValue>.Fail(CanonErrors.DepthExceeded(capsO.MaxDepth))
                    : ReadObject(ref reader, policy, depth);
            case JsonTokenType.StartArray:
                return policy.Caps is { } capsA && depth > capsA.MaxDepth
                    ? Result<JsonValue>.Fail(CanonErrors.DepthExceeded(capsA.MaxDepth))
                    : ReadArray(ref reader, policy, depth);
            case JsonTokenType.String: return ReadString(ref reader, policy);
            case JsonTokenType.Number: return ReadNumber(ref reader, policy);
            case JsonTokenType.True: return Result<JsonValue>.Ok(new JsonValue.Bool(true));
            case JsonTokenType.False: return Result<JsonValue>.Ok(new JsonValue.Bool(false));
            case JsonTokenType.Null: return Result<JsonValue>.Ok(JsonValue.Null.Instance);
            default: return Result<JsonValue>.Fail(CanonErrors.Malformed($"unexpected token {reader.TokenType}"));
        }
    }

    /// <summary>
    /// A syntactically valid number literal can still overflow a double: Utf8JsonReader.
    /// GetDouble() returns +/-Infinity for e.g. <c>1e400</c> rather than throwing, so
    /// without this check that value would become a <see cref="JsonValue.Number"/> and
    /// CanonicalJson.Canonicalize would go on to emit the literal <c>Infinity</c> -- not
    /// valid JSON. Rejecting the whole non-finite class -- under both <see cref="Parse"/>
    /// and <see cref="ParseUnrestricted"/>, unconditionally -- is a well-definedness rule,
    /// not policy (R6.38): RFC 8785 defines no canonical output for a value that does not
    /// survive being read as a double in the first place. Underflow is unaffected and
    /// deliberately so -- a literal too small to represent (e.g. <c>1e-400</c>) rounds to
    /// positive zero, an entirely ordinary finite double, and remains accepted.
    ///
    /// R6.33 (rev. 2)'s integer-and-safe-range bound, by contrast, IS policy (errata E4) and
    /// applies only when <paramref name="policy"/>.EnforceNumericBound is set -- i.e. only
    /// under <see cref="Parse"/>. See <see cref="SafeMaxInteger"/>'s remarks.
    /// </summary>
    private static Result<JsonValue> ReadNumber(ref Utf8JsonReader reader, Policy policy)
    {
        var value = reader.GetDouble();
        if (!double.IsFinite(value))
            return Result<JsonValue>.Fail(CanonErrors.NonFiniteNumber());

        if (policy.EnforceNumericBound)
        {
            if (!double.IsInteger(value))
                return Result<JsonValue>.Fail(CanonErrors.NonIntegerNumber());
            if (Math.Abs(value) > SafeMaxInteger)
                return Result<JsonValue>.Fail(CanonErrors.UnsafeInteger());
        }

        return Result<JsonValue>.Ok(new JsonValue.Number(value));
    }

    private static Result<JsonValue> ReadString(ref Utf8JsonReader reader, Policy policy)
    {
        if (policy.Caps is { } caps && reader.ValueSpan.Length > caps.MaxStringBytes)
            return Result<JsonValue>.Fail(CanonErrors.StringTooLong(caps.MaxStringBytes));

        var value = ReadStringValue(ref reader, policy.RejectNoncharacters);
        return value.IsOk
            ? Result<JsonValue>.Ok(new JsonValue.String(value.Match(v => v, _ => "")))
            : value.ToFailure<JsonValue>();
    }

    /// <summary>
    /// Wraps Utf8JsonReader.GetString(); callers must only invoke this when TokenType is
    /// String or PropertyName. Contrary to a common assumption, GetString() does not
    /// substitute U+FFFD for a \uXXXX escape that decodes to an unpaired surrogate — it
    /// throws InvalidOperationException instead, before this code ever sees a string to
    /// inspect. That failure is mapped explicitly to the specific slug (R6.15) rather than
    /// left to surface as an unhandled exception or collapse into a generic "malformed" --
    /// unconditionally, under both <see cref="Parse"/> and <see cref="ParseUnrestricted"/>:
    /// an unpaired surrogate is well-definedness (R6.38), not policy.
    ///
    /// Also rejects any Unicode noncharacter (see <see cref="IsNoncharacter"/>) found in the
    /// decoded string, but only when <paramref name="rejectNoncharacters"/> is set -- R6.38
    /// names noncharacters as policy ADMIT alone enforces, so <see cref="ParseUnrestricted"/>
    /// passes <c>false</c> here. This is the single call site for both object property names
    /// and string values (see ReadObject/ReadString), so whichever rule applies, applies
    /// uniformly to both.
    ///
    /// Enforcing the noncharacter rule at ADMIT, rather than downstream in canonicalization,
    /// originally mattered concretely for a different reason: a real .NET bug means
    /// <c>string.Normalize(NormalizationForm.FormC)</c> throws ArgumentException on U+FFFE
    /// specifically (ICU reads it as a reversed byte-order mark), so before this check
    /// existed, an admitted document carrying U+FFFE reached CanonicalizeWithNfc and crashed
    /// instead of returning a Result.Fail (CS-10). R6.38 later required CanonicalizeWithNfc
    /// itself to accept and correctly canonicalize a noncharacter when a caller reaches it
    /// without ADMIT having run -- exactly the case <see cref="ParseUnrestricted"/> now
    /// deliberately allows -- so <c>CanonicalJson.NormalizeString</c> was hardened a second
    /// time: it now works around the platform defect and succeeds on U+FFFE, rather than
    /// merely catching the exception and returning curia/canon/normalization-failed instead
    /// of crashing. Bypassing ADMIT no longer risks a crash (CS-10) or a spurious rejection
    /// (R6.38) — both are independently guaranteed regardless of which layer noticed the
    /// noncharacter first.
    /// </summary>
    private static Result<string> ReadStringValue(ref Utf8JsonReader reader, bool rejectNoncharacters)
    {
        string value;
        try
        {
            value = reader.GetString()!;
        }
        catch (InvalidOperationException)
        {
            return Result<string>.Fail(CanonErrors.UnpairedSurrogate());
        }

        if (rejectNoncharacters)
        {
            foreach (var rune in value.EnumerateRunes())
            {
                if (IsNoncharacter(rune.Value))
                    return Result<string>.Fail(CanonErrors.Noncharacter());
            }
        }

        return Result<string>.Ok(value);
    }

    /// <summary>
    /// True when <paramref name="codePoint"/> is one of the 66 Unicode noncharacters
    /// (Unicode 16.0 §23.7): U+FDD0-U+FDEF, or the last two code points of any plane
    /// (U+FFFE/U+FFFF through U+10FFFE/U+10FFFF). <c>codePoint &amp; 0xFFFE == 0xFFFE</c>
    /// catches both "...FFFE" and "...FFFF" endings for any plane in one comparison, since
    /// masking off the low bit of 0xFFFF also yields 0xFFFE. Internal rather than private:
    /// Curia.Canon.Tests (InternalsVisibleTo) reuses this exact rule in the property suite's
    /// generators, which construct JsonValue trees directly and so must mirror ADMIT's own
    /// rejection rules by hand rather than diverging with a second, hand-rolled definition.
    /// </summary>
    internal static bool IsNoncharacter(int codePoint) =>
        (codePoint is >= 0xFDD0 and <= 0xFDEF) || (codePoint & 0xFFFE) == 0xFFFE;

    private static Result<JsonValue> ReadObject(ref Utf8JsonReader reader, Policy policy, int depth)
    {
        var members = ImmutableArray.CreateBuilder<KeyValuePair<string, JsonValue>>();
        var keys = new HashSet<string>(StringComparer.Ordinal);

        while (true)
        {
            if (!reader.Read())
                return Result<JsonValue>.Fail(CanonErrors.Malformed("truncated object"));

            if (reader.TokenType == JsonTokenType.EndObject)
                return Result<JsonValue>.Ok(new JsonValue.Object(members.ToImmutable()));

            if (reader.TokenType != JsonTokenType.PropertyName)
                return Result<JsonValue>.Fail(CanonErrors.Malformed($"expected property name, saw {reader.TokenType}"));

            var keyResult = ReadStringValue(ref reader, policy.RejectNoncharacters);
            if (!keyResult.IsOk)
                return keyResult.ToFailure<JsonValue>();

            var key = keyResult.Match(v => v, _ => "");

            // Raw duplicate member names are well-definedness (R6.38), not policy: rejected
            // unconditionally, under both Parse and ParseUnrestricted, with the identical
            // curia/admit/duplicate-key slug either way (see this type's own remarks).
            if (!keys.Add(key))
                return Result<JsonValue>.Fail(CanonErrors.DuplicateKey(key));

            if (policy.Caps is { } caps && members.Count + 1 > caps.MaxMembersPerObject)
                return Result<JsonValue>.Fail(CanonErrors.MembersExceeded(caps.MaxMembersPerObject));

            if (!reader.Read())
                return Result<JsonValue>.Fail(CanonErrors.Malformed("truncated member value"));

            var value = ReadValue(ref reader, policy, depth + 1);
            if (!value.IsOk)
                return value;

            members.Add(new KeyValuePair<string, JsonValue>(key, value.Match(v => v, _ => JsonValue.Null.Instance)));
        }
    }

    private static Result<JsonValue> ReadArray(ref Utf8JsonReader reader, Policy policy, int depth)
    {
        var items = ImmutableArray.CreateBuilder<JsonValue>();
        while (true)
        {
            if (!reader.Read())
                return Result<JsonValue>.Fail(CanonErrors.Malformed("truncated array"));

            if (reader.TokenType == JsonTokenType.EndArray)
                return Result<JsonValue>.Ok(new JsonValue.Array(items.ToImmutable()));

            var value = ReadValue(ref reader, policy, depth + 1);
            if (!value.IsOk)
                return value;

            items.Add(value.Match(v => v, _ => JsonValue.Null.Instance));
        }
    }

    /// <summary>
    /// Which checks a single ParseCore walk enforces. Deliberately private and never
    /// exposed: the R6.41 seam that must be visible in the type system lives at the public
    /// API surface (<see cref="Parse"/> vs <see cref="ParseUnrestricted"/> are two distinctly
    /// named, distinctly shaped static methods -- see ParseUnrestricted's remarks), not in
    /// this struct's field values. This type exists only so both public entry points can
    /// share one Utf8JsonReader walk (and its delicate depth/exception-handling behavior)
    /// instead of maintaining two near-duplicate copies of <see cref="ReadValue"/> and its
    /// callees.
    /// </summary>
    private readonly record struct Policy(AdmitLimits? Caps, bool RejectNoncharacters, bool EnforceNumericBound)
    {
        public static Policy Admit(AdmitLimits limits) =>
            new(limits, RejectNoncharacters: true, EnforceNumericBound: true);

        public static readonly Policy Unrestricted = new(Caps: null, RejectNoncharacters: false, EnforceNumericBound: false);
    }
}
