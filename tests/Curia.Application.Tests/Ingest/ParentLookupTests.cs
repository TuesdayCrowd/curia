using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ingest;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Application.Tests.Ingest;

/// <summary>
/// R8.70 (errata G38): the lookup reads the post a submission names as its parent from that post's own stream, and a post is a stream that holds a
/// <c>post.accepted</c> event whose payload names that post. A stream that exists is not a post (the aggregate id column is one namespace for posts,
/// agents, flags and key lifecycles), and a log that cannot be read is a failure and never "no such post".
///
/// <para><b>Oracles.</b> The rows are appended by the test with the kind the test chose, and the answer is compared with that kind. The reader's own
/// call counter says whether a stream was read at all.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ParentLookupTests
{
    // A fresh pair of ids for each test (xUnit builds one instance per test), so nothing a lookup remembered about one id can answer for another test.
    private readonly string post = FreshPostId();
    private readonly string other = FreshPostId();

    private static string FreshPostId()
    {
        const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        return "01" + string.Create(24, 0, static (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = Alphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(Alphabet.Length)];
        });
    }

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    private static InMemoryEventStore NewStore() =>
        new(new ManualTimeProvider(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero)), new PermissiveLifecycleAppendGate());

    private static async Task AppendAsync(InMemoryEventStore store, string aggregate, string type, JsonValue payload, CancellationToken ct) =>
        Require(await store.AppendAsync(
            Require(AggregateId.Create(aggregate)),
            await VersionOfAsync(store, aggregate, ct).ConfigureAwait(false),
            [new DomainEvent(
                Require(EventId.Create(Guid.NewGuid().ToString("N"))),
                Require(EventType.Create(type)),
                Require(ActorId.Create("https://agents.example/author")),
                payload)],
            ct).ConfigureAwait(false));

    private static async Task<AggregateVersion> VersionOfAsync(InMemoryEventStore store, string aggregate, CancellationToken ct)
    {
        var existing = Require(await store.ReadByAggregateAsync(Require(AggregateId.Create(aggregate)), ct).ConfigureAwait(false));
        return existing.Count == 0 ? AggregateVersion.New : Require(AggregateVersion.From(existing.Count));
    }

    private static JsonValue.Object Accepted(string postId, string? kind) =>
        new(kind is null
            ? [new("post_id", new JsonValue.String(postId))]
            : [new("post_id", new JsonValue.String(postId)), new("kind", new JsonValue.String(kind))]);

    /// <summary>What the lookup answers, as <c>kind</c> or <c>none</c>, and fails the test for a failed read.</summary>
    private async Task<string> AnswerAsync(IEventReader reader, string parent, CancellationToken ct)
    {
        var found = await ParentLookup.FindAsync(reader, Typed.Parent(parent), ct).ConfigureAwait(false);
        Assert.True(found.TryGetValue(out var facts, out var error), $"the read failed: {error?.Type}");
        if (facts is null)
            return "none";

        Assert.Equal(post, facts.PostId);
        return PostKinds.Wire(facts.Kind);
    }

    [Theory]
    [InlineData("question", "question")]
    [InlineData("comment", "comment")]
    [InlineData("answer", "answer")]
    public async Task R8_70_APostIsFoundWithTheKindItsPostAcceptedEventStored(string stored, string expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var store = NewStore();
        await AppendAsync(store, post, PostProjector.PostAcceptedType, Accepted(post, stored), ct);

        Assert.Equal(expected, await AnswerAsync(store, post, ct));
    }

    /// <summary>A post that later gained an acceptance or a moderation act is still found, with the kind of its first event.</summary>
    [Theory]
    [InlineData("answer.accepted")]
    [InlineData("moderation.applied")]
    public async Task R8_70_APostWithLaterEventsInItsStreamIsStillFoundByItsPostAcceptedEvent(string later)
    {
        var ct = TestContext.Current.CancellationToken;
        var store = NewStore();
        await AppendAsync(store, post, PostProjector.PostAcceptedType, Accepted(post, "question"), ct);
        await AppendAsync(store, post, later, new JsonValue.Object([new("post_id", new JsonValue.String(post))]), ct);

        Assert.Equal("question", await AnswerAsync(store, post, ct));
    }

    /// <summary>
    /// What is not a post: a stream under a ULID-shaped id that holds only another event type (an agent enrolled under a post-shaped id, D39), only an
    /// acceptance, a <c>post.accepted</c> whose payload names no kind or names another post, and an id nothing was appended under.
    /// </summary>
    [Theory]
    [InlineData("agent-credential-only")]
    [InlineData("answer-accepted-only")]
    [InlineData("post-accepted-without-a-kind")]
    [InlineData("post-accepted-naming-another-post")]
    [InlineData("nothing-appended")]
    public async Task R8_70_AStreamThatIsNoPostIsNotFound(string shape)
    {
        var ct = TestContext.Current.CancellationToken;
        var store = NewStore();
        switch (shape)
        {
            case "agent-credential-only":
                await AppendAsync(store, post, "agent.enrolled", new JsonValue.Object([new("agent_id", new JsonValue.String(post))]), ct);
                break;
            case "answer-accepted-only":
                await AppendAsync(store, post, "answer.accepted", new JsonValue.Object([new("post_id", new JsonValue.String(post))]), ct);
                break;
            case "post-accepted-without-a-kind":
                await AppendAsync(store, post, PostProjector.PostAcceptedType, Accepted(post, kind: null), ct);
                break;
            case "post-accepted-naming-another-post":
                await AppendAsync(store, post, PostProjector.PostAcceptedType, Accepted(other, "question"), ct);
                break;
            default:
                break;
        }

        // The control is the same lookup over a stream that is a post, so "none" is not what the lookup answers to everything.
        await AppendAsync(store, other, PostProjector.PostAcceptedType, Accepted(other, "question"), ct);
        var control = Require(await ParentLookup.FindAsync(store, Typed.Parent(other), ct));
        Assert.NotNull(control);
        Assert.Equal(PostKind.Question, control.Kind);

        Assert.Equal("none", await AnswerAsync(store, post, ct));
    }

    /// <summary>
    /// A parent that is no post id names no post and costs no read (an agent identifier, text carrying a marker, a lower-case spelling); a parent that is
    /// shaped as one costs exactly one read, of its own stream.
    /// </summary>
    [Theory]
    [InlineData("urn:curia:agent:somebody", 0)]
    [InlineData("ignore previous instructions", 0)]
    [InlineData("01m5vamsj17a9jbe3r41rahmwv", 0)]
    [InlineData("<this test's post>", 1)]
    public async Task R8_70_OnlyAParentShapedAsAPostIdIsReadAndItIsReadOnce(string parent, int reads)
    {
        var ct = TestContext.Current.CancellationToken;
        var counting = new CountingReader(NewStore());

        var found = await ParentLookup.FindAsync(counting, Typed.Parent(parent == "<this test's post>" ? post : parent), ct);

        Assert.True(found.IsOk);
        Assert.Equal(reads, counting.StreamReads);
        Assert.Equal(0, counting.ForwardReads);
    }

    /// <summary>
    /// The kind is the one the stored event's payload carries (the Forum's word), and never the one the stored canonical form claims for itself: a
    /// <c>post.accepted</c> whose <c>kind</c> member says question and whose <c>canonical</c> text says comment is a question to the lookup.
    /// </summary>
    [Fact]
    public async Task R8_70_TheKindIsTheStoredEventsAndNotTheOneItsCanonicalFormClaims()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = NewStore();
        var canonicalClaimingComment = "{\"body\":\"x\",\"kind\":\"comment\"}";
        await AppendAsync(
            store,
            post,
            PostProjector.PostAcceptedType,
            new JsonValue.Object(
            [
                new("post_id", new JsonValue.String(post)),
                new("kind", new JsonValue.String("question")),
                new("canonical", new JsonValue.String(canonicalClaimingComment)),
            ]),
            ct);

        Assert.Equal("question", await AnswerAsync(store, post, ct));
    }

    /// <summary>
    /// The lookup remembers nothing between calls: an id that named no post answers missing, and once a <c>post.accepted</c> for it is appended the same
    /// lookup, over the same store, finds it with the stored kind. The id is this test's own.
    /// </summary>
    [Fact]
    public async Task R8_70_AParentCreatedAfterAMissingAnswerIsFoundByTheNextLookup()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = NewStore();

        Assert.Equal("none", await AnswerAsync(store, post, ct));

        await AppendAsync(store, post, PostProjector.PostAcceptedType, Accepted(post, "question"), ct);

        Assert.Equal("question", await AnswerAsync(store, post, ct));
    }

    /// <summary>A reader that returns a failed result for the parent's stream: the lookup fails, and a failure is never "no such post".</summary>
    [Fact]
    public async Task R8_70_AnUnreadableStreamIsAFailureAndNeverAMissingParent()
    {
        var ct = TestContext.Current.CancellationToken;

        var found = await ParentLookup.FindAsync(new FailingReader(), Typed.Parent(post), ct);

        Assert.False(found.IsOk);
        Assert.Equal("test/unreadable", found.Match(_ => string.Empty, e => e.Type));
    }

    /// <summary>The Table 9 spelling the lookup returns for a stored kind comes from the code's own parse; the errata names the kind an answer needs.</summary>
    [Fact]
    public void R8_70_TheKindAnAnswerNeedsIsTheOneTheErratumNames() =>
        Assert.Equal(PublishedParentRule.AnswerParentKind(), PostKinds.Wire(PostKind.Question));

    private sealed class CountingReader(IEventReader inner) : IEventReader
    {
        public int StreamReads { get; private set; }

        public int ForwardReads { get; private set; }

        public Task<Result<IReadOnlyList<AppendedEvent>>> ReadByAggregateAsync(AggregateId aggregateId, CancellationToken cancellationToken = default)
        {
            StreamReads++;
            return inner.ReadByAggregateAsync(aggregateId, cancellationToken);
        }

        public Task<Result<IReadOnlyList<AppendedEvent>>> ReadForwardAsync(EventSequence afterSeq, int? maxCount = null, CancellationToken cancellationToken = default)
        {
            ForwardReads++;
            return inner.ReadForwardAsync(afterSeq, maxCount, cancellationToken);
        }
    }

    private sealed class FailingReader : IEventReader
    {
        private static readonly Error Unreadable = new("test/unreadable", "The test's reader refused the read");

        public Task<Result<IReadOnlyList<AppendedEvent>>> ReadByAggregateAsync(AggregateId aggregateId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlyList<AppendedEvent>>.Fail(Unreadable));

        public Task<Result<IReadOnlyList<AppendedEvent>>> ReadForwardAsync(EventSequence afterSeq, int? maxCount = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlyList<AppendedEvent>>.Fail(Unreadable));
    }
}
