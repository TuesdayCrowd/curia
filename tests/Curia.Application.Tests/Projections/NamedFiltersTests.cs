using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Curia.Application.Credentials;
using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Content;
using Curia.Domain.Credentials;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;
using Xunit;
using Curia.Tests.Shared;

namespace Curia.Application.Tests.Projections;

/// <summary>
/// R9.55 and R8.88 (errata G62), NMN1 to NMN3: <see cref="NamedFilters"/> over a log built the way the projectors read one. A filter is unmatched when no post of a
/// discussion kind that moderation serves bears it, judged ordinally and on its own; the two shapes of the predicate (the projection the search and inbox routes fold and the
/// corpus the near-duplicate check folds) answer alike.
///
/// <para><b>Oracles.</b> The table of expected answers is written here, by hand, from what each stored post is; no row takes its answer from <see cref="NamedFilters"/>. The
/// accepting rows (a board with a served post, a full agent identifier) are the first of each group, so a rule that lists everything is red on them and not only on the
/// refusals. Characters outside the ASCII range are built at run time, never written as escapes.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class NamedFiltersTests
{
    private const string AuthorRust = "https://agents.example/rustacean";
    private const string AuthorMeta = "https://agents.example/moderated";
    private const string AuthorChat = "https://agents.example/chatty";
    private const string AuthorVotes = "https://agents.example/voter";
    private const string AuthorCafe = "https://agents.example/barista";

    private static readonly DateTimeOffset Start = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private static readonly string Composed = "caf" + char.ConvertFromUtf32(0xE9);
    private static readonly string Decomposed = "cafe" + char.ConvertFromUtf32(0x301);

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    private static string Canonical(string kind, string board, string author, string? parent)
    {
        var members = ImmutableArray.CreateBuilder<KeyValuePair<string, JsonValue>>();
        members.Add(new("v", new JsonValue.Number(PostEnvelope.CurrentVersion)));
        members.Add(new("kind", new JsonValue.String(kind)));
        members.Add(new("author", new JsonValue.String(author)));
        members.Add(new("board", new JsonValue.String(board)));
        if (parent is not null) members.Add(new("parent", new JsonValue.String(parent)));
        if (kind == "question") members.Add(new("title", new JsonValue.String("A title " + board)));
        members.Add(new("body", new JsonValue.String("A body on " + board)));
        members.Add(new("code_blocks", new JsonValue.Array([])));
        members.Add(new("refs", new JsonValue.Array([])));
        members.Add(new("tags", new JsonValue.Array([new JsonValue.String("t")])));
        members.Add(new("content_type", new JsonValue.String(PostEnvelope.RequiredContentType)));
        members.Add(new("created_at", new JsonValue.String(Start.ToString("o", CultureInfo.InvariantCulture))));
        members.Add(new("nonce", new JsonValue.String("00112233445566778899aabbccddeeff")));
        var canonical = Require(CanonicalJson.CanonicalizeWithNfc(new JsonValue.Object(members.ToImmutable())));
        return System.Text.Encoding.UTF8.GetString(canonical.Span);
    }

    private static async Task AcceptAsync(InMemoryEventStore store, string postId, string kind, string board, string author, string? parent, CancellationToken ct)
    {
        var payload = new JsonValue.Object(
        [
            new("post_id", new JsonValue.String(postId)),
            new("canonical", new JsonValue.String(Canonical(kind, board, author, parent))),
            new("signature", new JsonValue.String("sig")),
            new("digest", new JsonValue.String($"sha-256:{postId}")),
            new("author", new JsonValue.String(author)),
            new("board", new JsonValue.String(board)),
            new("kind", new JsonValue.String(kind)),
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

    /// <summary>
    /// The corpus: board <c>rust</c> (one served question, by AuthorRust); <c>meta</c> (one question, withheld, by AuthorMeta); <c>chat</c> (a withheld question and a served
    /// comment by AuthorChat); <c>votes</c> (a vote only, by AuthorVotes); the composed spelling of <c>cafe</c> (one served question, by AuthorCafe).
    /// </summary>
    private static async Task<(ImmutableArray<PostView> Posts, Func<string, bool> Servable, IReadOnlyList<AppendedEvent> Log)> CorpusAsync(CancellationToken ct)
    {
        var store = new InMemoryEventStore(new ManualTimeProvider(Start), new PermissiveLifecycleAppendGate());
        await AcceptAsync(store, "01JRUST1", "question", "rust", AuthorRust, null, ct).ConfigureAwait(false);
        await AcceptAsync(store, "01JMETA1", "question", "meta", AuthorMeta, null, ct).ConfigureAwait(false);
        await WithholdAsync(store, "01JMETA1", ct).ConfigureAwait(false);
        await AcceptAsync(store, "01JCHAT1", "question", "chat", AuthorChat, null, ct).ConfigureAwait(false);
        await WithholdAsync(store, "01JCHAT1", ct).ConfigureAwait(false);
        await AcceptAsync(store, "01JCHAT2", "comment", "chat", AuthorChat, "01JCHAT1", ct).ConfigureAwait(false);
        await AcceptAsync(store, "01JVOTE1", "vote", "votes", AuthorVotes, null, ct).ConfigureAwait(false);
        await AcceptAsync(store, "01JCAFE1", "question", Composed, AuthorCafe, null, ct).ConfigureAwait(false);

        var log = Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct).ConfigureAwait(false));
        var moderation = FlagProjector.Fold(log);
        return (PostProjector.Fold(log), id => !moderation.TryGetValue(id, out var state) || state.MayServe, log);
    }

    /// <summary>The boards a row names and whether a served post is on each, by hand: what the corpus above holds.</summary>
    public static TheoryData<string, bool> BoardRows()
    {
        var data = new TheoryData<string, bool>
        {
            { "rust", true },
            { "chat", true },
            { Composed, true },
            { "RUST", false },
            { "Rust", false },
            { Decomposed, false },
            { "meta", false },
            { "votes", false },
            { "nowhere", false },
            { string.Empty, false },
            { "rust ", false },
            { "ru" + char.ConvertFromUtf32(0), false },
            { "ru" + ((char)0xD800), false },
            { new string('r', 70), false },
        };
        return data;
    }

    /// <summary>
    /// NMN1 and NMN2, boards. Ordinal (a case variant and a decomposed spelling are unmatched); a board whose only discussion post is withheld is unmatched; a board that holds one
    /// served comment is matched; a board that holds only a vote is unmatched; a value no board name can hold (empty, U+0000, a lone surrogate, over the cap) is listed and never throws.
    /// The accepting rows come first in the table. Predicted red on the seam: nothing is listed, so every row of <c>false</c> reads as matched.
    /// </summary>
    [Theory]
    [MemberData(nameof(BoardRows))]
    public async Task R9_55_NMN1_ABoardIsUnmatchedExactlyWhenNoServedPostBearsIt(string board, bool served)
    {
        var (posts, servable, _) = await CorpusAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        var unmatched = NamedFilters.Unmatched(posts, servable, board, null);

        Assert.True(
            unmatched.SequenceEqual(served ? [] : (string[])["board"]),
            $"board {DisplayLiteral.Of(board)}: Expected: {(served ? "[]" : "[board]")} Actual: [{string.Join(",", unmatched)}]");
    }

    /// <summary>NMN1, authors: the full identifier of a served post's author is matched; a slug, a case variant and a withheld-only author are not.</summary>
    [Theory]
    [InlineData(AuthorRust, true)]
    [InlineData(AuthorChat, true)]
    [InlineData(AuthorCafe, true)]
    [InlineData("rustacean", false)]
    [InlineData("https://agents.example/RUSTACEAN", false)]
    [InlineData(AuthorMeta, false)]
    [InlineData(AuthorVotes, false)]
    [InlineData("", false)]
    public async Task R9_55_NMN1_AnAuthorIsUnmatchedExactlyWhenNoServedPostBearsIt(string author, bool served)
    {
        var (posts, servable, _) = await CorpusAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        var unmatched = NamedFilters.Unmatched(posts, servable, null, author);

        Assert.True(
            unmatched.SequenceEqual(served ? [] : (string[])["author"]),
            $"author {DisplayLiteral.Of(author)}: Expected: {(served ? "[]" : "[author]")} Actual: [{string.Join(",", unmatched)}]");
    }

    /// <summary>NMN1, order and absence: both filters are listed in the order board then author; a filter the request did not name (null) is never listed, whatever the other says.</summary>
    [Fact]
    public async Task R9_55_NMN1_BothAreListedBoardThenAuthorAndAnUnnamedFilterIsNeverListed()
    {
        var (posts, servable, _) = await CorpusAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        var both = NamedFilters.Unmatched(posts, servable, "RUST", "rustacean");
        var onlyBoardNamed = NamedFilters.Unmatched(posts, servable, "RUST", null);
        var onlyAuthorNamed = NamedFilters.Unmatched(posts, servable, null, "rustacean");
        var neither = NamedFilters.Unmatched(posts, servable, null, null);
        var matchedBoardBadAuthor = NamedFilters.Unmatched(posts, servable, "rust", "rustacean");

        Assert.Equal(
            "[board,author] | [board] | [author] | [] | [author]",
            string.Join(" | ", new[] { both, onlyBoardNamed, onlyAuthorNamed, neither, matchedBoardBadAuthor }.Select(u => $"[{string.Join(",", u)}]")));
    }

    /// <summary>
    /// NMN2, the withheld semantics on their own, with the controls beside them: the withheld-only board is unmatched where its twin with a served post is matched, so the rule
    /// is moderation's servability and not the board's existence. Predicted red on the seam: the withheld-only board is matched.
    /// </summary>
    [Fact]
    public async Task R9_55_NMN2_AWithheldOnlyBoardIsUnmatchedWhereABoardWithAServedCommentIsMatched()
    {
        var (posts, servable, _) = await CorpusAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        var comment = NamedFilters.Unmatched(posts, servable, "chat", null);
        var withheld = NamedFilters.Unmatched(posts, servable, "meta", null);
        var votes = NamedFilters.Unmatched(posts, servable, "votes", null);

        Assert.Equal("chat [] | meta [board] | votes [board]", $"chat [{string.Join(",", comment)}] | meta [{string.Join(",", withheld)}] | votes [{string.Join(",", votes)}]");
    }

    /// <summary>
    /// NMN3. The two shapes of the predicate (over <see cref="PostProjector"/>'s projection and over <see cref="SearchProjector"/>'s corpus, ruling 18), both folded from one log, answer
    /// alike for every board row, and agree with <see cref="NamedFilters.Unmatched"/>. The rows are asserted to hold both answers before the agreement is compared, so a pair of
    /// functions that both answer one constant is not an agreement (NOT REACHED otherwise). Predicted red on the seam: the vacuity guard, which finds only <c>true</c>.
    /// </summary>
    [Fact]
    public async Task R9_55_NMN3_TheTwoShapesOfTheOnePredicateAnswerAlike()
    {
        var (posts, servable, log) = await CorpusAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        var corpus = SearchProjector.Fold(log);
        var boards = new[] { "rust", "chat", Composed, "RUST", Decomposed, "meta", "votes", "nowhere", string.Empty };

        var projectionAnswers = boards.Select(b => NamedFilters.BoardHasServedPost(posts, servable, b)).ToArray();
        var corpusAnswers = boards.Select(b => NamedFilters.BoardHasServedPost(corpus, b)).ToArray();
        var unmatchedAnswers = boards.Select(b => !NamedFilters.Unmatched(posts, servable, b, null).Contains("board")).ToArray();

        Assert.True(
            projectionAnswers.Contains(true) && projectionAnswers.Contains(false),
            $"NOT REACHED: the predicate answered {string.Join(",", projectionAnswers)} for the rows; both answers must be reached before the shapes are compared");
        Assert.Equal(string.Join(",", projectionAnswers), string.Join(",", corpusAnswers));
        Assert.Equal(string.Join(",", projectionAnswers), string.Join(",", unmatchedAnswers));
    }
}
