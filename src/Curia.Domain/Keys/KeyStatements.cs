using Curia.Canon.Canonical;
using Curia.Canon.Envelope;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain.Primitives;

namespace Curia.Domain.Keys;

/// <summary>A rotation statement (spec section 4.5): the members, read from canonical bytes.</summary>
public sealed record RotationStatement(string AgentId, string AuthorizedBy, JsonValue.Object Jwk, string Kid, long Prev);

/// <summary>A retirement statement (spec section 4.5): the members, read from canonical bytes.</summary>
public sealed record RetirementStatement(string AgentId, string Kid, long Prev);

/// <summary>
/// The closed set of reasons a declaration gives (spec section 4.5, KF4): an unknown one is not a reason
/// this reader can judge.
/// </summary>
public enum CompromiseReason
{
    /// <summary>The owner told the operator the key was lost or stolen.</summary>
    OwnerReported,

    /// <summary>The operator saw evidence the key was used by someone else.</summary>
    OperatorObserved,
}

/// <summary>The wire spellings of <see cref="CompromiseReason"/>.</summary>
public static class CompromiseReasons
{
    public const string OwnerReported = "owner-reported";
    public const string OperatorObserved = "operator-observed";

    /// <summary>The spelling a statement and an entry carry.</summary>
    public static string Wire(CompromiseReason reason) => reason switch
    {
        CompromiseReason.OwnerReported => OwnerReported,
        CompromiseReason.OperatorObserved => OperatorObserved,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Not a compromise reason"),
    };

    /// <summary>The reason a spelling names; a refusal naming <c>reason</c> for any other text.</summary>
    public static Result<CompromiseReason> Parse(string? text) => text switch
    {
        OwnerReported => Result<CompromiseReason>.Ok(CompromiseReason.OwnerReported),
        OperatorObserved => Result<CompromiseReason>.Ok(CompromiseReason.OperatorObserved),
        _ => Result<CompromiseReason>.Fail(KeyStatementErrors.Unreadable("reason")),
    };
}

/// <summary>A compromise declaration's statement (spec section 4.5): the members, read from canonical bytes.</summary>
public sealed record CompromiseStatement(string AgentId, long FromIndex, IReadOnlyList<string> Kids, long Prev, CompromiseReason Reason);

/// <summary>The refusals of reading a key statement, by name (spec section 4.7). None names a key, a statement or a signature.</summary>
public static class KeyStatementErrors
{
    public const string UnreadableType = "curia/keys/statement-unreadable";
    public const string NotCanonicalType = "curia/keys/statement-not-canonical";
    public const string MismatchType = "curia/keys/statement-mismatch";
    public const string JwkAlgMismatchType = "curia/keys/jwk-alg-mismatch";
    public const string SelfAuthorizedType = "curia/keys/self-authorized";
    public const string SignatureInvalidType = "curia/keys/signature-invalid";

    /// <summary>The statement is not JSON ADMIT admits, its <c>typ</c> is not the kind's version, or a member has the wrong kind of value.</summary>
    public static Error Unreadable(string field) => new(UnreadableType, "The statement could not be read", "field=" + field);

    /// <summary>The statement's bytes are not their own canonical form (spec section 4.5): rejected, never repaired.</summary>
    public static Error NotCanonical() => new(NotCanonicalType, "The statement is not in its canonical form");

    /// <summary>The statement carries a member outside its set or lacks one, or disagrees with the route or the entry's members.</summary>
    public static Error Mismatch(string field) => new(MismatchType, "The statement disagrees with its route or its entry", "field=" + field);

    /// <summary>KF5: the <c>jwk</c>'s <c>alg</c> is not the one its <c>kty</c> and <c>crv</c> determine.</summary>
    public static Error JwkAlgMismatch() => new(JwkAlgMismatchType, "The key's alg does not match its key type", "field=jwk.alg");

    /// <summary>KF5: a rotation's <c>authorized_by</c> equals its <c>kid</c>.</summary>
    public static Error SelfAuthorized() => new(SelfAuthorizedType, "A key cannot authorize its own binding", "field=authorized_by");

    /// <summary>KF1, KF2: a signature whose header <c>typ</c> is not the kind's or whose header <c>kid</c> is not its role's member. Names the role, never a key.</summary>
    public static Error SignatureInvalid(string role) => new(SignatureInvalidType, "The signature does not satisfy its role", "field=" + role);
}

