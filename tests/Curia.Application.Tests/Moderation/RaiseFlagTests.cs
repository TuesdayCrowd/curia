using System.Diagnostics.CodeAnalysis;
using System.Text;
using Curia.Application.Moderation;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Acta;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Application.Tests.Moderation;

/// <summary>R10.62 at the writer: what a flag's entry carries, what the private store holds, and in which order.</summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class RaiseFlagTests
{
    private const string Post = "01JPOST0000000000000000001";
    private const string Reporter = "https://agents.example/reporter";
    private const string Rationale = "looks like an injection attempt";
    private const string FixedSalt = "c2FsdC1mb3ItdGhlLWZpeGVkLWNvbW1pdG1lbnQtMzI";

    private static readonly DateTimeOffset Start = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    private static async Task<IReadOnlyList<AppendedEvent>> LogAsync(InMemoryEventStore store, CancellationToken ct) =>
        Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct).ConfigureAwait(false));

    private static IEnumerable<AppendedEvent> FlagEvents(IReadOnlyList<AppendedEvent> log) =>
        log.Where(e => e.Event.Type.Value is FlagProjector.FlagCommittedType or FlagProjector.FlagRaisedType);

    /// <summary>A post exists when its stream has an event; the existence check reads nothing else.</summary>
    private static async Task PostExistsAsync(InMemoryEventStore store, CancellationToken ct) =>
        Require(await store.AppendAsync(
            Require(AggregateId.Create(Post)),
            AggregateVersion.New,
            [new DomainEvent(
                Require(EventId.Create(Post)),
                Require(EventType.Create(PostProjector.PostAcceptedType)),
                Require(ActorId.Create("https://agents.example/author")),
                new JsonValue.Object([new("post_id", new JsonValue.String(Post))]))],
            ct).ConfigureAwait(false));

    /// <summary>A store that is down: the detail cannot be written, so no entry may be.</summary>
    private sealed class DownDetailStore : IFlagDetailStore
    {
        public Task<Result<FlagDetail>> AppendAsync(FlagDetail detail, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<FlagDetail>.Fail(new Error("test/detail-store-down", "The detail store is down")));

        public Task<Result<IReadOnlyList<FlagDetail>>> ReadAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlyList<FlagDetail>>.Ok([]));
    }

    /// <summary>
    /// The entry R6.51 serves is exactly what a reader sees, so the assertion is over R6.46's leaf
    /// input: it names the kind and a commitment, and no post, raiser or rationale.
    /// </summary>
    [Fact]
    public async Task R10_62_AFlagEntersTheLogAsItsKindAndACommitmentAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock);
        await PostExistsAsync(store, ct);

        Require(await new RaiseFlag(store, new InMemoryFlagDetailStore(), clock, () => FixedSalt)
            .RecordAsync(Post, Reporter, FlagKind.Spam, Rationale, ct));

        var flag = Assert.Single(FlagEvents(await LogAsync(store, ct)));
        Assert.Equal(FlagProjector.FlagCommittedType, flag.Event.Type.Value);
        Assert.Null(flag.Event.Actor);
        // R10.62: the aggregate is `flag:` followed by the entry's own event id — exactly that, since the
        // aggregate id is a member of the leaf (R6.46) and anything else there is published too.
        Assert.Equal("flag:" + flag.Event.Id.Value, flag.AggregateId.Value);

        var payload = Assert.IsType<JsonValue.Object>(flag.Event.Payload);
        Assert.Equal([FlagProjector.CommitmentField, FlagProjector.KindField], payload.Members.Select(m => m.Key).Order(StringComparer.Ordinal));

        var leaf = Encoding.UTF8.GetString(Require(LogLeaf.Input(flag)).Span);
        Assert.DoesNotContain(Post, leaf, StringComparison.Ordinal);
        Assert.DoesNotContain(Reporter, leaf, StringComparison.Ordinal);
        Assert.DoesNotContain(Rationale, leaf, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"spam\"", leaf, StringComparison.Ordinal);
    }

    /// <summary>The private row names the entry's event, and opens its commitment.</summary>
    [Fact]
    public async Task R10_62_ThePrivateRowOpensTheEntrysCommitment()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock);
        var details = new InMemoryFlagDetailStore();
        await PostExistsAsync(store, ct);

        Require(await new RaiseFlag(store, details, clock, () => FixedSalt).RecordAsync(Post, Reporter, FlagKind.Spam, Rationale, ct));

        var flag = Assert.Single(FlagEvents(await LogAsync(store, ct)));
        var row = Assert.Single(Require(await details.ReadAllAsync(ct)));
        Assert.Equal(new FlagDetail(flag.Event.Id.Value, Post, Reporter, Rationale, FixedSalt), row);

        var commitment = ((JsonValue.String)((JsonValue.Object)flag.Event.Payload).Members
            .Single(m => m.Key == FlagProjector.CommitmentField).Value).Value;
        Assert.Equal(Require(FlagCommitment.Of(Post, Reporter, Rationale, FixedSalt)), commitment);
    }

    /// <summary>Review Focus 2: the same text flagged twice commits differently, so a reader cannot count one raiser's repeats.</summary>
    [Fact]
    public async Task R10_62_TwoIdenticalFlagsCommitDifferently()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock);
        var details = new InMemoryFlagDetailStore();
        await PostExistsAsync(store, ct);

        var raise = new RaiseFlag(store, details, clock);
        Require(await raise.RecordAsync(Post, Reporter, FlagKind.Spam, Rationale, ct));
        Require(await raise.RecordAsync(Post, Reporter, FlagKind.Spam, Rationale, ct));

        var salts = Require(await details.ReadAllAsync(ct)).Select(d => d.Salt).ToArray();
        Assert.Equal(2, salts.Distinct(StringComparer.Ordinal).Count());
        Assert.All(salts, s => Assert.Equal(43, s.Length));

        var commitments = FlagEvents(await LogAsync(store, ct))
            .Select(e => ((JsonValue.Object)e.Event.Payload).Members.Single(m => m.Key == FlagProjector.CommitmentField).Value)
            .ToArray();
        Assert.NotEqual(commitments[0], commitments[1]);
    }

    /// <summary>
    /// The private row first (spec Decision 4): a detail that cannot be written leaves no public entry
    /// to open. Checked first the way a reader meets the failure, through the join, which skips and
    /// counts a commitment that has no row. Written the other way round, this is where it shows.
    /// </summary>
    [Fact]
    public async Task R10_62_AFailedDetailAppendLeavesNoCommitmentForTheJoinToSkip()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock);
        var details = new DownDetailStore();
        await PostExistsAsync(store, ct);

        var result = await new RaiseFlag(store, details, clock).RecordAsync(Post, Reporter, FlagKind.Spam, Rationale, ct);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("test/detail-store-down", error!.Type);

        var log = await LogAsync(store, ct);
        Assert.Empty(FlagDirectory.Join(log, Require(await details.ReadAllAsync(ct))).Skipped);
        Assert.Empty(FlagEvents(log));
    }

    /// <summary>Review Focus 1: a rationale the store cannot hold is refused by name, and nothing is written anywhere.</summary>
    [Fact]
    public async Task R11_21_ARationaleCarryingANulIsRefusedAndNothingIsWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock);
        var details = new InMemoryFlagDetailStore();
        await PostExistsAsync(store, ct);

        var result = await new RaiseFlag(store, details, clock).RecordAsync(Post, Reporter, FlagKind.Spam, "bad\0rationale", ct);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/flag/detail-unstorable", error!.Type);
        Assert.Empty(FlagEvents(await LogAsync(store, ct)));
        Assert.Empty(Require(await details.ReadAllAsync(ct)));
    }

    /// <summary>A flag against a post the log never accepted is refused, and neither store is written: the log stays empty.</summary>
    [Fact]
    public async Task AFlagAgainstAPostThatDoesNotExistIsRefusedAndNothingIsWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock);
        var details = new InMemoryFlagDetailStore();

        var result = await new RaiseFlag(store, details, clock).RecordAsync(Post, Reporter, FlagKind.Spam, Rationale, ct);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/flag/no-such-post", error!.Type);
        Assert.Empty(Require(await details.ReadAllAsync(ct)));
        Assert.Empty(await LogAsync(store, ct));
    }

    /// <summary>R10.26: a credential in the rationale is refused before either store sees it.</summary>
    [Fact]
    public async Task R10_26_ACredentialInTheRationaleIsRefusedBeforeEitherStore()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock);
        var details = new InMemoryFlagDetailStore();
        await PostExistsAsync(store, ct);

        var result = await new RaiseFlag(store, details, clock)
            .RecordAsync(Post, Reporter, FlagKind.CredentialLeak, "Leaks AKIAIOSFODNN7EXAMPLE in the body.", ct);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/flag/rationale-rejected", error!.Type);
        Assert.DoesNotContain("AKIA", error.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.Empty(FlagEvents(await LogAsync(store, ct)));
        Assert.Empty(Require(await details.ReadAllAsync(ct)));
    }
}
