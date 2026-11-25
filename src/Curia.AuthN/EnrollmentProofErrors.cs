using Curia.Domain.Primitives;

namespace Curia.AuthN;

/// <summary>
/// R4.46 (errata G79): the seven refusals of an enrollment proof, one slug and one factory each. Each names a member or a claim and never a value the sender supplied
/// (the detail is <c>member=...</c> or <c>claim=...</c>, a name from a closed set the validator writes). The route answers each <c>400</c> through <c>StatusForEnrollmentProof</c>; a type this
/// class does not define is a fault of the Forum and answers <c>500</c> there.
/// </summary>
public static class EnrollmentProofErrors
{
    public const string RequiredType = "curia/enroll/proof-required";
    public const string MalformedType = "curia/enroll/proof-malformed";
    public const string MismatchType = "curia/enroll/proof-mismatch";
    public const string NotPossessedType = "curia/enroll/proof-not-possessed";
    public const string WindowType = "curia/enroll/proof-window";
    public const string NonceStaleType = "curia/enroll/proof-nonce-stale";
    public const string ReplayedType = "curia/enroll/proof-replayed";

    /// <summary>Step 1: the request carries no proof, or an empty one.</summary>
    public static Error Required() => new(RequiredType, "The enrollment request carries no proof of possession", "member=proof");

    /// <summary>Steps 2 and 5: the proof, its header or its payload is not what R4.46 describes; <paramref name="member"/> names which.</summary>
    public static Error Malformed(string member) => new(MalformedType, "The enrollment proof is malformed", $"member={member}");

    /// <summary>Steps 3 and 5: a header member or claim is not the request's; <paramref name="claim"/> names which.</summary>
    public static Error Mismatch(string claim) => new(MismatchType, "The enrollment proof is not for this request", $"claim={claim}");

    /// <summary>Step 4: the signature does not verify under the request's key.</summary>
    public static Error NotPossessed() => new(NotPossessedType, "The enrollment proof was not signed by the key being enrolled");

    /// <summary>Step 5: <c>iat</c> is further from the Forum's clock than the permitted skew.</summary>
    public static Error Window() => new(WindowType, "The enrollment proof was made outside the permitted window", "claim=iat");

    /// <summary>Step 6: the nonce is absent, not a nonce value, or not one the Forum reports current.</summary>
    public static Error NonceStale() => new(NonceStaleType, "The enrollment proof's nonce is not one the Forum chose, or has expired", "claim=nonce");

    /// <summary>Step 7: the proof's <c>jti</c> was already consumed.</summary>
    public static Error Replayed() => new(ReplayedType, "The enrollment proof was already used", "claim=jti");
}
