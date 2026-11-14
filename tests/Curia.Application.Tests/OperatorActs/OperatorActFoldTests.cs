using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Curia.Application.Credentials;
using Curia.Application.Projections;
using Curia.Application.Tests.Credentials;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain;
using Xunit;

namespace Curia.Application.Tests.OperatorActs;

/// <summary>The conformance family <c>conformance/operator-acts/</c> as the fold's probes read it: the entries of each case, its subject and stream kind, the production verifiers (the vectors carry real signatures).</summary>
internal static class ActVectors
{
    internal static string Directory(string name) => Path.Combine(ConformanceLog.Root(), "operator-acts", name);

    internal static string[] Cases() => ConformanceLog.Names("operator-acts");

    /// <summary>The case's log, read from <c>entries/&lt;i&gt;.json</c> (the entry at log index <i>i</i>, R6.47).</summary>
    internal static List<AppendedEvent> LogOf(string name)
    {
        var log = new List<AppendedEvent>();
        for (var i = 0; File.Exists(Path.Combine(Directory(name), "entries", $"{i}.json")); i++)
            log.Add(ConformanceLog.Event(i, File.ReadAllBytes(Path.Combine(Directory(name), "entries", $"{i}.json"))));
        return log;
    }

    internal static JsonElement Meta(string name) => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Directory(name), "meta.json"))).RootElement;

    internal static string Subject(string name) => Meta(name).GetProperty("subject").GetString()!;

    internal static IReadOnlyDictionary<string, IContentVerifier> Verifiers() => new Dictionary<string, IContentVerifier>(StringComparer.Ordinal)
    {
        ["ES256"] = new Curia.Canon.Sodium.Es256Adapter(),
        ["EdDSA"] = new Curia.Canon.Sodium.Ed25519Adapter(),
    };

    internal static ImmutableArray<ActDecision> Decide(IReadOnlyList<AppendedEvent> log, IReadOnlyDictionary<string, IContentVerifier>? verifiers = null) =>
        OperatorActFold.Decide(log, PublishedKeys.OfLog(log), verifiers ?? Verifiers());

    internal static string Row(ActDecision d) => $"{d.LogIndex} {(d.Predicate is null ? "honoured" : "skipped")} {d.Predicate ?? "-"}";

    internal static string Rows(IEnumerable<ActDecision> rows) => string.Join(";", rows.Select(Row));

    internal static VerifiedLog Verified(IReadOnlyList<AppendedEvent> log) => ConformanceLog.Verifier().OfLog(log);

    private static string Word(bool value) => value ? "true" : "false";

    /// <summary>
    /// What the raw projectors say when fed the honoured events alone: the owner and whether it is verified (an agent's stream), whether the post is served (a post's). The one place a test
    /// feeds the raw projectors, and the end-to-end check that honoured rows alone decide the owner and the withholding (R4.43, R10.60).
    /// </summary>
    internal static string State(string stream, string subject, VerifiedLog verified)
    {
        var events = verified.HonouredEvents.Events;
        if (stream == "agent")
        {
            AgentStandingProjector.Fold(events).TryGetValue(subject, out var standing);
            return $"owner {standing?.OwnerId ?? "none"}; verified {Word(standing?.OwnerVerified ?? false)}";
        }

        var moderation = FlagProjector.Fold(events);
        return $"served {Word(!moderation.TryGetValue(subject, out var post) || post.MayServe)}";
    }

    /// <summary>The <c>state</c> block of a case's <c>expected.json</c> in the form <see cref="State"/> writes.</summary>
    internal static string StateOf(string stream, JsonElement expected)
    {
        var state = expected.GetProperty("state");
        if (stream == "agent")
        {
            var owner = state.TryGetProperty("owner", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null;
            return $"owner {owner ?? "none"}; verified {Word(state.TryGetProperty("verified", out var v) && v.ValueKind == JsonValueKind.True)}";
        }

        return $"served {Word(state.GetProperty("served").GetBoolean())}";
    }
}

