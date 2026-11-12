using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Curia.Application.Credentials;
using Curia.Application.Projections;
using Curia.Application.Tests.Credentials;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Acta;
using Curia.Domain.Keys;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;
using Xunit;
using static Curia.Application.Tests.Credentials.KeyActHarness;
using static Curia.Application.Tests.Credentials.LifecycleProbeSupport;

namespace Curia.Application.Tests.Projections;

/// <summary>
/// LRV9 and LRV30 (errata G68, R6.57, ruling 33): the marks. A forged declaration disputes no post and leaves the key active; a genuine declaration under the published declaration
/// key still marks the posts in its window (LRV9's control), and a malformed row before it does not disarm it (LRV30: the vector <c>malformed-row-before-genuine-declaration</c> through
/// <c>DisputedPosts.Fold</c>, where today's fold refuses the stream and the consumer discards the refusal).
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class MarksSurviveAMalformedRowTests
{
    private static readonly ActorId Operator = Require(ActorId.Create("operator:ops"));

    /// <summary>A post of <paramref name="author"/> signed under <paramref name="kid"/>, whose id is the id of the log event standing for it.</summary>
    private static PostView PostBy(KeyActHarness harness, string author, TestIdentity signer, string postId) =>
        new(
            postId,
            "{}",
            harness.Sign("{}", "curia-post+jws", signer.Signing).Value,
            "sha256:" + new string('a', 64),
            author,
            "canonicalization",
            "question",
            null,
            ServerTimestamp.At(KeyActHarness.Start),
            []);

    /// <summary>Appends a stand-in for a post to <paramref name="harness"/>'s log, in its own stream, and returns its event id.</summary>
    private static async Task<string> AppendPostAsync(KeyActHarness harness, string id)
    {
        Require(await harness.Events.AppendAsync(
            Require(AggregateId.Create(id)),
            AggregateVersion.New,
            [new DomainEvent(Require(EventId.Create(id)), Require(EventType.Create("post.accepted")), Require(ActorId.Create(Alice)), new JsonValue.Object([]))],
            TestContext.Current.CancellationToken).ConfigureAwait(true));
        return id;
    }

    private static async Task PublishDeclarationKeyAsync(KeyActHarness harness, InMemoryLifecycleStatementSigner signer)
    {
        var acta = Require(ActaLog.Fold(await LogAsync(harness).ConfigureAwait(true)));
        Require(await harness.Events.AppendAsync(
            Require(AggregateId.Create(LogEntries.DeclarationKeysAggregate)),
            Require(AggregateVersion.From(acta.DeclarationKeys.Length)),
            [new DomainEvent(
                Require(EventId.Create("01K4CQ1V9P3R5T7W9Y1A3C5EKY")),
                Require(EventType.Create(LogEntries.DeclarationKeyType)),
                Operator,
                new JsonValue.Object(
                [
                    new(LogEntries.KidMember, S(signer.Kid)),
                    new(LogEntries.JwkMember, signer.PublicJwk()),
                    new(LogEntries.ValidFromMember, S(LogLeaf.RenderServerTimestamp(ServerTimestamp.At(harness.Clock.GetUtcNow())))),
                ]))],
            TestContext.Current.CancellationToken).ConfigureAwait(true));
    }

    /// <summary>LRV9: a forged declaration (zero signature; signed by a key the log never published as a declaration key) disputes no post, and the key is active.</summary>
    [Fact]
    public async Task LRV9_AForgedDeclarationDisputesNoPost()
    {
        var (harness, victim) = await EnrolledAsync().ConfigureAwait(true);
        var post = PostBy(harness, Alice, victim, await AppendPostAsync(harness, "post-before-forgery").ConfigureAwait(true));
        await AppendRowAsync(harness, Alice, "agent.key-compromised", Declaration(Alice, [victim.Kid], 1, 1, string.Empty)).ConfigureAwait(true);
        var statement = ((JsonValue.String)Declaration(Alice, [victim.Kid], 1, 2, "x").Members.First(m => m.Key == "statement").Value).Value;
        var bySomeKey = harness.Sign(statement, KeyStatements.CompromiseHeaderTyp, TestIdentity.New("log-key").Signing).Value;
        await AppendRowAsync(harness, Alice, "agent.key-compromised", Declaration(Alice, [victim.Kid], 1, 2, bySomeKey)).ConfigureAwait(true);

        var disputed = DisputedPosts.Fold(VerifierOf(harness).OfLog(await LogAsync(harness).ConfigureAwait(true)), [post]);

        WholeText.Same("Count 0, key active", $"Count {disputed.Count}, key {disputed.KeyStatusOf(post)}");
    }

    /// <summary>LRV9's control: a genuine declaration under the published declaration key disputes the posts in its window, by log position as before.</summary>
    [Fact]
    public async Task LRV9_AGenuineDeclarationStillDisputesThePostsInItsWindow_Control()
    {
        var (harness, victim) = await EnrolledAsync().ConfigureAwait(true);
        var post = PostBy(harness, Alice, victim, await AppendPostAsync(harness, "post-in-the-window").ConfigureAwait(true));
        using var signer = new InMemoryLifecycleStatementSigner();
        await PublishDeclarationKeyAsync(harness, signer).ConfigureAwait(true);
        var declared = Require(await new DeclareCompromise(harness.Events, VerifierOf(harness), harness.Clock)
            .DeclareAsync(Typed.Agent(Alice), Typed.Kid(victim.Kid), 1, CompromiseReason.OwnerReported, signer, Operator, TestContext.Current.CancellationToken).ConfigureAwait(true));

        var disputed = DisputedPosts.Fold(VerifierOf(harness).OfLog(await LogAsync(harness).ConfigureAwait(true)), [post]);

        WholeText.Same($"Count 1, by {declared.LogIndex}, key compromised", $"Count {disputed.Count}, by {disputed.DisputedByLogIndex(post)}, key {disputed.KeyStatusOf(post)}");
    }

    /// <summary>LRV30: vector 44's log through <c>DisputedPosts.Fold</c>: the post in the declaration's window is marked with the declaration's index, and the key is compromised.</summary>
    [Fact]
    public void LRV30_AMalformedRowBeforeAGenuineDeclarationDoesNotDisarmIt()
    {
        var log = ConformanceLog.Load("honoured", "malformed-row-before-genuine-declaration");
        var harness = new KeyActHarness();
        var standIn = log.Log[3].Event.Id.Value;
        var post = PostBy(harness, log.Agent, TestIdentity.New("conformance-ed25519-minimal"), standIn);

        var disputed = DisputedPosts.Fold(ConformanceLog.Verifier().OfLog(log.Log), ImmutableArray.Create(post));

        Assert.True(
            disputed.Count == 1 && disputed.DisputedByLogIndex(post) == 6 && disputed.KeyStatusOf(post) == "compromised",
            $"Expected: 1 disputed post, by log index 6, key compromised  Actual: {disputed.Count} disputed post(s), by {disputed.DisputedByLogIndex(post)?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}, key {disputed.KeyStatusOf(post)}");
    }
}
