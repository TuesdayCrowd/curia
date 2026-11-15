using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Application.Credentials;
using Curia.Application.Moderation;
using Curia.Application.Projections;
using Curia.Application.Ports;
using Curia.Application.Tests.Credentials;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Acta;
using Curia.Domain.Credentials;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;
using Curia.Application.Tests.Ingest;
using Xunit;
using static Curia.Application.Tests.Credentials.KeyActHarness;

namespace Curia.Application.Tests.OperatorActs;

/// <summary>
/// PR N2 (errata G70), Task 4: the writers' probes. OAS19d (register item 300): a publisher's expected version is the count of its aggregate's events in the log it read, and not the length of a
/// projection, which counts only the keys that hold a position (R6.66). The expectations are written from R6.66's rule (c) and from the aggregate's own count, never from a run.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class OperatorActWriterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ActorId OperatorName = Require(ActorId.Create("operator:ops"));

    private static async Task<int> CountAsync(KeyActHarness harness) =>
        Require(await harness.Events.ReadAllAsync(Ct).ConfigureAwait(false)).Count;

    private static async Task AppendKeyRowAsync(KeyActHarness harness, string type, string aggregate, string kid, JsonValue.Object jwk)
    {
        var count = await CountAsync(harness).ConfigureAwait(false);
        var version = Require(await harness.Events.ReadAllAsync(Ct).ConfigureAwait(false)).Count(e => string.Equals(e.AggregateId.Value, aggregate, StringComparison.Ordinal));
        var payload = new JsonValue.Object(
        [
            new("kid", new JsonValue.String(kid)),
            new("jwk", jwk),
            new("valid_from", new JsonValue.String(LogLeaf.RenderServerTimestamp(ServerTimestamp.At(harness.Clock.GetUtcNow())))),
        ]);
        Require(await harness.Events.AppendAsync(
            Require(AggregateId.Create(aggregate)),
            Require(AggregateVersion.From(version)),
            [new DomainEvent(Require(EventId.Create($"01K4CQ1V9P3R5T7W9Y1A3C5F{count:D2}")), Require(EventType.Create(type)), OperatorName, payload)],
            Ct).ConfigureAwait(false));
    }

    /// <summary>
    /// OAS19d: the first declaration key is published whatever rows of its aggregate hold no position. Legs: a <c>log.declaration-key</c> row that republishes the operator key's public key under
    /// another <c>kid</c>, and one that carries another key under the operator key's <c>kid</c> (each holds no position by R6.66 rule (c)); and the control, which has no such row. Each leg
    /// states the outcome and the index the key took, which is the log's length at the call.
    /// </summary>
    [Theory]
    [InlineData("the operator key's public key under another kid")]
    [InlineData("another key under the operator key's kid")]
    [InlineData("no such row (control)")]
    public async Task OAS19d_ThePublisherOfTheFirstDeclarationKeyCountsItsAggregatesEventsNotItsKeys(string leg)
    {
        ArgumentNullException.ThrowIfNull(leg);
        var harness = new KeyActHarness();
        using var operatorKey = new InMemoryOperatorActSigner("operator-1");
        using var declarationKey = new InMemoryLifecycleStatementSigner("declaration-1");
        Require(await new PublishOperatorKey(harness.Events, harness.Clock).PublishAsync(operatorKey, OperatorName, Ct));

        // The legs whose point is that the keys are equal say so: the republishing row carries the operator key's own public key.
        using var sameKeyOtherKid = operatorKey.WithKid("operator-1-as-declaration");
        using var otherMaterial = new InMemoryOperatorActSigner("operator-1");
        if (leg.StartsWith("the operator key's public key", StringComparison.Ordinal))
            await AppendKeyRowAsync(harness, LogEntries.DeclarationKeyType, LogEntries.DeclarationKeysAggregate, "operator-1-as-declaration", sameKeyOtherKid.PublicJwk());
        else if (leg.StartsWith("another key", StringComparison.Ordinal))
            await AppendKeyRowAsync(harness, LogEntries.DeclarationKeyType, LogEntries.DeclarationKeysAggregate, "operator-1", otherMaterial.PublicJwk());

        Assert.NotEqual(operatorKey.PublicJwk().Members.Single(m => m.Key == "x").Value, declarationKey.PublicJwk().Members.Single(m => m.Key == "x").Value);

        var length = await CountAsync(harness);
        var result = await new PublishDeclarationKey(harness.Events, harness.Clock).PublishAsync(declarationKey, OperatorName, Ct);
        var grown = await CountAsync(harness) - length;

        Assert.Equal(
            $"ok (index {length}, appended 1)",
            result.Match(index => $"ok (index {index}, appended {grown})", error => $"{error.Type} (appended {grown})"));
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    // Task 4 (re-cut): the writers sign. OAS13, OAS13b, OAS13c, OAS13d, OAS24 and the ActErrors shape.
    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------

    private const string Alice = "agent://curia.example/tuesdaycrowd/alice";
    private const string Bob = "agent://curia.example/tuesdaycrowd/bob";
    private const string PostOne = "01JP0ST0000000000000000011";
    private const string PostTwo = "01JP0ST0000000000000000012";
    private static readonly string Digest = "sha256:" + new string('a', 64);

    private static ActorId Operator(string name) => Require(ActorId.Create("operator:" + name));

    private static async Task EnrollAsync(KeyActHarness harness, string agent) =>
        await harness.EnrollAsync(agent, TestIdentity.New("k-" + Guid.NewGuid().ToString("N")[..8])).ConfigureAwait(false);

    /// <summary>A post's root event, shaped as ingest persists one (the members PostProjector reads); its stream is the post's id.</summary>
    private static async Task AcceptPostAsync(KeyActHarness harness, string postId) =>
        Require(await harness.Events.AppendAsync(
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
                    new("digest", new JsonValue.String(Digest)),
                    new("author", new JsonValue.String("https://agents.example/author")),
                    new("board", new JsonValue.String("board-1")),
                    new("kind", new JsonValue.String("question")),
                ]))],
            Ct).ConfigureAwait(false));

    private static async Task<Result<ModerationRecorded>> ModerateAsync(
        KeyActHarness harness, string postId, ModerationEffect effect, FlagKind category, IOperatorActSigner signer, IEventStore? store = null) =>
        await new ApplyModeration(store ?? harness.Events, new InMemoryFlagDetailStore(), harness.OperatorActVerifier(), harness.Clock)
            .RecordAsync(PostId.Parse(postId), effect, category, Rationale.Parse("reviewed for a test"), Operator("reviewer"), signer, Ct).ConfigureAwait(false);

    private static async Task<IReadOnlyList<AppendedEvent>> LogOfAsync(KeyActHarness harness) =>
        Require(await harness.Events.ReadAllAsync(Ct).ConfigureAwait(false));

    private static string ActType(string kind) => kind == "attestation" ? AgentStandingProjector.OwnerAttestedType : FlagProjector.ModerationAppliedType;

    private static JsonValue.Object StatementOf(AppendedEvent row)
    {
        var statement = ((JsonValue.Object)row.Event.Payload).Members.Single(m => m.Key == "statement").Value;
        return (JsonValue.Object)Require(JsonReader.Parse(System.Text.Encoding.UTF8.GetBytes(((JsonValue.String)statement).Value), AdmitLimits.Default));
    }

    private static long PrevOf(AppendedEvent row) => (long)((JsonValue.Number)StatementOf(row).Members.Single(m => m.Key == "prev").Value).Value;

    /// <summary>
    /// OAS13 (the guard that fails open): the writers' own rows are honoured by the fold. Run in sequence over several agents and posts (an attestation, a lapse, a second agent's attestation, a withhold,
    /// a restore, a withhold of a second post), the log the writers leave has no operator-act row the Forum skips, and the facts follow: the owner, the verified flag and the served state. A fold that
    /// skipped a genuine row would strip a verified owner silently, and a writer that disagreed with it would have written rows that do nothing.
    /// </summary>
    [Fact]
    public async Task OAS13_EveryRowTheWritersLeaveIsHonouredAndTheFactsFollow()
    {
        var harness = new KeyActHarness();
        using var signer = new InMemoryOperatorActSigner("operator-1");
        await harness.PublishOperatorKeyAsync(signer, Ct);
        await EnrollAsync(harness, Alice);
        await EnrollAsync(harness, Bob);
        await AcceptPostAsync(harness, PostOne);
        await AcceptPostAsync(harness, PostTwo);

        Require(await harness.AttestOwnerAsync(Alice, "owner:alice", true, signer, Ct));
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        Require(await harness.AttestOwnerAsync(Alice, "owner:alice", false, signer, Ct));
        Require(await harness.AttestOwnerAsync(Bob, "owner:bob", true, signer, Ct));
        Require(await ModerateAsync(harness, PostOne, ModerationEffect.Withhold, FlagKind.Spam, signer));
        Require(await ModerateAsync(harness, PostOne, ModerationEffect.Restore, FlagKind.Spam, signer));
        Require(await ModerateAsync(harness, PostTwo, ModerationEffect.Withhold, FlagKind.Spam, signer));

        var verified = harness.OperatorActVerifier().OfLog(await LogOfAsync(harness));
        var rows = verified.Log.Count(e => e.Event.Type.Value is AgentStandingProjector.OwnerAttestedType or FlagProjector.ModerationAppliedType);

        Assert.Equal(6, rows);
        Assert.Empty(verified.ActSkipped());
        Assert.Equal("owner:alice", verified.Standings()[Alice].OwnerId);
        Assert.False(verified.Standings()[Alice].OwnerVerified);
        Assert.True(verified.Standings()[Bob].OwnerVerified);
        Assert.True(verified.Moderation()[PostOne].MayServe);
        Assert.False(verified.Moderation()[PostTwo].MayServe);
    }

    private static string ErrataParagraph(string startsWith)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curia.sln"))) dir = dir.Parent;
        var lines = File.ReadAllLines(Path.Combine(dir!.FullName, "curia-whitepaper-ERRATA-AND-ADDENDUM.md"));
        var from = Array.FindIndex(lines, l => l.StartsWith(startsWith, StringComparison.Ordinal));
        var to = Array.FindIndex(lines, from, l => l.StartsWith("A row that no step skips is honoured.", StringComparison.Ordinal));
        Assert.True(from >= 0 && to > from, "R6.67's paragraph was not found in the errata");

        // R6.67 is a lead paragraph, seven numbered steps and a closing paragraph.
        return string.Join('\n', lines[from..(to + 1)]);
    }

    private static string[] Names(string list) => [.. Regex.Matches(list, "`([a-z_]+)`").Select(m => m.Groups[1].Value)];

    /// <summary>
    /// OAS13b (the writer is not the fold's twin): the members of the statement each writer emits are the closed form R6.67's paragraph states, read from the errata and not from a constant, with the
    /// <c>typ</c> and the <c>moderator</c> it states; and every <c>method</c>, <c>effect</c> and <c>category</c> a writer emits is in the value set step 3 states. A writer that adds, drops or renames
    /// a member, or emits a value outside the sets, is red here whether or not the fold would have caught it.
    /// </summary>
    [Theory]
    [InlineData("attestation")]
    [InlineData("moderation")]
    public async Task OAS13b_TheStatementAWriterEmitsIsTheClosedFormTheErrataStates(string kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        var paragraph = ErrataParagraph("**R6.67** The Forum SHALL decide each operator-act row");
        var forms = Regex.Match(paragraph, @"The closed forms are, for an attestation, (?<a>[^;]+); for a moderation record, (?<m>[^.]+)\.");
        Assert.True(forms.Success, "R6.67 step 3's closed forms were not found in the errata");
        var closed = Names(forms.Groups[kind == "attestation" ? "a" : "m"].Value).Order(StringComparer.Ordinal).ToArray();
        var typ = Regex.Match(paragraph, kind == "attestation" ? @"`(curia\.owner-attestation/\d+)`" : @"`(curia\.moderation/\d+)`").Groups[1].Value;
        Assert.NotEmpty(typ);
        var methods = Names(Regex.Match(paragraph, @"`method` is one of R4\.24's four proofs \((?<v>[^)]+)\)").Groups["v"].Value);
        var effects = Names(Regex.Match(paragraph, @"`effect` one of (?<v>.+?), and `category`").Groups["v"].Value);
        Assert.Equal(4, methods.Length);
        Assert.Equal(4, effects.Length);
        Assert.Equal(7, Enum.GetValues<FlagKind>().Length); // "category one of R10.35's seven flag types"

        var harness = new KeyActHarness();
        using var signer = new InMemoryOperatorActSigner("operator-1");
        await harness.PublishOperatorKeyAsync(signer, Ct);
        await EnrollAsync(harness, Alice);
        var emitted = new List<AppendedEvent>();
        if (kind == "attestation")
        {
            foreach (var method in Enum.GetValues<OwnerVerificationMethod>())
                Require(await new AttestOwner(harness.Events, harness.OperatorActVerifier(), harness.Clock).RecordAsync(
                    AgentId.Parse(Alice), Require(OwnerId.Create("owner:alice")), true, method, "attested for a test", Operator("ops"), signer, Ct));
        }
        else
        {
            var n = 0;
            foreach (var category in Enum.GetValues<FlagKind>())
            {
                var post = $"01JP0ST00000000000000001{n++:D2}";
                await AcceptPostAsync(harness, post);
                foreach (var effect in new[] { ModerationEffect.Quarantine, ModerationEffect.Restore, ModerationEffect.Withhold })
                    Require(await ModerateAsync(harness, post, effect, category, signer));
            }
        }

        emitted.AddRange((await LogOfAsync(harness)).Where(e => e.Event.Type.Value == ActType(kind)));
        Assert.NotEmpty(emitted);
        foreach (var row in emitted)
        {
            var statement = StatementOf(row);
            Assert.Equal(closed, statement.Members.Select(m => m.Key).Order(StringComparer.Ordinal).ToArray());
            Assert.Equal(typ, ((JsonValue.String)statement.Members.Single(m => m.Key == "typ").Value).Value);
            if (kind == "attestation")
                Assert.Contains(((JsonValue.String)statement.Members.Single(m => m.Key == "method").Value).Value, methods);
            else
            {
                Assert.Equal("human", ((JsonValue.String)statement.Members.Single(m => m.Key == "moderator").Value).Value);
                Assert.Contains(((JsonValue.String)statement.Members.Single(m => m.Key == "effect").Value).Value, effects);
                Assert.Contains(((JsonValue.String)statement.Members.Single(m => m.Key == "category").Value).Value, Enum.GetValues<FlagKind>().Select(FlagKinds.Wire));
            }
        }

        Assert.Empty(harness.OperatorActVerifier().OfLog(await LogOfAsync(harness)).ActSkipped());
    }

    /// <summary>
    /// OAS13c (the writer cannot see its own row skipped, R11.97): a store that corrupts the signature of the row it appends. The writer returns <c>curia/acts/not-honoured</c> naming the row's log
    /// index and <c>act-signature-invalid</c>, the row is in the log, and no success is reported.
    /// </summary>
    [Theory]
    [InlineData("attestation")]
    [InlineData("moderation")]
    public async Task OAS13c_AWriterWhoseRowTheForumSkipsSaysSoNamingTheIndexAndThePredicate(string kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        var harness = new KeyActHarness();
        using var signer = new InMemoryOperatorActSigner("operator-1");
        await harness.PublishOperatorKeyAsync(signer, Ct);
        await EnrollAsync(harness, Alice);
        await AcceptPostAsync(harness, PostOne);
        var expectedIndex = await CountAsync(harness);
        var corrupting = new KeyActHarness.AuthorizationCorruptingStore(harness.Events);

        var outcome = kind == "attestation"
            ? (await harness.AttestOwnerAsync(Alice, "owner:alice", true, signer, Ct, store: corrupting)).Map(_ => 0)
            : (await ModerateAsync(harness, PostOne, ModerationEffect.Withhold, FlagKind.Spam, signer, corrupting)).Map(_ => 0);

        var log = await LogOfAsync(harness);
        var skipped = harness.OperatorActVerifier().OfLog(log).ActSkipped().Select(r => $"{r.LogIndex} {r.Predicate}").ToArray();
        var actual = outcome.Match(_ => "ok", e => $"{e.Type} {e.Detail}") + $"; log {log.Count}; skipped [{string.Join(", ", skipped)}]";

        Assert.Equal(
            $"curia/acts/not-honoured the row was appended at log index {expectedIndex} and the Forum skips it (act-signature-invalid); it acts on nothing; log {expectedIndex + 1}; skipped [{expectedIndex} act-signature-invalid]",
            actual);
    }

    /// <summary>
    /// OAS13d (no row the writer knows will be skipped): a signer whose key the log has not published is refused before anything is signed or appended (<c>curia/keys/operator-key-not-published</c>, the
    /// event count unchanged); the control, the published key, records. The leg "another key is the first" says the same of a log that published a different key.
    /// </summary>
    [Theory]
    [InlineData("attestation")]
    [InlineData("moderation")]
    public async Task OAS13d_AWriterRefusesAKeyTheLogHasNotPublishedAndAppendsNothing(string kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        var harness = new KeyActHarness();
        using var published = new InMemoryOperatorActSigner("operator-1");
        using var unpublished = new InMemoryOperatorActSigner("operator-2");
        Assert.NotEqual(published.PublicJwk().Members.Single(m => m.Key == "x").Value, unpublished.PublicJwk().Members.Single(m => m.Key == "x").Value);
        await EnrollAsync(harness, Alice);
        await AcceptPostAsync(harness, PostOne);

        async Task<Result<int>> ActAsync(IOperatorActSigner signer) => kind == "attestation"
            ? (await harness.AttestOwnerAsync(Alice, "owner:alice", true, signer, Ct).ConfigureAwait(false)).Map(_ => 0)
            : (await ModerateAsync(harness, PostOne, ModerationEffect.Withhold, FlagKind.Spam, signer).ConfigureAwait(false)).Map(_ => 0);

        var before = await CountAsync(harness);
        var noKeyAtAll = await ActAsync(unpublished);
        Assert.Equal(KeyActErrors.OperatorKeyNotPublishedType, Refusal(noKeyAtAll).Type);
        Assert.Equal(before, await CountAsync(harness));

        await harness.PublishOperatorKeyAsync(published, Ct);
        before = await CountAsync(harness);
        var anotherIsFirst = await ActAsync(unpublished);
        Assert.Equal(KeyActErrors.OperatorKeyNotPublishedType, Refusal(anotherIsFirst).Type);
        Assert.Equal(before, await CountAsync(harness));

        Require(await ActAsync(published));
        Assert.Equal(before + 1, await CountAsync(harness));
    }

    /// <summary>
    /// OAS24 (<c>prev</c> survives a moving stream): between the writer's read and its append another row reaches the agent's stream. Leg one: a second operator's genuine attestation; both rows are
    /// honoured, in the order the log gives them, and the loser's <c>prev</c> names the winner's row. Leg two: a forged, unsigned row; the version fails, the retry honours, and the genuine row's
    /// <c>prev</c> is the stream's root, because a skipped row moves nothing a later row is decided against (R6.67).
    /// </summary>
    [Fact]
    public async Task OAS24_PrevSurvivesAMovingStream()
    {
        await MovingStreamAsync("a second operator's genuine row").ConfigureAwait(true);
        await MovingStreamAsync("a forged unsigned row").ConfigureAwait(true);
    }

    private static async Task MovingStreamAsync(string leg)
    {
        var harness = new KeyActHarness();
        using var signer = new InMemoryOperatorActSigner("operator-1");
        await harness.PublishOperatorKeyAsync(signer, Ct).ConfigureAwait(false);
        await EnrollAsync(harness, Alice).ConfigureAwait(false);
        var root = (await harness.StreamAsync(Alice).ConfigureAwait(false))[0];
        var rootIndex = (await LogOfAsync(harness).ConfigureAwait(false)).ToList().FindIndex(e => e.Event.Id == root.Event.Id);
        var forged = leg.Contains("forged", StringComparison.Ordinal);

        var store = new KeyActHarness.InterceptingStore(harness.Events, afterFirstLogRead: async () =>
        {
            if (forged)
            {
                var version = (await harness.StreamAsync(Alice).ConfigureAwait(false)).Count;
                Require(await harness.Events.AppendAsync(
                    Require(AggregateId.Create(Alice)),
                    Require(AggregateVersion.From(version)),
                    [new DomainEvent(
                        Require(EventId.Create("01K4CQ1V9P3R5T7W9Y1A3C5F99")),
                        Require(EventType.Create(AgentStandingProjector.OwnerAttestedType)),
                        Operator("forger"),
                        new JsonValue.Object(
                        [
                            new("agent_id", new JsonValue.String(Alice)),
                            new("owner_id", new JsonValue.String("owner:mallory")),
                            new("owner_verified", new JsonValue.Bool(true)),
                            new("method", new JsonValue.String("manual")),
                            new("reason", new JsonValue.String("forged")),
                        ]))],
                    Ct).ConfigureAwait(false));
            }
            else
            {
                Require(await harness.AttestOwnerAsync(Alice, "owner:alice", true, signer, Ct, by: "operator:second").ConfigureAwait(false));
            }
        });

        Require(await harness.AttestOwnerAsync(Alice, "owner:alice", true, signer, Ct, by: "operator:first", store: store).ConfigureAwait(false));

        var log = await LogOfAsync(harness).ConfigureAwait(false);
        var verified = harness.OperatorActVerifier().OfLog(log);
        var acts = log.Select((e, i) => (e, i)).Where(x => x.e.Event.Type.Value == AgentStandingProjector.OwnerAttestedType && x.e.Event.Id.Value != "01K4CQ1V9P3R5T7W9Y1A3C5F99").ToArray();

        if (forged)
        {
            Assert.Single(acts);
            Assert.Equal(["act-unsigned"], verified.ActSkipped().Select(r => r.Predicate).ToArray());
            Assert.Equal(rootIndex, PrevOf(acts[0].e));
        }
        else
        {
            Assert.Equal(2, acts.Length);
            Assert.Empty(verified.ActSkipped());
            Assert.Equal(rootIndex, PrevOf(acts[0].e));
            Assert.Equal(acts[0].i, PrevOf(acts[1].e));
            Assert.Equal("operator:second", acts[0].e.Event.Actor?.Value);
            Assert.Equal("operator:first", acts[1].e.Event.Actor?.Value);
        }
    }

    /// <summary>The refusal an act's row the Forum skips answers (R11.97): its type is the literal the errata states, and its detail names the index and the predicate and no payload member.</summary>
    [Fact]
    public void ActErrors_NotHonouredHasTheLiteralTypeAndNamesTheIndexAndThePredicateOnly()
    {
        var error = ActErrors.NotHonoured(7, "act-signature-invalid");

        Assert.Equal("curia/acts/not-honoured", error.Type);
        Assert.Contains("7", error.Detail, StringComparison.Ordinal);
        Assert.Contains("act-signature-invalid", error.Detail, StringComparison.Ordinal);
        foreach (var marker in new[] { "owner", "statement", "authorization", "rationale", "reason", "digest" })
            Assert.DoesNotContain(marker, error.Detail, StringComparison.Ordinal);
    }
}