/// <summary>
/// PR N2 (errata G70, R4.43, R6.67), Task 3: the act fold and <see cref="HonouredEvents"/>. Every expected string is written from R6.67's text, the vectors' <c>expected.json</c> and the plan, never from the fold.
/// A probe that asserts a skip also asserts a control that is honoured, in the same test.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class OperatorActFoldTests
{
    private const string Alice = "agent://curia.example/tuesdaycrowd/scriptor";
    private const string PostP1 = "01K4CQ4B0000000000000000P1";

    /// <summary>
    /// OAS8: the fold decides every one of the 76 cases' rows as <c>expected.json</c> says (index, verdict, predicate), and the raw projectors fed <see cref="HonouredEvents"/> alone say what its
    /// <c>state</c> says (the owner and whether it is verified; whether the post is served). Every disagreement is collected and the test fails once with the whole list.
    /// </summary>
    [Fact]
    public void OAS8_TheFoldDecidesEveryVectorAsItsExpectedSays()
    {
        var failures = new List<string>();
        var cases = ActVectors.Cases();
        Assert.True(cases.Length == 76, $"Expected: 76 cases  Actual: {cases.Length}");
        foreach (var name in cases)
        {
            try
            {
                using var expected = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ActVectors.Directory(name), "expected.json")));
                var meta = ActVectors.Meta(name);
                var subject = meta.GetProperty("subject").GetString()!;
                var stream = meta.GetProperty("stream").GetString()!;
                var log = ActVectors.LogOf(name);

                var want = string.Join(";", expected.RootElement.GetProperty("rows").EnumerateArray()
                    .Select(r => $"{r.GetProperty("log_index").GetInt64()} {r.GetProperty("verdict").GetString()} {(r.TryGetProperty("predicate", out var p) ? p.GetString() : "-")}"));
                // The stream's own rows: an agent's stream is its attestation rows and a post's its moderation rows (vector 75's aggregate is both, and holds one row of each kind).
                var rowType = stream == "agent" ? "agent.owner-attested" : "moderation.applied";
                var got = ActVectors.Rows(ActVectors.Decide(log).Where(d => d.Aggregate == subject && d.Type == rowType));
                if (want != got) failures.Add($"{name} rows: Expected: [{want}]  Actual: [{got}]");

                var wantState = ActVectors.StateOf(stream, expected.RootElement);
                var gotState = ActVectors.State(stream, subject, ActVectors.Verified(log));
                if (wantState != gotState) failures.Add($"{name} state: Expected: [{wantState}]  Actual: [{gotState}]");
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or InvalidCastException)
            {
                failures.Add($"{name} threw: {e.GetType().Name}: {e.Message}");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Select(f => f.Split(' ')[0]).Distinct(StringComparer.Ordinal).Count()} of {cases.Length} cases differ:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    /// <summary>
    /// OAS8b: <see cref="HonouredEvents"/> holds the log less exactly the skipped act rows, in order, each event keeping its own <c>Seq</c>; it is not a log (a later event's position in it is not its <c>Seq</c>);
    /// <c>Log</c> is the whole log; a lifecycle row and an unrelated event are kept.
    /// </summary>
    [Fact]
    public void OAS8b_HonouredEventsIsTheLogLessTheSkippedActRows()
    {
        HonouredEventsIsTheLogLess("moderation-forged-row-between-genuine-ones", 5);
        HonouredEventsIsTheLogLess("skipped-row-does-not-move-the-chain", 3);
    }

    private static void HonouredEventsIsTheLogLess(string name, int skippedIndex)
    {
        var log = ActVectors.LogOf(name);
        var honoured = ActVectors.Verified(log).HonouredEvents;

        var expectedEvents = log.Where((_, i) => i != skippedIndex).ToArray();
        Assert.Equal(expectedEvents.Select(e => e.Seq.Value), honoured.Events.Select(e => e.Seq.Value));
        Assert.Equal(expectedEvents.Select(e => e.Event.Id.Value), honoured.Events.Select(e => e.Event.Id.Value));
        Assert.Equal(log.Count - 1, honoured.Events.Length);

        // Not a log: the last event's Seq is the whole log's length, its position in Events is one less (R6.47).
        Assert.Equal(log.Count, honoured.Events[^1].Seq.Value);
        Assert.NotEqual(honoured.Events.Length, honoured.Events[^1].Seq.Value);

        Assert.Equal(log.Select(e => e.Event.Id.Value), honoured.Log.Select(e => e.Event.Id.Value));

        // A lifecycle row (the key pair at 1), the operator key's entry (2) and the post or enrolment root are kept.
        Assert.Contains(honoured.Events, e => e.Event.Type.Value == "agent.key-bound");
        Assert.Contains(honoured.Events, e => e.Event.Type.Value == "log.operator-key");
        Assert.DoesNotContain(honoured.Events, e => e.Event.Id.Value == log[skippedIndex].Event.Id.Value);
    }

    private static JsonValue.Object Object(params (string Name, JsonValue Value)[] members) =>
        new([.. members.Select(m => new KeyValuePair<string, JsonValue>(m.Name, m.Value))]);

    private static JsonValue.String Text(string value) => new(value);

    private const string Marker = "MARKER-7f3a";

    /// <summary>
    /// OAS8c: <c>Skipped()</c> lists lifecycle rows only (unchanged meaning), <c>ActSkipped()</c> the act rows only, <c>AllSkipped()</c> both in log order; and no member of a skipped row, and no
    /// <c>ToString()</c> of one, carries a payload member (R10.27, R10.28): a forged row whose payload holds a marker string says nothing of it.
    /// </summary>
    [Fact]
    public void OAS8c_AllSkippedListsBothKindsAndNoRowCarriesAPayload()
    {
        var at = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var jwk = Object(("alg", Text("EdDSA")), ("crv", Text("Ed25519")), ("kid", Text("k1")), ("kty", Text("OKP")), ("x", Text("HRzJlnTufZYYTZyCDBpyP5ldQ38JlbCeDOQHgIozgg8")));
        var log = new List<AppendedEvent>
        {
            LogEvents.At(0, Alice, at, "e0", "agent.enrolled", Alice, Object(("agent_id", Text(Alice)), ("kid", Text("k1")))),
            LogEvents.At(1, Alice, at, "e1", "agent.key-bound", Alice, Object(("agent_id", Text(Alice)), ("jwk", jwk), ("kid", Text("k1")))),
            // A lifecycle row that is not a retirement of the closed form: the lifecycle verifier skips it.
            LogEvents.At(2, Alice, at, "e2", "agent.key-retired", Alice, Object(("agent_id", Text(Alice)))),
            // An attestation with no statement: an act row the act fold skips.
            LogEvents.At(3, Alice, at, "e3", "agent.owner-attested", "operator:ops", Object(("agent_id", Text(Alice)), ("owner_id", Text("owner:alice")), ("owner_verified", new JsonValue.Bool(true)))),
            // A forged row whose payload holds the marker everywhere a payload could leak from.
            LogEvents.At(4, Alice, at, "e4", "agent.owner-attested", "operator:ops", Object(("statement", Text(Marker)), ("authorization", Text(Marker)), ("owner_id", Text(Marker)))),
        };

        var verified = ActVectors.Verified(log);
        Assert.Equal(["2 agent.key-retired"], verified.Skipped().Select(r => $"{r.LogIndex} {r.Type}"));
        Assert.Equal(["3 agent.owner-attested act-unsigned", "4 agent.owner-attested act-statement-mismatch"], verified.ActSkipped().Select(r => $"{r.LogIndex} {r.Type} {r.Predicate}"));
        Assert.Equal([2L, 3L, 4L], verified.AllSkipped().Select(r => r.LogIndex));

        foreach (var row in verified.AllSkipped())
        {
            Assert.DoesNotContain(Marker, row.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(Marker, string.Join('|', row.Type, row.Aggregate, row.Predicate), StringComparison.Ordinal);
        }

        // The control: the lifecycle rows that are honoured (the root and the pair) are in HonouredEvents, and no skipped row is.
        Assert.Equal(["e0", "e1", "e2"], verified.HonouredEvents.Events.Select(e => e.Event.Id.Value));
    }

    /// <summary>
    /// OAS14: a skipped act moves nothing. A forged attestation naming Bob (garbage authorization), then the genuine one for Alice whose <c>prev</c> is the enrolment: the genuine row is honoured, the owner is Alice.
    /// A forged restore between two genuine moderation rows is skipped as <c>act-unsigned</c> and the genuine restore, linking to the withhold, is honoured: the post is served again.
    /// </summary>
    [Fact]
    public void OAS14_ASkippedActMovesNothing()
    {
        var attestation = ActVectors.LogOf("skipped-row-does-not-move-the-chain");
        var rows = ActVectors.Decide(attestation).Where(d => d.Aggregate == Alice).ToArray();
        Assert.Equal(["3 skipped", "4 honoured"], rows.Select(r => $"{r.LogIndex} {(r.Predicate is null ? "honoured" : "skipped")}"));
        Assert.Equal("owner owner:alice; verified true", ActVectors.State("agent", Alice, ActVectors.Verified(attestation)));

        var moderation = ActVectors.LogOf("moderation-forged-row-between-genuine-ones");
        var moderationRows = ActVectors.Decide(moderation).Where(d => d.Aggregate == PostP1).ToArray();
        Assert.Equal([4L, 6L], moderationRows.Where(r => r.Predicate is null).Select(r => r.LogIndex));
        Assert.Equal("act-unsigned", moderationRows.Single(r => r.LogIndex == 5).Predicate);
        Assert.Equal("served true", ActVectors.State("post", PostP1, ActVectors.Verified(moderation)));
    }

    /// <summary>OAS26: a replay does not revive a lapse. The attestation at 3 and its lapse at 4 are honoured; the first statement and signature again at 5 names <c>prev</c> 0, and the previous honoured act is 4: <c>act-broken-link</c>. The owner stays, not verified.</summary>
    [Fact]
    public void OAS26_AReplayDoesNotReviveALapse()
    {
        var log = ActVectors.LogOf("attestation-replayed-after-lapse");
        var rows = ActVectors.Decide(log).Where(d => d.Aggregate == Alice).ToArray();
        Assert.Equal(["3 honoured -", "4 honoured -", "5 skipped act-broken-link"], rows.Select(ActVectors.Row));
        Assert.Equal("owner owner:alice; verified false", ActVectors.State("agent", Alice, ActVectors.Verified(log)));
    }

    /// <summary>OAS38: only <c>human</c> is a moderator: a statement with <c>automated</c> or <c>delegated_agent</c>, correctly signed, is <c>act-statement-mismatch</c>; the honoured twin, the same with <c>human</c>, is honoured.</summary>
    [Theory]
    [InlineData("moderation-moderator-automated-is-not-honoured")]
    [InlineData("moderation-moderator-delegated-agent-is-not-honoured")]
    public void OAS38_OnlyHumanIsAModerator(string name)
    {
        var skipped = ActVectors.Decide(ActVectors.LogOf(name)).Where(d => d.Aggregate == PostP1).ToArray();
        Assert.Equal(["4 skipped act-statement-mismatch"], skipped.Select(ActVectors.Row));

        var twin = ActVectors.Decide(ActVectors.LogOf("moderation-honoured")).Where(d => d.Aggregate == PostP1).ToArray();
        Assert.Equal(["4 honoured -"], twin.Select(ActVectors.Row));
    }

    /// <summary>The malformed payloads <c>OAS33</c> (Api) appends, as data: a string, an array, wrong-typed members, a non-string statement.</summary>
    private static readonly (string Type, string Payload)[] MalformedActPayloads =
    [
        ("agent.owner-attested", "\"not an object\""),
        ("agent.owner-attested", "[1,2,3]"),
        ("agent.owner-attested", "{\"owner_id\":7}"),
        ("agent.owner-attested", "{\"statement\":5,\"authorization\":{}}"),
        ("moderation.applied", "\"not an object\""),
        ("moderation.applied", "{\"effect\":[],\"moderator\":null}"),
        ("moderation.applied", "{\"statement\":\"{}\",\"authorization\":\"x\"}"),
    ];

    private static readonly string[] ReplacementPayloads = ["null", "true", "1", "\"x\"", "[]", "{}"];

    private static JsonValue ParsedPayload(string json) =>
        JsonReader.ParseUnrestricted(System.Text.Encoding.UTF8.GetBytes(json)).Match(v => v, e => throw new InvalidOperationException(e.Type));

    private static AppendedEvent WithPayload(AppendedEvent e, long index, JsonValue payload) =>
        LogEvents.At(index, e.AggregateId.Value, e.ServerTimestamp.Value, e.Event.Id.Value, e.Event.Type.Value, e.Event.Actor?.Value ?? string.Empty, payload);

    /// <summary>
    /// OAS33b: the fold never throws and never returns a failure for a payload. Each of OAS33's seven malformed payloads, in a stream with a genuine act (the control, honoured), decides to a skipped row
    /// with one of the eight predicates; and every row of every vector with its payload replaced in turn by JSON <c>null</c>, <c>true</c>, <c>1</c>, <c>"x"</c>, <c>[]</c> and <c>{}</c> decides without throwing, an act row
    /// to a skip with one of the eight.
    /// </summary>
    [Fact]
    public void OAS33b_TheFoldNeverThrowsOnAnyPayload()
    {
        foreach (var (type, payload) in MalformedActPayloads)
            FoldSkipsTheMalformedPayloadAndHonoursTheControl(type, payload);

        EveryRowOfEveryVectorWithAnyPayloadDecidesWithoutThrowing();
    }

    private static void FoldSkipsTheMalformedPayloadAndHonoursTheControl(string type, string payload)
    {
        var name = type == "agent.owner-attested" ? "attestation-honoured" : "moderation-honoured";
        var log = ActVectors.LogOf(name);
        var subject = ActVectors.Subject(name);
        var malformed = LogEvents.At(log.Count, subject, log[^1].ServerTimestamp.Value, "malformed", type, "operator:ops", ParsedPayload(payload));
        log.Add(malformed);

        var rows = ActVectors.Decide(log).Where(d => d.Aggregate == subject).ToArray();
        Assert.Null(rows.Single(r => r.LogIndex == 3 + (type == "agent.owner-attested" ? 0 : 1)).Predicate);
        var last = rows.Single(r => r.LogIndex == log.Count - 1);
        Assert.Contains(last.Predicate, OperatorActPredicates.All);
    }

    private static void EveryRowOfEveryVectorWithAnyPayloadDecidesWithoutThrowing()
    {
        var decided = 0;
        foreach (var name in ActVectors.Cases())
        {
            var log = ActVectors.LogOf(name);
            for (var i = 0; i < log.Count; i++)
                foreach (var replacement in ReplacementPayloads)
                {
                    var mutated = new List<AppendedEvent>(log) { [i] = WithPayload(log[i], i, ParsedPayload(replacement)) };
                    var rows = ActVectors.Decide(mutated);
                    decided++;
                    var type = log[i].Event.Type.Value;
                    if (type is "agent.owner-attested" or "moderation.applied")
                    {
                        var row = rows.Single(r => r.LogIndex == i);
                        Assert.True(row.Predicate is not null && OperatorActPredicates.All.Contains(row.Predicate), $"{name} row {i} payload {replacement}: Expected: a skip by one of the eight  Actual: {ActVectors.Row(row)}");
                    }
                }
        }

        Assert.True(decided > 1000, $"Expected: the data to be the vectors' rows x six payloads  Actual: {decided} folds");
    }

    /// <summary>Counts the calls the primitive receives: the cost is the signatures verified (R6.67's order puts the signature last, so a row a cheaper step skips costs none).</summary>
    private sealed class CountingVerifier(IContentVerifier inner) : IContentVerifier
    {
        internal int Calls { get; private set; }

        public bool Verify(ReadOnlySpan<byte> input, ReadOnlySpan<byte> sig, PublicKeyMaterial key)
        {
            Calls++;
            return inner.Verify(input, sig, key);
        }
    }

    /// <summary>
    /// OAS30a, the cost, deterministic: over each of the 76 cases the primitive is called once for each row that reaches step 7 (a row <c>expected.json</c> says is honoured, or skipped as
    /// <c>act-signature-invalid</c>) and for no other, except that a case whose <c>meta.json</c> carries <c>primitive_calls</c> states its total (a row can reach step 7 without the primitive being called: a key
    /// off its curve, and a header R6.37 refuses, are <c>act-signature-invalid</c> and cost no verification, vectors 62, 66 and 67); and asking a <see cref="VerifiedLog"/> for <c>HonouredEvents</c> a second time, or for the skipped act rows, verifies nothing again.
    /// </summary>
    [Fact]
    public void OAS30a_TheSignatureIsVerifiedOncePerRowThatReachesItAndNeverTwice()
    {
        var failures = new List<string>();
        foreach (var name in ActVectors.Cases())
        {
            using var expected = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ActVectors.Directory(name), "expected.json")));
            var reaching = expected.RootElement.GetProperty("rows").EnumerateArray()
                .Count(r => r.GetProperty("verdict").GetString() == "honoured" || (r.TryGetProperty("predicate", out var p) && p.GetString() == OperatorActPredicates.SignatureInvalid));
            using var meta = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ActVectors.Directory(name), "meta.json")));
            if (meta.RootElement.TryGetProperty("primitive_calls", out var stated)) reaching = stated.GetInt32();
            var log = ActVectors.LogOf(name);

            var direct = new CountingVerifier(new Curia.Canon.Sodium.Es256Adapter());
            ActVectors.Decide(log, new Dictionary<string, IContentVerifier>(StringComparer.Ordinal) { ["ES256"] = direct, ["EdDSA"] = new Curia.Canon.Sodium.Ed25519Adapter() });
            if (direct.Calls != reaching) failures.Add($"{name}: Expected: {reaching} verifications  Actual: {direct.Calls}");

            var counting = new CountingVerifier(new Curia.Canon.Sodium.Es256Adapter());
            var verified = new LifecycleVerifier(new Dictionary<string, IContentVerifier>(StringComparer.Ordinal) { ["ES256"] = counting, ["EdDSA"] = new Curia.Canon.Sodium.Ed25519Adapter() }).OfLog(log);
            _ = verified.HonouredEvents;
            var afterFirst = counting.Calls;
            _ = verified.HonouredEvents;
            _ = verified.ActSkipped();
            _ = verified.AllSkipped();
            if (afterFirst != reaching || counting.Calls != afterFirst) failures.Add($"{name} (memo): Expected: {reaching} verifications, then no more  Actual: {afterFirst}, then {counting.Calls}");
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}

