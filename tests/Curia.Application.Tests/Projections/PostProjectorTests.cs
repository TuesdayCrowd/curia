using System.Diagnostics.CodeAnalysis;
using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Application.Tests.Projections;

/// <summary>
/// The post read model's own rules. The end-to-end shape -- what a served post looks like -- is
/// <c>Curia.Api.Tests</c>; this is what the fold derives from a persisted event.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class PostProjectorTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    /// <summary>
    /// Appends a <c>post.accepted</c> event shaped the way <c>IngestPipeline.PersistAsync</c> shapes
    /// one, with the members the projector reads and a canonical the caller supplies -- the
    /// canonical is what carries <c>prev</c>, so it is the thing under test here.
    /// </summary>
    private static async Task PersistAsync(
        InMemoryEventStore store, string postId, string kind, string canonical, CancellationToken ct)
    {
        var payload = new JsonValue.Object(
        [
            new("post_id", new JsonValue.String(postId)),
            new("canonical", new JsonValue.String(canonical)),
            new("signature", new JsonValue.String("sig")),
            new("digest", new JsonValue.String("sha-256:" + postId)),
            new("author", new JsonValue.String("https://agents.example/author")),
            new("board", new JsonValue.String("board")),
            new("kind", new JsonValue.String(kind)),
        ]);

        Require(await store.AppendAsync(
            Require(AggregateId.Create(postId)),
            AggregateVersion.New,
            [new DomainEvent(
                Require(EventId.Create(postId)),
                Require(EventType.Create(PostProjector.PostAcceptedType)),
                Require(ActorId.Create("https://agents.example/author")),
                payload)],
            ct).ConfigureAwait(false));
    }

    /// <summary>
    /// R6.7: a revision's <c>prev</c> is read from the canonical bytes the author signed, and a
    /// post that is not a revision has none. The persisted payload does not carry it beside the
    /// bytes, and should not: the bytes are the authority.
    /// </summary>
    [Fact]
    public async Task R6_7_PrevIsReadFromTheSignedCanonicalBytes()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        await PersistAsync(store, "q-1", "question",
            """{"board":"board","body":"a question","kind":"question","prev":null}""", ct);
        await PersistAsync(store, "r-1", "revision",
            """{"board":"board","body":"a corrected body","kind":"revision","prev":"sha-256:q-1"}""", ct);
        await PersistAsync(store, "junk", "question", "not json at all", ct);

        var log = Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct));
        var posts = PostProjector.Fold(log).ToDictionary(p => p.PostId, StringComparer.Ordinal);

        Assert.Null(posts["q-1"].Prev);
        Assert.Equal("sha-256:q-1", posts["r-1"].Prev);

        // Lenient: an unparseable canonical is still a post, with no predecessor -- prev is an
        // annotation on the view, not a condition of serving it.
        Assert.Null(posts["junk"].Prev);
        Assert.Equal(3, posts.Count);
    }
}
