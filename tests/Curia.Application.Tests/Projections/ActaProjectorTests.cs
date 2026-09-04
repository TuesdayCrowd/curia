using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Acta;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Acta;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Application.Tests.Projections;

/// <summary>
/// The Acta over the in-memory store: every event is a leaf in seq order, every proof the
/// projection hands out verifies with the tree's own verifier, and the operator's head and key
/// entries fold into what the Forum serves.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ActaProjectorTests
{
    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    private static AggregateId Agg(string v) => Require(AggregateId.Create(v));

    private static DomainEvent Event(string id, string type, JsonValue payload, string? actor = "agent://example/a") => new(
        Require(EventId.Create(id)),
        Require(EventType.Create(type)),
        actor is null ? null : Require(ActorId.Create(actor)),
        payload);

    private static InMemoryEventStore NewStore() =>
        new(new ManualTimeProvider(new DateTimeOffset(2026, 9, 4, 16, 0, 0, TimeSpan.Zero)));

    private static async Task<InMemoryEventStore> LogOfAsync(int n, CancellationToken ct)
    {
        var store = NewStore();
        for (var i = 0; i < n; i++)
        {
            Require(await store.AppendAsync(
                Agg($"a{i}"), AggregateVersion.New,
                [Event($"e{i}", "test.event", new JsonValue.Object([new("i", new JsonValue.Number(i))]))], ct).ConfigureAwait(false));
        }
        return store;
    }

    private static async Task<(IReadOnlyList<AppendedEvent> Log, ActaLog Acta)> FoldAsync(IEventReader reader, CancellationToken ct)
    {
        var log = Require(await reader.ReadAllAsync(ct).ConfigureAwait(false));
        return (log, Require(ActaLog.Fold(log)));
    }

    [Fact]
    public async Task R6_46_R6_47_LeavesAreTheEventsInSeqOrderAndTheIndexIsTheOrdinal()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await LogOfAsync(7, ct);
        var (log, acta) = await FoldAsync(store, ct);

        Assert.Equal(7, acta.TreeSize);
        for (var i = 0; i < 7; i++)
        {
            Assert.Equal(Require(LogLeaf.Hash(log[i])), acta.Leaves[i]);
            Assert.Equal(i, acta.IndexOf($"e{i}"));
        }
        Assert.Null(acta.IndexOf("never-appended"));
        Assert.Equal(MerkleTree.Root(acta.Leaves), acta.Root);
        Assert.Equal(acta.Root, acta.RootAt(7));
        Assert.Empty(acta.Heads);
        Assert.Null(acta.LatestHead);
    }

    [Fact]
    public async Task R6_23_EveryInclusionProofVerifiesAndAnythingElseIsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, acta) = await FoldAsync(await LogOfAsync(8, ct), ct);

        for (var size = 1; size <= 8; size++)
        {
            for (var index = 0; index < size; index++)
            {
                var proof = acta.Inclusion(index, size);
                Assert.NotNull(proof);
                Assert.Equal(acta.RootAt(size), proof.Root);
                Assert.True(
                    MerkleTree.VerifyInclusion(proof.LeafHash.AsSpan(), index, size, proof.AuditPath, proof.Root.AsSpan()),
                    $"leaf {index} of {size}");
            }
        }

        Assert.Null(acta.Inclusion(8, 8));
        Assert.Null(acta.Inclusion(0, 9));
        Assert.Null(acta.Inclusion(-1, 8));
        Assert.Null(acta.Inclusion(0, 0));
    }

    [Fact]
    public async Task R6_23_EveryConsistencyProofVerifiesIncludingEqualSizes()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, acta) = await FoldAsync(await LogOfAsync(8, ct), ct);

        for (var to = 1; to <= 8; to++)
        {
            for (var from = 1; from <= to; from++)
            {
                var proof = acta.Consistency(from, to);
                Assert.NotNull(proof);
                Assert.True(
                    MerkleTree.VerifyConsistency(from, to, proof.FromRoot.AsSpan(), proof.ToRoot.AsSpan(), proof.Path),
                    $"{from} -> {to}");
            }
        }

        Assert.Null(acta.Consistency(0, 8));
        Assert.Null(acta.Consistency(5, 9));
        Assert.Null(acta.Consistency(6, 5));
    }

    [Fact]
    public async Task R6_49_R6_50_HeadAndKeyEntriesFoldIntoTheActaAndAreThemselvesLeaves()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await LogOfAsync(3, ct);

        Require(await store.AppendAsync(Agg(LogEntries.KeysAggregate), AggregateVersion.New,
            [Event("k1", LogEntries.KeyType, new JsonValue.Object(
            [
                new(LogEntries.KidMember, new JsonValue.String("kid-1")),
                new(LogEntries.JwkMember, new JsonValue.Object([new("kty", new JsonValue.String("EC"))])),
                new(LogEntries.ValidFromMember, new JsonValue.String("2026-09-04T16:00:00.000000Z")),
            ]), "operator:ops")], ct));

        // The head covers the four leaves before it (three events and the key) and becomes leaf 4.
        var (_, before) = await FoldAsync(store, ct);
        Assert.Equal(4, before.TreeSize);
        var headDoc = LogEntries.HeadDocument(before.Root, "2026-09-04T17:00:00.000000Z", 4);

        Require(await store.AppendAsync(Agg(LogEntries.HeadsAggregate), AggregateVersion.New,
            [Event("h1", LogEntries.HeadType, new JsonValue.Object(
            [
                new(LogEntries.HeadMember, headDoc),
                new(LogEntries.KidMember, new JsonValue.String("kid-1")),
                new(LogEntries.SignatureMember, new JsonValue.String("eyJ..sig")),
            ]), "operator:ops")], ct));

        var (_, acta) = await FoldAsync(store, ct);
        Assert.Equal(5, acta.TreeSize);

        var key = Assert.Single(acta.Keys);
        Assert.Equal(("kid-1", 3L), (key.Kid, key.LogIndex));

        var head = Assert.Single(acta.Heads);
        Assert.Same(head, acta.LatestHead);
        Assert.Equal((4L, 4L, "kid-1", "eyJ..sig"), (head.TreeSize, head.LogIndex, head.Kid, head.Signature));
        Assert.Equal(before.Root, head.Root);
        Assert.Equal(acta.RootAt(4), head.Root);
        // The store round-trips the payload, so the document is equal, not the same instance; what
        // matters is that its canonical form -- the signed bytes -- came back untouched.
        Assert.Equal(Require(LogEntries.HeadCanonical(headDoc)).ToArray(), Require(LogEntries.HeadCanonical(head.Document)).ToArray());

        // The head's own leaf is provable against the whole log, and the log is consistent with the head.
        var inclusion = acta.Inclusion(head.LogIndex, acta.TreeSize)!;
        Assert.True(MerkleTree.VerifyInclusion(inclusion.LeafHash.AsSpan(), 4, 5, inclusion.AuditPath, inclusion.Root.AsSpan()));
        var consistency = acta.Consistency(4, 5)!;
        Assert.True(MerkleTree.VerifyConsistency(4, 5, head.Root.AsSpan(), acta.Root.AsSpan(), consistency.Path));
    }

    [Fact]
    public async Task R6_49_AHeadTheLogCouldNotHaveReachedFailsTheFoldLoudly()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await LogOfAsync(2, ct);

        Require(await store.AppendAsync(Agg(LogEntries.HeadsAggregate), AggregateVersion.New,
            [Event("h1", LogEntries.HeadType, new JsonValue.Object(
            [
                new(LogEntries.HeadMember, LogEntries.HeadDocument(MerkleTree.EmptyRoot(), "2026-09-04T17:00:00.000000Z", 99)),
                new(LogEntries.KidMember, new JsonValue.String("kid-1")),
                new(LogEntries.SignatureMember, new JsonValue.String("eyJ..sig")),
            ]), "operator:ops")], ct));

        var log = Require(await store.ReadAllAsync(ct));
        var folded = ActaLog.Fold(log);
        Assert.False(folded.TryGetValue(out _, out var error));
        Assert.Equal("curia/acta/malformed-head", error!.Type);
    }

    [Fact]
    public async Task ReadAllAsync_ReadsPastTheOldTenThousandCap()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = await LogOfAsync(EventReaderExtensions.PageSize + 1, ct);

        var all = Require(await store.ReadAllAsync(ct));
        Assert.Equal(EventReaderExtensions.PageSize + 1, all.Count);
        Assert.Equal(EventReaderExtensions.PageSize, Require(ActaLog.Fold(all)).IndexOf($"e{EventReaderExtensions.PageSize}"));
    }
}
