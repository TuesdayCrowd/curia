using System.Security.Cryptography;
using System.Text;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Keys;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;

namespace Curia.Application.Credentials;

/// <summary>
/// R6.26 (rev.), R6.57: the operator declares a key compromised from a log index, out of band. The log is
/// folded, <c>from_index</c> is held to the key's binding and to the index the declaration will take, the
/// cascade is computed over rotation entries (transitively, from the compromised key, at or after
/// <c>from_index</c>), and the statement is signed by a log key whose <c>log.key</c> entry the log already
/// holds, under <c>curia-key-compromise+jws</c> (KF1, KF2) and never R6.49's head <c>typ</c>.
///
/// <para>The use case does not ask the lifecycle gate: the operator verb does, first, before it reads the
/// log key (R4.38 add., GF2). The store's own chokepoint is the second lock.</para>
/// </summary>
public sealed class DeclareCompromise
{
    private readonly IEventStore _events;
    private readonly UlidGenerator _ids;

    public DeclareCompromise(IEventStore events, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(clock);

        _events = events;
        _ids = new UlidGenerator(clock);
    }

    /// <summary>Declares <paramref name="kid"/> compromised from <paramref name="fromIndex"/>, or reports the first step that refuses.</summary>
    public async Task<Result<CompromiseRecorded>> DeclareAsync(
        AgentId agent,
        Kid kid,
        long fromIndex,
        CompromiseReason reason,
        ILifecycleStatementSigner signer,
        ActorId operatorName,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(kid);
        ArgumentNullException.ThrowIfNull(signer);

        // The stream's version, before the log is read; then the log and the identity's lifecycle.
        if (!(await KeyActs.ReadStreamAsync(_events, agent, ct).ConfigureAwait(false)).TryGetValue(out var stream, out var streamError))
            return Result<CompromiseRecorded>.Fail(streamError!);
        if (!(await KeyActs.ReadLifecycleAsync(_events, agent, ct).ConfigureAwait(false)).TryGetValue(out var read, out var lifecycleError))
            return Result<CompromiseRecorded>.Fail(lifecycleError!);
        if (read!.LatestIndex is not { } latest)
            return Result<CompromiseRecorded>.Fail(KeyActErrors.UnknownKey());

        // The key is bound to this identity, and from_index is no lower than that binding.
        var binding = read.Lifecycle.Entries.FirstOrDefault(e =>
            (string.Equals(e.Type, AgentStandingProjector.KeyBoundType, StringComparison.Ordinal)
                || string.Equals(e.Type, AgentStandingProjector.EnrolledType, StringComparison.Ordinal))
            && string.Equals(e.Kid, kid.Value, StringComparison.Ordinal));
        if (binding is null || read.Log.IndexOf(binding.EventId) is not { } boundAt)
            return Result<CompromiseRecorded>.Fail(KeyActErrors.UnknownKey());
        if (fromIndex < boundAt)
            return Result<CompromiseRecorded>.Fail(KeyActErrors.BeforeBinding());

        // The declaration takes the next index, and from_index is strictly below it (R6.57).
        var declarationIndex = read.Log.TreeSize;
        if (fromIndex >= declarationIndex)
            return Result<CompromiseRecorded>.Fail(KeyActErrors.FromIndexBeyondLog());

        // The cascade: the compromised key first, then each key a rotation at or after from_index bound
        // under an authorization by a key already in it, in the log's order. A rotation's authorizing key
        // was bound before the rotation, so one pass in log order is the transitive closure.
        var kids = new List<string> { kid.Value };
        foreach (var entry in read.Lifecycle.Entries)
        {
            if (!entry.IsRotation || entry.Kid is null || entry.AuthorizedBy is null) continue;
            if (read.Log.IndexOf(entry.EventId) is not { } at || at < fromIndex) continue;
            if (kids.Contains(entry.AuthorizedBy, StringComparer.Ordinal) && !kids.Contains(entry.Kid, StringComparer.Ordinal))
                kids.Add(entry.Kid);
        }

        // KF2: the signing key's log.key entry is in the log already. After the gate and the key read,
        // before anything is signed.
        if (!Published(read.Log, signer))
            return Result<CompromiseRecorded>.Fail(KeyActErrors.LogKeyNotPublished());

        var statementObject = new JsonValue.Object(
        [
            new("agent_id", new JsonValue.String(agent.Value)),
            new("from_index", new JsonValue.Number(fromIndex)),
            new("kids", new JsonValue.Array([.. kids.Select(k => (JsonValue)new JsonValue.String(k))])),
            new("prev", new JsonValue.Number(latest)),
            new("reason", new JsonValue.String(CompromiseReasons.Wire(reason))),
            new("typ", new JsonValue.String(KeyStatements.CompromiseStatementTyp)),
        ]);
        if (!CanonicalJson.CanonicalizeWithNfc(statementObject).TryGetValue(out var canonical, out var canonicalError))
            return Result<CompromiseRecorded>.Fail(canonicalError!);
        var statement = Encoding.UTF8.GetString(canonical!.Span);

        var jws = new DetachedJws(
            new Dictionary<string, IContentSigner>(StringComparer.Ordinal) { ["ES256"] = new AllowListOnly() },
            new Dictionary<string, IContentVerifier>(StringComparer.Ordinal),
            KeyStatements.CompromiseHeaderTyp);
        if (!jws.Sign(canonical, new DelegatedSigner(signer)).TryGetValue(out var signature, out var signError))
            return Result<CompromiseRecorded>.Fail(signError!);

        var payload = new JsonValue.Object(
        [
            new("agent_id", new JsonValue.String(agent.Value)),
            new("declaration", new JsonValue.String(signature!.Compact)),
            new("from_index", new JsonValue.Number(fromIndex)),
            new("kids", new JsonValue.Array([.. kids.Select(k => (JsonValue)new JsonValue.String(k))])),
            new("prev", new JsonValue.Number(latest)),
            new("reason", new JsonValue.String(CompromiseReasons.Wire(reason))),
            new("statement", new JsonValue.String(statement)),
        ]);
        if (!KeyActs.NewEvent(_ids, LifecycleAppendGuard.KeyCompromisedType, operatorName, payload).TryGetValue(out var appended, out var entryError))
            return Result<CompromiseRecorded>.Fail(entryError!);

        var index = await KeyActs.AppendAsync(_events, agent, stream!, appended!, ct).ConfigureAwait(false);
        return index.Map(i => new CompromiseRecorded(agent.Value, kids, i));
    }

