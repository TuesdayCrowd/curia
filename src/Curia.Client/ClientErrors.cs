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

    public static Error NoKeyForPost(string detail) => new(
        "curia/client/no-key-for-post",
        "The author's JWKS carries no key matching the post's kid",
        detail);

    public static Error Transport(string detail) => new(
        "curia/client/transport",
        "The Forum could not be reached",
        detail);
}
