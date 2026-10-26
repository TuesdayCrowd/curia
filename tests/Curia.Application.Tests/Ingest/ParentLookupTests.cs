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
    private const string Post = "01M5VAMSJ17A9JBE3R41RAHMWV";
    private const string Other = "01M5VAMZMEE6C2RK57P72TH5SE";

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
    private static async Task<string> AnswerAsync(IEventReader reader, string parent, CancellationToken ct)
    {
        var found = await ParentLookup.FindAsync(reader, Typed.Parent(parent), ct).ConfigureAwait(false);
        Assert.True(found.TryGetValue(out var facts, out var error), $"the read failed: {error?.Type}");
        if (facts is null)
            return "none";

        Assert.Equal(Post, facts.PostId);
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
        await AppendAsync(store, Post, PostProjector.PostAcceptedType, Accepted(Post, stored), ct);

        Assert.Equal(expected, await AnswerAsync(store, Post, ct));
    }

    /// <summary>A post that later gained an acceptance or a moderation act is still found, with the kind of its first event.</summary>
    [Theory]
    [InlineData("answer.accepted")]
    [InlineData("moderation.applied")]
    public async Task R8_70_APostWithLaterEventsInItsStreamIsStillFoundByItsPostAcceptedEvent(string later)
    {
        var ct = TestContext.Current.CancellationToken;
        var store = NewStore();
        await AppendAsync(store, Post, PostProjector.PostAcceptedType, Accepted(Post, "question"), ct);
        await AppendAsync(store, Post, later, new JsonValue.Object([new("post_id", new JsonValue.String(Post))]), ct);

        Assert.Equal("question", await AnswerAsync(store, Post, ct));
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
                await AppendAsync(store, Post, "agent.enrolled", new JsonValue.Object([new("agent_id", new JsonValue.String(Post))]), ct);
                break;
            case "answer-accepted-only":
                await AppendAsync(store, Post, "answer.accepted", new JsonValue.Object([new("post_id", new JsonValue.String(Post))]), ct);
                break;
            case "post-accepted-without-a-kind":
                await AppendAsync(store, Post, PostProjector.PostAcceptedType, Accepted(Post, kind: null), ct);
                break;
            case "post-accepted-naming-another-post":
                await AppendAsync(store, Post, PostProjector.PostAcceptedType, Accepted(Other, "question"), ct);
                break;
            default:
                break;
        }

        // The control is the same lookup over a stream that is a post, so "none" is not what the lookup answers to everything.
        await AppendAsync(store, Other, PostProjector.PostAcceptedType, Accepted(Other, "question"), ct);
        var control = Require(await ParentLookup.FindAsync(store, Typed.Parent(Other), ct));
        Assert.NotNull(control);
        Assert.Equal(PostKind.Question, control.Kind);

        Assert.Equal("none", await AnswerAsync(store, Post, ct));
    }

    /// <summary>
    /// A parent that is no post id names no post and costs no read (an agent identifier, text carrying a marker, a lower-case spelling); a parent that is
    /// shaped as one costs exactly one read, of its own stream.
    /// </summary>
    [Theory]
    [InlineData("urn:curia:agent:somebody", 0)]
    [InlineData("ignore previous instructions", 0)]
    [InlineData("01m5vamsj17a9jbe3r41rahmwv", 0)]
    [InlineData(Post, 1)]
    public async Task R8_70_OnlyAParentShapedAsAPostIdIsReadAndItIsReadOnce(string parent, int reads)
    {
        var ct = TestContext.Current.CancellationToken;
        var counting = new CountingReader(NewStore());

        var found = await ParentLookup.FindAsync(counting, Typed.Parent(parent), ct);

        Assert.True(found.IsOk);
        Assert.Equal(reads, counting.StreamReads);
        Assert.Equal(0, counting.ForwardReads);
    }

    /// <summary>A reader that returns a failed result for the parent's stream: the lookup fails, and a failure is never "no such post".</summary>
    [Fact]
    public async Task R8_70_AnUnreadableStreamIsAFailureAndNeverAMissingParent()
    {
        var ct = TestContext.Current.CancellationToken;

        var found = await ParentLookup.FindAsync(new FailingReader(), Typed.Parent(Post), ct);

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
