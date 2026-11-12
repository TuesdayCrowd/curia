using System.Collections.Immutable;
using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Keys;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;
using Xunit;
using static Curia.Application.Tests.Credentials.KeyActHarness;

namespace Curia.Application.Tests.Credentials;

/// <summary>
/// What the PR N probes (errata G68) share: rows appended to an identity's stream the way the credential can write them, with no use case in front (an in-memory
/// store takes whatever it is given), and the shapes a forger can build. A forged row here is well-formed in every member the old fold checked and carries a signature no
/// key made, which is exactly the row the fold honoured by shape.
/// </summary>
internal static class LifecycleProbeSupport
{
    private static int _forged;

    internal static LifecycleVerifier VerifierOf(KeyActHarness harness) => new(harness.Verifiers);

    internal static async Task<IReadOnlyList<AppendedEvent>> LogAsync(KeyActHarness harness) =>
        Require(await harness.Events.ReadAllAsync(TestContext.Current.CancellationToken).ConfigureAwait(false));

    /// <summary>Appends <paramref name="payload"/> to <paramref name="agent"/>'s own stream as an event of <paramref name="type"/> and returns its log index.</summary>
    internal static async Task<long> AppendRowAsync(KeyActHarness harness, string agent, string type, JsonValue payload)
    {
        var aggregate = Require(AggregateId.Create(agent));
        var version = Require(AggregateVersion.From((await harness.StreamAsync(agent).ConfigureAwait(false)).Count));
        var id = "forged-row-" + Interlocked.Increment(ref _forged).ToString(System.Globalization.CultureInfo.InvariantCulture);
        Require(await harness.Events.AppendAsync(
            aggregate,
            version,
            [new DomainEvent(Require(EventId.Create(id)), Require(EventType.Create(type)), Require(ActorId.Create(agent)), payload)],
            TestContext.Current.CancellationToken).ConfigureAwait(false));
        var all = await LogAsync(harness).ConfigureAwait(false);
        for (var i = 0; i < all.Count; i++)
            if (all[i].Event.Id.Value == id) return i;
        throw new InvalidOperationException("the appended row is not in the log");
    }

    internal static JsonValue.String S(string value) => new(value);

    /// <summary>A retirement row: its statement is the genuine form, its signature a string no key made.</summary>
    internal static JsonValue.Object Retirement(string agent, string kid, long prev, string statementTyp = KeyStatements.RetirementStatementTyp, string? agentId = null)
    {
        var statement = Canonical(new JsonValue.Object(
        [
            new("agent_id", S(agent)),
            new("kid", S(kid)),
            new("prev", new JsonValue.Number(prev)),
            new("typ", S(statementTyp)),
        ]));
        return new JsonValue.Object(
        [
            .. agentId is null ? Array.Empty<KeyValuePair<string, JsonValue>>() : [new("agent_id", S(agentId))],
            new("authorization", S("not-a-signature")),
            new("kid", S(kid)),
            new("prev", new JsonValue.Number(prev)),
            new("statement", S(statement)),
        ]);
    }

    /// <summary>A statement-bearing <c>agent.key-bound</c> row binding <paramref name="key"/>: genuine statement, signatures no key made.</summary>
    internal static JsonValue.Object Rotation(string agent, string authorizedBy, PublicKeyMaterial key, long prev) =>
        new(
        [
            new("agent_id", S(agent)),
            new("authorization", S("not-a-signature")),
            new("authorized_by", S(authorizedBy)),
            new("jwk", Require(PublicJwk.Of(key))),
            new("kid", S(key.Kid)),
            new("possession", S("not-a-signature")),
            new("prev", new JsonValue.Number(prev)),
            new("statement", S(RotationStatement(agent, authorizedBy, key, prev))),
        ]);

    /// <summary>A <c>/2</c> declaration row naming <paramref name="kids"/>: genuine statement, <paramref name="signature"/> for the declaration.</summary>
    internal static JsonValue.Object Declaration(string agent, string[] kids, long fromIndex, long prev, string signature)
    {
        var statement = Canonical(new JsonValue.Object(
        [
            new("agent_id", S(agent)),
            new("from_index", new JsonValue.Number(fromIndex)),
            new("kids", new JsonValue.Array([.. kids.Select(k => (JsonValue)S(k))])),
            new("prev", new JsonValue.Number(prev)),
            new("reason", S("owner-reported")),
            new("typ", S(KeyStatements.CompromiseStatementTyp)),
        ]));
        return new JsonValue.Object(
        [
            new("agent_id", S(agent)),
            new("declaration", S(signature)),
            new("from_index", new JsonValue.Number(fromIndex)),
            new("kids", new JsonValue.Array([.. kids.Select(k => (JsonValue)S(k))])),
            new("prev", new JsonValue.Number(prev)),
            new("reason", S("owner-reported")),
            new("statement", S(statement)),
        ]);
    }

    /// <summary>The indices of the rows a verified lifecycle skips, ascending.</summary>
    internal static long[] SkippedIndices(VerifiedLifecycle verified) => [.. verified.Skipped.Select(r => r.LogIndex)];

    /// <summary>Alice enrolled with <c>alice-1</c> (log index 0 and 1), nothing else.</summary>
    internal static async Task<(KeyActHarness Harness, TestIdentity Victim)> EnrolledAsync(string agent = Alice)
    {
        var harness = new KeyActHarness();
        var victim = TestIdentity.New("alice-1");
        await harness.EnrollAsync(agent, victim).ConfigureAwait(false);
        return (harness, victim);
    }

    internal static string Describe(VerifiedLifecycle verified) =>
        verified.Refusal is { } refusal
            ? $"Refusal {refusal.Type}"
            : $"honoured [{string.Join(", ", verified.Honoured.Entries.Select(e => e.Type + ":" + e.Kid))}] skipped [{string.Join(", ", verified.Skipped.Select(r => $"{r.LogIndex} {r.Predicate}"))}]";
}
