using System.Text;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Canon.Sodium;
using Curia.Domain.Keys;
using Curia.Domain.Primitives;

namespace Curia.Client;

/// <summary>A rotation, ready to send: the statement and the two detached signatures over its bytes (spec section 4.5).</summary>
/// <param name="Statement">The statement, the exact output of <c>CanonicalizeWithNfc</c>.</param>
/// <param name="Authorization">The detached JWS by the key that authorizes the new one, header <c>kid</c> its <c>authorized_by</c>.</param>
/// <param name="Possession">The detached JWS by the new key, header <c>kid</c> the statement's <c>kid</c>.</param>
public sealed record RotationRequest(string Statement, string Authorization, string Possession)
{
    /// <summary>The request body of <c>POST /v1/agents/{id}/keys</c> (spec section 4.7), all three members strings.</summary>
    internal string Body() => KeyActs.RenderBody(
        [("statement", Statement), ("authorization", Authorization), ("possession", Possession)]);
}

/// <summary>A retirement, ready to send: the statement and the retiring key's own signature over its bytes.</summary>
/// <param name="Statement">The statement, the exact output of <c>CanonicalizeWithNfc</c>.</param>
/// <param name="Authorization">The detached JWS by the key being retired, header <c>kid</c> the statement's <c>kid</c>.</param>
public sealed record RetirementRequest(string Statement, string Authorization)
{
    /// <summary>The request body of <c>POST /v1/agents/{id}/keys/{kid}/retire</c> (spec section 4.7).</summary>
    internal string Body() => KeyActs.RenderBody([("statement", Statement), ("authorization", Authorization)]);
}

/// <summary>
/// What a successful rotation or retirement answers (spec section 4.7): the identity, the key, and the log index
/// the Forum appended the act at. <paramref name="AuthorizedBy"/> is present for a rotation alone.
/// </summary>
public sealed record KeyActReceipt(string AgentId, string Kid, long LogIndex, string? AuthorizedBy);

/// <summary>
/// Building the two key statements a client sends (errata G21, spec sections 4.5 and 4.12): a rotation, signed by
/// the key that authorizes it and by the key it adds, and a retirement, signed by the key it ends.
///
/// <para><b>Never <see cref="SubmissionBuilder"/>'s header.</b> Each signature carries its kind's header
/// <c>typ</c> (<c>curia-key-rotation+jws</c>, <c>curia-key-retirement+jws</c>, KF1), and each header
/// <c>kid</c> is its role's member (KF2): the authorizing key's for <c>authorization</c>, the new key's for
/// <c>possession</c>, the retiring key's for a retirement. A signature a post's header would also verify
/// under would let a post's signature be replayed as a lifecycle act.</para>
///
/// <para><b>Through the signer's own operation.</b> An external signer is asked through
/// <c>sign-lifecycle</c> (R4.39, R11.20), never through <c>sign</c>, which refuses a lifecycle
/// <c>typ</c>; the MCP adapter never reaches this type, so a process that may post cannot thereby obtain a
/// signature that adds or ends a key. An in-process key signs as it signs anything.</para>
/// </summary>
public static class KeyActs
{
    /// <summary>
    /// A rotation of <paramref name="current"/>'s identity onto <paramref name="next"/>, linked from the lifecycle
    /// entry at log index <paramref name="prev"/>. The authorizing key is <paramref name="current"/>'s registered
    /// key; the new key proves possession by signing the same statement.
    /// </summary>
    public static Result<RotationRequest> BuildRotation(EnrolledAgent current, IAgentSigner next, long prev)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(next);

        if (!PublicJwk.Of(new PublicKeyMaterial(next.Alg, next.Kid, next.PublicKey)).TryGetValue(out var jwk, out var jwkError))
            return Result<RotationRequest>.Fail(ClientErrors.KeyStatementInvalid($"{jwkError!.Type}: {jwkError.Title}"));

