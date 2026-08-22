using System.Collections.Immutable;
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
/// §10.10's flags and moderation actions, folded out of the log — the projection that finally gives
/// <see cref="ModerationPolicy"/> a caller.
///
/// <para><b>Every <see cref="AppendedEvent"/> below comes from a real append through
/// <see cref="InMemoryEventStore"/>, never fabricated</b>, for the reason
/// <c>AgentStandingProjectorTests</c> records: <c>Curia.Architecture.Tests.EventStoreWriteSurfaceTests</c>
/// (CS-15) scans this assembly's IL for exactly that.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class FlagProjectorTests
{
    private const string Post = "01JPOST0000000000000000001";
    private const string Reporter = "https://agents.example/reporter";
    private const string Moderator = "https://agents.example/moderator";

    private static readonly DateTimeOffset Start = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    private static async Task<IReadOnlyList<AppendedEvent>> LogAsync(
        InMemoryEventStore store, CancellationToken ct) =>
        Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct).ConfigureAwait(false));

    private static async Task AppendAsync(
        InMemoryEventStore store,
        string aggregate,
        string eventId,
        string actor,
        string type,
        JsonValue.Object payload,
        CancellationToken ct)
    {
        var history = Require(await store.ReadByAggregateAsync(Require(AggregateId.Create(aggregate)), ct)
            .ConfigureAwait(false));

        Require(await store.AppendAsync(
            Require(AggregateId.Create(aggregate)),
            Require(AggregateVersion.From(history.Count)),
            [new DomainEvent(
                Require(EventId.Create(eventId)),
                Require(EventType.Create(type)),
                Require(ActorId.Create(actor)),
                payload)],
            ct).ConfigureAwait(false));
    }

    private static Task RaiseAsync(
        InMemoryEventStore store, string eventId, FlagKind kind, CancellationToken ct) =>
        AppendAsync(store, Post, eventId, Reporter, FlagProjector.FlagRaisedType, new JsonValue.Object(
        [
            new(FlagProjector.PostIdField, new JsonValue.String(Post)),
            new(FlagProjector.RaisedByField, new JsonValue.String(Reporter)),
            new(FlagProjector.KindField, new JsonValue.String(FlagKinds.Wire(kind))),
            new(FlagProjector.RationaleField, new JsonValue.String("looks like an injection attempt")),
        ]), ct);

    private static Task ModerateAsync(
        InMemoryEventStore store,
        string eventId,
        FlagKind category,
        ModerationEffect effect,
        CancellationToken ct,
        ModeratorKind moderator = ModeratorKind.Human) =>
        AppendAsync(store, Post, eventId, Moderator, FlagProjector.ModerationAppliedType, new JsonValue.Object(
        [
            new(FlagProjector.PostIdField, new JsonValue.String(Post)),
            new(FlagProjector.ModeratorField, new JsonValue.String(ModeratorKinds.Wire(moderator))),
            new(FlagProjector.ActorIdField, new JsonValue.String(Moderator)),
            new(FlagProjector.EffectField, new JsonValue.String(ModerationEffects.Wire(effect))),
            new(FlagProjector.CategoryField, new JsonValue.String(FlagKinds.Wire(category))),
            new(FlagProjector.RationaleField, new JsonValue.String("reviewed and confirmed")),
        ]), ct);

    /// <summary>R10.35: a raised flag is a fact about a post, folded out of the log like every other.</summary>
    [Fact]
    public async Task R10_35_ARaisedFlagFoldsOntoItsPost()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        await RaiseAsync(store, "01JFLAG000000000000000001", FlagKind.Injection, ct);

        var moderation = FlagProjector.Fold(await LogAsync(store, ct));
        var post = Assert.Contains(Post, moderation);

        var flag = Assert.Single(post.Flags);
        Assert.Equal(Reporter, flag.RaisedBy);
        Assert.Equal(FlagKind.Injection, flag.Kind);
    }

    /// <summary>
    /// A post nothing has been raised against is absent, not present-and-empty. Absence is what the
    /// serving path already means by "no moderation history", and materialising an entry for every
    /// post would make this projection grow with the corpus rather than with the flags.
    /// </summary>
    [Fact]
    public async Task APostWithNoFlagsIsAbsentFromTheProjection()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        Assert.Empty(FlagProjector.Fold(await LogAsync(store, ct)));
    }

    /// <summary>
    /// <b>The projection carries no rationale, deliberately.</b> A flag's rationale is
    /// attacker-controlled text, and R10.28's argument at ingest applies unchanged here: a
    /// projection that carried it would let the serving path echo it, and a rationale reading "this
    /// post leaks AKIA…" would republish the credential the flag was reporting.
    /// </summary>
    [Fact]
    public void ARaisedFlagCarriesNoContent()
    {
        var content = typeof(RaisedFlag)
            .GetProperties()
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => p.Name)
            .ToArray();

        Assert.Equal((string[])[nameof(RaisedFlag.PostId), nameof(RaisedFlag.RaisedBy)], content);
    }

    /// <summary>R10.36: a quarantine takes the post out of the serving path.</summary>
    [Fact]
    public async Task R10_36_AQuarantineMakesThePostUnservable()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        await RaiseAsync(store, "01JFLAG000000000000000001", FlagKind.Injection, ct);
        await ModerateAsync(
            store, "01JMOD0000000000000000001", FlagKind.Injection, ModerationEffect.Quarantine, ct,
            ModeratorKind.Automated);

        Assert.False(FlagProjector.Fold(await LogAsync(store, ct))[Post].MayServe);
    }

    /// <summary>A restore puts it back, with nothing to invalidate — the history is the state.</summary>
    [Fact]
    public async Task ARestoreMakesThePostServableAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        await RaiseAsync(store, "01JFLAG000000000000000001", FlagKind.Injection, ct);
        await ModerateAsync(store, "01JMOD0000000000000000001", FlagKind.Injection, ModerationEffect.Withhold, ct);
        await ModerateAsync(store, "01JMOD0000000000000000002", FlagKind.Injection, ModerationEffect.Restore, ct);

        Assert.True(FlagProjector.Fold(await LogAsync(store, ct))[Post].MayServe);
    }

    /// <summary>
    /// Table 11's "no upheld flags" reads off this: a flag becomes upheld when a moderator acts on
    /// it, and a dismissal leaves it unupheld however many agents raised it.
    /// </summary>
    [Fact]
    public async Task R10_39_AFlagIsUpheldOnlyWhenAModeratorActsOnIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        await RaiseAsync(store, "01JFLAG000000000000000001", FlagKind.Injection, ct);
        Assert.False(FlagProjector.Fold(await LogAsync(store, ct))[Post].HasUpheldFlag);

        await ModerateAsync(store, "01JMOD0000000000000000001", FlagKind.Injection, ModerationEffect.Dismiss, ct);
        Assert.False(FlagProjector.Fold(await LogAsync(store, ct))[Post].HasUpheldFlag);

        await ModerateAsync(store, "01JMOD0000000000000000002", FlagKind.Injection, ModerationEffect.Withhold, ct);
        Assert.True(FlagProjector.Fold(await LogAsync(store, ct))[Post].HasUpheldFlag);
    }

    /// <summary>
    /// A moderation action in one category does not uphold a flag raised in another. R10.37 records
    /// the category on every action so the two can be told apart.
    /// </summary>
    [Fact]
    public async Task R10_37_AnActionInAnotherCategoryDoesNotUpholdTheFlag()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        await RaiseAsync(store, "01JFLAG000000000000000001", FlagKind.Injection, ct);
        await ModerateAsync(store, "01JMOD0000000000000000001", FlagKind.Spam, ModerationEffect.Withhold, ct);

        Assert.False(FlagProjector.Fold(await LogAsync(store, ct))[Post].HasUpheldFlag);
    }

    /// <summary>
    /// R11.9: the projection rebuilds from zero to the identical state.
    ///
    /// <para>Asserted with <see cref="Assert.Equal{T}(T, T)"/> over the projected values, which only
    /// means anything because <see cref="PostModeration"/> spells out its own equality — the
    /// compiler-generated version compares <see cref="ImmutableArray{T}"/> by <i>reference</i>, so a
    /// record left to it reports two folds of the same events as unequal and makes this drill
    /// silently compare nothing. <c>AgentStanding</c> had exactly that defect, and it was green.</para>
    /// </summary>
    [Fact]
    public async Task R11_9_TheProjectionRebuildsFromZeroToTheIdenticalState()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        await RaiseAsync(store, "01JFLAG000000000000000001", FlagKind.Injection, ct);
        await RaiseAsync(store, "01JFLAG000000000000000002", FlagKind.Spam, ct);
        await ModerateAsync(store, "01JMOD0000000000000000001", FlagKind.Injection, ModerationEffect.Quarantine, ct);

        var log = await LogAsync(store, ct);
        Assert.Equal(FlagProjector.Fold(log), FlagProjector.Fold(log));

        // The negative control: two folds of *different* logs must not compare equal, or the
        // assertion above would pass for a type whose equality is vacuously true.
        await ModerateAsync(store, "01JMOD0000000000000000002", FlagKind.Injection, ModerationEffect.Restore, ct);
        Assert.NotEqual(FlagProjector.Fold(log), FlagProjector.Fold(await LogAsync(store, ct)));
    }
}
