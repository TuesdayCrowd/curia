using System.Diagnostics.CodeAnalysis;
using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Application.Tests.Projections;

/// <summary>
/// §10.10's moderation records, folded out of the log (R10.60, R10.61). Flags themselves are
/// <see cref="FlagDirectoryTests"/>' subject: a flag's entry names no post (R10.62). Every
/// <see cref="AppendedEvent"/> comes from a real append through <see cref="InMemoryEventStore"/>
/// (CS-15).
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class FlagProjectorTests
{
    private const string Post = "01JPOST0000000000000000001";
    private const string Moderator = "operator:reviewer";
    private const string Flag = "01JFLAG000000000000000001";

    private static readonly DateTimeOffset Start = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    private static async Task<IReadOnlyList<AppendedEvent>> LogAsync(InMemoryEventStore store, CancellationToken ct) =>
        Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct).ConfigureAwait(false));

    private static async Task ModerateAsync(
        InMemoryEventStore store,
        string eventId,
        FlagKind category,
        ModerationEffect effect,
        CancellationToken ct,
        ModeratorKind moderator = ModeratorKind.Human,
        bool withAdjudicates = true,
        params string[] adjudicates)
    {
        var aggregate = Require(AggregateId.Create(Post));
        var history = Require(await store.ReadByAggregateAsync(aggregate, ct).ConfigureAwait(false));

        List<KeyValuePair<string, JsonValue>> members =
        [
            new(FlagProjector.PostIdField, new JsonValue.String(Post)),
            new(FlagProjector.ModeratorField, new JsonValue.String(ModeratorKinds.Wire(moderator))),
            new(FlagProjector.ActorIdField, new JsonValue.String(Moderator)),
            new(FlagProjector.EffectField, new JsonValue.String(ModerationEffects.Wire(effect))),
            new(FlagProjector.CategoryField, new JsonValue.String(FlagKinds.Wire(category))),
            new(FlagProjector.RationaleField, new JsonValue.String("reviewed and confirmed")),
        ];

        if (withAdjudicates)
            members.Add(new(FlagProjector.AdjudicatesField, new JsonValue.Array([.. adjudicates.Select(a => (JsonValue)new JsonValue.String(a))])));

        Require(await store.AppendAsync(
            aggregate,
            Require(AggregateVersion.From(history.Count)),
            [new DomainEvent(
                Require(EventId.Create(eventId)),
                Require(EventType.Create(FlagProjector.ModerationAppliedType)),
                Require(ActorId.Create(Moderator)),
                new JsonValue.Object([.. members]))],
            ct).ConfigureAwait(false));
    }

    /// <summary>
    /// A flag on the post's own stream, as the log held one before R10.62. It is the one shape whose
    /// post a public fold could ever see, so it is the one a category-keyed fold would have upheld.
    /// </summary>
    private static async Task RaiseLegacyAsync(InMemoryEventStore store, string eventId, FlagKind kind, CancellationToken ct)
    {
        var aggregate = Require(AggregateId.Create(Post));
        var history = Require(await store.ReadByAggregateAsync(aggregate, ct).ConfigureAwait(false));

        Require(await store.AppendAsync(
            aggregate,
            Require(AggregateVersion.From(history.Count)),
            [new DomainEvent(
                Require(EventId.Create(eventId)),
                Require(EventType.Create(FlagProjector.FlagRaisedType)),
                Require(ActorId.Create("https://agents.example/reporter")),
                new JsonValue.Object(
                [
                    new(FlagProjector.PostIdField, new JsonValue.String(Post)),
                    new(FlagProjector.RaisedByField, new JsonValue.String("https://agents.example/reporter")),
                    new(FlagProjector.KindField, new JsonValue.String(FlagKinds.Wire(kind))),
                    new(FlagProjector.RationaleField, new JsonValue.String("reported after the withholding")),
                ]))],
            ct).ConfigureAwait(false));
    }

    /// <summary>A post no record names is absent, not present-and-empty.</summary>
    [Fact]
    public async Task APostNoRecordNamesIsAbsentFromTheProjection()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Empty(FlagProjector.Fold(await LogAsync(new InMemoryEventStore(new ManualTimeProvider(Start)), ct)));
    }

    /// <summary>R10.36: an automated quarantine takes the post out of the serving path — and upholds nothing (R10.61).</summary>
    [Fact]
    public async Task R10_36_AQuarantineMakesThePostUnservable()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        await ModerateAsync(store, "01JMOD0000000000000000001", FlagKind.Injection, ModerationEffect.Quarantine, ct,
            ModeratorKind.Automated, true, Flag);

        var post = FlagProjector.Fold(await LogAsync(store, ct))[Post];
        Assert.False(post.MayServe);
        Assert.False(post.HasUpheldFlag);
    }

    /// <summary>A restore puts it back, with nothing to invalidate — the history is the state.</summary>
    [Fact]
    public async Task ARestoreMakesThePostServableAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        await ModerateAsync(store, "01JMOD0000000000000000001", FlagKind.Injection, ModerationEffect.Withhold, ct, ModeratorKind.Human, true, Flag);
        await ModerateAsync(store, "01JMOD0000000000000000002", FlagKind.Injection, ModerationEffect.Restore, ct, ModeratorKind.Human, true, Flag);

        var post = FlagProjector.Fold(await LogAsync(store, ct))[Post];
        Assert.True(post.MayServe);
        Assert.False(post.HasUpheldFlag);
    }

    /// <summary>Table 11 reads off this: a flag becomes upheld when a record that names it acts; a dismissal leaves it unupheld.</summary>
    [Fact]
    public async Task R10_61_AFlagIsUpheldOnlyWhenARecordThatNamesItActs()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        await ModerateAsync(store, "01JMOD0000000000000000001", FlagKind.Injection, ModerationEffect.Dismiss, ct, ModeratorKind.Human, true, Flag);
        Assert.False(FlagProjector.Fold(await LogAsync(store, ct))[Post].HasUpheldFlag);

        await ModerateAsync(store, "01JMOD0000000000000000002", FlagKind.Injection, ModerationEffect.Withhold, ct, ModeratorKind.Human, true, Flag);
        Assert.Equal([Flag], FlagProjector.Fold(await LogAsync(store, ct))[Post].UpheldFlags);
    }

    /// <summary>
    /// Spec Decision 5's late-flag test, carried across from Task 2 when flags left this fold. A
    /// withholding names the flags it reviewed. A flag raised against the post afterwards, in the same
    /// category, was reviewed by nobody. Keyed to the category, that flag was upheld the instant it was
    /// raised. Keyed to the record that names it, it is not upheld until a record does. The first half
    /// is also Decision 16: a proactive withholding moves no one's standing.
    /// </summary>
    [Fact]
    public async Task R10_61_AFlagRaisedAfterAWithholdingIsNotUpheldUntilARecordNamesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        await ModerateAsync(store, "01JMOD0000000000000000001", FlagKind.Spam, ModerationEffect.Withhold, ct);
        await RaiseLegacyAsync(store, Flag, FlagKind.Spam, ct);

        var post = FlagProjector.Fold(await LogAsync(store, ct))[Post];
        Assert.False(post.MayServe);
        Assert.False(post.HasUpheldFlag);

        await ModerateAsync(store, "01JMOD0000000000000000002", FlagKind.Spam, ModerationEffect.Withhold, ct, ModeratorKind.Human, true, Flag);
        Assert.Equal([Flag], FlagProjector.Fold(await LogAsync(store, ct))[Post].UpheldFlags);
    }

    /// <summary>
    /// A record written without <c>adjudicates</c> — as hand-built fixtures wrote them before R10.60 —
    /// is read, not dropped: it still withholds, and it upholds nothing.
    /// </summary>
    [Fact]
    public async Task R10_61_ARecordWithoutAdjudicatesStillWithholdsAndUpholdsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        await ModerateAsync(store, "01JMOD0000000000000000001", FlagKind.Spam, ModerationEffect.Withhold, ct,
            ModeratorKind.Human, withAdjudicates: false);

        var post = FlagProjector.Fold(await LogAsync(store, ct))[Post];
        Assert.False(post.MayServe);
        Assert.False(post.HasUpheldFlag);
    }

    /// <summary>
    /// R11.9: the projection rebuilds from zero to the identical state. Meaningful only because
    /// <see cref="PostModeration"/> and <see cref="ModerationAction"/> spell out structural equality.
    /// </summary>
    [Fact]
    public async Task R11_9_TheProjectionRebuildsFromZeroToTheIdenticalState()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        await ModerateAsync(store, "01JMOD0000000000000000001", FlagKind.Injection, ModerationEffect.Quarantine, ct, ModeratorKind.Human, true, Flag);

        var log = await LogAsync(store, ct);
        Assert.Equal(FlagProjector.Fold(log), FlagProjector.Fold(log));

        // The negative control: two folds of different logs must not compare equal.
        await ModerateAsync(store, "01JMOD0000000000000000002", FlagKind.Injection, ModerationEffect.Restore, ct, ModeratorKind.Human, true, Flag);
        Assert.NotEqual(FlagProjector.Fold(log), FlagProjector.Fold(await LogAsync(store, ct)));
    }
}