/// <summary>OAS30m's own collection: a measurement taken while the rest of the suite runs beside it measures the suite, so the collection runs alone.</summary>
[CollectionDefinition("operator-act-cost", DisableParallelization = true)]
[SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "xUnit requires a collection definition to be public.")]
public sealed class OperatorActCostGroup;

[Collection("operator-act-cost")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class OperatorActCostTests
{
    private static void Report(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    /// <summary>
    /// OAS30m, a measurement (restated by Amendment 4, rulings 52 and 53): <c>c</c> is the median of 2,000 calls of the ES256 verifier port over one signing input and one key, taken in this process just before the
    /// timed runs. Then, on a <see cref="VerifiedLog"/> built fresh per run, 3 warm-up and 30 timed runs: (a) 10,000 events with 20 lifecycle rows and N honoured act rows, N in {50, 100, 200, 400}; (b) N = 200
    /// with 1,000 forged well-structured rows. Baseline is the lifecycle view alone (<c>Skipped()</c>); folded adds <c>HonouredEvents</c>. The assertions are ratios to <c>c</c>, which a loaded host moves in the
    /// same breath: (a) <c>(F p50 - B p50) &lt;= 5 * N * c</c>, (b) <c>&lt;= 5 * forged * c</c>. Ruling 52's stops ((a1) linearity, (a2) the fold's cost against the primitive's) are not assertions: the implementer
    /// reads them from the numbers this writes. A report-only leg folds K fresh logs at once and reports wall time per fold and process CPU per fold. Every number goes to the test output.
    /// </summary>
    [Fact]
    public async Task OAS30m_TheFoldsCostOnTenThousandEventsStaysWithinFiveTimesThePrimitive()
    {
        var verifier = ConformanceLog.Verifier();

        var (input, signature, key) = OperatorActsCostLog.PrimitiveSample();
        var es256 = new Curia.Canon.Sodium.Es256Adapter();
        Assert.True(es256.Verify(input, signature, key), "the sample signature does not verify");
        for (var i = 0; i < 200; i++) _ = es256.Verify(input, signature, key);
        var calls = new double[2000];
        for (var i = 0; i < calls.Length; i++)
        {
            var started = Stopwatch.GetTimestamp();
            _ = es256.Verify(input, signature, key);
            calls[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        Array.Sort(calls);
        var c = calls[1000];
        Report($"OAS30m c (median of 2000 ES256 verifier-port calls): {c:F4} ms; p95 {calls[1900]:F4} ms");

        var failures = new List<string>();
        var perRow = new Dictionary<int, double>();
        double over200 = 0;
        foreach (var (label, honoured, forged) in new[] { ("(a) N=50", 50, 0), ("(a) N=100", 100, 0), ("(a) N=200", 200, 0), ("(a) N=400", 400, 0), ("(b) N=200 + 1,000 forged", 200, 1000) })
        {
            var built = OperatorActsCostLog.Build(events: 10_000, honouredActs: honoured, forgedActs: forged);
            var baseline = OperatorActsCostLog.Time(() => _ = verifier.OfLog(built.Log).Skipped());
            var folded = OperatorActsCostLog.Time(() =>
            {
                var fresh = verifier.OfLog(built.Log);
                _ = fresh.Skipped();
                _ = fresh.HonouredEvents;
            });
            var rows = forged > 0 ? forged : honoured;
            var over50 = folded.P50 - baseline.P50;
            var over95 = folded.P95 - baseline.P95;
            Report($"OAS30m {label}: B p50 {baseline.P50:F2} p95 {baseline.P95:F2} ms; F p50 {folded.P50:F2} p95 {folded.P95:F2} ms; F-B p50 {over50:F2} p95 {over95:F2} ms; per row (F-B p50)/{(forged > 0 ? "forged" : "N")} {over50 / rows:F4} ms; (F-B p50)/(rows*c) {over50 / (rows * c):F2}");
            if (forged == 0) perRow[honoured] = over50 / honoured;
            if (forged == 0 && honoured == 200) over200 = over50;
            if (over50 > 5 * rows * c) failures.Add($"{label}: Expected: (F-B) at p50 at most 5 * {rows} * c = {5 * rows * c:F1} ms  Actual: {over50:F1} ms");
        }

        Report($"OAS30m (a1) per-row cost by N: {string.Join(", ", perRow.OrderBy(p => p.Key).Select(p => $"N={p.Key}: {p.Value:F4} ms"))}; dearest/cheapest {perRow.Values.Max() / perRow.Values.Min():F2} (stop above 1.5)");
        Report($"OAS30m (a2) (F-B p50 at N=200)/(N*c) {over200 / (200 * c):F2} (stop above 2); break-even: 25 ms / per-row cost = {25 / perRow[200]:F0} honoured acts; 250 ms / forged per-row cost, see the (b) row");

        var concurrent = OperatorActsCostLog.Build(events: 10_000, honouredActs: 200, forgedActs: 0);
        var k = Math.Min(8, Environment.ProcessorCount);
        var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        var each = Enumerable.Range(0, k).Select(fold => Task.Run(() =>
        {
            var started = Stopwatch.GetTimestamp();
            var fresh = verifier.OfLog(concurrent.Log);
            _ = fresh.Skipped();
            _ = fresh.HonouredEvents;
            return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        })).ToArray();
        var perFold = await Task.WhenAll(each);
        wall.Stop();
        process.Refresh();
        var cpu = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        Report($"OAS30m concurrency (report only): K={k} folds of fresh logs at once, N=200: wall {wall.Elapsed.TotalMilliseconds:F1} ms for all; per fold min {perFold.Min():F1} max {perFold.Max():F1} mean {perFold.Average():F1} ms (serial figure {over200:F1} ms over baseline); process CPU {cpu:F1} ms in all, {cpu / k:F1} ms per fold");

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}