        var statement = Canonical(new JsonValue.Object(
        [
            new("agent_id", new JsonValue.String(current.Profile.AgentId)),
            new("authorized_by", new JsonValue.String(current.Signer.Kid)),
            new("jwk", jwk!),
            new("kid", new JsonValue.String(next.Kid)),
            new("prev", new JsonValue.Number(prev)),
            new("typ", new JsonValue.String(KeyStatements.RotationStatementTyp)),
        ]));
        if (!statement.TryGetValue(out var bytes, out var statementError))
            return Result<RotationRequest>.Fail(ClientErrors.KeyStatementInvalid($"{statementError!.Type}: {statementError.Title}"));

        var authorization = Sign(bytes, KeyStatements.RotationHeaderTyp, current.Signer);
        if (!authorization.TryGetValue(out var authorized, out var authorizationError))
            return Result<RotationRequest>.Fail(authorizationError!);

        var possession = Sign(bytes, KeyStatements.RotationHeaderTyp, next);
        if (!possession.TryGetValue(out var possessed, out var possessionError))
            return Result<RotationRequest>.Fail(possessionError!);

        return Result<RotationRequest>.Ok(new RotationRequest(Encoding.UTF8.GetString(bytes.Span), authorized!, possessed!));
    }

    /// <summary>
    /// A retirement of <paramref name="retiring"/>'s key, linked from the lifecycle entry at log index
    /// <paramref name="prev"/>. Only the key it ends can sign it (R4.39).
    /// </summary>
    public static Result<RetirementRequest> BuildRetirement(IAgentSigner retiring, string agentId, long prev)
    {
        ArgumentNullException.ThrowIfNull(retiring);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var statement = Canonical(new JsonValue.Object(
        [
            new("agent_id", new JsonValue.String(agentId)),
            new("kid", new JsonValue.String(retiring.Kid)),
            new("prev", new JsonValue.Number(prev)),
            new("typ", new JsonValue.String(KeyStatements.RetirementStatementTyp)),
        ]));
        if (!statement.TryGetValue(out var bytes, out var statementError))
            return Result<RetirementRequest>.Fail(ClientErrors.KeyStatementInvalid($"{statementError!.Type}: {statementError.Title}"));

        var authorization = Sign(bytes, KeyStatements.RetirementHeaderTyp, retiring);
        return authorization.TryGetValue(out var authorized, out var authorizationError)
            ? Result<RetirementRequest>.Ok(new RetirementRequest(Encoding.UTF8.GetString(bytes.Span), authorized!))
            : Result<RetirementRequest>.Fail(authorizationError!);
    }

    internal static string RenderBody((string Name, string Value)[] members) =>
        ClientJson.Render(members.Select(m => new KeyValuePair<string, JsonValue>(m.Name, new JsonValue.String(m.Value))));

    private static Result<CanonicalBytes> Canonical(JsonValue.Object statement) =>
        CanonicalJson.CanonicalizeWithNfc(statement);

    /// <summary>
    /// One detached JWS over the statement's bytes under header <c>typ</c> <paramref name="typ"/>, signed through
    /// the lifecycle operation of an external signer and through the signer as it is otherwise.
    /// </summary>
    private static Result<string> Sign(CanonicalBytes statement, string typ, IAgentSigner signer)
    {
        var jws = new DetachedJws(
            new Dictionary<string, IContentSigner>(StringComparer.Ordinal) { ["ES256"] = new Es256Adapter() },
            new Dictionary<string, IContentVerifier>(StringComparer.Ordinal),
            typ);

        IAgentSigner lifecycle = signer is ExternalSigner external ? new LifecycleSigner(external) : signer;
        return jws.Sign(statement, lifecycle).TryGetValue(out var signature, out var error)
            ? Result<string>.Ok(signature!.Compact)
            : Result<string>.Fail(ClientErrors.KeyStatementInvalid($"{error!.Type}: {error.Title}"));
    }

    /// <summary>An external signer, asked for its lifecycle operation whenever it is asked to sign.</summary>
    private sealed class LifecycleSigner(ExternalSigner inner) : IAgentSigner
    {
        public string Alg => inner.Alg;

        public string Kid => inner.Kid;

        public ReadOnlyMemory<byte> PublicKey => inner.PublicKey;

        public byte[] Sign(ReadOnlySpan<byte> signingInput) => inner.SignLifecycle(signingInput);
    }
}
