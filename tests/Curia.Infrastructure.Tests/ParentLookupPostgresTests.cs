using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ingest;
using Curia.Application.Projections;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Infrastructure.Tests;

/// <summary>
/// R11.4's adapter parity for R8.70's lookup: the rows <c>ParentLookupTests</c> appends to the in-memory store, appended to the real one, answer the
/// same. The Postgres reader throws rather than returning a failed result, so the unreadable-log row stays with the in-memory fake.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection(PostgresCollectionDefinition.Name)]
public sealed class ParentLookupPostgresTests(PostgresDatabaseFixture fixture)
{
    private const string Post = "01M5VAMSJ17A9JBE3R41RAHMWV";
    private const string Other = "01M5VAMZMEE6C2RK57P72TH5SE";

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    private PostgresEventStore NewStore() =>
        new(fixture.AppRoleDataSource, new ManualTimeProvider(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero)), fixture.PermissiveLifecycleGate());

    private static async Task AppendAsync(PostgresEventStore store, string aggregate, string type, JsonValue payload, CancellationToken ct) =>
        Require(await store.AppendAsync(
            Require(AggregateId.Create(aggregate)),
            AggregateVersion.New,
            [new DomainEvent(
                Require(EventId.Create(Guid.NewGuid().ToString("N"))),
                Require(EventType.Create(type)),
                Require(ActorId.Create("https://agents.example/author")),
                payload)],
            ct).ConfigureAwait(false));

    private static JsonValue.Object Accepted(string postId, string kind) =>
        new([new("post_id", new JsonValue.String(postId)), new("kind", new JsonValue.String(kind))]);

    private static async Task<string> AnswerAsync(PostgresEventStore store, string parent, CancellationToken ct)
    {
        var found = Require(await ParentLookup.FindAsync(store, Typed.Parent(parent), ct).ConfigureAwait(false));
        return found is null ? "none" : $"{found.PostId} {PostKinds.Wire(found.Kind)}";
    }

    [Theory]
    [InlineData("question")]
    [InlineData("comment")]
    [InlineData("answer")]
    public async Task R11_4_APostIsFoundWithItsStoredKindOnTheRealStore(string kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await fixture.ResetEventsTableAsync(ct);
        var store = NewStore();
        await AppendAsync(store, Post, PostProjector.PostAcceptedType, Accepted(Post, kind), ct);

        Assert.Equal($"{Post} {kind}", await AnswerAsync(store, Post, ct));
    }

    /// <summary>
    /// What is no post on the real store: a ULID-shaped agent (the D39 fixture), a stream holding only an acceptance, a <c>post.accepted</c> that
    /// names another post, and an id nothing was appended under. The control is a real post in the same table.
    /// </summary>
    [Theory]
    [InlineData("agent-under-a-ulid-shaped-id")]
    [InlineData("answer-accepted-only")]
    [InlineData("post-accepted-naming-another-post")]
    [InlineData("nothing-appended")]
    public async Task R11_4_AStreamThatIsNoPostIsNotFoundOnTheRealStore(string shape)
    {
        var ct = TestContext.Current.CancellationToken;
        await fixture.ResetEventsTableAsync(ct);
        var store = NewStore();
        await AppendAsync(store, Other, PostProjector.PostAcceptedType, Accepted(Other, "question"), ct);
        switch (shape)
        {
            case "agent-under-a-ulid-shaped-id":
                await AppendAsync(store, Post, "agent.enrolled", new JsonValue.Object([new("agent_id", new JsonValue.String(Post))]), ct);
                break;
            case "answer-accepted-only":
                await AppendAsync(store, Post, "answer.accepted", new JsonValue.Object([new("post_id", new JsonValue.String(Post))]), ct);
                break;
            case "post-accepted-naming-another-post":
                await AppendAsync(store, Post, PostProjector.PostAcceptedType, Accepted(Other, "question"), ct);
                break;
            default:
                break;
        }

        Assert.Equal($"{Other} question", await AnswerAsync(store, Other, ct));
        Assert.Equal("none", await AnswerAsync(store, Post, ct));
    }

    /// <summary>A parent that is no post id is dismissed without a read, so an agent's stream under a URI is never consulted.</summary>
    [Fact]
    public async Task R11_4_AnAgentIdentifierNamedAsAParentIsNoPostOnTheRealStore()
    {
        var ct = TestContext.Current.CancellationToken;
        await fixture.ResetEventsTableAsync(ct);
        var store = NewStore();
        await AppendAsync(store, "urn:curia:agent:somebody", PostProjector.PostAcceptedType, Accepted("urn:curia:agent:somebody", "question"), ct);

        Assert.Equal("none", await AnswerAsync(store, "urn:curia:agent:somebody", ct));
    }
}
