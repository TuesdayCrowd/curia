using Curia.Application.Ports;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Primitives;
using CsCheck;
using Xunit;

namespace Curia.Application.Tests;

/// <summary>
/// The <see cref="IEventStore"/> port contract every implementation must satisfy, independent of
/// how (or whether) it persists anything durably. <see cref="InMemoryEventStoreTests"/> below is
/// the Stage 1 subclass. Stage 2's Postgres-backed adapter is meant to get its own
/// <c>Curia.Infrastructure.Tests</c> project that references this project and subclasses this
/// exact class rather than writing a parallel suite -- that reuse is the Stage 1 brief's whole
/// point ("Put it where the test projects can use it and Stage 2's Postgres adapter can be
/// checked against the *same* suite").
/// </summary>
public abstract class EventStorePortContractTests
{
    /// <summary>A fresh, empty store. Called once per test (and, for the property test below,
    /// once per generated case), so tests never see state another test left behind.</summary>
    protected abstract IEventStore CreateStore();

    private static DomainEvent NewEvent(string id, string type = "test.event", JsonValue? payload = null) => new(
        Require(EventId.Create(id)),
        Require(EventType.Create(type)),
        Actor: null,
        Payload: payload ?? new JsonValue.Object([]));

    private static AggregateId Agg(string value) => Require(AggregateId.Create(value));

    // The literal orders PayloadMembersReadBackInCanonicalOrderAtEveryDepth pins, hoisted to
    // fields only because CA1861 objects to constant array arguments inside a loop; they are
    // the assertion's whole content, so they are named for what they are and kept next to it.
    private static readonly string[] CanonicalRootMemberOrder = ["a", "arr", "longer_key_b", "z"];
    private static readonly string[] CanonicalNestedMemberOrder = ["b", "q"];
    private static readonly string[] DecomposedKeyUnchanged = ["cafe\u0301"];

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    [Fact]
    public async Task AppendThenReadRoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var aggregateId = Agg("agg-round-trip");
        var proposed = NewEvent("evt-1");

        var appended = Require(await store.AppendAsync(aggregateId, AggregateVersion.New, [proposed], ct));
        Assert.Single(appended);
        Assert.Equal(proposed.Id, appended[0].Event.Id);
        Assert.Equal(aggregateId, appended[0].AggregateId);

