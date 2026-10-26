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
        await AppendAsync(store, post, PostProjector.PostAcceptedType, Accepted(post, kind), ct);

        Assert.Equal($"{post} {kind}", await AnswerAsync(store, post, ct));
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
        await AppendAsync(store, other, PostProjector.PostAcceptedType, Accepted(other, "question"), ct);
        switch (shape)
        {
            case "agent-under-a-ulid-shaped-id":
                await AppendAsync(store, post, "agent.enrolled", new JsonValue.Object([new("agent_id", new JsonValue.String(post))]), ct);
                break;
            case "answer-accepted-only":
                await AppendAsync(store, post, "answer.accepted", new JsonValue.Object([new("post_id", new JsonValue.String(post))]), ct);
                break;
            case "post-accepted-naming-another-post":
                await AppendAsync(store, post, PostProjector.PostAcceptedType, Accepted(other, "question"), ct);
                break;
            default:
                break;
        }

        Assert.Equal($"{other} question", await AnswerAsync(store, other, ct));
        Assert.Equal("none", await AnswerAsync(store, post, ct));
    }

    /// <summary>The kind is the stored event's, never the one its canonical form claims (the in-memory row, on the real store).</summary>
    [Fact]
    public async Task R11_4_TheKindIsTheStoredEventsAndNotTheOneItsCanonicalFormClaimsOnTheRealStore()
    {
        var ct = TestContext.Current.CancellationToken;
        await fixture.ResetEventsTableAsync(ct);
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

        Assert.Equal($"{post} question", await AnswerAsync(store, post, ct));
    }

    /// <summary>The lookup remembers nothing between calls: missing, then appended, then found, over one store and one lookup.</summary>
    [Fact]
    public async Task R11_4_AParentCreatedAfterAMissingAnswerIsFoundByTheNextLookupOnTheRealStore()
    {
        var ct = TestContext.Current.CancellationToken;
        await fixture.ResetEventsTableAsync(ct);
        var store = NewStore();

        Assert.Equal("none", await AnswerAsync(store, post, ct));

        await AppendAsync(store, post, PostProjector.PostAcceptedType, Accepted(post, "question"), ct);

        Assert.Equal($"{post} question", await AnswerAsync(store, post, ct));
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