/// <summary>
/// Reading a key statement (spec sections 4.5, 4.6; KF1, KF4, KF5). Pure: no store, no clock. A statement
/// is the exact output of <c>CanonicalizeWithNfc</c> over its object, so each read parses the bytes,
/// reads the <c>typ</c> first (KF4: an unknown version is unreadable whatever else it carries), requires
/// the bytes to equal their own canonical form, and requires the member set to be the kind's, closed.
/// A statement is rejected and never repaired.
/// </summary>
public static class KeyStatements
{
    public const string RotationStatementTyp = "curia.key-rotation/1";
    public const string RetirementStatementTyp = "curia.key-retirement/1";
    public const string RotationHeaderTyp = "curia-key-rotation+jws";
    public const string RetirementHeaderTyp = "curia-key-retirement+jws";
    public const string CompromiseStatementTyp = "curia.key-compromise/1";
    public const string CompromiseHeaderTyp = "curia-key-compromise+jws";

    private const double SafeMaxInteger = 9_007_199_254_740_991d;

    private static readonly string[] RotationMembers = ["agent_id", "authorized_by", "jwk", "kid", "prev", "typ"];
    private static readonly string[] RetirementMembers = ["agent_id", "kid", "prev", "typ"];
    private static readonly string[] CompromiseMembers = ["agent_id", "from_index", "kids", "prev", "reason", "typ"];

    /// <summary>Reads a rotation statement from its exact bytes.</summary>
    public static Result<RotationStatement> ReadRotation(ReadOnlySpan<byte> statement)
    {
        var read = ReadObject(statement, RotationStatementTyp, RotationMembers);
        if (!read.TryGetValue(out var o, out var error)) return Result<RotationStatement>.Fail(error!);

        if (Member(o!, "agent_id") is not JsonValue.String agent) return Result<RotationStatement>.Fail(KeyStatementErrors.Unreadable("agent_id"));
        if (Member(o!, "authorized_by") is not JsonValue.String authorizedBy) return Result<RotationStatement>.Fail(KeyStatementErrors.Unreadable("authorized_by"));
        if (Member(o!, "jwk") is not JsonValue.Object jwk) return Result<RotationStatement>.Fail(KeyStatementErrors.Unreadable("jwk"));
        if (Member(o!, "kid") is not JsonValue.String kid) return Result<RotationStatement>.Fail(KeyStatementErrors.Unreadable("kid"));
        if (!Integer(Member(o!, "prev")).TryGetValue(out var prev, out var prevError)) return Result<RotationStatement>.Fail(prevError!);

        if (!JwkAlgMatches(jwk)) return Result<RotationStatement>.Fail(KeyStatementErrors.JwkAlgMismatch());
        if (string.Equals(authorizedBy.Value, kid.Value, StringComparison.Ordinal))
            return Result<RotationStatement>.Fail(KeyStatementErrors.SelfAuthorized());

        return Result<RotationStatement>.Ok(new RotationStatement(agent.Value, authorizedBy.Value, jwk, kid.Value, prev));
    }

    /// <summary>Reads a retirement statement from its exact bytes.</summary>
    public static Result<RetirementStatement> ReadRetirement(ReadOnlySpan<byte> statement)
    {
        var read = ReadObject(statement, RetirementStatementTyp, RetirementMembers);
        if (!read.TryGetValue(out var o, out var error)) return Result<RetirementStatement>.Fail(error!);

        if (Member(o!, "agent_id") is not JsonValue.String agent) return Result<RetirementStatement>.Fail(KeyStatementErrors.Unreadable("agent_id"));
        if (Member(o!, "kid") is not JsonValue.String kid) return Result<RetirementStatement>.Fail(KeyStatementErrors.Unreadable("kid"));
        if (!Integer(Member(o!, "prev")).TryGetValue(out var prev, out var prevError)) return Result<RetirementStatement>.Fail(prevError!);

        return Result<RetirementStatement>.Ok(new RetirementStatement(agent.Value, kid.Value, prev));
    }

