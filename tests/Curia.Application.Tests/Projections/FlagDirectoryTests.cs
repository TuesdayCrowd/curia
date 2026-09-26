using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Application.Tests.Projections;

/// <summary>
/// R10.62: a flag's post, raiser and rationale live in the private store, bound to a log entry that
/// names none of them; R7.18's views are served from the join. Every event below is a real append
/// through <see cref="InMemoryEventStore"/> (CS-15).
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class FlagDirectoryTests
{
    private const string Post = "01JPOST0000000000000000001";
    private const string Reporter = "https://agents.example/reporter";
    private const string Salt = "c2FsdC1mb3ItdGhlLWZpeGVkLWNvbW1pdG1lbnQtMzI";

    private static readonly DateTimeOffset Start = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    private static async Task<IReadOnlyList<AppendedEvent>> LogAsync(InMemoryEventStore store, CancellationToken ct) =>
        Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct).ConfigureAwait(false));

    private static async Task<IReadOnlyList<FlagDetail>> DetailsAsync(InMemoryFlagDetailStore details, CancellationToken ct) =>
        Require(await details.ReadAllAsync(ct).ConfigureAwait(false));

    /// <summary>A committed flag as <c>RaiseFlag</c> writes one after Task 6: the private row, then an entry naming only kind and commitment.</summary>
    private static async Task CommitAsync(
        InMemoryEventStore store, InMemoryFlagDetailStore details, string flagId, FlagKind kind, CancellationToken ct,
        string rationale = "looks like an injection attempt", bool storeDetail = true, string? commitmentOverride = null)
    {
        if (storeDetail)
            Require(await details.AppendAsync(new FlagDetail(flagId, Post, Reporter, rationale, Salt), ct).ConfigureAwait(false));

        var commitment = commitmentOverride ?? Require(FlagCommitment.Of(Post, Reporter, rationale, Salt));

        Require(await store.AppendAsync(
            Require(AggregateId.Create("flag:" + flagId)),
            AggregateVersion.New,
            [new DomainEvent(
                Require(EventId.Create(flagId)),
                Require(EventType.Create(FlagProjector.FlagCommittedType)),
                null,
                new JsonValue.Object(
                [
                    new(FlagProjector.CommitmentField, new JsonValue.String(commitment)),
                    new(FlagProjector.KindField, new JsonValue.String(FlagKinds.Wire(kind))),
                ]))],
            ct).ConfigureAwait(false));
    }

    /// <summary>A flag as the log held one before R10.62: public, on the post's own stream.</summary>
    private static async Task RaiseLegacyAsync(InMemoryEventStore store, string flagId, FlagKind kind, CancellationToken ct)
    {
        var aggregate = Require(AggregateId.Create(Post));
        var history = Require(await store.ReadByAggregateAsync(aggregate, ct).ConfigureAwait(false));

        Require(await store.AppendAsync(
            aggregate,
            Require(AggregateVersion.From(history.Count)),
            [new DomainEvent(
                Require(EventId.Create(flagId)),
                Require(EventType.Create(FlagProjector.FlagRaisedType)),
                Require(ActorId.Create(Reporter)),
                new JsonValue.Object(
                [
                    new(FlagProjector.PostIdField, new JsonValue.String(Post)),
                    new(FlagProjector.RaisedByField, new JsonValue.String(Reporter)),
                    new(FlagProjector.KindField, new JsonValue.String(FlagKinds.Wire(kind))),
                    new(FlagProjector.RationaleField, new JsonValue.String("an old, public rationale")),
                ]))],
            ct).ConfigureAwait(false));
    }

    [Fact]
    public async Task R10_62_ACommittedFlagIsJoinedToItsPostAndRaiser()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));
        var details = new InMemoryFlagDetailStore();

        await CommitAsync(store, details, "01JFLAG000000000000000001", FlagKind.Spam, ct);

        var directory = FlagDirectory.Join(await LogAsync(store, ct), await DetailsAsync(details, ct));

        var flag = Assert.Single(directory.Flags);
        Assert.Equal("01JFLAG000000000000000001", flag.FlagId);
        Assert.Equal(Post, flag.PostId);
        Assert.Equal(Reporter, flag.RaisedBy);
        Assert.Equal(FlagKind.Spam, flag.Kind);
        Assert.Equal(Start, flag.At.Value);
        Assert.Empty(directory.Skipped);
    }

    /// <summary>A log written before R10.62 still lists its flags; their disclosure is permanent, and they are not lost.</summary>
    [Fact]
    public async Task R10_62_ALegacyFlagIsStillListed()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        await RaiseLegacyAsync(store, "01JLEGACY00000000000000001", FlagKind.Injection, ct);

        var flag = Assert.Single(FlagDirectory.Join(await LogAsync(store, ct), []).Flags);
        Assert.Equal(("01JLEGACY00000000000000001", Post, Reporter, FlagKind.Injection), (flag.FlagId, flag.PostId, flag.RaisedBy, flag.Kind));
    }

    /// <summary>
    /// A public commitment with no private row is not listed, and is counted — R11.31's shape for the
    /// one join this stage creates. Silence would make a lost row indistinguishable from no flag.
    /// </summary>
    [Fact]
    public async Task R10_62_ACommittedFlagWithNoDetailIsSkippedAndCounted()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));
        var details = new InMemoryFlagDetailStore();

        await CommitAsync(store, details, "01JFLAG000000000000000001", FlagKind.Spam, ct, storeDetail: false);

        var directory = FlagDirectory.Join(await LogAsync(store, ct), await DetailsAsync(details, ct));

        Assert.Empty(directory.Flags);
        Assert.Equal(1, directory.Skipped[FlagDirectory.SkippedNoDetail]);
    }

    /// <summary>A private row that no longer opens its entry's commitment is not believed — the commitment is what makes substitution detectable.</summary>
    [Fact]
    public async Task R10_62_ATamperedDetailIsSkippedAndCounted()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));
        var details = new InMemoryFlagDetailStore();

        await CommitAsync(store, details, "01JFLAG000000000000000001", FlagKind.Spam, ct,
            commitmentOverride: Require(FlagCommitment.Of(Post, Reporter, "a different rationale", Salt)));

        var directory = FlagDirectory.Join(await LogAsync(store, ct), await DetailsAsync(details, ct));

        Assert.Empty(directory.Flags);
        Assert.Equal(1, directory.Skipped[FlagDirectory.SkippedCommitmentMismatch]);
    }

    /// <summary>The projection carries no rationale: nothing that serves from it can echo one.</summary>
    [Fact]
    public void R10_44_ARaisedFlagCarriesNoRationale()
    {
        var strings = typeof(RaisedFlag).GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => p.Name)
            .ToArray();

        Assert.Equal([nameof(RaisedFlag.FlagId), nameof(RaisedFlag.PostId), nameof(RaisedFlag.RaisedBy)], strings);
    }

    /// <summary>
    /// R11.9 (addendum): the directory rebuilds from both stores to the identical state, and a
    /// different log gives a different one.
    ///
    /// <para>The rebuild is a fresh read of both stores, with the private rows handed over in reverse,
    /// so a join that depended on row order or on anything but its inputs would diverge. One committed
    /// entry has no row, so the skip count R11.31 asks the drill to assert on is not empty.</para>
    /// </summary>
    [Fact]
    public async Task R11_9_TheDirectoryRebuildsFromBothStores()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));
        var details = new InMemoryFlagDetailStore();

        await CommitAsync(store, details, "01JFLAG000000000000000001", FlagKind.Spam, ct);
        await CommitAsync(store, details, "01JFLAG000000000000000002", FlagKind.Injection, ct, rationale: "a second reason");
        await CommitAsync(store, details, "01JFLAG000000000000000003", FlagKind.Spam, ct, storeDetail: false);
        await RaiseLegacyAsync(store, "01JLEGACY00000000000000001", FlagKind.Injection, ct);

        var first = FlagDirectory.Join(await LogAsync(store, ct), await DetailsAsync(details, ct));

        var reread = await DetailsAsync(details, ct);
        var second = FlagDirectory.Join(await LogAsync(store, ct), [.. reread.Reverse()]);

        Assert.Equal(3, first.Flags.Length);
        Assert.Equal(1, first.Skipped[FlagDirectory.SkippedNoDetail]);
        Assert.True(first.Flags.SequenceEqual(second.Flags));
        Assert.True(first.Skipped.SequenceEqual(second.Skipped));

        // The negative control: a longer log must not rebuild to the same directory.
        await CommitAsync(store, details, "01JFLAG000000000000000004", FlagKind.Duplicate, ct, rationale: "a repeat");
        Assert.False(first.Flags.SequenceEqual(FlagDirectory.Join(await LogAsync(store, ct), await DetailsAsync(details, ct)).Flags));
    }
}
