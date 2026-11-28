using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Reads;
using Curia.Application.Tests.Credentials;
using Curia.Application.Tests.InMemory;
using Curia.Application.Tests.OperatorActs;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Acta;
using Curia.Domain.Content;
using Curia.Domain.Credentials;
using Curia.Domain.Keys;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;
using Curia.Tests.Shared;
using Xunit;
using static Curia.Application.Tests.Credentials.KeyActHarness;

namespace Curia.Application.Tests.Reads;

/// <summary>
/// RC2, RC3 and RC16 (errata G87, R11.113): a view over a read refuses a question about a row it was not read for and never answers "none"; nothing is carried between requests, so a row the log gains is
/// decided on by the very next read with no clock advanced; and the one payload-keyed clause, the signals on an agent's findings, is read for an agent that holds a finding, names no digest for one that
/// holds none, and is decided as the whole-log fold decides it, a caster whose key was declared compromised included (R6.57).
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ScopedPostureTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(KeyActHarness Harness, TestIdentity Alice, TestIdentity Bob, InMemoryOperatorActSigner Operator)> WorldAsync()
    {
        var harness = new KeyActHarness();
        var alice = TestIdentity.New("alice-1");
        var bob = TestIdentity.New("bob-1");
        await harness.EnrollAsync(Alice, alice).ConfigureAwait(false);
        await harness.EnrollAsync(Bob, bob).ConfigureAwait(false);
        var signer = new InMemoryOperatorActSigner("scoped-operator");
        await harness.PublishOperatorKeyAsync(signer, Ct).ConfigureAwait(false);
        Require(await harness.AttestOwnerAsync(Alice, "owner:alice", true, signer, Ct).ConfigureAwait(false));
        Require(await harness.AttestOwnerAsync(Bob, "owner:bob", true, signer, Ct).ConfigureAwait(false));
        return (harness, alice, bob, signer);
    }

    private static string Header(string kid) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($$"""{"alg":"ES256","kid":"{{kid}}","typ":"curia.post+jws"}""")).TrimEnd('=').Replace('+', '-').Replace('/', '_') + "..AAAA";

    private static async Task PostAsync(KeyActHarness harness, string id, string digest, string kind, string author, string? target, string signature = "sig")
    {
        var members = new List<KeyValuePair<string, JsonValue>>
        {
            new("v", new JsonValue.Number(PostEnvelope.CurrentVersion)),
            new("kind", new JsonValue.String(kind)),
            new("author", new JsonValue.String(author)),
            new("board", new JsonValue.String("b")),
            new("code_blocks", new JsonValue.Array([])),
            new("refs", kind == "verification"
                ? new JsonValue.Array([new JsonValue.Object([new("kind", new JsonValue.String("url")), new("target", new JsonValue.String("https://example.test/" + id))])])
                : new JsonValue.Array([])),
            new("tags", new JsonValue.Array([])),
            new("content_type", new JsonValue.String(PostEnvelope.RequiredContentType)),
            new("created_at", new JsonValue.String("2026-11-27T12:00:00Z")),
            new("nonce", new JsonValue.String("n" + id)),
        };
        if (kind != "vote") members.Add(new("body", new JsonValue.String("a body")));
        if (target is not null) members.Add(new("target", new JsonValue.String(target)));
        if (kind == "verification")
        {
            members.Add(new("method", new JsonValue.String("ran it")));
            members.Add(new("result", new JsonValue.String("reproduced")));
        }

        var canonical = Require(CanonicalJson.CanonicalizeWithNfc(new JsonValue.Object([.. members])));
        Require(await harness.Events.AppendAsync(
            Require(AggregateId.Create(id)),
            AggregateVersion.New,
            [new DomainEvent(Require(EventId.Create(id)), Require(EventType.Create(PostProjector.PostAcceptedType)), Require(ActorId.Create(author)), new JsonValue.Object(
            [
                new("post_id", new JsonValue.String(id)),
                new("canonical", new JsonValue.String(Encoding.UTF8.GetString(canonical.Span))),
                new("signature", new JsonValue.String(signature)),
                new("digest", new JsonValue.String(digest)),
                new("author", new JsonValue.String(author)),
                new("board", new JsonValue.String("b")),
                new("kind", new JsonValue.String(kind)),
            ]))],
            Ct).ConfigureAwait(false));
    }

    private static readonly string FindingDigest = "sha256:" + new string('f', 64);

    private static Task<AgentRead> ReadOf(KeyActHarness harness, string agent, AgentReadOptions options) =>
        AgentRead.ReadAsync(harness.Events, new LifecycleVerifier(harness.Verifiers), Typed.Agent(agent), options, Ct);

    [Fact]
    public async Task RC2_AViewOverAReadRefusesAQuestionAboutARowItWasNotReadFor()
    {
        var (harness, _, _, _) = await WorldAsync();
        var read = await ReadOf(harness, Alice, AgentReadOptions.PostureOnly);

        Assert.Throws<OutOfScopeException>(() => read.Verified.For(Typed.Agent(Bob)));
        Assert.Throws<OutOfScopeException>(() => read.Verified.StandingOf(Typed.Agent(Bob)));
        Assert.Throws<OutOfScopeException>(() => read.Verified.PostureStandingOf(Typed.Agent(Bob)));
        Assert.Throws<OutOfScopeException>(() => read.Verified.ModerationOf("01UNREAD0000000000000000ZZ"));
        Assert.Throws<OutOfScopeException>(() => read.Verified.Standings());
        Assert.Throws<OutOfScopeException>(() => read.Verified.Moderation());
        Assert.Throws<OutOfScopeException>(() => read.Verified.Log);
        Assert.Throws<OutOfScopeException>(() => read.Verified.Skipped());
        Assert.Throws<OutOfScopeException>(() => read.Verified.HonouredEvents.Log);

        // A stream read without the agent's posts does not answer for them either.
        var streamOnly = await ReadOf(harness, Alice, AgentReadOptions.Stream);
        Assert.Throws<OutOfScopeException>(() => streamOnly.Verified.PostureStandingOf(Typed.Agent(Alice)));
        Assert.Throws<OutOfScopeException>(() => streamOnly.Posture);
        Assert.NotNull(streamOnly.Lifecycle.Binding);
    }

    [Fact]
    public async Task RC3_ARowTheLogGainsIsDecidedOnByTheVeryNextReadWithNoClockAdvanced()
    {
        var (harness, _, _, signer) = await WorldAsync();
        var before = Require((await ReadOf(harness, Alice, AgentReadOptions.PostureOnly)).Posture);
        var at = harness.Clock.GetUtcNow();

        // An attestation that withdraws the owner's verification, appended between two requests; the clock is the same.
        Require(await harness.AttestOwnerAsync(Alice, "owner:alice", false, signer, Ct));
        var after = Require((await ReadOf(harness, Alice, AgentReadOptions.PostureOnly)).Posture);

        Assert.Equal(at, harness.Clock.GetUtcNow());
        Assert.True(before.OwnerVerified);
        Assert.False(after.OwnerVerified);
    }

    [Fact]
    public async Task RC16_AnAgentWithNoFindingNamesNoSignalTarget_AnAgentWithOneNamesIt()
    {
        var (harness, _, _, _) = await WorldAsync();
        await PostAsync(harness, "01RC16QUESTION00000000000Z", "sha256:" + new string('1', 64), "question", Bob, null);
        var none = await ReadOf(harness, Bob, AgentReadOptions.PostureAndFindings);
        Assert.Empty(none.Slice.Scope.SignalTargets);

        await PostAsync(harness, "01RC16FINDING000000000000Z", FindingDigest, "finding", Bob, null);
        var some = await ReadOf(harness, Bob, AgentReadOptions.PostureAndFindings);
        Assert.Equal([FindingDigest], some.Slice.Scope.SignalTargets);

        // The token's stamped tier never reads the arm.
        var stamped = await ReadOf(harness, Bob, AgentReadOptions.PostureOnly);
        Assert.Empty(stamped.Slice.Scope.SignalTargets);
    }

    /// <summary>
    /// RC16 (R6.57): Alice's reproduction of Bob's finding makes it V2 and Bob holds a verified finding. Alice's key is then declared compromised from before her report: the report is disputed, carries no weight,
    /// and the finding falls back. The scoped read decides both states as the whole-log fold does; red against a read whose caster disputes are not asked (FRC18).
    /// </summary>
    [Fact]
    public async Task RC16_ACastersDisputedSignalCarriesNoWeightInTheScopedReadAsInTheWholeLog()
    {
        var (harness, alice, _, _) = await WorldAsync();
        await PostAsync(harness, "01RC16FINDING000000000000Z", FindingDigest, "finding", Bob, null);
        await PostAsync(harness, "01RC16REPORT0000000000000Z", "sha256:" + new string('e', 64), "verification", Alice, FindingDigest, Header(alice.Kid));
        var verifier = new LifecycleVerifier(harness.Verifiers);

        async Task<(int Whole, int Scoped)> FindingsAsync()
        {
            var all = Require(await harness.Events.ReadAllAsync(Ct).ConfigureAwait(false));
            var whole = Require(PostureQuery.Of(verifier.OfLog(all), Typed.Agent(Bob))).VerifiedFindings;
            var scoped = Require((await ReadOf(harness, Bob, AgentReadOptions.PostureAndFindings).ConfigureAwait(false)).Posture).VerifiedFindings;
            return (whole, scoped);
        }

        var before = await FindingsAsync();
        Assert.Equal((1, 1), before);

        // The declaration: the operator's declaration key, published once, then Alice's key compromised from its binding on.
        using var declarer = new InMemoryLifecycleStatementSigner();
        var acta = Require(ActaLog.Fold(Require(await harness.Events.ReadAllAsync(Ct))));
        Require(await harness.Events.AppendAsync(
            Require(AggregateId.Create(LogEntries.DeclarationKeysAggregate)),
            Require(AggregateVersion.From(acta.DeclarationKeys.Length)),
            [new DomainEvent(Require(EventId.Create("01RC16DECLKEY00000000000ZZ")), Require(EventType.Create(LogEntries.DeclarationKeyType)), Require(ActorId.Create("operator:ops")), new JsonValue.Object(
            [
                new(LogEntries.KidMember, new JsonValue.String(declarer.Kid)),
                new(LogEntries.JwkMember, declarer.PublicJwk()),
                new(LogEntries.ValidFromMember, new JsonValue.String(LogLeaf.RenderServerTimestamp(ServerTimestamp.At(harness.Clock.GetUtcNow())))),
            ]))],
            Ct));
        var bound = Require(await new DeclareCompromise(harness.Events, verifier, harness.Clock)
            .DeclareAsync(Typed.Agent(Alice), Typed.Kid(alice.Kid), fromIndex: 1, CompromiseReason.OwnerReported, declarer, Require(ActorId.Create("operator:ops")), Ct));
        Assert.NotNull(bound);

        var after = await FindingsAsync();
        Assert.Equal((0, 0), after);
    }
}