    /// <summary>Reads a compromise declaration's statement from its exact bytes (R6.26 rev., R6.57).</summary>
    public static Result<CompromiseStatement> ReadCompromise(ReadOnlySpan<byte> statement)
    {
        var read = ReadObject(statement, CompromiseStatementTyp, CompromiseMembers);
        if (!read.TryGetValue(out var o, out var error)) return Result<CompromiseStatement>.Fail(error!);

        if (Member(o!, "agent_id") is not JsonValue.String agent) return Result<CompromiseStatement>.Fail(KeyStatementErrors.Unreadable("agent_id"));
        if (!Integer(Member(o!, "from_index"), "from_index").TryGetValue(out var fromIndex, out var fromIndexError)) return Result<CompromiseStatement>.Fail(fromIndexError!);
        if (!Integer(Member(o!, "prev"), "prev").TryGetValue(out var prev, out var prevError)) return Result<CompromiseStatement>.Fail(prevError!);
        if (Member(o!, "kids") is not JsonValue.Array { Items.Length: > 0 } kidItems) return Result<CompromiseStatement>.Fail(KeyStatementErrors.Unreadable("kids"));
        var kids = new List<string>(kidItems.Items.Length);
        foreach (var item in kidItems.Items)
        {
            if (item is not JsonValue.String kid) return Result<CompromiseStatement>.Fail(KeyStatementErrors.Unreadable("kids"));
            kids.Add(kid.Value);
        }

        if (Member(o!, "reason") is not JsonValue.String reasonText) return Result<CompromiseStatement>.Fail(KeyStatementErrors.Unreadable("reason"));
        if (!CompromiseReasons.Parse(reasonText.Value).TryGetValue(out var reason, out var reasonError)) return Result<CompromiseStatement>.Fail(reasonError!);

        return Result<CompromiseStatement>.Ok(new CompromiseStatement(agent.Value, fromIndex, kids, prev, reason));
    }

    /// <summary>
    /// The key a rotation statement's <c>jwk</c> describes, under the statement's <c>kid</c> (spec section
    /// 4.5): <c>EdDSA</c> as the raw 32 bytes of <c>x</c>, <c>ES256</c> as the DER SubjectPublicKeyInfo of
    /// <c>x</c> and <c>y</c>. The <c>jwk</c> must equal, member for member, <see cref="PublicJwk.Of"/> of
    /// that key, so a <c>curia_</c> member, a non-canonical coordinate or a <c>kid</c> that is not the
    /// statement's is <c>statement-mismatch</c>, rejected and never repaired.
    /// </summary>
    public static Result<PublicKeyMaterial> MaterialOf(RotationStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);

        var jwk = statement.Jwk;
        PublicKeyMaterial material;
        try
        {
            switch (AlgOf(jwk))
            {
                case "EdDSA":
                {
                    if (Member(jwk, "x") is not JsonValue.String x) return Result<PublicKeyMaterial>.Fail(KeyStatementErrors.Unreadable("jwk"));
                    var raw = System.Buffers.Text.Base64Url.DecodeFromChars(x.Value);
                    if (raw.Length != 32) return Result<PublicKeyMaterial>.Fail(KeyStatementErrors.Unreadable("jwk"));
                    material = new PublicKeyMaterial("EdDSA", statement.Kid, raw);
                    break;
                }

                case "ES256":
                {
                    if (Member(jwk, "x") is not JsonValue.String x || Member(jwk, "y") is not JsonValue.String y)
                        return Result<PublicKeyMaterial>.Fail(KeyStatementErrors.Unreadable("jwk"));

                    var parameters = new System.Security.Cryptography.ECParameters
                    {
                        Curve = System.Security.Cryptography.ECCurve.NamedCurves.nistP256,
                        Q = new System.Security.Cryptography.ECPoint
                        {
                            X = System.Buffers.Text.Base64Url.DecodeFromChars(x.Value),
                            Y = System.Buffers.Text.Base64Url.DecodeFromChars(y.Value),
                        },
                    };

                    using var ecdsa = System.Security.Cryptography.ECDsa.Create(parameters);
                    material = new PublicKeyMaterial("ES256", statement.Kid, ecdsa.ExportSubjectPublicKeyInfo());
                    break;
                }

                default:
                    return Result<PublicKeyMaterial>.Fail(KeyStatementErrors.Unreadable("jwk"));
            }
        }
        catch (FormatException)
        {
            return Result<PublicKeyMaterial>.Fail(KeyStatementErrors.Unreadable("jwk"));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return Result<PublicKeyMaterial>.Fail(KeyStatementErrors.Unreadable("jwk"));
        }
        catch (ArgumentException)
        {
            return Result<PublicKeyMaterial>.Fail(KeyStatementErrors.Unreadable("jwk"));
        }

