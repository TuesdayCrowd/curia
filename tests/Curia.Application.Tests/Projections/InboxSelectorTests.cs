using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Application.Tests.Projections;

/// <summary>
/// The inbox: open questions an agent could usefully answer.
///
/// <para><b>What makes this different from search is memory, not tags.</b> An agent has none
/// between sessions, so a Forum that hands back a question the agent already answered gets that
/// question answered twice — the agent cannot tell it has been here before. Search can be anonymous
/// because "what does the corpus say about X" is impersonal; this cannot, because "where can I
/// contribute" is only answerable relative to what this agent has already done. The personalisation
/// comes from the log's record of that, not from a stored preference that could be silently
/// wrong.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class InboxSelectorTests
{
    private const string Me = "https://agents.example/me";
    private const string Other = "https://agents.example/other";

    private static readonly DateTimeOffset Start = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    private static async Task<IReadOnlyList<AppendedEvent>> LogAsync(
        InMemoryEventStore store, CancellationToken ct) =>
        Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct).ConfigureAwait(false));

    private static string Canonical(PostKind kind, string author, string board, string? parent, string[] tags)
    {
        var members = ImmutableArray.CreateBuilder<KeyValuePair<string, JsonValue>>();
        members.Add(new("v", new JsonValue.Number(PostEnvelope.CurrentVersion)));
        members.Add(new("kind", new JsonValue.String(PostKinds.Wire(kind))));
        members.Add(new("author", new JsonValue.String(author)));
        members.Add(new("board", new JsonValue.String(board)));
        if (parent is not null) members.Add(new("parent", new JsonValue.String(parent)));
        if (PostKinds.RequiresTitle(kind)) members.Add(new("title", new JsonValue.String("A title")));
        members.Add(new("body", new JsonValue.String("a body")));
        members.Add(new("code_blocks", new JsonValue.Array([])));
        members.Add(new("refs", new JsonValue.Array([])));
        members.Add(new("tags", new JsonValue.Array([.. tags.Select(t => new JsonValue.String(t))])));
        members.Add(new("content_type", new JsonValue.String(PostEnvelope.RequiredContentType)));
        members.Add(new("created_at", new JsonValue.String(Start.ToString("o", CultureInfo.InvariantCulture))));
        members.Add(new("nonce", new JsonValue.String("00112233445566778899aabbccddeeff")));

        return System.Text.Encoding.UTF8.GetString(
            Require(CanonicalJson.CanonicalizeWithNfc(new JsonValue.Object(members.ToImmutable()))).Span);
    }

    private static async Task PostAsync(
        InMemoryEventStore store,
        string postId,
        PostKind kind,
        string author,
        CancellationToken ct,
        string? parent = null,
        string board = "general",
        string[]? tags = null)
    {
        var payload = new JsonValue.Object(
        [
            new("post_id", new JsonValue.String(postId)),
            new("canonical", new JsonValue.String(Canonical(kind, author, board, parent, tags ?? ["jcs"]))),
            new("signature", new JsonValue.String("sig")),
            new("digest", new JsonValue.String($"sha-256:{postId}")),
            new("author", new JsonValue.String(author)),
            new("board", new JsonValue.String(board)),
            new("kind", new JsonValue.String(PostKinds.Wire(kind))),
            .. parent is null
                ? Array.Empty<KeyValuePair<string, JsonValue>>()
                : [new KeyValuePair<string, JsonValue>("parent", new JsonValue.String(parent))],
        ]);

        Require(await store.AppendAsync(
            Require(AggregateId.Create(postId)),
            AggregateVersion.New,
            [new DomainEvent(
                Require(EventId.Create(postId)),
                Require(EventType.Create(PostProjector.PostAcceptedType)),
                Require(ActorId.Create(author)),
                payload)],
            ct).ConfigureAwait(false));
    }

    private static async Task AcceptAsync(
        InMemoryEventStore store, string root, string answerId, string acceptedBy, CancellationToken ct)
    {
        var aggregate = Require(AggregateId.Create(root));
        var history = Require(await store.ReadByAggregateAsync(aggregate, ct).ConfigureAwait(false));

        Require(await store.AppendAsync(
            aggregate,
            Require(AggregateVersion.From(history.Count)),
            [new DomainEvent(
                Require(EventId.Create($"{answerId}-acc")),
                Require(EventType.Create(AcceptanceProjector.AnswerAcceptedType)),
                Require(ActorId.Create(acceptedBy)),
                new JsonValue.Object(
                [
                    new(AcceptanceProjector.ThreadRootField, new JsonValue.String(root)),
                    new(AcceptanceProjector.AnswerIdField, new JsonValue.String(answerId)),
                    new(AcceptanceProjector.AcceptedByField, new JsonValue.String(acceptedBy)),
                ]))],
            ct).ConfigureAwait(false));
    }

    /// <summary>Another agent's unanswered question is exactly what an inbox is for.</summary>
    [Fact]
    public async Task AnUnansweredQuestionByAnotherAgentIsInTheInbox()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        await PostAsync(store, "q1", PostKind.Question, Other, ct);

        var inbox = InboxSelector.Select(await LogAsync(store, ct), Me);

        Assert.Equal("q1", Assert.Single(inbox.Open).PostId);
        Assert.Equal(0, inbox.ExcludedAsOwn);
        Assert.Equal(0, inbox.ExcludedAsAlreadyAnswered);
    }

    /// <summary>
    /// A resolved question is not open. Stage 10 made this computable; before acceptance existed
    /// there was no way to tell a thread that had been answered from one that had not.
    /// </summary>
    [Fact]
    public async Task AQuestionWithAnAcceptedAnswerIsNotOpen()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        await PostAsync(store, "q1", PostKind.Question, Other, ct);
        await PostAsync(store, "a1", PostKind.Answer, Other, ct, parent: "q1");
        await AcceptAsync(store, "q1", "a1", Other, ct);

        Assert.Empty(InboxSelector.Select(await LogAsync(store, ct), Me).Open);
    }

    /// <summary>
    /// An agent's own question is not a contribution opportunity. Excluded and <i>counted</i>, so an
    /// empty inbox can say which kind of empty it is.
    /// </summary>
    [Fact]
    public async Task MyOwnQuestionIsExcludedAndCounted()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        await PostAsync(store, "q1", PostKind.Question, Me, ct);

        var inbox = InboxSelector.Select(await LogAsync(store, ct), Me);

        Assert.Empty(inbox.Open);
        Assert.Equal(1, inbox.ExcludedAsOwn);
    }

    /// <summary>
    /// <b>The exclusion this endpoint exists for.</b> An agent has no memory between sessions, so a
    /// question it already answered would be re-read, re-reasoned and answered again on every poll.
    /// The Forum knows what this agent has contributed; the agent does not.
    /// </summary>
    [Fact]
    public async Task AQuestionIAlreadyAnsweredIsExcludedAndCounted()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        await PostAsync(store, "q1", PostKind.Question, Other, ct);
        await PostAsync(store, "a1", PostKind.Answer, Me, ct, parent: "q1");

        var inbox = InboxSelector.Select(await LogAsync(store, ct), Me);

        Assert.Empty(inbox.Open);
        Assert.Equal(1, inbox.ExcludedAsAlreadyAnswered);
    }

    /// <summary>
    /// A question someone else answered — but nobody accepted — stays open. An unaccepted answer is
    /// not a resolution, and an agent may have a better one.
    /// </summary>
    [Fact]
    public async Task AQuestionAnsweredByAnotherAgentButNotAcceptedStaysOpen()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        await PostAsync(store, "q1", PostKind.Question, Other, ct);
        await PostAsync(store, "a1", PostKind.Answer, Other, ct, parent: "q1");

        Assert.Single(InboxSelector.Select(await LogAsync(store, ct), Me).Open);
    }

    /// <summary>Only questions. An answer or a comment is not something to answer.</summary>
    [Fact]
    public async Task OnlyQuestionsAppear()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        await PostAsync(store, "q1", PostKind.Question, Other, ct);
        await PostAsync(store, "c1", PostKind.Comment, Other, ct, parent: "q1");

        Assert.Equal("q1", Assert.Single(InboxSelector.Select(await LogAsync(store, ct), Me).Open).PostId);
    }

    /// <summary>
    /// <c>Matching</c> carries every open question before the personal exclusions, which is what
    /// makes an empty inbox diagnosable: "nothing is open here" and "you have dealt with all of it"
    /// are different situations that imply different next actions, and both are an empty array.
    /// </summary>
    [Fact]
    public async Task TheUnexcludedSetIsReportedSoAnEmptyInboxCanSayWhy()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        await PostAsync(store, "q1", PostKind.Question, Me, ct);
        await PostAsync(store, "q2", PostKind.Question, Other, ct);
        await PostAsync(store, "a2", PostKind.Answer, Me, ct, parent: "q2");

        var inbox = InboxSelector.Select(await LogAsync(store, ct), Me);

        Assert.Empty(inbox.Open);
        Assert.Equal(2, inbox.Matching.Length);
        Assert.Equal(1, inbox.ExcludedAsOwn);
        Assert.Equal(1, inbox.ExcludedAsAlreadyAnswered);
    }

    /// <summary>Oldest first: the neglected question is the one the corpus most needs answered.</summary>
    [Fact]
    public async Task OpenQuestionsArriveOldestFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        await PostAsync(store, "q1", PostKind.Question, Other, ct);
        await PostAsync(store, "q2", PostKind.Question, Other, ct);
        await PostAsync(store, "q3", PostKind.Question, Other, ct);

        var inbox = InboxSelector.Select(await LogAsync(store, ct), Me);

        Assert.Equal((string[])["q1", "q2", "q3"], inbox.Open.Select(p => p.PostId).ToArray());
    }

    /// <summary>R11.9: the selection rebuilds from zero to the identical state.</summary>
    [Fact]
    public async Task R11_9_TheSelectionRebuildsFromZeroToTheIdenticalState()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        await PostAsync(store, "q1", PostKind.Question, Other, ct);
        await PostAsync(store, "q2", PostKind.Question, Me, ct);

        var log = await LogAsync(store, ct);
        Assert.Equal(InboxSelector.Select(log, Me).Open, InboxSelector.Select(log, Me).Open);

        await PostAsync(store, "q3", PostKind.Question, Other, ct);
        Assert.NotEqual(
            InboxSelector.Select(log, Me).Open,
            InboxSelector.Select(await LogAsync(store, ct), Me).Open);
    }
}
