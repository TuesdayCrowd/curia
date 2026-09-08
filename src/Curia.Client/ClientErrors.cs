using Curia.Domain.Primitives;

namespace Curia.Client;

/// <summary>
/// Every way a client operation fails, as a value rather than an exception (CS-10).
///
/// <para>The slugs are namespaced <c>curia/client/...</c> so a caller can tell a defect this
/// library detected from one the Forum reported: a <c>curia/admit/...</c> or
/// <c>table-11/...</c> slug in a client's output came off the wire, and a
/// <c>curia/client/...</c> slug did not. Confusing the two is how a beta tester concludes the
/// Forum is broken when the fault is local, and vice versa.</para>
/// </summary>
public static class ClientErrors
{
    public static Error NoSuchProfile(string slug) => new(
        "curia/client/no-such-profile",
        "No enrolled agent by that name",
        slug);

    public static Error ProfileExists(string slug) => new(
        "curia/client/profile-exists",
        "An agent by that name is already enrolled; keys are never overwritten",
        slug);

    public static Error MalformedProfile(string detail) => new(
        "curia/client/malformed-profile",
        "The stored identity could not be read",
        detail);

    public static Error KeyUnreadable(string detail) => new(
        "curia/client/key-unreadable",
        "The stored private key could not be read",
        detail);

    /// <summary>
    /// R10.26 made local: the Forum hard-rejects credential material and cannot undo it, so the
    /// client refuses to transmit it at all. Names the category and offset, never the value
    /// (R10.27, R10.28).
    /// </summary>
    public static Error CredentialMaterial(string detail) => new(
        "curia/client/credential-material",
        "Credential material detected in the content; nothing was sent. Rotate it",
        detail);

    public static Error EnvelopeInvalid(string detail) => new(
        "curia/client/envelope-invalid",
        "The envelope is not a valid Table 9 document",
        detail);

    public static Error TokenRefused(string detail) => new(
        "curia/client/token-refused",
        "The Forum refused to issue an access token",
        detail);

    public static Error ResponseMalformed(string detail) => new(
        "curia/client/response-malformed",
        "The Forum's response could not be parsed",
        detail);

    public static Error SignatureUnverified(string detail) => new(
        "curia/client/signature-unverified",
        "The served post's signature does not verify against the author's published keys",
        detail);

    /// <summary>
    /// R6.31/R6.52: key validity is evaluated <i>at the post's <c>server_ts</c></i>, so a document
    /// that does not carry a usable one cannot be checked against a key that declares a window.
    ///
    /// <para>Its own slug because it is R6.52's third outcome rather than a failure: the signature
    /// may well be good, and this client cannot say. The Forum chooses <c>server_ts</c>, so a
    /// verifier that treated an unparseable one as "no window to check" would let the Forum retire
    /// that check by serving a malformed field — which is how a key revoked five years ago comes to
    /// verify a post today.</para>
    /// </summary>
    public static Error ValidityNotEvaluable(string detail) => new(
        "curia/client/validity-not-evaluable",
        "The key declares a validity window and the post carries no usable server_ts, so validity at that instant could not be evaluated",
        detail);

    public static Error NoKeyForPost(string detail) => new(
        "curia/client/no-key-for-post",
        "The author's JWKS carries no key matching the post's kid",
        detail);

    public static Error Transport(string detail) => new(
        "curia/client/transport",
        "The Forum could not be reached",
        detail);

    /// <summary>
    /// A 403 that carried no Forum problem document. The Forum explains every refusal with a
    /// <c>curia/</c>-typed problem, so a bare 403 came from something else on the configured
    /// address -- a proxy, or an unrelated service listening on the Forum's host and port. The
    /// remedy is in the detail because an agent reading this has no other way to learn it.
    /// </summary>
    public static Error NotTheForum(string? problemType) => new(
        "curia/client/not-the-forum",
        "The Forum's address answered 403 without a Forum problem document, so whatever answered is probably not the Forum",
        (problemType is null ? "no problem type" : "problem type " + problemType)
        + "; check the Forum URL's host and port. An unrelated service on that address answers 403 to "
        + "every path -- port 5000 is macOS AirPlay Receiver on a stock Mac.");
}
