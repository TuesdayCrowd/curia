using System.Buffers.Text;
using System.Diagnostics;
using System.Text;
using Curia.Application.Credentials;
using Curia.Application.Tests.Credentials;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Acta;
using Xunit;

namespace Curia.Application.Tests.OperatorActs;

/// <summary>
/// OAS30's log builder (PR N2, Amendment 3 ruling 49 (e)): a log of a stated number of events, with twenty lifecycle rows (ten identities, each an enrolment and its key pair), a number of genuine attestations
/// signed by the operator key and, if asked, a number of forged ones that are well structured (every step before the signature passes) and signed by another key under the operator key's <c>kid</c>. The builder
/// checks what it built: every genuine row is honoured and every forged row is skipped <c>act-signature-invalid</c>, so the cost measured is the cost of the thing the case names.
/// </summary>
internal static class OperatorActsCostLog
{
    private const string OperatorActor = "operator:ops";
    private const int Identities = 10;

    internal sealed record Built(List<AppendedEvent> Log, int Honoured, int Forged);

    private static AppendedEvent Event(List<AppendedEvent> log, string aggregate, string type, string actor, JsonValue payload) =>
        LogEvents.At(log.Count, aggregate, new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), $"cost-{log.Count}", type, actor, payload);

    private static JsonValue.Object Obj(params (string Name, JsonValue Value)[] members) =>
        new([.. members.Select(m => new KeyValuePair<string, JsonValue>(m.Name, m.Value))]);

    private static AppendedEvent Filler(List<AppendedEvent> log) =>
        Event(log, $"filler-{log.Count}", "note.filler", "agent://curia.example/cost/filler", Obj(("n", new JsonValue.Number(log.Count))));

    private static string Agent(int n) => $"agent://curia.example/cost/a{n}";

    /// <summary>The detached JWS of <paramref name="canonical"/> by <paramref name="key"/>, whose header names <paramref name="headerKid"/> (the operator key's, whoever signs).</summary>
    private static string Sign(IOperatorActSigner key, string headerKid, byte[] canonical)
    {
        var header = Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{{\"alg\":\"ES256\",\"kid\":\"{headerKid}\",\"typ\":\"curia-operator-act+jws\",\"b64\":false,\"crit\":[\"b64\"]}}"));
        var input = Encoding.ASCII.GetBytes(header + ".").Concat(canonical).ToArray();
        var signature = key.SignStatement(input).Match(s => s, e => throw new InvalidOperationException(e.Type));
        return $"{header}..{Base64Url.EncodeToString(signature)}";
    }

    private static AppendedEvent Attestation(List<AppendedEvent> log, string agent, long prev, bool verified, IOperatorActSigner signer, string headerKid)
    {
        var statement = Obj(
            ("actor_id", new JsonValue.String(OperatorActor)),
            ("agent_id", new JsonValue.String(agent)),
            ("method", new JsonValue.String("manual")),
            ("owner_id", new JsonValue.String("owner:alice")),
            ("owner_verified", new JsonValue.Bool(verified)),
            ("prev", new JsonValue.Number(prev)),
            ("reason", new JsonValue.String("cost")),
            ("typ", new JsonValue.String("curia.owner-attestation/1")));
        var canonical = CanonicalJson.CanonicalizeWithNfc(statement).Match(c => c.ToArray(), e => throw new InvalidOperationException(e.Type));
        var members = statement.Members.Where(m => m.Key != "typ").Select(m => (m.Key, m.Value)).ToList();
        members.Add(("statement", new JsonValue.String(Encoding.UTF8.GetString(canonical))));
        members.Add(("authorization", new JsonValue.String(Sign(signer, headerKid, canonical))));
        return Event(log, agent, "agent.owner-attested", OperatorActor, Obj([.. members.OrderBy(m => m.Key, StringComparer.Ordinal)]));
    }

    /// <summary>
    /// <paramref name="events"/> events in all: the operator key's entry, ten identities (twenty lifecycle rows), <paramref name="honouredActs"/> genuine attestations over nine of them, <paramref name="forgedActs"/> forged
    /// ones over the tenth, and filler events between. Ends with the checks.
    /// </summary>
    internal static Built Build(int events, int honouredActs, int forgedActs = 0)
    {
        using var genuine = new InMemoryOperatorActSigner("operator-1");
        using var forger = new InMemoryOperatorActSigner("forger");
        Assert.NotEqual(genuine.PublicJwk().Members.Single(m => m.Key == "x").Value, forger.PublicJwk().Members.Single(m => m.Key == "x").Value);

        var log = new List<AppendedEvent>();
        log.Add(Event(log, LogEntries.OperatorKeysAggregate, LogEntries.OperatorKeyType, "operator",
            Obj(("jwk", genuine.PublicJwk()), ("kid", new JsonValue.String(genuine.Kid)), ("valid_from", new JsonValue.String("2026-09-01T00:00:00Z")))));

        var pair = Obj(("alg", new JsonValue.String("EdDSA")), ("crv", new JsonValue.String("Ed25519")), ("kid", new JsonValue.String("k1")), ("kty", new JsonValue.String("OKP")), ("x", new JsonValue.String("HRzJlnTufZYYTZyCDBpyP5ldQ38JlbCeDOQHgIozgg8")));
        var root = new long[Identities];
        for (var n = 0; n < Identities; n++)
        {
            root[n] = log.Count;
            log.Add(Event(log, Agent(n), "agent.enrolled", Agent(n), Obj(("agent_id", new JsonValue.String(Agent(n))), ("kid", new JsonValue.String("k1")))));
            log.Add(Event(log, Agent(n), "agent.key-bound", Agent(n), Obj(("agent_id", new JsonValue.String(Agent(n))), ("jwk", pair), ("kid", new JsonValue.String("k1")))));
        }

        var last = (long[])root.Clone();
        var acts = honouredActs + forgedActs;
        var fillers = events - log.Count - acts;
        Assert.True(fillers >= 0, $"{events} events cannot hold {acts} act rows beside the rows before them");
        var perAct = acts == 0 ? 0 : fillers / acts;
        var genuineWritten = 0;
        for (var i = 0; i < acts; i++)
        {
            for (var f = 0; f < perAct; f++) log.Add(Filler(log));

            // Forged rows are spread evenly among the genuine ones.
            var forged = (i + 1) * forgedActs / acts > i * forgedActs / acts;
            if (forged)
            {
                log.Add(Attestation(log, Agent(Identities - 1), root[Identities - 1], verified: true, forger, genuine.Kid));
                continue;
            }

            var n = genuineWritten % (Identities - 1);
            log.Add(Attestation(log, Agent(n), last[n], verified: genuineWritten % 2 == 0, genuine, genuine.Kid));
            last[n] = log.Count - 1;
            genuineWritten++;
        }

        while (log.Count < events) log.Add(Filler(log));
        Assert.Equal(events, log.Count);

        // The checks: every genuine row honoured, every forged row skipped by the signature and by nothing before it.
        var decisions = OperatorActFold.Decide(log, PublishedKeys.OfLog(log), ActVectors.Verifiers());
        Assert.Equal(honouredActs, decisions.Count(d => d.Predicate is null));
        Assert.Equal(forgedActs, decisions.Count(d => d.Predicate == OperatorActPredicates.SignatureInvalid));
        Assert.Equal(acts, decisions.Length);
        return new Built(log, honouredActs, forgedActs);
    }

    /// <summary>
    /// One genuine signing input, its signature and the key's material: what <c>c</c> (the cost of one call of the ES256 verifier port, Amendment 4 ruling 52) is measured over, built with the builder's own
    /// <see cref="Sign"/> so the input is the one a fold hands the port (the encoded header, a full stop, the canonical bytes).
    /// </summary>
    internal static (byte[] Input, byte[] Signature, PublicKeyMaterial Key) PrimitiveSample()
    {
        using var signer = new InMemoryOperatorActSigner("operator-1");
        var canonical = Encoding.UTF8.GetBytes("{\"typ\":\"curia.owner-attestation/1\"}");
        var compact = Sign(signer, signer.Kid, canonical).Split('.');
        var input = Encoding.ASCII.GetBytes(compact[0] + ".").Concat(canonical).ToArray();
        var key = ClosedJwk.Read(signer.PublicJwk(), signer.Kid)?.Material(signer.Kid) ?? throw new InvalidOperationException("the operator key's JWK does not read");
        return (input, Base64Url.DecodeFromChars(compact[2]), key);
    }

    /// <summary>The p50 and p95 (the 15th and the 29th of 30 sorted runs) of <paramref name="run"/>, after three warm-up runs.</summary>
    internal static (double P50, double P95) Time(Action run)
    {
        for (var i = 0; i < 3; i++) run();
        var samples = new double[30];
        for (var i = 0; i < samples.Length; i++)
        {
            var started = Stopwatch.GetTimestamp();
            run();
            samples[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        Array.Sort(samples);
        return (samples[14], samples[28]);
    }
}
