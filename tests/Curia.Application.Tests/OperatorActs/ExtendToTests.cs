using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
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
/// Register item 286 (errata G71, R9.65), Amendment 3 rulings 28 to 31: <c>VerifiedLog.ExtendTo</c> verifies only the rows a longer read added, and decides them as a whole <c>OfLog</c> of the longer read
/// would. The oracle is never the code under test run twice: EXT1 holds every split of every conformance vector to the vector's own <c>expected.json</c>; EXT2 counts signature verifications at the
/// primitive; EXT3 holds a longer read that does not begin with the log to a whole verification; EXT4 writes the delta's rows from R6.67's text.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ExtendToTests
{
    private sealed class CountingVerifier(IContentVerifier inner, Counter counter) : IContentVerifier
    {
        public bool Verify(ReadOnlySpan<byte> input, ReadOnlySpan<byte> sig, PublicKeyMaterial key)
        {
            counter.Calls++;
            return inner.Verify(input, sig, key);
        }
    }

    private sealed class Counter
    {
        internal long Calls { get; set; }
    }

    private static LifecycleVerifier Counting(Counter counter) =>
        new(ActVectors.Verifiers().ToDictionary(p => p.Key, p => (IContentVerifier)new CountingVerifier(p.Value, counter), StringComparer.Ordinal));

    private static string Skipped(VerifiedLog log) => string.Join(";", log.ActSkipped().Select(r => $"{r.LogIndex} {r.Predicate}"));

    private static string Honoured(VerifiedLog log) => string.Join(",", log.HonouredEvents.Events.Select(e => e.Seq.Value));

    private static List<AppendedEvent> Prefix(IReadOnlyList<AppendedEvent> log, int length) => [.. log.Take(length)];

    /// <summary>The number of cases <c>conformance/index.json</c> declares for the operator-acts family: the control the theory's case count is held to.</summary>
    private static int IndexCount()
    {
        using var index = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ConformanceLog.Root(), "index.json")));
        return index.RootElement.GetProperty("directories").EnumerateArray()
            .Single(e => e.GetProperty("name").GetString() == "operator-acts").GetProperty("count").GetInt32();
    }

    public static TheoryData<string> VectorCases() => new(ActVectors.Cases());

    /// <summary>EXT1's control: the theory has a case for every vector the index declares (and the vectors on disk are as many).</summary>
    [Fact]
    public void EXT1_TheTheoryHasOneCaseForEveryVectorTheIndexDeclares()
    {
        var declared = IndexCount();
        Assert.True(declared == VectorCases().Count, $"Expected: {declared} cases  Actual: {VectorCases().Count}");
        Assert.True(declared == ActVectors.Cases().Length, $"Expected: {declared} case directories  Actual: {ActVectors.Cases().Length}");
    }

    /// <summary>
    /// EXT1: for every split <i>k</i> from 0 to the log's length, <c>OfLog(log[0..k]).ExtendTo(log)</c> skips the rows the case's <c>expected.json</c> says it skips, with the predicates it names (the
    /// vector is the oracle, not the whole-log run), and equals <c>OfLog(log)</c> in every skipped row of every stream and in the honoured events.
    /// </summary>
    [Theory]
    [MemberData(nameof(VectorCases))]
    public void EXT1_ExtendingAPrefixDecidesAsTheVectorSaysAtEverySplit(string name)
    {
        using var expected = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ActVectors.Directory(name), "expected.json")));
        var meta = ActVectors.Meta(name);
        var subject = meta.GetProperty("subject").GetString()!;
        var rowType = meta.GetProperty("stream").GetString() == "agent" ? "agent.owner-attested" : "moderation.applied";
        var wantSkipped = string.Join(";", expected.RootElement.GetProperty("rows").EnumerateArray()
            .Where(r => r.GetProperty("verdict").GetString() == "skipped")
            .Select(r => $"{r.GetProperty("log_index").GetInt64()} {r.GetProperty("predicate").GetString()}"));

        var log = ActVectors.LogOf(name);
        var whole = ActVectors.Verified(log);
        var failures = new List<string>();
        for (var k = 0; k <= log.Count; k++)
        {
            var extended = ActVectors.Verified(Prefix(log, k)).ExtendTo(log);
            var got = string.Join(";", extended.ActSkipped().Where(r => r.Aggregate == subject && r.Type == rowType).Select(r => $"{r.LogIndex} {r.Predicate}"));
            if (got != wantSkipped) failures.Add($"split {k} vs expected.json: Expected: [{wantSkipped}]  Actual: [{got}]");
            if (Skipped(extended) != Skipped(whole)) failures.Add($"split {k} vs OfLog: Expected: [{Skipped(whole)}]  Actual: [{Skipped(extended)}]");
            if (Honoured(extended) != Honoured(whole)) failures.Add($"split {k} honoured events: Expected: [{Honoured(whole)}]  Actual: [{Honoured(extended)}]");
        }

        Assert.True(failures.Count == 0, $"{name}:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    /// <summary>A log of <c>events</c> events whose act rows are all genuine, and the split that leaves <paramref name="before"/> of them in the prefix.</summary>
    private static (List<AppendedEvent> Log, int Split, int Before, int After) Acts(int before, int after)
    {
        var log = OperatorActsCostLog.Build(120, before + after).Log;
        var actRows = log.Select((e, i) => (e, i)).Where(p => p.e.Event.Type.Value == "agent.owner-attested").Select(p => p.i).ToList();
        Assert.Equal(before + after, actRows.Count);
        return (log, before == 0 ? actRows[0] : actRows[before - 1] + 1, before, after);
    }

    /// <summary>
    /// EXT2: over a log with <i>p</i> genuine act rows before the split and <i>d</i> after, <c>OfLog(prefix)</c> makes <i>p</i> verifications at the primitive, <c>ExtendTo(whole)</c> makes exactly <i>d</i> more, and
    /// <c>ExtendTo</c> of the same log makes none; the views the consumers read (standings, moderation, honoured events) make none more.
    /// </summary>
    [Theory]
    [InlineData(5, 3)]
    [InlineData(0, 4)]
    [InlineData(4, 1)]
    public void EXT2_AnExtensionVerifiesOnlyTheRowsTheLongerReadAdded(int before, int after)
    {
        var (log, split, p, d) = Acts(before, after);
        var counter = new Counter();
        var verifier = Counting(counter);

        var prefix = verifier.OfLog(Prefix(log, split));
        _ = prefix.ActSkipped();
        Assert.True(counter.Calls == p, $"Expected: {p} verifications of the prefix  Actual: {counter.Calls}");

        var extended = prefix.ExtendTo(log);
        _ = extended.ActSkipped();
        Assert.True(counter.Calls == p + d, $"Expected: {d} more verifications  Actual: {counter.Calls - p}");
        Assert.Equal(d + p, log.Count(e => e.Event.Type.Value == "agent.owner-attested"));
        Assert.Empty(extended.ActSkipped());

        var again = extended.ExtendTo(log);
        _ = again.ActSkipped();
        Assert.True(counter.Calls == p + d, $"Expected: 0 more verifications of the same log  Actual: {counter.Calls - p - d}");

        _ = extended.Standings();
        _ = extended.Moderation();
        _ = extended.HonouredEvents;
        _ = extended.AllSkipped();
        Assert.True(counter.Calls == p + d, $"Expected: 0 more verifications by the views  Actual: {counter.Calls - p - d}");
    }

    private static AppendedEvent WithSeq(AppendedEvent e, long index) =>
        LogEvents.At(index, e.AggregateId.Value, e.ServerTimestamp.Value, e.Event.Id.Value, e.Event.Type.Value, e.Event.Actor!.Value.Value, e.Event.Payload);

    /// <summary>
    /// EXT3: a longer read that does not begin with the log (a different <c>Seq</c> at the prefix's first row, or at its last; or a read shorter than the log) is verified whole: the verifications equal a
    /// whole <c>OfLog</c>'s, and the decisions are a whole <c>OfLog</c>'s.
    /// </summary>
    [Theory]
    [InlineData("first")]
    [InlineData("last")]
    [InlineData("shorter")]
    public void EXT3_ALongerReadThatDoesNotBeginWithTheLogIsVerifiedWhole(string disagreement)
    {
        var (whole, split, _, _) = Acts(4, 3);
        var wholeCounter = new Counter();
        var wholeVerified = Counting(wholeCounter).OfLog(whole);
        var wholeSkipped = Skipped(wholeVerified);
        var wholeCalls = wholeCounter.Calls;
        Assert.True(wholeCalls == 7, $"Expected: 7 verifications of a whole log  Actual: {wholeCalls}");

        List<AppendedEvent> prior = Prefix(whole, split);
        IReadOnlyList<AppendedEvent> longer = whole;
        switch (disagreement)
        {
            case "first":
                prior[0] = WithSeq(prior[0], 500);
                break;
            case "last":
                prior[^1] = WithSeq(prior[^1], 500);
                break;
            default:
                prior = Prefix(whole, whole.Count);
                longer = Prefix(whole, split);
                break;
        }

        var counter = new Counter();
        var verified = Counting(counter).OfLog(prior);
        _ = verified.ActSkipped();
        var before = counter.Calls;
        var extended = verified.ExtendTo(longer);
        _ = extended.ActSkipped();

        var wanted = longer.Count(e => e.Event.Type.Value == "agent.owner-attested");
        Assert.True(counter.Calls - before == wanted, $"Expected: {wanted} verifications, those of a whole OfLog of the longer read  Actual: {counter.Calls - before}");
        Assert.Equal(Skipped(Counting(new Counter()).OfLog(longer)), Skipped(extended));
        Assert.Equal(Honoured(Counting(new Counter()).OfLog(longer)), Honoured(extended));
        if (disagreement != "shorter") Assert.Equal(wholeSkipped, Skipped(extended));
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------
    // EXT4: a delta written from R6.67's text. A small log is built here, with the same signer double and the statement shape the cost log builds (its builder is private to that file).
    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------

    private const string OperatorActor = "operator:ops";
    private const string AgentA = "agent://curia.example/ext/a";
    private const string AgentB = "agent://curia.example/ext/b";

    private static JsonValue.Object Obj(params (string Name, JsonValue Value)[] members) =>
        new([.. members.Select(m => new KeyValuePair<string, JsonValue>(m.Name, m.Value))]);

    private static AppendedEvent Row(List<AppendedEvent> log, string aggregate, string type, string actor, JsonValue payload) =>
        LogEvents.At(log.Count, aggregate, new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), $"ext-{log.Count}", type, actor, payload);

    private static string Sign(IOperatorActSigner key, string headerKid, byte[] canonical)
    {
        var header = Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{{\"alg\":\"ES256\",\"kid\":\"{headerKid}\",\"typ\":\"curia-operator-act+jws\",\"b64\":false,\"crit\":[\"b64\"]}}"));
        var input = Encoding.ASCII.GetBytes(header + ".").Concat(canonical).ToArray();
        var signature = key.SignStatement(input).Match(s => s, e => throw new InvalidOperationException(e.Type));
        return $"{header}..{Base64Url.EncodeToString(signature)}";
    }

    private static AppendedEvent Attest(List<AppendedEvent> log, string agent, long prev, IOperatorActSigner signer, string headerKid)
    {
        var statement = Obj(
            ("actor_id", new JsonValue.String(OperatorActor)),
            ("agent_id", new JsonValue.String(agent)),
            ("method", new JsonValue.String("manual")),
            ("owner_id", new JsonValue.String("owner:alice")),
            ("owner_verified", new JsonValue.Bool(true)),
            ("prev", new JsonValue.Number(prev)),
            ("reason", new JsonValue.String("ext")),
            ("typ", new JsonValue.String("curia.owner-attestation/1")));
        var canonical = CanonicalJson.CanonicalizeWithNfc(statement).Match(c => c.ToArray(), e => throw new InvalidOperationException(e.Type));
        var members = statement.Members.Where(m => m.Key != "typ").Select(m => (m.Key, m.Value)).ToList();
        members.Add(("statement", new JsonValue.String(Encoding.UTF8.GetString(canonical))));
        members.Add(("authorization", new JsonValue.String(Sign(signer, headerKid, canonical))));
        return Row(log, agent, "agent.owner-attested", OperatorActor, Obj([.. members.OrderBy(m => m.Key, StringComparer.Ordinal)]));
    }

    /// <summary>Index 0 the operator key's entry; 1 the root of agent A's stream and 2 the root of agent B's.</summary>
    private static List<AppendedEvent> Opening(InMemoryOperatorActSigner genuine)
    {
        var log = new List<AppendedEvent>();
        log.Add(Row(log, LogEntries.OperatorKeysAggregate, LogEntries.OperatorKeyType, "operator",
            Obj(("jwk", genuine.PublicJwk()), ("kid", new JsonValue.String(genuine.Kid)), ("valid_from", new JsonValue.String("2026-09-01T00:00:00Z")))));
        foreach (var agent in new[] { AgentA, AgentB })
            log.Add(Row(log, agent, "agent.enrolled", agent, Obj(("agent_id", new JsonValue.String(agent)), ("kid", new JsonValue.String("k1")))));
        return log;
    }

    /// <summary>
    /// Every split of <paramref name="log"/> extends to what a whole <c>OfLog</c> decides; the extension at <paramref name="split"/> says exactly <paramref name="want"/> (R6.67's text decides it, written
    /// by hand in the test) and honours the rows the test names honoured.
    /// </summary>
    private static void Holds(List<AppendedEvent> log, int split, string want)
    {
        var whole = ActVectors.Verified(log);
        Assert.True(want == Skipped(whole), $"the whole log, Expected: [{want}]  Actual: [{Skipped(whole)}]");
        for (var k = 0; k <= log.Count; k++)
        {
            var extended = ActVectors.Verified(Prefix(log, k)).ExtendTo(log);
            Assert.True(Skipped(extended) == want, $"split {k}, Expected: [{want}]  Actual: [{Skipped(extended)}]");
        }

        var atSplit = ActVectors.Verified(Prefix(log, split)).ExtendTo(log);
        Assert.True(Skipped(atSplit) == want, $"split {split}, Expected: [{want}]  Actual: [{Skipped(atSplit)}]");
    }

    /// <summary>EXT4 (step 7): a row in the delta whose signature is another key's, under the operator key's kid, is skipped act-signature-invalid; a genuine row beside it is honoured.</summary>
    [Fact]
    public void EXT4_Step7_ADeltaRowSignedByAnotherKeyIsSkippedAndAGenuineOneIsHonoured()
    {
        using var genuine = new InMemoryOperatorActSigner("operator-1");
        using var forger = new InMemoryOperatorActSigner("forger");
        var log = Opening(genuine);
        log.Add(Attest(log, AgentA, 1, genuine, genuine.Kid)); // 3: honoured, in the prefix at split 4
        log.Add(Attest(log, AgentB, 2, forger, genuine.Kid)); // 4: forged, in the delta
        log.Add(Attest(log, AgentA, 3, genuine, genuine.Kid)); // 5: genuine, chains to the honoured prefix row

        Holds(log, 4, "4 act-signature-invalid");
    }

    /// <summary>EXT4 (step 6): a row in the delta naming a kid the log does not publish is skipped act-key-unpublished before any signature is checked.</summary>
    [Fact]
    public void EXT4_Step6_ADeltaRowNamingAnUnpublishedKeyIsSkipped()
    {
        using var genuine = new InMemoryOperatorActSigner("operator-1");
        var log = Opening(genuine);
        log.Add(Attest(log, AgentA, 1, genuine, genuine.Kid)); // 3: honoured
        log.Add(Attest(log, AgentB, 2, genuine, "ghost-key")); // 4: names a key nothing published
        log.Add(Attest(log, AgentA, 3, genuine, genuine.Kid)); // 5: honoured

        Holds(log, 4, "4 act-key-unpublished");
    }

    /// <summary>EXT4 (step 4): a row in the delta whose prev is not the stream's last honoured act (or its root) is skipped act-broken-link, though its signature is genuine.</summary>
    [Fact]
    public void EXT4_Step4_ADeltaRowWithABrokenPrevIsSkipped()
    {
        using var genuine = new InMemoryOperatorActSigner("operator-1");
        var log = Opening(genuine);
        log.Add(Attest(log, AgentA, 1, genuine, genuine.Kid)); // 3: honoured
        log.Add(Attest(log, AgentA, 1, genuine, genuine.Kid)); // 4: prev is the root, not row 3: broken
        log.Add(Attest(log, AgentA, 3, genuine, genuine.Kid)); // 5: chains to row 3: honoured

        Holds(log, 4, "4 act-broken-link");
    }

    /// <summary>EXT4 (step 4, chain): a delta row that chains to an honoured act of the prefix is honoured, whichever way the log is split between the two.</summary>
    [Fact]
    public void EXT4_Step4_ADeltaRowChainingToAnHonouredPrefixRowIsHonoured()
    {
        using var genuine = new InMemoryOperatorActSigner("operator-1");
        var log = Opening(genuine);
        log.Add(Attest(log, AgentA, 1, genuine, genuine.Kid)); // 3
        log.Add(Attest(log, AgentA, 3, genuine, genuine.Kid)); // 4
        log.Add(Attest(log, AgentA, 4, genuine, genuine.Kid)); // 5

        Holds(log, 4, string.Empty);
        var counter = new Counter();
        var prefix = Counting(counter).OfLog(Prefix(log, 4));
        _ = prefix.ActSkipped();
        var extended = prefix.ExtendTo(log);
        Assert.Equal("1,2,3,4,5,6", Honoured(extended));
        Assert.True(counter.Calls == 3, $"Expected: 3 verifications in all  Actual: {counter.Calls}");
    }

    /// <summary>EXT4 (step 4, skipped link): a delta row that chains to a row the prefix skipped is skipped act-broken-link, and a delta row that chains to the stream's root past it is honoured.</summary>
    [Fact]
    public void EXT4_Step4_ADeltaRowChainingToASkippedPrefixRowIsSkippedAsBrokenLink()
    {
        using var genuine = new InMemoryOperatorActSigner("operator-1");
        using var forger = new InMemoryOperatorActSigner("forger");
        var log = Opening(genuine);
        log.Add(Attest(log, AgentA, 1, forger, genuine.Kid)); // 3: forged, skipped in the prefix
        log.Add(Attest(log, AgentA, 3, genuine, genuine.Kid)); // 4: chains to the skipped row: broken
        log.Add(Attest(log, AgentA, 1, genuine, genuine.Kid)); // 5: chains to the root: honoured

        Holds(log, 4, "3 act-signature-invalid;4 act-broken-link");
    }
}
