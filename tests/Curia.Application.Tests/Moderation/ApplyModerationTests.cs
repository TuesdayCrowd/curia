using System.Diagnostics.CodeAnalysis;
using Curia.Application.Moderation;
using Curia.Application.Ports;
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
    private const string OtherPost = "01JPOST0000000000000000002";
    private const string Reporter = "https://agents.example/reporter";
    private static readonly string Digest = "sha256:" + new string('a', 64);
    private static readonly string OtherDigest = "sha256:" + new string('b', 64);
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
        await AcceptPostAsync(world, Post, Digest, ct).ConfigureAwait(false);
        return world;
    }

    /// <summary>Every member PostProjector requires, shaped as IngestPipeline persists a post.</summary>
    private static async Task AcceptPostAsync(World world, string postId, string digest, CancellationToken ct) =>
        Require(await world.Store.AppendAsync(
            Require(AggregateId.Create(postId)),
            AggregateVersion.New,
            [new DomainEvent(
                Require(EventId.Create(postId)),
                Require(EventType.Create(PostProjector.PostAcceptedType)),
                Require(ActorId.Create("https://agents.example/author")),
                new JsonValue.Object(
                [
                    new("post_id", new JsonValue.String(postId)),
                    new("canonical", new JsonValue.String("{\"body\":\"a question\"}")),
                    new("signature", new JsonValue.String("sig")),
                    new("digest", new JsonValue.String(digest)),
                    new("author", new JsonValue.String("https://agents.example/author")),
                    new("board", new JsonValue.String("board-1")),
                    new("kind", new JsonValue.String("question")),
                ]))],
            ct).ConfigureAwait(false));

    private static async Task<IReadOnlyList<AppendedEvent>> LogAsync(World world, CancellationToken ct) =>
        Require(await world.Store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct).ConfigureAwait(false));

    /// <summary>Raises a flag against <see cref="Post"/> through the real writer and returns its event id.</summary>
    private static Task<string> FlagAsync(World world, FlagKind kind, CancellationToken ct) => FlagAsync(world, Post, kind, ct);

    /// <summary>Raises a flag against <paramref name="postId"/> through the real writer and returns its event id.</summary>
    private static async Task<string> FlagAsync(World world, string postId, FlagKind kind, CancellationToken ct)
    {
        Require(await new RaiseFlag(world.Store, world.Details, world.Clock).RecordAsync(postId, Reporter, kind, "reported", ct).ConfigureAwait(false));
        return (await LogAsync(world, ct).ConfigureAwait(false)).Last(e => e.Event.Type.Value == FlagProjector.FlagCommittedType).Event.Id.Value;
    }

    /// <summary>Raises a flag against <see cref="Post"/> with the given raiser and rationale, through the real writer.</summary>
    private static async Task<string> FlagAsync(World world, FlagKind kind, string raiser, string rationale, CancellationToken ct)
    {
        Require(await new RaiseFlag(world.Store, world.Details, world.Clock).RecordAsync(Post, raiser, kind, rationale, ct).ConfigureAwait(false));
        return (await LogAsync(world, ct).ConfigureAwait(false)).Last(e => e.Event.Type.Value == FlagProjector.FlagCommittedType).Event.Id.Value;
    }

    private static async Task<PostModeration> ModerationAsync(World world, CancellationToken ct) =>
        FlagProjector.Fold(await LogAsync(world, ct).ConfigureAwait(false))[Post];

    /// <summary>
    /// A detail store that lets a second writer append between the writer's read of the log and its
    /// own append: where two operators acting on one post at once would interleave.
    /// </summary>
    private sealed class InterleavingDetailStore(IFlagDetailStore inner, Func<Task> interleave) : IFlagDetailStore
    {
        public Task<Result<FlagDetail>> AppendAsync(FlagDetail detail, CancellationToken cancellationToken = default) =>
            inner.AppendAsync(detail, cancellationToken);

        public async Task<Result<IReadOnlyList<FlagDetail>>> ReadAllAsync(CancellationToken cancellationToken = default)
        {
            await interleave().ConfigureAwait(false);
            return await inner.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        }
    }

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

    /// <summary>
    /// R10.59's <c>operator:&lt;name&gt;</c> names who acted. <c>operator: </c> is a valid
    /// <see cref="ActorId"/> and passes the prefix test, but it names no one, in a leaf that is public
    /// and permanent; it is refused by name, and nothing is appended. The operator tool refuses a
    /// blank <c>--by</c> first; this is the writer's own guard, for every other caller.
    /// </summary>
    [Fact]
    public async Task R10_59_ABlankOperatorNameIsRefusedAndNothingIsAppended()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await WorldWithPostAsync(ct);
        var before = (await LogAsync(world, ct)).Count;

        var result = await world.Moderate.RecordAsync(
            Post, ModerationEffect.Withhold, FlagKind.Spam, "Reviewed.", Require(ActorId.Create("operator:   ")), ct);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/moderation/blank-operator-name", error!.Type);
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

    /// <summary>
    /// R10.60 names the flags raised against the record's own post. A flag of the same category on
    /// another post is neither named nor adjudicated: naming it would put a false flag-to-post link in a
    /// leaf nothing can take back (R11.6, R6.51), and would decide that flag without anyone reviewing it.
    /// </summary>
    [Fact]
    public async Task R10_60_AFlagOfTheSameCategoryOnAnotherPostIsNeitherNamedNorAdjudicated()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await WorldWithPostAsync(ct);
        await AcceptPostAsync(world, OtherPost, OtherDigest, ct);
        var theirs = await FlagAsync(world, OtherPost, FlagKind.Spam, ct);
        var mine = await FlagAsync(world, Post, FlagKind.Spam, ct);

        Require(await world.Moderate.RecordAsync(Post, ModerationEffect.Withhold, FlagKind.Spam, "Reviewed: advertising.", Operator, ct));

        var log = await LogAsync(world, ct);
        var record = log.Single(e => e.Event.Type.Value == FlagProjector.ModerationAppliedType);
        var named = ((JsonValue.Object)record.Event.Payload).Members.Single(m => m.Key == FlagProjector.AdjudicatesField).Value;
        Assert.Equal([mine], ((JsonValue.Array)named).Items.Cast<JsonValue.String>().Select(s => s.Value));
        Assert.DoesNotContain(theirs, FlagProjector.Fold(log).Values.SelectMany(m => ModerationPolicy.AdjudicatedFlags(m.History)));
    }

    /// <summary>
    /// The final review's probe. A post withheld for a credential leak, then "restored" in <c>spam</c>:
    /// under R10.61 the restore releases nothing, since only the credential-leak hold keeps the post
    /// from being served, and the writer refuses it by name. The open spam flag is there so the no-op
    /// rule cannot refuse it first: the restore would adjudicate that flag for the first time.
    /// </summary>
    [Fact]
    public async Task R10_61_TheProbeSequenceIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await WorldWithPostAsync(ct);
        var leak = await FlagAsync(world, FlagKind.CredentialLeak, ct);
        Require(await world.Moderate.RecordAsync(Post, ModerationEffect.Withhold, FlagKind.CredentialLeak, "A credential on line 2.", Operator, ct));
        await FlagAsync(world, FlagKind.Spam, ct);
        var before = (await LogAsync(world, ct)).Count;

        var restore = await world.Moderate.RecordAsync(Post, ModerationEffect.Restore, FlagKind.Spam, "Not advertising after all.", Operator, ct);

        Assert.False(restore.TryGetValue(out _, out var error));
        Assert.Equal("curia/moderation/restore-of-unheld-category", error!.Type);
        Assert.Equal(before, (await LogAsync(world, ct)).Count);

        var moderation = await ModerationAsync(world, ct);
        Assert.False(moderation.MayServe);
        Assert.Equal([leak], moderation.UpheldFlags);
    }

    /// <summary>
    /// R10.61: a dismissal holds and releases nothing, so in a category that holds the post it would
    /// leave the post withheld with the flag behind the hold no longer upheld. The writer refuses it by
    /// name, and nothing is appended.
    /// </summary>
    [Fact]
    public async Task R10_61_ADismissalInAHeldCategoryIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await WorldWithPostAsync(ct);
        var flag = await FlagAsync(world, FlagKind.Spam, ct);
        Require(await world.Moderate.RecordAsync(Post, ModerationEffect.Withhold, FlagKind.Spam, "Reviewed: advertising.", Operator, ct));
        var before = (await LogAsync(world, ct)).Count;

        var dismissal = await world.Moderate.RecordAsync(Post, ModerationEffect.Dismiss, FlagKind.Spam, "Reviewed again: not advertising.", Operator, ct);

        Assert.False(dismissal.TryGetValue(out _, out var error));
        Assert.Equal("curia/moderation/dismissal-of-held-category", error!.Type);
        Assert.Equal(before, (await LogAsync(world, ct)).Count);

        var moderation = await ModerationAsync(world, ct);
        Assert.False(moderation.MayServe);
        Assert.Equal([flag], moderation.UpheldFlags);
    }

    /// <summary>
    /// R10.61: a hold in a second category changes which categories hold the post, though not whether
    /// it is served, so it is a record rather than a no-op; and a restore in the first category then
    /// leaves the post held by the second.
    /// </summary>
    [Fact]
    public async Task R10_61_AHoldInASecondCategoryIsNotANoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await WorldWithPostAsync(ct);
        Require(await world.Moderate.RecordAsync(Post, ModerationEffect.Withhold, FlagKind.Spam, "Reviewed: advertising.", Operator, ct));
        var before = (await LogAsync(world, ct)).Count;

        var second = await world.Moderate.RecordAsync(Post, ModerationEffect.Withhold, FlagKind.CredentialLeak, "A credential on line 2.", Operator, ct);

        Assert.True(second.TryGetValue(out _, out var error), error?.Type);
        Assert.Equal(before + 1, (await LogAsync(world, ct)).Count);

        Require(await world.Moderate.RecordAsync(Post, ModerationEffect.Restore, FlagKind.Spam, "Advertising was a misreading.", Operator, ct));
        Assert.False((await ModerationAsync(world, ct)).MayServe);
    }

    /// <summary>
    /// The escalation. After a human quarantine, a human withholding in the same category changes how
    /// the category holds the post, which R10.60 publishes as the record's effect, so it is a record:
    /// one entry, the post never servable in between, the upheld flags unchanged. Refused as a no-op,
    /// the only way to escalate was restore-then-withhold, which serves the post in between and leaves
    /// a permanent restore nobody meant. A repeated withholding is still a no-op.
    /// </summary>
    [Fact]
    public async Task R10_61_AHumanWithholdingAfterAHumanQuarantineIsARecord()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await WorldWithPostAsync(ct);
        var flag = await FlagAsync(world, FlagKind.Spam, ct);
        Require(await world.Moderate.RecordAsync(Post, ModerationEffect.Quarantine, FlagKind.Spam, "Pending a closer look.", Operator, ct));
        var quarantined = await ModerationAsync(world, ct);
        var before = (await LogAsync(world, ct)).Count;

        var withheld = await world.Moderate.RecordAsync(Post, ModerationEffect.Withhold, FlagKind.Spam, "Reviewed: advertising.", Operator, ct);

        Assert.True(withheld.TryGetValue(out var recorded, out var error), error?.Type);
        Assert.Equal([flag], recorded!.Adjudicates);
        Assert.Equal(before + 1, (await LogAsync(world, ct)).Count);

        var after = await ModerationAsync(world, ct);
        Assert.False(quarantined.MayServe);
        Assert.False(after.MayServe);
        Assert.Equal([flag], quarantined.UpheldFlags);
        Assert.Equal([flag], after.UpheldFlags);

        var again = await world.Moderate.RecordAsync(Post, ModerationEffect.Withhold, FlagKind.Spam, "Reviewed: advertising, again.", Operator, ct);

        Assert.False(again.TryGetValue(out _, out var repeated));
        Assert.Equal("curia/moderation/no-op", repeated!.Type);
        Assert.Equal(before + 1, (await LogAsync(world, ct)).Count);
    }

    /// <summary>A rationale long enough for R10.62's quote rule, and no raiser's identity in it.</summary>
    private const string Advert = "This post is an advert for a storefront, with a list of coupon codes and no question in it.";

    /// <summary>
    /// R10.62: a flag's raiser is published never, and the record's reason is published always (R10.60),
    /// so a reason naming the raiser is refused before anything is appended — refused, not repaired
    /// (R10.26). A moderator, or a model drafting the reason from the review queue, repeating the report
    /// is the natural way this fails.
    /// </summary>
    [Fact]
    public async Task R10_62_ARaiserInTheReasonIsRefusedAndNothingAppended()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await WorldWithPostAsync(ct);
        await FlagAsync(world, FlagKind.Spam, Reporter, "Advertising, not a question.", ct);
        var before = (await LogAsync(world, ct)).Count;

        var result = await world.Moderate.RecordAsync(
            Post, ModerationEffect.Withhold, FlagKind.Spam, "Upheld, as " + Reporter + " reported.", Operator, ct);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/moderation/rationale-discloses-flag", error!.Type);
        Assert.Equal("field=raised_by", error.Detail);
        Assert.Equal(before, (await LogAsync(world, ct)).Count);
        Assert.True((await LogAsync(world, ct)).All(e => e.Event.Type.Value != FlagProjector.ModerationAppliedType));
    }

    /// <summary>
    /// R10.62's raiser rule compares normalized text — NFKC, lower-cased, whitespace runs collapsed — and
    /// matches the raiser with or without its <c>scheme://</c>. An ordinal comparison lets a change of case
    /// through, and one form alone lets the other through. The last row spells the first letter as a
    /// fullwidth <c>a</c>, which NFKC folds.
    /// </summary>
    [Theory]
    [InlineData("Upheld, as HTTPS://AGENTS.EXAMPLE/REPORTER reported.")]
    [InlineData("Upheld, as agents.example/reporter reported.")]
    [InlineData("Upheld, as Agents.Example/Reporter reported.")]
    [InlineData("Upheld, as \uFF41gents.example/reporter reported.")]
    public async Task R10_62_ARaiserMatchesCaseFoldedAndSchemeless(string reason)
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await WorldWithPostAsync(ct);
        await FlagAsync(world, FlagKind.Spam, Reporter, "Advertising, not a question.", ct);
        var before = (await LogAsync(world, ct)).Count;

        var result = await world.Moderate.RecordAsync(Post, ModerationEffect.Withhold, FlagKind.Spam, reason, Operator, ct);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/moderation/rationale-discloses-flag", error!.Type);
        Assert.Equal("field=raised_by", error.Detail);
        Assert.Equal(before, (await LogAsync(world, ct)).Count);
    }

    /// <summary>
    /// R10.62's quote rule: 32 consecutive characters of a rationale are refused and 31 are not, and a
    /// rationale shorter than 32 characters is not checked at all, so a one-word rationale such as
    /// "spam" never blocks a reason.
    /// </summary>
    [Fact]
    public async Task R10_62_ThirtyTwoCharactersOfARationaleAreRefusedThirtyOneAreNot()
    {
        const string shortRationale = "The premise on line 1 is false.";
        Assert.Equal(31, shortRationale.Length);

        var ct = TestContext.Current.CancellationToken;
        var world = await WorldWithPostAsync(ct);
        await FlagAsync(world, FlagKind.Spam, Reporter, Advert, ct);
        await FlagAsync(world, FlagKind.Incorrect, Reporter, shortRationale, ct);

        var thirtyOne = await world.Moderate.RecordAsync(
            Post, ModerationEffect.Withhold, FlagKind.Spam,
            "Reviewed: \"" + Advert[..31] + "\" and \"" + shortRationale + "\".", Operator, ct);
        Assert.True(thirtyOne.TryGetValue(out _, out var accepted), accepted?.Type + " " + accepted?.Detail);
        var before = (await LogAsync(world, ct)).Count;

        var thirtyTwo = await world.Moderate.RecordAsync(
            Post, ModerationEffect.Restore, FlagKind.Spam, "Reviewed: \"" + Advert[..32] + "\".", Operator, ct);

        Assert.False(thirtyTwo.TryGetValue(out _, out var error));
        Assert.Equal("curia/moderation/rationale-discloses-flag", error!.Type);
        Assert.Equal("field=rationale", error.Detail);
        Assert.Equal(before, (await LogAsync(world, ct)).Count);
    }

    /// <summary>
    /// R10.62's quote rule checks every 32-character window of a rationale, so quoting part of it is
    /// refused as quoting all of it is; and the comparison is over normalized text, so a change of case
    /// or of line breaks does not get a quote through.
    /// </summary>
    [Theory]
    [InlineData("Reviewed: the flag said \"an advert for a storefront, with a list of coupon codes\", which holds.")]
    [InlineData("Reviewed: the flag said \"AN ADVERT FOR A STOREFRONT,\n  WITH A LIST OF COUPON CODES\", which holds.")]
    public async Task R10_62_APartialQuoteIsRefused(string reason)
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await WorldWithPostAsync(ct);
        await FlagAsync(world, FlagKind.Spam, Reporter, Advert, ct);
        var before = (await LogAsync(world, ct)).Count;

        var result = await world.Moderate.RecordAsync(Post, ModerationEffect.Withhold, FlagKind.Spam, reason, Operator, ct);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/moderation/rationale-discloses-flag", error!.Type);
        Assert.Equal("field=rationale", error.Detail);
        Assert.Equal(before, (await LogAsync(world, ct)).Count);
    }

    /// <summary>
    /// R10.62 publishes a raiser never, whichever record is being written: every flag on the post is
    /// checked, not only the flags this record adjudicates. A record in <c>injection</c> naming the
    /// raiser of a <c>spam</c> flag publishes that raiser as surely as a record in <c>spam</c> would.
    /// </summary>
    [Fact]
    public async Task R10_62_AFlagOfAnotherCategoryIsChecked()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await WorldWithPostAsync(ct);
        await FlagAsync(world, FlagKind.Spam, Reporter, "Advertising, not a question.", ct);
        var before = (await LogAsync(world, ct)).Count;

        var result = await world.Moderate.RecordAsync(
            Post, ModerationEffect.Withhold, FlagKind.Injection, "Addresses the reader; " + Reporter + " saw it first.", Operator, ct);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/moderation/rationale-discloses-flag", error!.Type);
        Assert.Equal("field=raised_by", error.Detail);
        Assert.Equal(before, (await LogAsync(world, ct)).Count);
    }

    /// <summary>
    /// The refusal says which field was repeated and nothing else: not the raiser, not the rationale's
    /// text, and not the flag, whose id would tie the flag to this post for whoever reads the refusal
    /// (R10.27, R10.28, R10.62).
    /// </summary>
    [Fact]
    public async Task R10_62_TheRefusalNamesNoFlagOrText()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await WorldWithPostAsync(ct);
        var flag = await FlagAsync(world, FlagKind.Spam, Reporter, Advert, ct);

        var byRaiser = await world.Moderate.RecordAsync(
            Post, ModerationEffect.Withhold, FlagKind.Spam, "Upheld, as " + Reporter + " reported.", Operator, ct);
        var byRationale = await world.Moderate.RecordAsync(
            Post, ModerationEffect.Withhold, FlagKind.Spam, "Upheld: \"" + Advert[10..50] + "\".", Operator, ct);

        Assert.False(byRaiser.TryGetValue(out _, out var raiserError));
        Assert.False(byRationale.TryGetValue(out _, out var rationaleError));
        Assert.Equal("field=raised_by", raiserError!.Detail);
        Assert.Equal("field=rationale", rationaleError!.Detail);

        foreach (var error in (Error[])[raiserError, rationaleError])
        {
            var said = error.Type + " " + error.Title + " " + error.Detail;
            Assert.DoesNotContain("agents.example", said, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(flag, said, StringComparison.Ordinal);
            Assert.DoesNotContain("storefront", said, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("coupon", said, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Records a withholding in <c>spam</c> on a post whose one spam flag was raised by <paramref name="raiser"/>.</summary>
    private static async Task<(Result<ModerationRecorded> Result, int Before, int After)> WithholdAfterFlagByAsync(
        string raiser, string reason, CancellationToken ct)
    {
        var world = await WorldWithPostAsync(ct).ConfigureAwait(false);
        await FlagAsync(world, FlagKind.Spam, raiser, "Advertising, not a question.", ct).ConfigureAwait(false);
        var before = (await LogAsync(world, ct).ConfigureAwait(false)).Count;
        var result = await world.Moderate.RecordAsync(Post, ModerationEffect.Withhold, FlagKind.Spam, reason, Operator, ct).ConfigureAwait(false);
        return (result, before, (await LogAsync(world, ct).ConfigureAwait(false)).Count);
    }

    /// <summary>
    /// R10.62's raiser floor. Enrolment accepts any non-blank id (D4), so a raiser form shorter than 16
    /// characters is not checked: matched inside ordinary words, a one-character id would make its post
    /// unmoderatable. A raiser below the floor leaves only itself unprotected. The last two rows use
    /// <c>e</c> as a word of its own, which only the floor lets through.
    /// </summary>
    [Theory]
    [InlineData("https://e", "Reviewed: advertising.")]
    [InlineData("e", "Reviewed: advertising.")]
    [InlineData("https://e", "Reviewed: advertising, as in exhibit e.")]
    [InlineData("e", "Reviewed: advertising, as in exhibit e.")]
    public async Task R10_62_AOneCharacterRaiserCannotShieldItsPost(string raiser, string reason)
    {
        var (result, before, after) = await WithholdAfterFlagByAsync(raiser, reason, TestContext.Current.CancellationToken);

        Assert.True(result.TryGetValue(out _, out var error), error?.Type + " " + error?.Detail);
        Assert.Equal(before + 1, after);
    }

    /// <summary>R10.62's raiser floor is 16 characters: a 16-character raiser is checked, a 15-character one is not.</summary>
    [Fact]
    public async Task R10_62_SixteenCharactersAreCheckedFifteenAreNot()
    {
        var ct = TestContext.Current.CancellationToken;
        const string sixteen = "did:example:1234";
        const string fifteen = "did:example:123";
        Assert.Equal((16, 15), (sixteen.Length, fifteen.Length));

        var (checkedResult, checkedBefore, checkedAfter) = await WithholdAfterFlagByAsync(sixteen, "Upheld, as " + sixteen + " reported.", ct);
        Assert.False(checkedResult.TryGetValue(out _, out var error));
        Assert.Equal("curia/moderation/rationale-discloses-flag", error!.Type);
        Assert.Equal(checkedBefore, checkedAfter);

        var (uncheckedResult, uncheckedBefore, uncheckedAfter) = await WithholdAfterFlagByAsync(fifteen, "Upheld, as " + fifteen + " reported.", ct);
        Assert.True(uncheckedResult.TryGetValue(out _, out var uncheckedError), uncheckedError?.Type + " " + uncheckedError?.Detail);
        Assert.Equal(uncheckedBefore + 1, uncheckedAfter);
    }

    /// <summary>
    /// R10.62 matches a raiser as a whole token: a form with a letter or digit directly beside it is part
    /// of a longer word or id, not a repeat. Citing <c>agents.example/reporter</c> does not name
    /// <c>https://agents.example/rep</c>.
    /// </summary>
    [Fact]
    public async Task R10_62_ARaiserInsideALongerIdIsNotARepeat()
    {
        var (result, before, after) = await WithholdAfterFlagByAsync(
            "https://agents.example/rep",
            "Reviewed: the pattern agents.example/reporter described elsewhere applies here too.",
            TestContext.Current.CancellationToken);

        Assert.True(result.TryGetValue(out _, out var error), error?.Type + " " + error?.Detail);
        Assert.Equal(before + 1, after);
    }

    /// <summary>
    /// A token boundary is anything that is not a letter or a digit, so a raiser echoed at the end of a
    /// sentence, before its full stop, is still refused. Punctuation is not part of a token.
    /// </summary>
    [Fact]
    public async Task R10_62_AnEchoBeforeAFullStopIsRefused()
    {
        var (result, before, after) = await WithholdAfterFlagByAsync(
            Reporter, "Upheld, as reported by " + Reporter + ".", TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/moderation/rationale-discloses-flag", error!.Type);
        Assert.Equal("field=raised_by", error.Detail);
        Assert.Equal(before, after);
    }

    /// <summary>
    /// The floor applies to each form. <c>agent://c.io/a/b</c> is 16 characters and is checked; its
    /// schemeless form, <c>c.io/a/b</c>, is 8 and is not. A short host is still caught by its full form.
    /// </summary>
    [Fact]
    public async Task R10_62_AShortHostIsCaughtByItsFullForm()
    {
        const string raiser = "agent://c.io/a/b";
        var ct = TestContext.Current.CancellationToken;
        var world = await WorldWithPostAsync(ct);
        await FlagAsync(world, FlagKind.Spam, raiser, "Advertising, not a question.", ct);
        var before = (await LogAsync(world, ct)).Count;

        var full = await world.Moderate.RecordAsync(
            Post, ModerationEffect.Withhold, FlagKind.Spam, "Upheld, as " + raiser + " reported.", Operator, ct);

        Assert.False(full.TryGetValue(out _, out var error));
        Assert.Equal("curia/moderation/rationale-discloses-flag", error!.Type);
        Assert.Equal(before, (await LogAsync(world, ct)).Count);

        var schemeless = await world.Moderate.RecordAsync(
            Post, ModerationEffect.Withhold, FlagKind.Spam, "Upheld: see c.io/a/b for the policy.", Operator, ct);

        Assert.True(schemeless.TryGetValue(out _, out var schemelessError), schemelessError?.Type + " " + schemelessError?.Detail);
        Assert.Equal(before + 1, (await LogAsync(world, ct)).Count);
    }

    /// <summary>
    /// A raiser form containing white space is not checked. Enrolment accepts one, and checked, an id
    /// such as "upheld: advertising" would refuse the phrase a moderator is most likely to write.
    /// </summary>
    [Fact]
    public async Task R10_62_ASpacedRaiserCannotRefuseAPhrase()
    {
        var (result, before, after) = await WithholdAfterFlagByAsync(
            "upheld: advertising", "Upheld: advertising.", TestContext.Current.CancellationToken);

        Assert.True(result.TryGetValue(out _, out var error), error?.Type + " " + error?.Detail);
        Assert.Equal(before + 1, after);
    }

    /// <summary>
    /// R10.39 counts records, so a record decided on a view another record has since overtaken is
    /// refused by the store, not appended: the expected version comes from the read the decision was
    /// made on. Two operators withholding the same flag at once leave one record, not two.
    /// </summary>
    [Fact]
    public async Task R10_39_ARecordDecidedOnAViewAnotherRecordOvertookIsRefusedAndNotAppended()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await WorldWithPostAsync(ct);
        await FlagAsync(world, FlagKind.Spam, ct);

        var racing = new ApplyModeration(
            world.Store,
            new InterleavingDetailStore(
                world.Details,
                async () => Require(await world.Moderate.RecordAsync(Post, ModerationEffect.Withhold, FlagKind.Spam, "Reviewed first.", Operator, ct).ConfigureAwait(false))),
            world.Clock);

        var overtaken = await racing.RecordAsync(Post, ModerationEffect.Withhold, FlagKind.Spam, "Reviewed second.", Operator, ct);

        Assert.False(overtaken.TryGetValue(out _, out var error));
        Assert.Equal(DomainErrors.ConcurrencyConflictType, error!.Type);
        Assert.Single(await LogAsync(world, ct), e => e.Event.Type.Value == FlagProjector.ModerationAppliedType);
    }
}
