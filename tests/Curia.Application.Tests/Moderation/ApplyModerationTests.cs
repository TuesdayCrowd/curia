using System.Diagnostics.CodeAnalysis;
using Curia.Application.Moderation;
using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Application.Tests.Moderation;

/// <summary>R10.59 and R10.60 at the writer: what a record carries, what it names, and what it refuses.</summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ApplyModerationTests
{
    private const string Post = "01JPOST0000000000000000001";
    private const string Reporter = "https://agents.example/reporter";
    private static readonly string Digest = "sha256:" + new string('a', 64);
    private static readonly DateTimeOffset Start = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} ({e.Detail})"));

    private static ActorId Operator => Require(ActorId.Create("operator:reviewer"));

    private sealed record World(InMemoryEventStore Store, InMemoryFlagDetailStore Details, ManualTimeProvider Clock)
    {
        public ApplyModeration Moderate => new(Store, Details, Clock);
    }

    private static async Task<World> WorldWithPostAsync(CancellationToken ct)
    {
        var clock = new ManualTimeProvider(Start);
        var world = new World(new InMemoryEventStore(clock), new InMemoryFlagDetailStore(), clock);

        // Every member PostProjector requires, shaped as IngestPipeline persists a post.
        Require(await world.Store.AppendAsync(
            Require(AggregateId.Create(Post)),
            AggregateVersion.New,
            [new DomainEvent(
                Require(EventId.Create(Post)),
                Require(EventType.Create(PostProjector.PostAcceptedType)),
                Require(ActorId.Create("https://agents.example/author")),
                new JsonValue.Object(
                [
                    new("post_id", new JsonValue.String(Post)),
                    new("canonical", new JsonValue.String("{\"body\":\"a question\"}")),
                    new("signature", new JsonValue.String("sig")),
                    new("digest", new JsonValue.String(Digest)),
                    new("author", new JsonValue.String("https://agents.example/author")),
                    new("board", new JsonValue.String("board-1")),
                    new("kind", new JsonValue.String("question")),
                ]))],
            ct).ConfigureAwait(false));

        return world;
    }

    private static async Task<IReadOnlyList<AppendedEvent>> LogAsync(World world, CancellationToken ct) =>
        Require(await world.Store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct).ConfigureAwait(false));

    /// <summary>Raises a flag through the real writer and returns its event id.</summary>
    private static async Task<string> FlagAsync(World world, FlagKind kind, CancellationToken ct)
    {
        Require(await new RaiseFlag(world.Store, world.Details, world.Clock).RecordAsync(Post, Reporter, kind, "reported", ct).ConfigureAwait(false));
        return (await LogAsync(world, ct).ConfigureAwait(false)).Last(e => e.Event.Type.Value == FlagProjector.FlagCommittedType).Event.Id.Value;
    }

    private static async Task<PostModeration> ModerationAsync(World world, CancellationToken ct) =>
        FlagProjector.Fold(await LogAsync(world, ct).ConfigureAwait(false))[Post];

    [Fact]
    public async Task R10_60_ARecordNamesThePostItsDigestAndTheFlagsItAdjudicates()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await WorldWithPostAsync(ct);
        var flag = await FlagAsync(world, FlagKind.Spam, ct);

        var recorded = Require(await world.Moderate.RecordAsync(Post, ModerationEffect.Withhold, FlagKind.Spam, "Reviewed: advertising.", Operator, ct));

        Assert.Equal(Digest, recorded.Digest);
        Assert.Equal([flag], recorded.Adjudicates);

        var record = (await LogAsync(world, ct)).Single(e => e.Event.Type.Value == FlagProjector.ModerationAppliedType);
        Assert.Equal(Post, record.AggregateId.Value);
        Assert.Equal("operator:reviewer", record.Event.Actor?.Value);

        var payload = ((JsonValue.Object)record.Event.Payload).Members.ToDictionary(m => m.Key, m => m.Value, StringComparer.Ordinal);
        Assert.Equal(new JsonValue.String(Digest), payload[FlagProjector.DigestField]);
        Assert.Equal(new JsonValue.String("human"), payload[FlagProjector.ModeratorField]);
        Assert.Equal([flag], ((JsonValue.Array)payload[FlagProjector.AdjudicatesField]).Items.Cast<JsonValue.String>().Select(s => s.Value));

        var moderation = await ModerationAsync(world, ct);
        Assert.False(moderation.MayServe);
        Assert.Equal([flag], moderation.UpheldFlags);
    }

    /// <summary>Review Focus 3: only the record's category is adjudicated; the other flag stays open.</summary>
    [Fact]
    public async Task R10_60_OnlyTheRecordsCategoryIsAdjudicated()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await WorldWithPostAsync(ct);
        var spam = await FlagAsync(world, FlagKind.Spam, ct);
        var injection = await FlagAsync(world, FlagKind.Injection, ct);

        var recorded = Require(await world.Moderate.RecordAsync(Post, ModerationEffect.Withhold, FlagKind.Spam, "Reviewed.", Operator, ct));

        Assert.Equal([spam], recorded.Adjudicates);
        Assert.DoesNotContain(injection, ModerationPolicy.AdjudicatedFlags((await ModerationAsync(world, ct)).History));
    }

    /// <summary>A flag written before R10.62, public on the post's stream, is adjudicated like a committed one.</summary>
    [Fact]
    public async Task R10_60_ALegacyFlagIsAdjudicatedLikeACommittedOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await WorldWithPostAsync(ct);

        var aggregate = Require(AggregateId.Create(Post));
        var history = Require(await world.Store.ReadByAggregateAsync(aggregate, ct));
        Require(await world.Store.AppendAsync(
            aggregate,
            Require(AggregateVersion.From(history.Count)),
            [new DomainEvent(
                Require(EventId.Create("01JLEGACY00000000000000001")),
                Require(EventType.Create(FlagProjector.FlagRaisedType)),
                Require(ActorId.Create(Reporter)),
                new JsonValue.Object(
                [
                    new(FlagProjector.PostIdField, new JsonValue.String(Post)),
                    new(FlagProjector.RaisedByField, new JsonValue.String(Reporter)),
                    new(FlagProjector.KindField, new JsonValue.String("spam")),
                    new(FlagProjector.RationaleField, new JsonValue.String("an old, public rationale")),
                ]))],
            ct));

        var recorded = Require(await world.Moderate.RecordAsync(Post, ModerationEffect.Withhold, FlagKind.Spam, "Reviewed.", Operator, ct));

        Assert.Equal(["01JLEGACY00000000000000001"], recorded.Adjudicates);
    }

    /// <summary>R10.39 counts records, so a record that changes nothing is refused by name, and nothing is appended.</summary>
    [Fact]
    public async Task R10_39_ASecondIdenticalRecordIsRefusedAsANoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await WorldWithPostAsync(ct);
        await FlagAsync(world, FlagKind.Spam, ct);

        Require(await world.Moderate.RecordAsync(Post, ModerationEffect.Withhold, FlagKind.Spam, "Reviewed.", Operator, ct));
        var before = (await LogAsync(world, ct)).Count;

        var again = await world.Moderate.RecordAsync(Post, ModerationEffect.Withhold, FlagKind.Spam, "Reviewed again.", Operator, ct);

        Assert.False(again.TryGetValue(out _, out var error));
        Assert.Equal("curia/moderation/no-op", error!.Type);
        Assert.Equal(before, (await LogAsync(world, ct)).Count);
    }

    /// <summary>A dismissal of open flags records a review (R10.39's denominator); a dismissal of nothing is a no-op.</summary>
    [Fact]
    public async Task R10_39_ADismissalOfOpenFlagsIsARecordAndADismissalOfNothingIsNot()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await WorldWithPostAsync(ct);

        var nothing = await world.Moderate.RecordAsync(Post, ModerationEffect.Dismiss, FlagKind.Incorrect, "Nothing to review.", Operator, ct);
        Assert.False(nothing.TryGetValue(out _, out var error));
        Assert.Equal("curia/moderation/no-op", error!.Type);

        var flag = await FlagAsync(world, FlagKind.Incorrect, ct);
        var dismissed = Require(await world.Moderate.RecordAsync(Post, ModerationEffect.Dismiss, FlagKind.Incorrect, "Reviewed: the premise holds.", Operator, ct));

        Assert.Equal([flag], dismissed.Adjudicates);
        var moderation = await ModerationAsync(world, ct);
        Assert.True(moderation.MayServe);
        Assert.Contains(flag, ModerationPolicy.AdjudicatedFlags(moderation.History));
        Assert.DoesNotContain(flag, moderation.UpheldFlags);
    }

    /// <summary>Review Focus 4: a restore after a proactive withholding is a record; a restore of a servable post is not.</summary>
    [Fact]
    public async Task R10_59_ARestoreAfterAProactiveWithholdingIsARecordARestoreOfAServablePostIsNot()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await WorldWithPostAsync(ct);

        var early = await world.Moderate.RecordAsync(Post, ModerationEffect.Restore, FlagKind.Spam, "Nothing withheld.", Operator, ct);
        Assert.False(early.TryGetValue(out _, out var error));
        Assert.Equal("curia/moderation/no-op", error!.Type);

        Require(await world.Moderate.RecordAsync(Post, ModerationEffect.Withhold, FlagKind.CredentialLeak, "A credential on line 2.", Operator, ct));
        Assert.False((await ModerationAsync(world, ct)).MayServe);

        Require(await world.Moderate.RecordAsync(Post, ModerationEffect.Restore, FlagKind.CredentialLeak, "Rotated; restoring.", Operator, ct));
        Assert.True((await ModerationAsync(world, ct)).MayServe);
    }

    /// <summary>R10.59: the human arm is an operator's. An agent-shaped actor is refused, and nothing is appended.</summary>
    [Fact]
    public async Task R10_59_OnlyAnOperatorRecordsAHumanAction()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await WorldWithPostAsync(ct);
        var before = (await LogAsync(world, ct)).Count;

        var result = await world.Moderate.RecordAsync(
            Post, ModerationEffect.Withhold, FlagKind.Spam, "Reviewed.", Require(ActorId.Create("https://agents.example/moderator")), ct);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/moderation/not-an-operator", error!.Type);
        Assert.Equal(before, (await LogAsync(world, ct)).Count);
    }

    /// <summary>R10.60: the rationale lands in a leaf R6.51 serves verbatim, so a credential in it is refused and not echoed.</summary>
    [Fact]
    public async Task R10_60_ACredentialInTheReasonIsRefusedAndNothingIsAppended()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await WorldWithPostAsync(ct);
        var before = (await LogAsync(world, ct)).Count;

        var result = await world.Moderate.RecordAsync(
            Post, ModerationEffect.Withhold, FlagKind.CredentialLeak, "It leaks AKIAIOSFODNN7EXAMPLE.", Operator, ct);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/moderation/rationale-rejected", error!.Type);
        Assert.DoesNotContain("AKIA", error.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(before, (await LogAsync(world, ct)).Count);
    }

    [Fact]
    public async Task AnActionOnAPostThatDoesNotExistIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await WorldWithPostAsync(ct);

        var result = await world.Moderate.RecordAsync(
            "01JNOSUCHPOST0000000000001", ModerationEffect.Withhold, FlagKind.Spam, "Reviewed.", Operator, ct);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/moderation/no-such-post", error!.Type);
    }

    /// <summary>
    /// R10.61 where lateness is visible. A flag raised after its category was withheld is neither upheld
    /// nor adjudicated by that withholding, and the next withholding is a record rather than a no-op,
    /// because it adjudicates the late flag. R10.60 has a reviewing record name every flag of its
    /// category raised before it, so that record names the first flag again; the late flag is the only
    /// one it adjudicates for the first time.
    /// </summary>
    [Fact]
    public async Task R10_61_ALateFlagIsNotUpheldByAnEarlierWithholdingAndTheNextWithholdingAdjudicatesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await WorldWithPostAsync(ct);

        var first = await FlagAsync(world, FlagKind.Spam, ct);
        var withheld = Require(await world.Moderate.RecordAsync(Post, ModerationEffect.Withhold, FlagKind.Spam, "Reviewed: advertising.", Operator, ct));
        Assert.Equal([first], withheld.Adjudicates);

        var late = await FlagAsync(world, FlagKind.Spam, ct);
        var between = await ModerationAsync(world, ct);
        Assert.Equal([first], between.UpheldFlags);
        Assert.DoesNotContain(late, ModerationPolicy.AdjudicatedFlags(between.History));

        var again = await world.Moderate.RecordAsync(Post, ModerationEffect.Withhold, FlagKind.Spam, "Reviewed: the same advertising.", Operator, ct);

        Assert.True(again.TryGetValue(out var recorded, out var error), error?.Type);
        Assert.Equal([first, late], recorded!.Adjudicates);
        var after = await ModerationAsync(world, ct);
        Assert.Equal([late], ModerationPolicy.AdjudicatedFlags(after.History).Except(ModerationPolicy.AdjudicatedFlags(between.History)));
        Assert.Contains(late, after.UpheldFlags);
    }

    /// <summary>
    /// R10.60: an automated record SHALL name no flag. The writer takes no moderator kind and records
    /// R10.59's human arm alone, so every record it writes, whatever its effect, names its flags as a
    /// human record, in the leaf and in the fold, and none is automated.
    /// </summary>
    [Fact]
    public async Task R10_60_EveryRecordTheWriterWritesIsHumanSoNoAutomatedRecordNamesAFlag()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await WorldWithPostAsync(ct);

        await FlagAsync(world, FlagKind.Spam, ct);
        Require(await world.Moderate.RecordAsync(Post, ModerationEffect.Quarantine, FlagKind.Spam, "Pending a closer look.", Operator, ct));
        Require(await world.Moderate.RecordAsync(Post, ModerationEffect.Restore, FlagKind.Spam, "Not advertising after all.", Operator, ct));
        await FlagAsync(world, FlagKind.Incorrect, ct);
        Require(await world.Moderate.RecordAsync(Post, ModerationEffect.Dismiss, FlagKind.Incorrect, "Reviewed: the premise holds.", Operator, ct));
        await FlagAsync(world, FlagKind.Injection, ct);
        Require(await world.Moderate.RecordAsync(Post, ModerationEffect.Withhold, FlagKind.Injection, "Reviewed: it addresses the reader.", Operator, ct));

        var records = (await LogAsync(world, ct)).Where(e => e.Event.Type.Value == FlagProjector.ModerationAppliedType).ToList();
        Assert.Equal(4, records.Count);
        Assert.All(records, r => Assert.Equal(
            new JsonValue.String("human"),
            ((JsonValue.Object)r.Event.Payload).Members.Single(m => m.Key == FlagProjector.ModeratorField).Value));

        var history = (await ModerationAsync(world, ct)).History;
        Assert.Equal(4, history.Length);
        Assert.All(history, action =>
        {
            Assert.NotEmpty(action.Adjudicates);
            Assert.Equal(ModeratorKind.Human, action.Moderator);
        });
    }
}
