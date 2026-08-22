using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Content;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Application.Tests.Projections;

/// <summary>
/// The searchable read model: the fields a lexical query matches against, mapped out of the
/// canonical bytes the log holds.
///
/// <para><b>Why this is a projection of its own</b> rather than three more fields on
/// <c>PostView</c>. Title, body and tags live <i>inside</i> the canonical envelope, and the serving
/// path deliberately treats <c>canonical</c> as an opaque string it hands to
/// <c>Datamarking.Render</c> — it has never needed the envelope's structure. Widening
/// <c>PostView</c> would put an envelope parse on every read path to serve one, so the parse and
/// its blast radius stay here.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class SearchProjectorTests
{
    private const string Author = "https://agents.example/alice";

    private static readonly DateTimeOffset Start = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    private static async Task<IReadOnlyList<AppendedEvent>> LogAsync(
        InMemoryEventStore store, CancellationToken ct) =>
        Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct).ConfigureAwait(false));

    /// <summary>A Table 9 envelope, canonicalized the way ingest canonicalizes one.</summary>
    private static string Canonical(string title, string body, string board, string[] tags)
    {
        var members = ImmutableArray.CreateBuilder<KeyValuePair<string, JsonValue>>();
        members.Add(new("v", new JsonValue.Number(PostEnvelope.CurrentVersion)));
        members.Add(new("kind", new JsonValue.String(PostKinds.Wire(PostKind.Question))));
        members.Add(new("author", new JsonValue.String(Author)));
        members.Add(new("board", new JsonValue.String(board)));
        members.Add(new("title", new JsonValue.String(title)));
        members.Add(new("body", new JsonValue.String(body)));
        members.Add(new("code_blocks", new JsonValue.Array([])));
        members.Add(new("refs", new JsonValue.Array([])));
        members.Add(new("tags", new JsonValue.Array([.. tags.Select(t => new JsonValue.String(t))])));
        members.Add(new("content_type", new JsonValue.String(PostEnvelope.RequiredContentType)));
        members.Add(new("created_at", new JsonValue.String(Start.ToString("o", CultureInfo.InvariantCulture))));
        members.Add(new("nonce", new JsonValue.String("00112233445566778899aabbccddeeff")));

        var canonical = Require(CanonicalJson.CanonicalizeWithNfc(new JsonValue.Object(members.ToImmutable())));
        return System.Text.Encoding.UTF8.GetString(canonical.Span);
    }

    private static async Task AcceptAsync(
        InMemoryEventStore store,
        string postId,
        CancellationToken ct,
        string title = "Member ordering in JCS",
        string body = "How does JCS order object members?",
        string board = "canonicalization",
        string[]? tags = null)
    {
        var payload = new JsonValue.Object(
        [
            new("post_id", new JsonValue.String(postId)),
            new("canonical", new JsonValue.String(Canonical(title, body, board, tags ?? ["jcs"]))),
            new("signature", new JsonValue.String("sig")),
            new("digest", new JsonValue.String($"sha-256:{postId}")),
            new("author", new JsonValue.String(Author)),
            new("board", new JsonValue.String(board)),
            new("kind", new JsonValue.String("question")),
        ]);

        Require(await store.AppendAsync(
            Require(AggregateId.Create(postId)),
            AggregateVersion.New,
            [new DomainEvent(
                Require(EventId.Create(postId)),
                Require(EventType.Create(PostProjector.PostAcceptedType)),
                Require(ActorId.Create(Author)),
                payload)],
            ct).ConfigureAwait(false));
    }

    private static async Task WithholdAsync(InMemoryEventStore store, string postId, CancellationToken ct)
    {
        var aggregate = Require(AggregateId.Create(postId));
        var history = Require(await store.ReadByAggregateAsync(aggregate, ct).ConfigureAwait(false));

        Require(await store.AppendAsync(
            aggregate,
            Require(AggregateVersion.From(history.Count)),
            [new DomainEvent(
                Require(EventId.Create($"{postId}-mod")),
                Require(EventType.Create(FlagProjector.ModerationAppliedType)),
                Require(ActorId.Create("https://agents.example/moderator")),
                new JsonValue.Object(
                [
                    new(FlagProjector.PostIdField, new JsonValue.String(postId)),
                    new(FlagProjector.ModeratorField, new JsonValue.String(ModeratorKinds.Wire(ModeratorKind.Human))),
                    new(FlagProjector.ActorIdField, new JsonValue.String("https://agents.example/moderator")),
                    new(FlagProjector.EffectField, new JsonValue.String(ModerationEffects.Wire(ModerationEffect.Withhold))),
                    new(FlagProjector.CategoryField, new JsonValue.String(FlagKinds.Wire(FlagKind.Injection))),
                    new(FlagProjector.RationaleField, new JsonValue.String("reviewed")),
                ]))],
            ct).ConfigureAwait(false));
    }

    /// <summary>The envelope's structure reaches the searchable view: title, body and tags.</summary>
    [Fact]
    public async Task TheEnvelopesTitleBodyAndTagsBecomeSearchable()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        await AcceptAsync(store, "01JPOST1", ct, tags: ["jcs", "rfc8785"]);

        var post = Assert.Single(SearchProjector.Fold(await LogAsync(store, ct)));

        Assert.Equal("Member ordering in JCS", post.Title);
        Assert.Equal("How does JCS order object members?", post.Body);
        Assert.Equal((string[])["jcs", "rfc8785"], post.Tags.ToArray());
        Assert.Equal(PostKind.Question, post.Kind);
        Assert.Equal("canonicalization", post.Board);
        Assert.Equal("sha-256:01JPOST1", post.Digest);
    }

    /// <summary>
    /// R9.7's ordering key comes from the event's own <c>seq</c>, which is the one total order the
    /// log offers and is immutable once assigned.
    /// </summary>
    [Fact]
    public async Task R9_7_TheSequenceComesFromTheEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        await AcceptAsync(store, "01JPOST1", ct);
        await AcceptAsync(store, "01JPOST2", ct);

        var posts = SearchProjector.Fold(await LogAsync(store, ct));

        Assert.Equal(2, posts.Length);
        Assert.True(posts[0].Sequence < posts[1].Sequence);
        Assert.Equal((string[])["01JPOST1", "01JPOST2"], posts.Select(p => p.PostId).ToArray());
    }

    /// <summary>
    /// <b>R10.36: a withheld post is not searchable.</b> A post that stays out of
    /// <c>GET /v1/posts/{id}</c> and surfaces in search is the withholding not having happened —
    /// and search is the likelier way anyone finds it. Filtered in the projection rather than at the
    /// route, so there is one place to get it right instead of one per caller.
    /// </summary>
    [Fact]
    public async Task R10_36_AWithheldPostIsNotSearchable()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        await AcceptAsync(store, "01JPOST1", ct);
        await AcceptAsync(store, "01JPOST2", ct);

        Assert.Equal(2, SearchProjector.Fold(await LogAsync(store, ct)).Length);

        await WithholdAsync(store, "01JPOST1", ct);

        var post = Assert.Single(SearchProjector.Fold(await LogAsync(store, ct)));
        Assert.Equal("01JPOST2", post.PostId);
    }

    /// <summary>
    /// A post whose canonical bytes do not parse is skipped rather than throwing. Nothing should be
    /// able to put such an event in the log — PERSIST only ever writes what VERIFY consumed — but a
    /// projection that threw would take down every read path over one bad row, and R11.9's replay
    /// would stop at it forever.
    /// </summary>
    [Fact]
    public async Task AnUnparseableEnvelopeIsSkippedNotThrown()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        Require(await store.AppendAsync(
            Require(AggregateId.Create("01JBAD")),
            AggregateVersion.New,
            [new DomainEvent(
                Require(EventId.Create("01JBAD")),
                Require(EventType.Create(PostProjector.PostAcceptedType)),
                Require(ActorId.Create(Author)),
                new JsonValue.Object(
                [
                    new("post_id", new JsonValue.String("01JBAD")),
                    new("canonical", new JsonValue.String("{ not json")),
                    new("signature", new JsonValue.String("sig")),
                    new("digest", new JsonValue.String("sha-256:bad")),
                    new("author", new JsonValue.String(Author)),
                    new("board", new JsonValue.String("b")),
                    new("kind", new JsonValue.String("question")),
                ]))],
            ct));

        await AcceptAsync(store, "01JPOST1", ct);

        var post = Assert.Single(SearchProjector.Fold(await LogAsync(store, ct)));
        Assert.Equal("01JPOST1", post.PostId);
    }

    /// <summary>R11.9: the projection rebuilds from zero to the identical state.</summary>
    [Fact]
    public async Task R11_9_TheProjectionRebuildsFromZeroToTheIdenticalState()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(Start));

        await AcceptAsync(store, "01JPOST1", ct);
        await AcceptAsync(store, "01JPOST2", ct, tags: ["nfc"]);
        await WithholdAsync(store, "01JPOST2", ct);

        var log = await LogAsync(store, ct);
        Assert.Equal(SearchProjector.Fold(log), SearchProjector.Fold(log));

        // The negative control: a fold of a longer log must differ, or the assertion above would
        // pass for a projection whose equality is vacuously true.
        await AcceptAsync(store, "01JPOST3", ct);
        Assert.NotEqual(SearchProjector.Fold(log), SearchProjector.Fold(await LogAsync(store, ct)));
    }
}
