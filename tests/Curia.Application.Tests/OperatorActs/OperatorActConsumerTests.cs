using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Curia.Application.Credentials;
using Curia.Application.Ingest;
using Curia.Application.Moderation;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Tests.Credentials;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Content;
using Curia.Domain.Credentials;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;
using Curia.Domain.Search;
using Xunit;
using static Curia.Application.Tests.Credentials.KeyActHarness;

namespace Curia.Application.Tests.OperatorActs;

/// <summary>
/// PR N2 (errata G70, R4.43, R6.67), Task 3b: the consumers decide from honoured acts. Every genuine act is built by the signing writers over an in-memory store; every forged row is an unsigned row
/// appended to the log the harness leaves (the shape of every row written before N2). OAS15b, OAS15c, OAS43, OAS44 and OAS45 are the Application-level probes of ruling 73, 74 and 75.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class OperatorActConsumerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Alice = "https://agents.example/alice";
    private const string Bob = "https://agents.example/bob";
    private const string PostOne = "01JP0ST0000000000000000011";
    private static readonly string PostDigest = "sha256:" + new string('a', 64);
    private static readonly ActorId Ops = Require(ActorId.Create("operator:ops"));

    private static T Require<T>(Result<T> result) => result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    private static async Task<IReadOnlyList<AppendedEvent>> LogOfAsync(KeyActHarness harness) =>
        Require(await harness.Events.ReadAllAsync(Ct).ConfigureAwait(false));

    private static string Canonical(string author, string board, string title)
    {
        var members = ImmutableArray.CreateBuilder<KeyValuePair<string, JsonValue>>();
        members.Add(new("v", new JsonValue.Number(PostEnvelope.CurrentVersion)));
        members.Add(new("kind", new JsonValue.String(PostKinds.Wire(PostKind.Question))));
        members.Add(new("author", new JsonValue.String(author)));
        members.Add(new("board", new JsonValue.String(board)));
        members.Add(new("title", new JsonValue.String(title)));
        members.Add(new("body", new JsonValue.String("How does it order object members?")));
        members.Add(new("code_blocks", new JsonValue.Array([])));
        members.Add(new("refs", new JsonValue.Array([])));
        members.Add(new("tags", new JsonValue.Array([])));
        members.Add(new("content_type", new JsonValue.String(PostEnvelope.RequiredContentType)));
        members.Add(new("created_at", new JsonValue.String(Start.ToString("o", CultureInfo.InvariantCulture))));
        members.Add(new("nonce", new JsonValue.String("00112233445566778899aabbccddeeff")));
        return Encoding.UTF8.GetString(Require(CanonicalJson.CanonicalizeWithNfc(new JsonValue.Object(members.ToImmutable()))).Span);
    }

    /// <summary>A question a reader can see: its root is the shape ingest persists, with a canonical envelope the searchable read model parses.</summary>
    private static async Task AcceptPostAsync(KeyActHarness harness, string postId, string author)
    {
        Require(await harness.Events.AppendAsync(
            Require(AggregateId.Create(postId)),
            AggregateVersion.New,
            [new DomainEvent(
                Require(EventId.Create(postId)),
                Require(EventType.Create(PostProjector.PostAcceptedType)),
                Require(ActorId.Create(author)),
                new JsonValue.Object(
                [
                    new("post_id", new JsonValue.String(postId)),
                    new("canonical", new JsonValue.String(Canonical(author, "board-1", "Member ordering " + postId))),
                    new("signature", new JsonValue.String("sig")),
                    new("digest", new JsonValue.String(PostDigest)),
                    new("author", new JsonValue.String(author)),
                    new("board", new JsonValue.String("board-1")),
                    new("kind", new JsonValue.String("question")),
                ]))],
            Ct).ConfigureAwait(false));
    }

    private static async Task<Result<ModerationRecorded>> ModerateAsync(KeyActHarness harness, string postId, ModerationEffect effect) =>
        await SignedActWriters.Moderate(harness.Events, new InMemoryFlagDetailStore(), harness.Clock)
            .RecordAsync(PostId.Parse(postId), effect, FlagKind.Spam, Rationale.Parse("reviewed for a test"), Ops, Ct).ConfigureAwait(false);

    private static async Task<Result<OwnerAttestation>> AttestAsync(KeyActHarness harness, string agent, string owner) =>
        await SignedActWriters.Attest(harness.Events, harness.Clock)
            .RecordAsync(AgentId.Parse(agent), Require(OwnerId.Create(owner)), true, OwnerVerificationMethod.Manual, "attested for a test", Ops, Ct).ConfigureAwait(false);

    /// <summary>A harness holding two enrolled agents and one question by the first: the log every probe starts from.</summary>
    private static async Task<KeyActHarness> HarnessAsync()
    {
        var harness = new KeyActHarness();
        await harness.EnrollAsync(Alice, TestIdentity.New("k-a")).ConfigureAwait(false);
        await harness.EnrollAsync(Bob, TestIdentity.New("k-b")).ConfigureAwait(false);
        await AcceptPostAsync(harness, PostOne, Alice).ConfigureAwait(false);
        return harness;
    }

    private static JsonValue.Object Obj(params (string Name, JsonValue Value)[] members) =>
        new([.. members.Select(m => new KeyValuePair<string, JsonValue>(m.Name, m.Value))]);

    private static JsonValue.String S(string text) => new(text);

    /// <summary>A moderation row as every row before N2 was written: every member R10.60 lists on the row, and no statement.</summary>
    private static JsonValue.Object ForgedModerationPayload(string post, string effect) =>
        Obj(
            ("actor_id", S("operator:ops")), ("adjudicates", new JsonValue.Array([])), ("category", S("spam")), ("digest", S(PostDigest)),
            ("effect", S(effect)), ("moderator", S("human")), ("post_id", S(post)), ("rationale", S("forged by a probe")));

    private static JsonValue.Object ForgedAttestationPayload(string agent, string owner) =>
        Obj(("agent_id", S(agent)), ("method", S("manual")), ("owner_id", S(owner)), ("owner_verified", new JsonValue.Bool(true)), ("reason", S("forged by a probe")));

    private static AppendedEvent Row(IReadOnlyList<AppendedEvent> log, string aggregate, string type, JsonValue payload) =>
        LogEvents.At(log.Count, aggregate, new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero), $"forged-{log.Count}", type, "operator:ops", payload);

    private static List<AppendedEvent> Plus(IReadOnlyList<AppendedEvent> log, params (string Aggregate, string Type, JsonValue Payload)[] rows)
    {
        var grown = new List<AppendedEvent>(log);
        foreach (var (aggregate, type, payload) in rows) grown.Add(Row(grown, aggregate, type, payload));
        return grown;
    }

    private static string Show(ImmutableArray<SearchablePost> corpus) =>
        string.Join("; ", corpus.Select(p => $"{p.PostId} by {p.Author} owner {p.Owner ?? "none"}"));

    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    // OAS15b, OAS15c: the guard's delta (ruling 73).
    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// OAS15b, the control of OAS15's forged leg: a genuine, honoured withhold in the delta makes the post not servable. The read is the log up to the post's root; the delta is the one signed row the
    /// writer appended. The control: an empty delta says servable.
    /// </summary>
    [Fact]
    public async Task OAS15b_AGenuineWithholdInTheDeltaMakesThePostUnservable()
    {
        var harness = await HarnessAsync();
        await harness.PublishOperatorKeyAsync(SignedActWriters.Key, Ct);
        var read = await LogOfAsync(harness);
        Require(await ModerateAsync(harness, PostOne, ModerationEffect.Withhold));
        var whole = await LogOfAsync(harness);
        var delta = whole.Skip(read.Count).ToArray();

        var facts = LogFacts.OfHonoured(SignedActWriters.Verifier.OfLog(read));

        Assert.Single(delta);
        Assert.True(facts.ServableAfter([])(PostOne), "the control, a delta with no act, must say servable");
        Assert.False(facts.ServableAfter(delta)(PostOne), "Expected: not servable  Actual: servable");
    }

    /// <summary>
    /// OAS15c, the replacement of the undecidable-delta leg (ruling 73): a delta row of type <c>moderation.applied</c> whose payload is not an object, and one whose <c>statement</c> is not a string, each
    /// leave the post servable (the fold is total: the row is skipped and a skipped act is inert). The control, a genuine withhold in the delta, is OAS15b's.
    /// </summary>
    [Fact]
    public async Task OAS15c_ADeltaRowTheFoldSkipsLeavesThePostServable()
    {
        var harness = await HarnessAsync();
        await harness.PublishOperatorKeyAsync(SignedActWriters.Key, Ct);
        var read = await LogOfAsync(harness);
        var facts = LogFacts.OfHonoured(SignedActWriters.Verifier.OfLog(read));
        Assert.True(facts.ServableAfter([])(PostOne));

        foreach (var (leg, payload) in new (string, JsonValue)[]
        {
            ("a payload that is not an object", S("?")),
            ("a statement that is not a string", Obj(("post_id", S(PostOne)), ("statement", new JsonValue.Number(5)))),
        })
        {
            var delta = new[] { Row(read, PostOne, FlagProjector.ModerationAppliedType, payload) };
            Assert.True(facts.ServableAfter(delta)(PostOne), $"Expected: servable  Actual: not servable ({leg})");
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    // OAS43: the searchable corpus and its owners.
    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// OAS43: <c>SearchProjector.FoldHonoured</c> over a log with two agents each carrying a forged attestation naming one owner and a forged withhold of a served post: the corpus holds the post and no post
    /// carries an owner, which is the corpus of the same log with no forged row. The control, genuine attestations, sets the owner; a genuine withhold takes the post out.
    /// </summary>
    [Fact]
    public async Task OAS43_TheSearchCorpusIsFoldedFromHonouredActsAlone()
    {
        var harness = await HarnessAsync();
        await harness.PublishOperatorKeyAsync(SignedActWriters.Key, Ct);
        var clean = await LogOfAsync(harness);
        var forged = Plus(
            clean,
            (Alice, AgentStandingProjector.OwnerAttestedType, ForgedAttestationPayload(Alice, "owner:forged")),
            (Bob, AgentStandingProjector.OwnerAttestedType, ForgedAttestationPayload(Bob, "owner:forged")),
            (PostOne, FlagProjector.ModerationAppliedType, ForgedModerationPayload(PostOne, "withhold")));

        var verifier = SignedActWriters.Verifier;
        var noForgedRow = Show(SearchProjector.FoldHonoured(verifier.OfLog(clean)));
        var withForgedRows = Show(SearchProjector.FoldHonoured(verifier.OfLog(forged)));

        Assert.Equal($"{PostOne} by {Alice} owner none", noForgedRow);
        Assert.Equal(noForgedRow, withForgedRows);

        // The control: the genuine path moves the same output.
        Require(await AttestAsync(harness, Alice, "owner:alice"));
        Assert.Equal($"{PostOne} by {Alice} owner owner:alice", Show(SearchProjector.FoldHonoured(verifier.OfLog(await LogOfAsync(harness)))));
        Require(await ModerateAsync(harness, PostOne, ModerationEffect.Withhold));
        Assert.Equal(string.Empty, Show(SearchProjector.FoldHonoured(verifier.OfLog(await LogOfAsync(harness)))));
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    // OAS44: the position of the facts.
    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// OAS44: <c>LogFacts.OfHonoured</c> over a log whose last row is a skipped act has the whole log's last sequence as its position (a position taken from the honoured events would sit below the skipped row,
    /// and the guard would read that row again as a delta), and <c>ServableAfter</c> with an empty delta equals the facts' own answer. The control: the same log with a genuine last act.
    /// </summary>
    [Fact]
    public async Task OAS44_ThePositionIsTheWholeLogsAndNotTheHonouredEvents()
    {
        var harness = await HarnessAsync();
        await harness.PublishOperatorKeyAsync(SignedActWriters.Key, Ct);
        var clean = await LogOfAsync(harness);
        var skippedLast = Plus(clean, (PostOne, FlagProjector.ModerationAppliedType, ForgedModerationPayload(PostOne, "withhold")));
        var verifier = SignedActWriters.Verifier;

        var facts = LogFacts.OfHonoured(verifier.OfLog(skippedLast));
        Assert.Equal(skippedLast[^1].Seq, facts.Position);
        Assert.Equal(facts.Servable(PostOne), facts.ServableAfter([])(PostOne));
        Assert.True(facts.Servable(PostOne));

        // The control: a genuine last act.
        Require(await ModerateAsync(harness, PostOne, ModerationEffect.Withhold));
        var genuineLast = await LogOfAsync(harness);
        var genuine = LogFacts.OfHonoured(verifier.OfLog(genuineLast));
        Assert.Equal(genuineLast[^1].Seq, genuine.Position);
        Assert.False(genuine.Servable(PostOne));
        Assert.False(genuine.ServableAfter([])(PostOne));
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    // OAS45: the statement rule (ruling 74), at the projectors.
    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------

    private static string CanonicalText(JsonValue.Object statement) =>
        Encoding.UTF8.GetString(Require(CanonicalJson.CanonicalizeWithNfc(statement)).Span);

    /// <summary>
    /// OAS45: an act row whose <c>statement</c> states members its own members differ from is read from the statement (the statement is the act); a row with no <c>statement</c> is read from its members (the
    /// fallback, pinned so that its removal is a decision). The feed is unfiltered and built by hand: this is the projector-level half of vectors 73 and 74.
    /// </summary>
    [Theory]
    [InlineData("attestation")]
    [InlineData("moderation")]
    public void OAS45_AnActRowIsReadFromItsStatementAndOnlyWithoutOneFromItsMembers(string kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        if (kind == "attestation")
        {
            var statement = Obj(
                ("actor_id", S("operator:ops")), ("agent_id", S(Alice)), ("method", S("manual")), ("owner_id", S("owner:statement")), ("owner_verified", new JsonValue.Bool(true)),
                ("prev", new JsonValue.Number(0)), ("reason", S("statement")), ("typ", S("curia.owner-attestation/1")));
            var disagreeing = ForgedAttestationPayload(Alice, "owner:row").Members.Where(m => m.Key != "owner_verified").Append(new("owner_verified", new JsonValue.Bool(false)))
                .Append(new("statement", S(CanonicalText(statement)))).Append(new("authorization", S("not checked here"))).ToImmutableArray();

            var fromStatement = AgentStandingProjector.Fold([LogEvents.At(0, Alice, Start, "att-1", AgentStandingProjector.OwnerAttestedType, "operator:ops", new JsonValue.Object(disagreeing))])[Alice];
            Assert.Equal("owner:statement", fromStatement.OwnerId);
            Assert.True(fromStatement.OwnerVerified);

            var fromRow = AgentStandingProjector.Fold([LogEvents.At(0, Alice, Start, "att-2", AgentStandingProjector.OwnerAttestedType, "operator:ops", ForgedAttestationPayload(Alice, "owner:row"))])[Alice];
            Assert.Equal("owner:row", fromRow.OwnerId);
            return;
        }

        var stated = Obj(
            ("actor_id", S("operator:ops")), ("adjudicates", new JsonValue.Array([])), ("category", S("spam")), ("digest", S(PostDigest)), ("effect", S("withhold")),
            ("moderator", S("human")), ("post_id", S(PostOne)), ("prev", new JsonValue.Number(0)), ("rationale", S("statement")), ("typ", S("curia.moderation/1")));
        var rowSaysRestore = ForgedModerationPayload(PostOne, "restore").Members.Append(new("statement", S(CanonicalText(stated)))).Append(new("authorization", S("not checked here"))).ToImmutableArray();

        var read = FlagProjector.Fold([LogEvents.At(0, PostOne, Start, "mod-1", FlagProjector.ModerationAppliedType, "operator:ops", new JsonValue.Object(rowSaysRestore))]);
        Assert.False(read[PostOne].MayServe, "Expected: withheld (the statement says withhold)  Actual: served (the row says restore)");

        var fallback = FlagProjector.Fold([LogEvents.At(0, PostOne, Start, "mod-2", FlagProjector.ModerationAppliedType, "operator:ops", ForgedModerationPayload(PostOne, "withhold"))]);
        Assert.False(fallback[PostOne].MayServe, "a row with no statement is read from its members");
    }
}