    /// <summary>Whether the folded log holds a <c>log.key</c> entry for the signer's kid with an equal public JWK.</summary>
    private static bool Published(ActaLog log, ILifecycleStatementSigner signer)
    {
        if (!CanonicalJson.CanonicalizeWithNfc(signer.PublicJwk()).TryGetValue(out var wanted, out _)) return false;

        foreach (var key in log.Keys)
        {
            if (!string.Equals(key.Kid, signer.Kid, StringComparison.Ordinal)) continue;
            if (CanonicalJson.CanonicalizeWithNfc(key.Jwk).TryGetValue(out var held, out _) && held!.Span.SequenceEqual(wanted!.Span))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The port as <see cref="DetachedJws"/> signs with it: one header and signing input computed in
    /// <see cref="DetachedJws"/>, the signature from the port.
    /// </summary>
    private sealed class DelegatedSigner(ILifecycleStatementSigner port) : IAgentSigner
    {
        public string Alg => "ES256";

        public string Kid => port.Kid;

        public ReadOnlyMemory<byte> PublicKey => ReadOnlyMemory<byte>.Empty;

        public byte[] Sign(ReadOnlySpan<byte> signingInput) =>
            port.SignStatement(signingInput).TryGetValue(out var signature, out var error)
                ? signature!
                : throw new CryptographicException(error!.Type);
    }

    /// <summary>
    /// <see cref="DetachedJws"/> signs only for an <c>alg</c> its signer dictionary names (R11.20's
    /// allow-list); the signature itself comes from the port through <see cref="DelegatedSigner"/>, so this
    /// entry is never called.
    /// </summary>
    private sealed class AllowListOnly : IContentSigner
    {
        public byte[] Sign(ReadOnlySpan<byte> input, SigningKey key) =>
            throw new InvalidOperationException("The delegated signer signs; this entry only names ES256 in the allow-list.");
    }
}

/// <summary>What a declaration recorded (R6.57): the identity, every key it ended in order, and its index in the log.</summary>
/// <param name="AgentId">The identity.</param>
/// <param name="Kids">The compromised key first, then its cascade.</param>
/// <param name="LogIndex">The declaration's index in the log.</param>
public sealed record CompromiseRecorded(string AgentId, IReadOnlyList<string> Kids, long LogIndex);