        return PublicJwk.Of(material).TryGetValue(out var rendered, out _) && PublicJwk.SameKey(jwk, rendered!)
            ? Result<PublicKeyMaterial>.Ok(material)
            : Result<PublicKeyMaterial>.Fail(KeyStatementErrors.Mismatch("jwk"));
    }

    /// <summary>
    /// KF1 and KF2: the header of <paramref name="signature"/> names the kind's <c>typ</c> and the role's
    /// member as its <c>kid</c>. Any other value, or a header that will not read, is
    /// <see cref="KeyStatementErrors.SignatureInvalid"/> naming <paramref name="role"/>.
    /// </summary>
    public static Result<JwsProtectedHeader> CheckHeader(JwsSignature signature, string role, string expectedTyp, string expectedKid)
    {
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentException.ThrowIfNullOrEmpty(role);
        ArgumentException.ThrowIfNullOrEmpty(expectedTyp);
        ArgumentException.ThrowIfNullOrEmpty(expectedKid);

        if (!DetachedJws.ReadProtectedHeader(signature).TryGetValue(out var header, out _)
            || !string.Equals(header!.Typ, expectedTyp, StringComparison.Ordinal)
            || !string.Equals(header.Kid, expectedKid, StringComparison.Ordinal))
            return Result<JwsProtectedHeader>.Fail(KeyStatementErrors.SignatureInvalid(role));

        return Result<JwsProtectedHeader>.Ok(header);
    }

    /// <summary>
    /// The <c>alg</c> a public JWK's <c>kty</c> and <c>crv</c> determine (R4.15): <c>EdDSA</c> for OKP/Ed25519,
    /// <c>ES256</c> for EC/P-256, otherwise null.
    /// </summary>
    public static string? AlgOf(JsonValue.Object jwk)
    {
        ArgumentNullException.ThrowIfNull(jwk);

        return (Member(jwk, "kty"), Member(jwk, "crv")) switch
        {
            (JsonValue.String { Value: "OKP" }, JsonValue.String { Value: "Ed25519" }) => "EdDSA",
            (JsonValue.String { Value: "EC" }, JsonValue.String { Value: "P-256" }) => "ES256",
            _ => null,
        };
    }

    private static bool JwkAlgMatches(JsonValue.Object jwk) =>
        AlgOf(jwk) is { } derived && Member(jwk, "alg") is JsonValue.String alg && string.Equals(alg.Value, derived, StringComparison.Ordinal);

    private static Result<JsonValue.Object> ReadObject(ReadOnlySpan<byte> statement, string expectedTyp, string[] members)
    {
        if (!JsonReader.Parse(statement, AdmitLimits.Default).TryGetValue(out var root, out _) || root is not JsonValue.Object o)
            return Result<JsonValue.Object>.Fail(KeyStatementErrors.Unreadable("statement"));

        // KF4's order: the typ before the canonical form and the member set, so a version this reader does
        // not know is unreadable whatever else it carries.
        if (Member(o, "typ") is not JsonValue.String typ || !string.Equals(typ.Value, expectedTyp, StringComparison.Ordinal))
            return Result<JsonValue.Object>.Fail(KeyStatementErrors.Unreadable("typ"));

        if (!CanonicalJson.CanonicalizeWithNfc(o).TryGetValue(out var canonical, out _)
            || !canonical.Span.SequenceEqual(statement))
            return Result<JsonValue.Object>.Fail(KeyStatementErrors.NotCanonical());

        if (!SameMembers(o, members))
            return Result<JsonValue.Object>.Fail(KeyStatementErrors.Mismatch("members"));

        return Result<JsonValue.Object>.Ok(o);
    }

    private static bool SameMembers(JsonValue.Object o, string[] members) =>
        o.Members.Length == members.Length
        && o.Members.Select(m => m.Key).Order(StringComparer.Ordinal).SequenceEqual(members, StringComparer.Ordinal);

    private static Result<long> Integer(JsonValue? value, string field = "prev")
    {
        if (value is not JsonValue.Number number
            || number.Value < 0
            || number.Value > SafeMaxInteger
            || number.Value != Math.Floor(number.Value))
            return Result<long>.Fail(KeyStatementErrors.Unreadable(field));

        return Result<long>.Ok((long)number.Value);
    }

    private static JsonValue? Member(JsonValue.Object o, string name)
    {
        foreach (var member in o.Members)
            if (string.Equals(member.Key, name, StringComparison.Ordinal)) return member.Value;
        return null;
    }
}