        var read = Require(await store.ReadByAggregateAsync(aggregateId, ct));
        Assert.Single(read);
        Assert.Equal(appended[0].Seq, read[0].Seq);
        Assert.Equal(proposed.Id, read[0].Event.Id);
    }

    [Fact]
    public async Task AppendingAnEmptyBatchFailsWithoutMutatingTheStore()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var aggregateId = Agg("agg-empty");

        var result = await store.AppendAsync(aggregateId, AggregateVersion.New, [], ct);

        Assert.False(result.IsOk);
        var read = Require(await store.ReadByAggregateAsync(aggregateId, ct));
        Assert.Empty(read);
    }

    [Fact]
    public async Task AppendWithWrongExpectedVersionFailsWithoutMutatingTheStore()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var aggregateId = Agg("agg-conflict");
        await store.AppendAsync(aggregateId, AggregateVersion.New, [NewEvent("first")], ct);

        // The aggregate now has one event, so New (0) is stale -- not an exception, a Result.
        var conflict = await store.AppendAsync(aggregateId, AggregateVersion.New, [NewEvent("second")], ct);

        Assert.False(conflict.IsOk);
        var error = conflict.Match(
            _ => throw new InvalidOperationException("expected a concurrency failure, got a success"),
            e => e);
        Assert.Equal("curia/domain/concurrency-conflict", error.Type);

        var stream = Require(await store.ReadByAggregateAsync(aggregateId, ct));
        Assert.Single(stream); // the conflicting append wrote nothing -- no silent overwrite
    }

    /// <summary>
    /// The payload-admissibility promise <see cref="IEventStore.AppendAsync"/> states (R6.42,
    /// errata E10), asserted here rather than in either adapter's own suite because it is a
    /// promise of the port: a payload with no RFC 8785 canonical form cannot be digested, cannot
    /// be signed, and does not re-parse to one unambiguous document, so it is not a fact a
    /// system of record can faithfully hold whichever adapter is underneath.
    ///
    /// This case exists because the adapters had drifted in exactly the direction a shared
    /// contract suite is meant to make impossible. <c>PostgresEventStore</c> refused a
    /// duplicate-membered payload -- <c>jsonb</c>'s input conversion resolves duplicate keys
    /// last-wins, so accepting one silently stored a different document than the caller
    /// appended -- while <c>InMemoryEventStore</c> accepted it and handed it back losslessly,
    /// because an in-process object graph physically can. Nothing failed, because this suite had
    /// no case for it. A fake more permissive than the real store is the worst shape the drift
    /// can take: code passes against the fake and loses data in production, which is what
    /// R11.4's in-memory adapter exists to prevent rather than create.
    ///
    /// The payload is built as a <see cref="JsonValue"/> tree rather than parsed from text, and
    /// that is the point, not a shortcut: every parse path already rejects duplicate member
    /// names, so a duplicate can only reach a store from a caller holding a tree nothing
    /// inspected -- and a <see cref="DomainEvent"/>'s payload is exactly such a tree.
    /// </summary>
    [Fact]
    public async Task AppendRefusesAPayloadWhoseRootObjectCarriesDuplicateMemberNames()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var aggregateId = Agg("agg-duplicate-root");

        var payload = new JsonValue.Object(
        [
            new("dup", new JsonValue.String("FIRST")),
            new("dup", new JsonValue.String("SECOND")),
        ]);

        var result = await store.AppendAsync(
            aggregateId, AggregateVersion.New, [NewEvent("dup-root", payload: payload)], ct);

        AssertRefusedAsDuplicateKey(result);
        await AssertNothingWasWrittenAsync(store, aggregateId, ct);
    }

    /// <summary>
    /// Nested inside an array inside the payload, because an adapter that inspected only the
    /// payload's root object would pass the test above and still accept -- and, through
    /// <c>jsonb</c>, silently collapse -- this one. The rule is a property of every object in
    /// the tree, at any depth.
    /// </summary>
    [Fact]
    public async Task AppendRefusesADuplicateMemberNestedAnywhereInThePayload()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var aggregateId = Agg("agg-duplicate-nested");

        var payload = new JsonValue.Object(
        [
            new("outer", new JsonValue.Array(
            [
                new JsonValue.Object(
                [
                    new("dup", new JsonValue.String("FIRST")),
                    new("dup", new JsonValue.String("SECOND")),
                ]),
            ])),
        ]);

        var result = await store.AppendAsync(
            aggregateId, AggregateVersion.New, [NewEvent("dup-nested", payload: payload)], ct);

        AssertRefusedAsDuplicateKey(result);
        await AssertNothingWasWrittenAsync(store, aggregateId, ct);
    }

    /// <summary>
    /// One inadmissible payload refuses the whole batch, including the events ahead of it that
    /// were fine on their own. This is the case that catches an adapter checking payloads inside
    /// its append loop instead of before it: such an adapter passes both tests above (their
    /// batches are one event long) and leaves a partially written batch behind here -- in an
    /// append-only log, where there is nothing to roll it back with.
    /// </summary>
    [Fact]
    public async Task ADuplicateLaterInABatchRefusesTheEventsAheadOfItToo()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var aggregateId = Agg("agg-duplicate-batch");

        var payload = new JsonValue.Object(
        [
            new("dup", new JsonValue.String("FIRST")),
            new("dup", new JsonValue.String("SECOND")),
        ]);

        var result = await store.AppendAsync(
            aggregateId,
            AggregateVersion.New,
            [NewEvent("batch-admissible"), NewEvent("batch-inadmissible", payload: payload)],
            ct);

        AssertRefusedAsDuplicateKey(result);
        await AssertNothingWasWrittenAsync(store, aggregateId, ct);
    }

    /// <summary>
    /// The payload-order promise <see cref="IEventReader"/> states (R11.23, errata E12): what
    /// comes back is the appended payload with every object's members in RFC 8785 §3.2.3 order,
    /// at every depth, from all three surfaces that hand an event back.
    ///
    /// This case exists for the same reason the duplicate-key cases above do, one step over.
    /// R11.21 closed what the two adapters *accept*; this is what they *return*, and they
    /// diverged: measured against PostgreSQL 18.4, a payload appended as
    /// <c>{"z":1,"longer_key_b":2,"a":3}</c> came back from Postgres as
    /// <c>{"a":3,"z":1,"longer_key_b":2}</c> -- <c>jsonb</c> is a parsed binary form that
    /// re-sorts keys by length then bytewise, so the canonical order the adapter wrote is not
    /// the order it reads -- while the in-memory adapter returned the caller's exact tree. The
    /// fake was the more *faithful* of the two, which misleads exactly as being the more
    /// permissive did: code written and tested against it could depend on payload member order
    /// and break in production, and this suite reported agreement because it had no case.
    ///
    /// The expected orders below are written out literally rather than computed by calling the
    /// same canonicalizer the adapters call. Deriving them would make this test agree with the
    /// implementation by construction and prove nothing about which order that is; the point of
    /// a contract case is to pin the answer independently. Note that <c>{"a", "arr",
    /// "longer_key_b", "z"}</c> is neither the order the payload was built in nor the order
    /// <c>jsonb</c> would hand back (<c>a, z, arr, longer_key_b</c> -- length first), so the
    /// assertion discriminates against both of the behaviours that were actually observed.
    /// </summary>
    [Fact]
    public async Task PayloadMembersReadBackInCanonicalOrderAtEveryDepth()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var aggregateId = Agg("agg-payload-order");

        // Deliberately scrambled at both levels, with an array in the middle: arrays are
        // ordered collections and RFC 8785 §3.2.3 leaves them alone (R6.8), so an
        // implementation that sorted indiscriminately would fail this too.
        var payload = new JsonValue.Object(
        [
            new("z", new JsonValue.Number(1)),
            new("longer_key_b", new JsonValue.Number(2)),
            new("arr", new JsonValue.Array(
            [
                new JsonValue.Object(
                [
                    new("q", new JsonValue.Bool(true)),
                    new("b", JsonValue.Null.Instance),
                ]),
                new JsonValue.String("first"),
                new JsonValue.String("second"),
            ])),
            new("a", new JsonValue.Number(3)),
        ]);

        var appended = Require(await store.AppendAsync(
            aggregateId, AggregateVersion.New, [NewEvent("evt-payload-order", payload: payload)], ct));
        var fromRead = Require(await store.ReadByAggregateAsync(aggregateId, ct));
        var fromForward = Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct));

        // All three surfaces, because AppendAsync's own return value is an event handed back
        // too: an adapter could fix up its read paths and still return the caller's tree from
        // the append that produced it, which is the copy a caller is most likely to use.
        foreach (var payloadReadBack in new[]
        {
            Assert.Single(appended).Event.Payload,
            Assert.Single(fromRead).Event.Payload,
            Assert.Single(fromForward).Event.Payload,
        })
        {
            var root = Assert.IsType<JsonValue.Object>(payloadReadBack);
            Assert.Equal(CanonicalRootMemberOrder, MemberKeys(root));

            var array = Assert.IsType<JsonValue.Array>(root.Members.Single(m => m.Key == "arr").Value);
            Assert.Equal(CanonicalNestedMemberOrder, MemberKeys(Assert.IsType<JsonValue.Object>(array.Items[0])));

            // Array order untouched: the object stays first, "first" before "second".
            Assert.Equal("first", Assert.IsType<JsonValue.String>(array.Items[1]).Value);
            Assert.Equal("second", Assert.IsType<JsonValue.String>(array.Items[2]).Value);

            // ...and reordering is the *only* thing that happened: same document, every scalar
            // intact. Canonical bytes are the order-independent equality this project already
            // trusts, so a payload that lost or altered a value fails here even though the
            // member-order assertions above cannot see it.
            Assert.Equal(CanonicalBytesOf(payload), CanonicalBytesOf(payloadReadBack));
        }
    }

    /// <summary>
    /// R11.24 (errata E12): admissibility is decided under the Cūria profile, so a payload
    /// whose member names are distinct on the wire but equal after NFC is refused.
    ///
    /// The store renders what it stores with the pure RFC 8785 <c>Canonicalize</c> -- storage is
    /// not signing -- and under that profile this payload is perfectly well defined, which is
    /// why it used to be accepted. <c>jsonb</c> compares keys bytewise and keeps both members,
    /// so nothing downstream objected either; round-tripping was lossless and nothing was lost.
    /// The reason to refuse is that this is the system of record (R11.9): the same tree fails
    /// <c>CanonicalizeWithNfc</c> with <c>curia/canon/duplicate-normalized-key</c>, so the
    /// moment event payloads are digested into a Merkle leaf or an <i>Acta</i> entry (§9's dump
    /// manifests), the store would already hold rows that cannot be canonicalized for the
    /// purpose -- discovered at signing time instead of at write time. Admission is the one
    /// decision that mutates nothing, so tightening it costs the no-mutation invariant nothing.
    /// </summary>
    [Fact]
    public async Task AppendRefusesAPayloadWhoseMemberNamesCollideOnlyUnderNfc()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var aggregateId = Agg("agg-nfc-collision");

        // Precomposed U+00E9 against "e" + U+0301 COMBINING ACUTE ACCENT: two distinct wire
        // keys, one NFC key.
        var payload = new JsonValue.Object(
        [
            new("caf\u00e9", new JsonValue.Number(1)),
            new("cafe\u0301", new JsonValue.Number(2)),
        ]);

        var result = await store.AppendAsync(
            aggregateId, AggregateVersion.New, [NewEvent("nfc-collision", payload: payload)], ct);

        Assert.False(result.IsOk);
        Assert.Equal(
            "curia/canon/duplicate-normalized-key",
            result.Match(_ => "<the append succeeded>", e => e.Type));
        await AssertNothingWasWrittenAsync(store, aggregateId, ct);
    }

    /// <summary>
    /// R11.24 again, for the condition errata E12 measured and E13 fixed: a payload carrying an
    /// unpaired UTF-16 surrogate is refused, as <c>curia/admit/unpaired-surrogate</c>.
    ///
    /// This is a port promise with two things to pin, not one. That it is <i>refused</i> was
    /// already true before E13 -- admitting under <c>CanonicalizeWithNfc</c> closed the second
    /// E12-shaped return divergence by construction (Postgres would have substituted U+FFFD,
    /// the in-memory adapter would have kept the surrogate), which is why the case belongs in
    /// the shared suite rather than one adapter's. <i>Which slug</i> it is refused with changed:
    /// the refusal used to come from <c>string.Normalize</c> throwing, and read
    /// <c>curia/canon/normalization-failed</c> -- the layer that noticed, carrying a
    /// platform-specific ICU message -- where R6.43 now requires the condition both parse paths
    /// and <c>curia-testis</c> already name. A slug is a public failure surface; a caller
    /// branching on it is entitled to have it pinned rather than inferred from whichever
    /// mechanism happens to detect the condition this release.
    ///
    /// The tree is built directly, which is the only way to reach this: both
    /// <c>JsonReader</c> paths reject the same input while parsing, and a domain event's payload
    /// is exactly such a caller-built tree -- E10's lesson, arriving for a second condition.
    /// </summary>
    [Fact]
    public async Task AppendRefusesAPayloadCarryingAnUnpairedSurrogate()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var aggregateId = Agg("agg-unpaired-surrogate");

        // A lone high surrogate, in a string value; "\uD800" has no low surrogate after it and
        // is therefore not a Unicode scalar value, so it has no UTF-8 encoding at all.
        var payload = new JsonValue.Object([new("a", new JsonValue.String("\uD800"))]);

        var result = await store.AppendAsync(
            aggregateId, AggregateVersion.New, [NewEvent("unpaired-surrogate", payload: payload)], ct);

        Assert.False(result.IsOk);
        Assert.Equal(
            "curia/admit/unpaired-surrogate",
            result.Match(_ => "<the append succeeded>", e => e.Type));
        await AssertNothingWasWrittenAsync(store, aggregateId, ct);
    }

    /// <summary>
    /// The overshoot control, at the port. A well-formed surrogate pair is how every character
    /// outside the BMP is spelled in UTF-16, so an admissibility check reading "contains a
    /// surrogate code unit" would refuse every astral character -- and this payload, which must
    /// round-trip unchanged through whichever adapter is underneath.
    /// </summary>
    [Fact]
    public async Task APayloadCarryingAWellFormedSurrogatePairIsStoredUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var aggregateId = Agg("agg-surrogate-pair");

        var payload = new JsonValue.Object([new("\U0001F602", new JsonValue.String("a\U0001F602b"))]);

        var appended = Require(await store.AppendAsync(
            aggregateId, AggregateVersion.New, [NewEvent("surrogate-pair", payload: payload)], ct));
        var read = Require(await store.ReadByAggregateAsync(aggregateId, ct));

        foreach (var payloadReadBack in new[] { Assert.Single(appended).Event.Payload, Assert.Single(read).Event.Payload })
        {
            var root = Assert.IsType<JsonValue.Object>(payloadReadBack);
            Assert.Equal("\U0001F602", root.Members[0].Key, StringComparer.Ordinal);
            Assert.Equal("a\U0001F602b", Assert.IsType<JsonValue.String>(root.Members[0].Value).Value, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// The other side of the NFC-collision case above, and the reason it is a narrow tightening
    /// rather than the store adopting the signing profile as its storage format: decomposed text
    /// is not the defect, a *collision* is. This payload is entirely NFD -- key and value -- and must be
    /// stored, unnormalized, exactly as it was appended. An adapter that satisfied R11.24 by
    /// NFC-normalizing payloads instead of refusing collisions would pass the case above and
    /// fail this one, which is the mutation §6.4 forbids outright.
    /// </summary>
    [Fact]
    public async Task ANonCollidingDecomposedPayloadIsStoredWithoutNormalization()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var aggregateId = Agg("agg-nfd-no-collision");

        var payload = new JsonValue.Object([new("cafe\u0301", new JsonValue.String("nai\u0308ve"))]);

        var appended = Require(await store.AppendAsync(
            aggregateId, AggregateVersion.New, [NewEvent("nfd-ok", payload: payload)], ct));
        var read = Require(await store.ReadByAggregateAsync(aggregateId, ct));

        foreach (var payloadReadBack in new[] { Assert.Single(appended).Event.Payload, Assert.Single(read).Event.Payload })
        {
            var root = Assert.IsType<JsonValue.Object>(payloadReadBack);
            Assert.Equal(DecomposedKeyUnchanged, MemberKeys(root));
            Assert.Equal("nai\u0308ve", Assert.IsType<JsonValue.String>(root.Members[0].Value).Value);
        }
    }

    /// <summary>
    /// R11.25 (errata E12): when an inadmissible payload and a stale <c>expectedVersion</c>
    /// both apply, the payload wins.
    ///
    /// Both adapters already behaved this way -- each checks payloads before it touches the
    /// store at all -- but nothing pinned it and the port did not promise it, which is precisely
    /// the shape E11 describes: two implementations agreeing is not a contract, and a suite with
    /// no case cannot tell the difference between the two. The precedence is not arbitrary.
    /// Admissibility is a property of the arguments, decidable without reading anything, so it
    /// can be settled first; and it is the more useful answer, because a concurrency conflict
    /// invites a re-read and a retry that would refuse this payload identically every time.
    /// </summary>
    [Fact]
    public async Task AnInadmissiblePayloadIsReportedAheadOfAStaleExpectedVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var aggregateId = Agg("agg-precedence");

        // The aggregate is now at version 1, so AggregateVersion.New below is stale on its own.
        Require(await store.AppendAsync(aggregateId, AggregateVersion.New, [NewEvent("precedence-first")], ct));

        var payload = new JsonValue.Object(
        [
            new("dup", new JsonValue.String("FIRST")),
            new("dup", new JsonValue.String("SECOND")),
        ]);

        var result = await store.AppendAsync(
            aggregateId, AggregateVersion.New, [NewEvent("precedence-second", payload: payload)], ct);

        AssertRefusedAsDuplicateKey(result);

        // And nothing beyond the first event was written -- a refusal that reported the right
        // slug while still appending would be a worse defect than reporting the wrong one.
        Assert.Single(Require(await store.ReadByAggregateAsync(aggregateId, ct)));
    }

    private static IReadOnlyList<string> MemberKeys(JsonValue.Object o) =>
        [.. o.Members.Select(m => m.Key)];

    private static byte[] CanonicalBytesOf(JsonValue value) =>
        CanonicalJson.Canonicalize(value).Match(
            b => b.ToArray(),
            e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    private static void AssertRefusedAsDuplicateKey(Result<IReadOnlyList<AppendedEvent>> result)
    {
        Assert.False(result.IsOk);

        // The slug names the condition, not the layer that noticed it (R6.42, R6.40): the same
        // curia/admit/duplicate-key ADMIT reports, because it is the same defect reached by a
        // caller ADMIT never ran for.
        Assert.Equal(
            "curia/admit/duplicate-key",
            result.Match(_ => "<the append succeeded>", e => e.Type));
    }

    /// <summary>
    /// Both read paths, because "this aggregate is empty" and "the log is empty" are different
    /// claims and an append-only store has to satisfy the second one too: an adapter could
    /// refuse to attach the events to the target aggregate's stream and still have appended
    /// them to the log every replay reads (R11.9), which is the copy that matters.
    /// </summary>
    private static async Task AssertNothingWasWrittenAsync(
        IEventStore store, AggregateId aggregateId, CancellationToken ct)
    {
        // ConfigureAwait(false) only because this helper is not itself a [Fact] and so is not
        // exempt from CA2007 the way every test method above is; xUnit v3's TestContext flows
        // through AsyncLocal, not a SynchronizationContext, so nothing here depends on resuming
        // on the captured context.
        Assert.Empty(Require(await store.ReadByAggregateAsync(aggregateId, ct).ConfigureAwait(false)));
        Assert.Empty(Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct).ConfigureAwait(false)));
    }

    [Fact]
    public async Task ReadByAggregateOnAnUntouchedAggregateIsEmptyNotAFailure()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var read = Require(await store.ReadByAggregateAsync(Agg("never-appended"), ct));
        Assert.Empty(read);
    }

    [Fact]
    public async Task ReadForwardFromZeroReplaysEverythingInAscendingSeqOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var a = Agg("agg-forward-a");
        var b = Agg("agg-forward-b");
        await store.AppendAsync(a, AggregateVersion.New, [NewEvent("a-1"), NewEvent("a-2")], ct);
        await store.AppendAsync(b, AggregateVersion.New, [NewEvent("b-1")], ct);

        var all = Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct));

        Assert.Equal(3, all.Count);
        Assert.True(all[0].Seq < all[1].Seq);
        Assert.True(all[1].Seq < all[2].Seq);
    }

    [Fact]
    public async Task ReadForwardAfterASeqExcludesThatEventAndEverythingBeforeIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var a = Agg("agg-forward-exclusive");
        var appended = Require(await store.AppendAsync(
            a, AggregateVersion.New, [NewEvent("z-1"), NewEvent("z-2"), NewEvent("z-3")], ct));

        var tail = Require(await store.ReadForwardAsync(appended[0].Seq, cancellationToken: ct));

        Assert.Equal(2, tail.Count);
        Assert.DoesNotContain(tail, e => e.Event.Id == appended[0].Event.Id);
    }

    [Fact]
    public async Task ReadForwardHonoursMaxCount()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var a = Agg("agg-page");
        await store.AppendAsync(a, AggregateVersion.New, [NewEvent("p-1"), NewEvent("p-2"), NewEvent("p-3")], ct);

        var page = Require(await store.ReadForwardAsync(EventSequence.Zero, maxCount: 2, cancellationToken: ct));

        Assert.Equal(2, page.Count);
    }

    /// <summary>
    /// Stage 1's headline property: across arbitrarily interleaved appends to several
    /// aggregates, every <c>seq</c> the store ever hands out is strictly greater than every
    /// <c>seq</c> handed out before it -- Appendix D's <c>seq</c> is one global IDENTITY column,
    /// not one counter per aggregate, so interleaving must not produce two streams whose
    /// sequence numbers interleave out of call order.
    /// </summary>
    [Fact]
    public void SeqIsStrictlyMonotonicAcrossAggregatesUnderRandomAppendSequences() =>
        Gen.Select(Gen.Int[0, 4], Gen.Int[1, 3])
            .List[1, 40]
            .Sample(script =>
            {
                var store = CreateStore();
                var versions = new Dictionary<int, AggregateVersion>();
                var lastSeq = 0L;

                foreach (var (aggregateIndex, count) in script)
                {
                    var aggregateId = Agg($"agg-{aggregateIndex}");
                    var expected = versions.GetValueOrDefault(aggregateIndex, AggregateVersion.New);
                    var batch = Enumerable.Range(0, count)
                        .Select(i => NewEvent($"{aggregateIndex}-{Guid.NewGuid():N}-{i}"))
                        .ToArray();

                    var appended = store.AppendAsync(aggregateId, expected, batch)
                        .GetAwaiter().GetResult()
                        .Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

                    foreach (var e in appended)
                    {
                        if (e.Seq.Value <= lastSeq)
                            return false;
                        lastSeq = e.Seq.Value;
                    }

                    versions[aggregateIndex] = Require(AggregateVersion.From(expected.Value + count));
                }

                return true;
            }, iter: 200);
}
