using System.Diagnostics.CodeAnalysis;
using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Tests.Acta;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Envelope;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Acta;
using Curia.Domain.Keys;
using Curia.Domain.Primitives;
using Xunit;
using static Curia.Application.Tests.Credentials.KeyActHarness;

namespace Curia.Application.Tests.Credentials;

/// <summary>
/// PR H (errata G67, R6.62): the declaration key at the domain and the use cases. The Acta folds the key's entry (HSU2); <c>DeclareCompromise</c> signs only under a key
/// the log has published as a declaration key and never under a log key (HSU3); the statement is <c>curia.key-compromise/2</c> and no longer <c>/1</c> (HSU4); and the guard
/// classifies the three lifecycle kinds as before and not the declaration key's entry (HSU5).
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class DeclarationKeyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ActorId Operator = Require(ActorId.Create("operator:ops"));

    /// <summary>Every part compared and every part shown, so that one red names all it differs in and not the first only.</summary>
    private static void Same(string[] want, string[] got) =>
        Assert.True(want.SequenceEqual(got, StringComparer.Ordinal), $"expected [{string.Join(" | ", want)}] but got [{string.Join(" | ", got)}]");

    private static string Describe<T>(Result<T> result) => result.Match(_ => "ok", e => e.Type);

    /// <summary>
    /// HSU2. The fold reads a published declaration key with its log index and keeps it out of <c>Keys</c>; an entry without its JWK fails the fold with the
    /// type and the log index. Red on the seam: <c>Assert.Single() Failure: The collection was empty</c>.
    /// </summary>
    [Fact]
    public void R6_62_HSU2_TheFoldReadsADeclarationKeyAndRefusesAMalformedOne()
    {
        var acta = Require(ActaLog.Fold(LogBuilder.LogOf(LogBuilder.Post(), LogBuilder.LogKey(), LogBuilder.DeclarationKey("decl-1"), LogBuilder.Head(3))));

        var declaration = Assert.Single(acta.DeclarationKeys);
        Assert.Equal("2 decl-1 1", $"{declaration.LogIndex} {declaration.Kid} {acta.Keys.Length}");

        var malformed = new LogBuilder.Entry(
            LogEntries.DeclarationKeysAggregate,
            LogEntries.DeclarationKeyType,
            new JsonValue.Object([new(LogEntries.KidMember, new JsonValue.String("decl-1")), new(LogEntries.ValidFromMember, new JsonValue.String("2026-11-11T09:00:00.000000Z"))]));
        var refused = ActaLog.Fold(LogBuilder.LogOf(LogBuilder.Post(), malformed));

        Assert.False(refused.IsOk, "a log.declaration-key with no jwk folded");
        Assert.Equal("curia/acta/malformed-declaration-key log_index=1", refused.Match(_ => string.Empty, e => $"{e.Type} {e.Detail}"));
    }

    private static async Task PublishAsync(KeyActHarness harness, string type, string aggregate, InMemoryLifecycleStatementSigner key, string eventId)
    {
        var log = Require(await harness.Events.ReadAllAsync(Ct).ConfigureAwait(false));
        var version = log.Count(e => string.Equals(e.AggregateId.Value, aggregate, StringComparison.Ordinal));
        var payload = new JsonValue.Object(
        [
            new(LogEntries.KidMember, new JsonValue.String(key.Kid)),
            new(LogEntries.JwkMember, key.PublicJwk()),
            new(LogEntries.ValidFromMember, new JsonValue.String(LogLeaf.RenderServerTimestamp(ServerTimestamp.At(harness.Clock.GetUtcNow())))),
        ]);
        Require(await harness.Events.AppendAsync(
            Require(AggregateId.Create(aggregate)),
            Require(AggregateVersion.From(version)),
            [new DomainEvent(Require(EventId.Create(eventId)), Require(EventType.Create(type)), Operator, payload)],
            Ct).ConfigureAwait(false));
    }

    private static Task<Result<CompromiseRecorded>> DeclareAsync(KeyActHarness harness, InMemoryLifecycleStatementSigner signer) =>
        new DeclareCompromise(harness.Events, harness.Clock)
            .DeclareAsync(Typed.Agent(Alice), Typed.Kid("alice-1"), 1, CompromiseReason.OwnerReported, signer, Operator, Ct);

    /// <summary>
    /// HSU3. The signer's key must be a published declaration key and never a log key: an unpublished key and a key that is both published as a log key and as a declaration
    /// key are refused by name; the right key signs under its own kid and under <c>curia.key-compromise/2</c>. Red on the seam, where the log key still signs and nothing
    /// names a declaration key.
    /// </summary>
    [Fact]
    public async Task R6_62_HSU3_ADeclarationIsSignedOnlyUnderAPublishedDeclarationKey()
    {
        // (a) nothing published as a declaration key.
        var unpublished = new KeyActHarness();
        await unpublished.EnrollAsync(Alice, TestIdentity.New("alice-1"));
        using var strangerKey = new InMemoryLifecycleStatementSigner("decl-key");
        var a = Describe(await DeclareAsync(unpublished, strangerKey));

        // (b) one kid published both as a log key and as a declaration key.
        var shared = new KeyActHarness();
        await shared.EnrollAsync(Alice, TestIdentity.New("alice-1"));
        using var sharedKey = new InMemoryLifecycleStatementSigner("shared-key");
        await PublishAsync(shared, LogEntries.KeyType, LogEntries.KeysAggregate, sharedKey, "01K4CQ1V9P3R5T7W9Y1A3C5EK1");
        await PublishAsync(shared, LogEntries.DeclarationKeyType, LogEntries.DeclarationKeysAggregate, sharedKey, "01K4CQ1V9P3R5T7W9Y1A3C5EK2");
        var b = Describe(await DeclareAsync(shared, sharedKey));

        // (c) the right key: published as a declaration key and as no log key.
        var right = new KeyActHarness();
        await right.EnrollAsync(Alice, TestIdentity.New("alice-1"));
        using var declarationKey = new InMemoryLifecycleStatementSigner("decl-key");
        await PublishAsync(right, LogEntries.DeclarationKeyType, LogEntries.DeclarationKeysAggregate, declarationKey, "01K4CQ1V9P3R5T7W9Y1A3C5EK3");
        var declared = await DeclareAsync(right, declarationKey);
        var c = Describe(declared);
        var seen = "no entry";
        if (declared.IsOk)
        {
            var entry = (await right.StreamAsync(Alice))[^1];
            var payload = (JsonValue.Object)entry.Event.Payload;
            var statement = ((JsonValue.String)payload.Members.Single(m => m.Key == "statement").Value).Value;
            var signature = new JwsSignature(((JsonValue.String)payload.Members.Single(m => m.Key == "declaration").Value).Value);
            var headerKid = KeyStatements.CheckHeader(signature, "declaration", KeyStatements.CompromiseHeaderTyp, "decl-key").IsOk;
            seen = $"header kid is the declaration key's: {headerKid}; typ /2: {statement.Contains("\"typ\":\"curia.key-compromise/2\"", StringComparison.Ordinal)}";
        }

        Same(
            [KeyActErrors.DeclarationKeyNotPublishedType, KeyActErrors.DeclarationKeyIsLogKeyType, "ok", "header kid is the declaration key's: True; typ /2: True"],
            [a, b, c, seen]);
    }

    private static string Canonical(string typ, params (string Name, JsonValue Value)[] extra)
    {
        var members = new List<KeyValuePair<string, JsonValue>>
        {
            new("agent_id", new JsonValue.String(Alice)),
            new("from_index", new JsonValue.Number(1)),
            new("kids", new JsonValue.Array([new JsonValue.String("alice-1")])),
            new("prev", new JsonValue.Number(1)),
            new("reason", new JsonValue.String("owner-reported")),
            new("typ", new JsonValue.String(typ)),
        };
        members.AddRange(extra.Select(e => new KeyValuePair<string, JsonValue>(e.Name, e.Value)));
        return KeyActHarness.Canonical(new JsonValue.Object([.. members]));
    }

    private static string Read(string statement) =>
        KeyStatements.ReadCompromise(System.Text.Encoding.UTF8.GetBytes(statement)).Match(_ => "ok", e => $"{e.Type} {e.Detail}");

    /// <summary>
    /// HSU4. The six members read under <c>/2</c>; the same bytes under <c>/1</c> are an unreadable <c>typ</c> (KF4: a version this build does not know); a <c>/2</c> with a
    /// seventh member is a mismatch. Red on the seam: <c>/2</c> is <c>statement-unreadable field=typ</c>.
    /// </summary>
    [Fact]
    public void R6_62_HSU4_TheCompromiseStatementIsVersionTwoAndNotVersionOne()
    {
        Same(
            ["ok", "curia/keys/statement-unreadable field=typ", "curia/keys/statement-mismatch field=members"],
            [
                Read(Canonical("curia.key-compromise/2")),
                Read(Canonical("curia.key-compromise/1")),
                Read(Canonical("curia.key-compromise/2", ("extra", new JsonValue.Number(1)))),
            ]);
    }

    private static string Kinds(AggregateVersion version, params DomainEvent[] events) =>
        string.Join(",", LifecycleAppendGuard.Classify(version, events).Select(k => k.ToString()).DefaultIfEmpty("none"));

    private static DomainEvent EventOf(string type) => new(
        Require(EventId.Create("evt-" + type)), Require(EventType.Create(type)), Operator, new JsonValue.Object([]));

    /// <summary>
    /// HSU5 (Application half; the adapters' half is <c>LifecycleRefusalContract</c>). The guard does not govern the entry and governs the three kinds as before
    /// (Amendment 2 ruling 29). A control on the seam, which classifies nothing new; red under FHSU5, where <c>Classify</c> is made to return a kind for the entry.
    /// </summary>
    [Fact]
    public void R4_38_HSU5_TheGuardClassifiesTheThreeKindsAndNotTheDeclarationKeysEntry()
    {
        Assert.Equal(
            "none; Compromise; Retirement; Rotation",
            string.Join(
                "; ",
                Kinds(AggregateVersion.New, EventOf(LogEntries.DeclarationKeyType)),
                Kinds(AggregateVersion.New, EventOf(LifecycleAppendGuard.KeyCompromisedType)),
                Kinds(AggregateVersion.New, EventOf(LifecycleAppendGuard.KeyRetiredType)),
                Kinds(Require(AggregateVersion.From(1)), EventOf(AgentStandingProjector.KeyBoundType))));
    }
}
