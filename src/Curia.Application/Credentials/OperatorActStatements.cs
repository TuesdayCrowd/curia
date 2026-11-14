using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Curia.Application.Projections;
using Curia.Canon.Canonical;
using Curia.Canon.Envelope;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Primitives;

namespace Curia.Application.Credentials;

/// <summary>
/// What an operator-act writer composes and signs (errata G70, R6.67): the two closed statement forms, the detached ES256 JWS over a statement's canonical bytes, and the two reads a writer makes of
/// the log before it signs, the stream's <c>prev</c> and whether the key it holds is the first operator key the log published. Written from R6.67's text, step 3's closed forms and step 4's
/// <c>prev</c>, and not from the fold that decides the rows: the two are separate artifacts that must agree, and a test (OAS13) is where they meet.
/// </summary>
internal static class OperatorActStatements
{
    /// <summary>The header <c>typ</c> of an operator act's authorization (R6.66, R6.67 step 5).</summary>
    internal const string HeaderTyp = "curia-operator-act+jws";

    /// <summary>The statement <c>typ</c> of an owner attestation (R6.67 step 2).</summary>
    internal const string AttestationTyp = "curia.owner-attestation/1";

    /// <summary>The statement <c>typ</c> of a moderation record (R6.67 step 2).</summary>
    internal const string ModerationTyp = "curia.moderation/1";

    /// <summary>The row's member that holds the statement as its own canonical text (R6.67 step 1).</summary>
    internal const string StatementMember = "statement";

    /// <summary>The row's member that holds the detached JWS over the statement (R6.67 step 5).</summary>
    internal const string AuthorizationMember = "authorization";

    /// <summary>R6.67 step 3: the one moderator the Forum honours.</summary>
    internal const string HumanModerator = "human";

    /// <summary>The statement as its own canonical text, the authorization over it, and the statement read back as a value (the row states its members from it).</summary>
    internal sealed record Signed(string Statement, string Authorization, JsonValue.Object Members);

    /// <summary>An attestation's statement: exactly <c>actor_id</c>, <c>agent_id</c>, <c>method</c>, <c>owner_id</c>, <c>owner_verified</c>, <c>prev</c>, <c>reason</c>, <c>typ</c>.</summary>
    internal static Result<Signed> SignAttestation(
        string actorId, string agentId, string method, string ownerId, bool ownerVerified, long prev, string reason, IOperatorActSigner signer) =>
        Sign(
            new JsonValue.Object(
            [
                new("actor_id", new JsonValue.String(actorId)),
                new("agent_id", new JsonValue.String(agentId)),
                new("method", new JsonValue.String(method)),
                new("owner_id", new JsonValue.String(ownerId)),
                new("owner_verified", new JsonValue.Bool(ownerVerified)),
                new("prev", new JsonValue.Number(prev)),
                new("reason", new JsonValue.String(reason)),
                new("typ", new JsonValue.String(AttestationTyp)),
            ]),
            signer);

    /// <summary>A moderation record's statement: exactly <c>actor_id</c>, <c>adjudicates</c>, <c>category</c>, <c>digest</c>, <c>effect</c>, <c>moderator</c>, <c>post_id</c>, <c>prev</c>, <c>rationale</c>, <c>typ</c>.</summary>
    internal static Result<Signed> SignModeration(
        string actorId, IEnumerable<string> adjudicates, string category, string digest, string effect, string postId, long prev, string rationale, IOperatorActSigner signer) =>
        Sign(
            new JsonValue.Object(
            [
                new("actor_id", new JsonValue.String(actorId)),
                new("adjudicates", new JsonValue.Array([.. adjudicates.Select(id => (JsonValue)new JsonValue.String(id))])),
                new("category", new JsonValue.String(category)),
                new("digest", new JsonValue.String(digest)),
                new("effect", new JsonValue.String(effect)),
                new("moderator", new JsonValue.String(HumanModerator)),
                new("post_id", new JsonValue.String(postId)),
                new("prev", new JsonValue.Number(prev)),
                new("rationale", new JsonValue.String(rationale)),
                new("typ", new JsonValue.String(ModerationTyp)),
            ]),
            signer);

    /// <summary>
    /// The statement's exact canonical form (JCS and NFC), the detached JWS over those bytes under header <c>typ</c> <see cref="HeaderTyp"/>, and the canonical form read back as a value. The row's
    /// members are taken from the value read back, so that a string NFC changes is the same string in the statement and in the row (R6.67 step 3 compares them code point for code point).
    /// </summary>
    private static Result<Signed> Sign(JsonValue.Object statement, IOperatorActSigner signer)
    {
        if (!CanonicalJson.CanonicalizeWithNfc(statement).TryGetValue(out var canonical, out var canonicalError))
            return Result<Signed>.Fail(canonicalError!);

        var text = Encoding.UTF8.GetString(canonical!.Span);
        if (!JsonReader.Parse(canonical.Span.ToArray(), AdmitLimits.Default).TryGetValue(out var read, out var readError))
            return Result<Signed>.Fail(readError!);

        var jws = new DetachedJws(
            new Dictionary<string, IContentSigner>(StringComparer.Ordinal) { ["ES256"] = new AllowListOnly() },
            new Dictionary<string, IContentVerifier>(StringComparer.Ordinal),
            HeaderTyp);
        if (!jws.Sign(canonical, new DelegatedSigner(signer)).TryGetValue(out var signature, out var signError))
            return Result<Signed>.Fail(signError!);

        return Result<Signed>.Ok(new Signed(text, signature!.Compact, (JsonValue.Object)read!));
    }

