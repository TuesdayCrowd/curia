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
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Application.Tests.Projections;

/// <summary>
/// R9.58 (errata G63), LOB1 and LOB2: <see cref="NamedFilters.ServedBoards"/> lists each board a post of a discussion kind that moderation serves is on, with the number of such
/// posts, the number of distinct authors and the greatest stored instant, most posts first and then by name in ordinal order; it is total over any text; and it is the one predicate
/// the filters and <c>board_new</c> already use (<see cref="NamedFilters.BoardHasServedPost(ImmutableArray{PostView}, Func{string, bool}, string)"/> over the projection and over the corpus).
///
/// <para><b>Oracles.</b> LOB1's table is written by hand from what each post is: nothing is read from <c>ServedBoards</c> to decide what it should list, and the accepting rows (a board
/// with posts, its counts) are asserted first so that a function that lists nothing is red on them and not only on the refusals. The one comparison LOB2 makes is between the three
/// shapes of the predicate over logs a seeded generator builds, each class of board (listed, withheld-only, votes-only) asserted reached.
/// Predicted red on the seam: LOB1's listing is empty (<c>0 rows</c> where there are seven), and LOB2's agreement holds only for the logs that list nothing (the generator's <c>NOT REACHED</c>
/// guard is green, the comparison is red for every log with a listed board).</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ServedBoardsTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private static readonly string Composed = "caf" + char.ConvertFromUtf32(0xE9);
    private static readonly string Decomposed = "cafe" + char.ConvertFromUtf32(0x301);

    private static ServerTimestamp At(int minutes) => ServerTimestamp.At(Start.AddMinutes(minutes));

    private static PostView View(string id, string kind, string board, string author, int minute) =>
        new(id, string.Empty, "sig", "sha256:" + id, author, board, kind, null, At(minute), []);

    private static string Row(BoardRow r) => string.Create(CultureInfo.InvariantCulture, $"{DisplayLiteral.Of(r.Board)} {r.Posts} posts {r.Authors} authors latest {r.LatestPostAt}");

    private static string Row(string board, int posts, int authors, int minute) =>
        string.Create(CultureInfo.InvariantCulture, $"{DisplayLiteral.Of(board)} {posts} posts {authors} authors latest {At(minute)}");

    /// <summary>
    /// The corpus: <c>rust</c> (a question by A1 at +1, an answer by A2 at +5, a comment by A1 at +3: three posts, two authors, latest +5); <c>chat</c> (a withheld question and a served comment: one);
    /// <c>meta</c> (a withheld question only: none); <c>votes</c> (a vote and a verification only: none); <c>Rust</c> (a case variant, one); the composed and the decomposed <c>café</c> (one each, two rows);
    /// <c>Meta</c> (one: a name stored before the grammar, served as stored); a name holding a line feed (one); and a finding and a revision that count as the posts they are.
    /// </summary>
    private static (ImmutableArray<PostView> Posts, Func<string, bool> Servable) Corpus()
    {
        var posts = ImmutableArray.Create(
            View("P01", "question", "rust", "A1", 1),
            View("P02", "answer", "rust", "A2", 5),
            View("P03", "comment", "rust", "A1", 3),
            View("P04", "question", "chat", "A3", 2),
            View("P05", "comment", "chat", "A4", 9),
            View("P06", "question", "meta", "A5", 4),
            View("P07", "vote", "votes", "A6", 6),
            View("P08", "verification", "votes", "A6", 7),
            View("P09", "question", "Rust", "A7", 8),
            View("P10", "question", Composed, "A8", 10),
            View("P11", "question", Decomposed, "A8", 11),
            View("P12", "question", "Meta", "A9", 12),
            View("P13", "finding", "a" + char.ConvertFromUtf32(10) + "b", "A10", 13),
            View("P14", "question", "rev", "A11", 14),
            View("P15", "revision", "rev", "A11", 15));
        var withheld = new HashSet<string> { "P04", "P06" };
        return (posts, id => !withheld.Contains(id));
    }

    /// <summary>The rows the corpus lists, by hand: three-post <c>rust</c> first, then the two-post <c>rev</c> (a question and its revision), then the one-post boards by ordinal name.</summary>
    private static string[] ExpectedRows() =>
    [
        Row("rust", 3, 2, 5),
        Row("rev", 2, 1, 15),
        Row("Meta", 1, 1, 12),
        Row("Rust", 1, 1, 8),
        Row("a" + char.ConvertFromUtf32(10) + "b", 1, 1, 13),
        Row(Decomposed, 1, 1, 11),
        Row(Composed, 1, 1, 10),
        Row("chat", 1, 1, 9),
    ];

    /// <summary>
    /// LOB1, the accepting half first: the listing holds eight rows with the counts, the distinct authors and the greatest instant, in the order of R9.58 (posts descending, then ordinal name; a case
    /// variant and a decomposed spelling are rows of their own; a name stored as <c>Meta</c> and one holding a line feed are served as stored; a revision and a finding count as the posts they are).
    /// Predicted red on the seam: no rows.
    /// </summary>
    [Fact]
    public void R9_58_LOB1_TheListingHoldsEachBoardWithAServedDiscussionPostWithItsCountsInOrder()
    {
        var (posts, servable) = Corpus();

        var rows = NamedFilters.ServedBoards(posts, servable).Select(Row).ToArray();

        Assert.True(
            rows.SequenceEqual(ExpectedRows()),
            $"Expected {ExpectedRows().Length} rows:{Environment.NewLine}{string.Join(Environment.NewLine, ExpectedRows())}{Environment.NewLine}Actual {rows.Length} rows:{Environment.NewLine}{string.Join(Environment.NewLine, rows)}");
    }

    /// <summary>LOB1, the refusing half with its control in the same assertion: a withheld-only board and a votes-only board are not listed where <c>chat</c>, which holds one served comment beside a withheld question, is.</summary>
    [Fact]
    public void R9_58_LOB1_AWithheldOnlyBoardAndAVotesOnlyBoardAreNotListedWhereABoardWithAServedCommentIs()
    {
        var (posts, servable) = Corpus();

        var names = NamedFilters.ServedBoards(posts, servable).Select(r => r.Board).ToArray();

        Assert.Equal("chat listed; meta not listed; votes not listed", $"chat {(names.Contains("chat") ? "listed" : "not listed")}; meta {(names.Contains("meta") ? "listed" : "not listed")}; votes {(names.Contains("votes") ? "listed" : "not listed")}");
    }

    /// <summary>
    /// LOB1, total over any text: a stored board that is empty, holds U+0000, a lone surrogate, or is longer than 64 is listed as stored and never throws (the projection reads back what the log
    /// holds and a log may hold a name written before a rule). The control row is a plain name beside them; predicted red on the seam: no rows, so none of the four.
    /// </summary>
    [Fact]
    public void R9_58_LOB1_ABoardTheGrammarWouldRefuseIsListedAsStoredAndNeverThrows()
    {
        var odd = new[] { string.Empty, "nul" + char.ConvertFromUtf32(0) + "x", "sur" + ((char)0xD800) + "x", new string('z', 70) };
        var posts = odd.Select((b, i) => View("Q" + i, "question", b, "AX", i)).Append(View("Q9", "question", "plain", "AX", 9)).ToImmutableArray();

        var listed = NamedFilters.ServedBoards(posts, _ => true).Select(r => r.Board).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(
            string.Join(" | ", odd.Append("plain").Order(StringComparer.Ordinal).Select(DisplayLiteral.Of)),
            string.Join(" | ", listed.Select(DisplayLiteral.Of)));
    }

    // ---- LOB2 ----

    private static T Require<T>(Result<T> result) => result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    private static string Canonical(string kind, string board, string author, string? parent)
    {
        var members = ImmutableArray.CreateBuilder<KeyValuePair<string, JsonValue>>();
        members.Add(new("v", new JsonValue.Number(PostEnvelope.CurrentVersion)));
        members.Add(new("kind", new JsonValue.String(kind)));
        members.Add(new("author", new JsonValue.String(author)));
        members.Add(new("board", new JsonValue.String(board)));
        // A comment names a parent and a question or a finding a title (Table 9): a canonical form without them is no post the corpus can read, so the oracle would disagree with a fixture and not with the code.
        if (parent is not null || kind == "comment") members.Add(new("parent", new JsonValue.String(parent ?? "01JLOB2ROOT")));
        if (kind is "question" or "finding") members.Add(new("title", new JsonValue.String("A title " + board)));
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
            [new DomainEvent(Require(EventId.Create(postId)), Require(EventType.Create(PostProjector.PostAcceptedType)), Require(ActorId.Create(author)), payload)],
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
    /// LOB2. One predicate in three shapes, over a hundred logs a seeded generator builds (a pool of six board spellings, each one a canonical form can hold; posts of a discussion kind or a vote; each withheld or not). For
    /// every spelling in the pool the three answers are the same: listed by <see cref="NamedFilters.ServedBoards"/>, <c>BoardHasServedPost</c> over <see cref="PostProjector"/>'s projection, and
    /// <c>BoardHasServedPost</c> over <see cref="SearchProjector"/>'s corpus. The classes (a board listed, a board whose only discussion posts are withheld, a board with only votes) are each asserted
    /// reached before the agreement is read, so a comparison over logs that never reach them is not an agreement. Predicted red on the seam: the first log with a listed board.
    /// </summary>
    [Fact]
    public async Task R9_58_LOB2_TheListingTheProjectionAndTheCorpusAgreeOnEveryBoardOverRandomLogs()
    {
        // Two pools (Amendment 2, ruling 28). Pool A: spellings a signed canonical form can hold (NFC), over all three shapes. Pool B: pool A and the decomposed spelling, over the listing and
        // BoardHasServedPost over PostView only, because a SearchablePost is built from the canonical form, which is NFC, so the corpus shape cannot be given a decomposed board. Whether a stored
        // payload can differ from its canonical form is not read here: LOB45 asks the real store.
        string[] poolA = ["rust", "Rust", "chat", Composed, "Chat", "meta"];
        string[] poolB = [.. poolA, Decomposed];
        var a = await AgreeOverAsync(poolA, includeCorpus: true).ConfigureAwait(true);
        var b = await AgreeOverAsync(poolB, includeCorpus: false).ConfigureAwait(true);

        var missing = a.Reached.Concat(b.Reached).Where(r => r.Value == 0).Select(r => r.Key).Distinct().ToArray();
        Assert.True(missing.Length == 0, "NOT REACHED: " + string.Join(", ", missing));
        Assert.True(a.Disagreements.Count == 0, $"pool A (three shapes): {a.Disagreements.Count} disagreements; first: {a.Disagreements.FirstOrDefault()}");
        Assert.True(b.Disagreements.Count == 0, $"pool B (the listing and PostView, with the decomposed spelling): {b.Disagreements.Count} disagreements; first: {b.Disagreements.FirstOrDefault()}");
    }

    private static async Task<(Dictionary<string, int> Reached, List<string> Disagreements)> AgreeOverAsync(string[] pool, bool includeCorpus)
    {
        var ct = TestContext.Current.CancellationToken;
        string[] kinds = ["question", "comment", "vote", "finding"];
        var reached = new Dictionary<string, int> { ["listed"] = 0, ["withheld-only"] = 0, ["votes-only"] = 0 };
        var disagreements = new List<string>();

        for (var seed = 0; seed < 100; seed++)
        {
            var state = (uint)(seed * 2654435761u + 12345u);
            int Next(int max)
            {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                return (int)(state % (uint)max);
            }

            var store = new InMemoryEventStore(new ManualTimeProvider(Start), new PermissiveLifecycleAppendGate());
            var count = 1 + Next(8);
            var perBoard = pool.ToDictionary(b => b, _ => (Discussion: 0, Served: 0, Votes: 0), StringComparer.Ordinal);
            for (var i = 0; i < count; i++)
            {
                var board = pool[Next(pool.Length)];
                var kind = kinds[Next(kinds.Length)];
                var id = $"01JLOB2S{seed:D3}P{i}";
                await AcceptAsync(store, id, kind, board, $"https://agents.example/a{Next(3)}", null, ct).ConfigureAwait(true);
                var withheld = Next(3) == 0;
                if (withheld) await WithholdAsync(store, id, ct).ConfigureAwait(true);

                var (d, s, v) = perBoard[board];
                perBoard[board] = kind == "vote" ? (d, s, v + 1) : (d + 1, s + (withheld ? 0 : 1), v);
            }

            var log = Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct).ConfigureAwait(true));
            var moderation = FlagProjector.Fold(log);
            bool Servable(string id) => !moderation.TryGetValue(id, out var state) || state.MayServe;
            var posts = PostProjector.Fold(log);
            var corpus = SearchProjector.Fold(log);
            var listed = NamedFilters.ServedBoards(posts, Servable).Select(r => r.Board).ToHashSet(StringComparer.Ordinal);

            foreach (var (board, (discussion, served, votes)) in perBoard)
            {
                if (served > 0) reached["listed"]++;
                else if (discussion > 0) reached["withheld-only"]++;
                else if (votes > 0) reached["votes-only"]++;

                var shapes = (Listed: listed.Contains(board), Projection: NamedFilters.BoardHasServedPost(posts, Servable, board), Corpus: !includeCorpus || NamedFilters.BoardHasServedPost(corpus, board));
                var corpusAgrees = !includeCorpus || shapes.Corpus == shapes.Projection;
                if (shapes.Listed != shapes.Projection || !corpusAgrees || shapes.Projection != served > 0)
                    disagreements.Add($"seed {seed} board {DisplayLiteral.Of(board)}: oracle {served > 0}, listed {shapes.Listed}, projection {shapes.Projection}, corpus {(includeCorpus ? shapes.Corpus.ToString() : "not asked")}");
            }
        }

        return (reached, disagreements);
    }
}
