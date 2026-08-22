using System.Diagnostics.CodeAnalysis;
using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Application.Tests.Projections;

/// <summary>
/// Table 10's <c>answer</c>/<c>accept</c>, folded out of the log — and Table 11's "≥ 5 accepted
/// answers", which has been a <c>PostureFacts</c> field with no writer since Stage 2.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AcceptanceProjectorTests
{
    private const string Asker = "https://agents.example/asker";
    private const string Answerer = "https://agents.example/answerer";
    private const string Other = "https://agents.example/other";

    private static readonly DateTimeOffset Start = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    private static async Task<IReadOnlyList<AppendedEvent>> LogAsync(
        InMemoryEventStore store, CancellationToken ct) =>
        Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct).ConfigureAwait(false));

    private static async Task AcceptedPostAsync(
        InMemoryEventStore store, string postId, string author, string kind, string? parent, CancellationToken ct)
    {
        var members = new List<KeyValuePair<string, JsonValue>>
        {
            new("post_id", new JsonValue.String(postId)),
            new("author", new JsonValue.String(author)),
            new("kind", new JsonValue.String(kind)),
        };

        if (parent is not null) members.Add(new("parent", new JsonValue.String(parent)));

        Require(await store.AppendAsync(
            Require(AggregateId.Create(postId)),
            AggregateVersion.New,
            [new DomainEvent(
                Require(EventId.Create(postId)),
                Require(EventType.Create(PostProjector.PostAcceptedType)),
                Require(ActorId.Create(author)),
                new JsonValue.Object([.. members]))],
            ct).ConfigureAwait(false));
    }

    private static async Task AcceptAsync(
        InMemoryEventStore store, string threadRoot, string answerId, string acceptedBy, CancellationToken ct)
    {
        var aggregate = Require(AggregateId.Create(threadRoot));
        var history = Require(await store.ReadByAggregateAsync(aggregate, ct).ConfigureAwait(false));

        Require(await store.AppendAsync(
            aggregate,
            Require(AggregateVersion.From(history.Count)),
            [new DomainEvent(
                Require(EventId.Create($"{answerId}-acc-{history.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)}")),
                Require(EventType.Create(AcceptanceProjector.AnswerAcceptedType)),
                Require(ActorId.Create(acceptedBy)),
                new JsonValue.Object(
                [
                    new(AcceptanceProjector.ThreadRootField, new JsonValue.String(threadRoot)),
                    new(AcceptanceProjector.AnswerIdField, new JsonValue.String(answerId)),
                    new(AcceptanceProjector.AcceptedByField, new JsonValue.String(acceptedBy)),
                ]))],
            ct).ConfigureAwait(false));
    }

    /// <summary>An acceptance names the answer that now stands as the thread's answer.</summary>
    [Fact]
    public async Task AnAcceptanceRecordsTheThreadsAcceptedAnswer()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        await AcceptedPostAsync(store, "q1", Asker, "question", null, ct);
        await AcceptedPostAsync(store, "a1", Answerer, "answer", "q1", ct);
        await AcceptAsync(store, "q1", "a1", Asker, ct);

        var accepted = AcceptanceProjector.Fold(await LogAsync(store, ct));

        Assert.Equal("a1", Assert.Contains("q1", accepted));
    }

    /// <summary>
    /// A thread has at most one accepted answer, and the most recent acceptance is the one that
    /// stands. The history is the state, as everywhere else here: an asker who changes their mind
    /// appends rather than edits, and nothing has to be invalidated.
    /// </summary>
    [Fact]
    public async Task TheMostRecentAcceptanceStands()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        await AcceptedPostAsync(store, "q1", Asker, "question", null, ct);
        await AcceptedPostAsync(store, "a1", Answerer, "answer", "q1", ct);
        await AcceptedPostAsync(store, "a2", Other, "answer", "q1", ct);

        await AcceptAsync(store, "q1", "a1", Asker, ct);
        await AcceptAsync(store, "q1", "a2", Asker, ct);

        var accepted = AcceptanceProjector.Fold(await LogAsync(store, ct));

        Assert.Single(accepted);
        Assert.Equal("a2", accepted["q1"]);
    }

    /// <summary>
    /// Table 11's "≥ 5 accepted answers" counts for the <b>answer's author</b>, not for the asker
    /// who accepted it. The criterion is evidence that an agent's answers were useful; crediting the
    /// asker would make it evidence that an agent asks questions and resolves them.
    /// </summary>
    [Fact]
    public async Task Table11_AnAcceptedAnswerCreditsItsAuthorNotTheAcceptor()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        await AcceptedPostAsync(store, "q1", Asker, "question", null, ct);
        await AcceptedPostAsync(store, "a1", Answerer, "answer", "q1", ct);
        await AcceptAsync(store, "q1", "a1", Asker, ct);

        var standings = AgentStandingProjector.Fold(await LogAsync(store, ct));

        Assert.Equal(1, Require(AgentStandingProjector.PostureOf(standings, Answerer)).AcceptedAnswers);
        Assert.Equal(0, Require(AgentStandingProjector.PostureOf(standings, Asker)).AcceptedAnswers);
    }

    /// <summary>
    /// Re-accepting a different answer moves the credit rather than handing out a second one. The
    /// count is over threads whose accepted answer an agent wrote, so an asker changing their mind
    /// cannot mint standing for both candidates.
    /// </summary>
    [Fact]
    public async Task Table11_ChangingTheAcceptedAnswerMovesTheCredit()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        await AcceptedPostAsync(store, "q1", Asker, "question", null, ct);
        await AcceptedPostAsync(store, "a1", Answerer, "answer", "q1", ct);
        await AcceptedPostAsync(store, "a2", Other, "answer", "q1", ct);

        await AcceptAsync(store, "q1", "a1", Asker, ct);
        await AcceptAsync(store, "q1", "a2", Asker, ct);

        var standings = AgentStandingProjector.Fold(await LogAsync(store, ct));

        Assert.Equal(0, Require(AgentStandingProjector.PostureOf(standings, Answerer)).AcceptedAnswers);
        Assert.Equal(1, Require(AgentStandingProjector.PostureOf(standings, Other)).AcceptedAnswers);
    }

    /// <summary>R11.9: the projection rebuilds from zero to the identical state.</summary>
    [Fact]
    public async Task R11_9_TheProjectionRebuildsFromZeroToTheIdenticalState()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        await AcceptedPostAsync(store, "q1", Asker, "question", null, ct);
        await AcceptedPostAsync(store, "a1", Answerer, "answer", "q1", ct);
        await AcceptAsync(store, "q1", "a1", Asker, ct);

        var log = await LogAsync(store, ct);
        Assert.Equal(AcceptanceProjector.Fold(log), AcceptanceProjector.Fold(log));

        await AcceptedPostAsync(store, "a2", Other, "answer", "q1", ct);
        await AcceptAsync(store, "q1", "a2", Asker, ct);

        Assert.NotEqual(AcceptanceProjector.Fold(log), AcceptanceProjector.Fold(await LogAsync(store, ct)));
    }
}