    /// <summary>The members of <paramref name="members"/> but the ones named in <paramref name="except"/>, in the statement's order.</summary>
    internal static IEnumerable<KeyValuePair<string, JsonValue>> Without(JsonValue.Object members, params string[] except) =>
        members.Members.Where(m => !except.Contains(m.Key, StringComparer.Ordinal)).Select(m => new KeyValuePair<string, JsonValue>(m.Key, m.Value));

    /// <summary>
    /// What a writer reads of one stream before it signs (R6.67 step 4): the stream's version, which is the count of its events in the log read (the append is conditioned on it), and the
    /// statement's <c>prev</c>, the log index of the stream's last act row that is not skipped or, when there is none, of the stream's root. Null <c>Prev</c> says the stream has no root: its first
    /// event is not of the root's type, so every row appended to it would be skipped <c>act-broken-link</c>.
    /// </summary>
    internal sealed record Chain(long Version, long? Prev);

    /// <summary>Reads <paramref name="aggregate"/>'s stream out of <paramref name="verified"/>'s log: <paramref name="rootType"/> is its first event's type, <paramref name="actType"/> the type of its act rows.</summary>
    internal static Chain ChainOf(VerifiedLog verified, string aggregate, string rootType, string actType)
    {
        var skipped = verified.ActSkipped().Select(r => r.LogIndex).ToHashSet();
        long version = 0;
        long? root = null;
        long? lastAct = null;
        var log = verified.Log;

        for (var i = 0; i < log.Count; i++)
        {
            if (!string.Equals(log[i].AggregateId.Value, aggregate, StringComparison.Ordinal)) continue;

            if (version == 0 && string.Equals(log[i].Event.Type.Value, rootType, StringComparison.Ordinal))
                root = i;
            version++;

            if (string.Equals(log[i].Event.Type.Value, actType, StringComparison.Ordinal) && !skipped.Contains(i))
                lastAct = i;
        }

        return new Chain(version, root is null ? null : lastAct ?? root);
    }

    /// <summary>Whether <paramref name="signer"/>'s <c>kid</c> is the first operator key <paramref name="log"/> publishes: a row signed under any other is skipped (R6.67 step 6).</summary>
    internal static bool IsFirstOperatorKey(IReadOnlyList<AppendedEvent> log, IOperatorActSigner signer)
    {
        var keys = PublishedKeys.OfLog(log).OperatorKeys;
        return !keys.IsEmpty && string.Equals(keys[0].Kid, signer.Kid, StringComparison.Ordinal);
    }

    /// <summary>The act row <paramref name="eventId"/> as the log now holds it: its index and, when the Forum skips it, the predicate. Null when the log does not hold it.</summary>
    internal static (long Index, string? Predicate)? Find(VerifiedLog verified, string eventId)
    {
        var log = verified.Log;
        for (var i = 0; i < log.Count; i++)
        {
            if (!string.Equals(log[i].Event.Id.Value, eventId, StringComparison.Ordinal)) continue;

            var skipped = verified.ActSkipped().FirstOrDefault(r => r.LogIndex == i);
            return (i, skipped?.Predicate);
        }

        return null;
    }

    /// <summary>The port as <see cref="DetachedJws"/> signs with it: the header and the signing input are computed there, the signature comes from the port.</summary>
    private sealed class DelegatedSigner(IOperatorActSigner port) : IAgentSigner
    {
        public string Alg => "ES256";

        public string Kid => port.Kid;

        public ReadOnlyMemory<byte> PublicKey => ReadOnlyMemory<byte>.Empty;

        public byte[] Sign(ReadOnlySpan<byte> signingInput) =>
            port.SignStatement(signingInput).TryGetValue(out var signature, out var error)
                ? signature!
                : throw new CryptographicException(error!.Type);
    }

    /// <summary><see cref="DetachedJws"/> signs only for an <c>alg</c> its signer dictionary names; the signature comes from the port, so this entry is never called.</summary>
    private sealed class AllowListOnly : IContentSigner
    {
        public byte[] Sign(ReadOnlySpan<byte> input, SigningKey key) =>
            throw new InvalidOperationException("The delegated signer signs; this entry only names ES256 in the allow-list.");
    }
}
