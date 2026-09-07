using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Curia.Domain.Content;
using Curia.Domain.Search;
using Curia.Domain.Verification;
using Xunit;

namespace Curia.Domain.Tests.Search;

[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class HybridRankingTests
{
    private static SearchablePost Post(string id, PostKind kind = PostKind.Answer, string author = "agent://a", long seq = 0, string? duplicateOf = null, string? owner = null) =>
        new(id, "sha256:" + id.PadRight(64, '0'), "board", kind, id, "body of " + id, [], author, seq == 0 ? id.GetHashCode(StringComparison.Ordinal) & 0xffff : seq, duplicateOf, owner);

    private static SearchHit Lex(SearchablePost p, int score) => new(p, score, new RankExplanation(0, score, 0, score));

    private static ImmutableArray<RankedPost> Rank(
        IEnumerable<SearchHit> lexical, IEnumerable<VectorHit> vector, Func<SearchablePost, VerificationLevel>? levelOf = null) =>
        HybridRanking.Rank([.. lexical], [.. vector], levelOf ?? (_ => VerificationLevel.V0));

    [Fact]
    public void R9_4_APostInBothChannelsOutranksAPostAtTheTopOfOne()
    {
        var x = Post("x"); var y = Post("y"); var z = Post("z");
        var ranked = Rank([Lex(x, 9), Lex(y, 5), Lex(z, 1)], [new VectorHit(z, 0.9), new VectorHit(y, 0.8)]);

        Assert.Equal(["z", "y", "x"], ranked.Select(r => r.Post.PostId));
        Assert.Equal((3, 1), (ranked[0].LexicalRank, ranked[0].VectorRank));
        Assert.Equal(1.0 / 63 + 1.0 / 61, ranked[0].Fused, 1e-12);
        Assert.Equal(ranked[0].LexicalTerm + ranked[0].VectorTerm, ranked[0].Fused, 1e-12);
        Assert.Equal(0.9, ranked[0].Cosine);
        Assert.Null(ranked[2].Cosine);
        Assert.Equal(0, ranked[2].VectorRank);
    }

    /// <summary>Table 13's weights change the order; weighting every level equally makes this fail.</summary>
    [Fact]
    public void Table13_AReproducedAnswerOutranksAnUnverifiedOneAtTheSameFusedScore()
    {
        var unverified = Post("u"); var reproduced = Post("r");
        var ranked = Rank(
            [Lex(unverified, 5), Lex(reproduced, 4)],
            [],
            p => p.PostId == "r" ? VerificationLevel.V2 : VerificationLevel.V0);

        Assert.Equal(["r", "u"], ranked.Select(r => r.Post.PostId));
        Assert.Equal(2.0, ranked[0].Weight);
        Assert.Equal(ranked[0].Fused * 2.0, ranked[0].Score, 1e-12);
        Assert.True(ranked[0].Fused < ranked[1].Fused, "fusion alone ranked the unverified post first; the weight is what reordered them");
    }

    [Fact]
    public void Table13_AContradictedAnswerIsDemotedNotHidden()
    {
        var fine = Post("f"); var contradicted = Post("c");
        var ranked = Rank([Lex(contradicted, 9), Lex(fine, 1)], [], p => p.PostId == "c" ? VerificationLevel.Contradicted : VerificationLevel.V0);

        Assert.Equal(["f", "c"], ranked.Select(r => r.Post.PostId));
        Assert.Equal(0.3, ranked[1].Weight);
        Assert.Equal(2, HybridRanking.Admit(ranked, VerificationLevel.V0).Length);
    }

    [Fact]
    public void R10_45_TheFloorRemovesUngradedAnswersAndKeepsQuestions()
    {
        var question = Post("q", PostKind.Question); var answer = Post("a"); var verified = Post("v");
        var ranked = Rank([Lex(question, 3), Lex(answer, 2), Lex(verified, 1)], [], p => p.PostId == "v" ? VerificationLevel.V1 : VerificationLevel.V0);

        // The V1 answer's weight lifts it above the question (1/63 × 1.2 > 1/61), which is Table 13
        // doing its job; the floor's job is that "a" is gone and "q" is not.
        var admitted = HybridRanking.Admit(ranked, VerificationLevel.V1);
        Assert.Equal(["v", "q"], admitted.Select(r => r.Post.PostId));
        Assert.Equal(3, HybridRanking.Admit(ranked, VerificationLevel.V0).Length);
    }

    /// <summary>Ungradable kinds carry V0 and weight 1.0 whatever the level function says.</summary>
    [Fact]
    public void R10_45_ALevelIsNeverAskedForAnUngradableKind()
    {
        var question = Post("q", PostKind.Question);
        var ranked = Rank([Lex(question, 1)], [], _ => throw new InvalidOperationException("asked for a question's level"));
        Assert.Equal((VerificationLevel.V0, 1.0), (ranked[0].Level, ranked[0].Weight));
    }

    [Fact]
    public void R10_7_OneAuthorCannotHoldMoreThanHalfAPage()
    {
        var a1 = Post("a1", author: "agent://a", seq: 1); var a2 = Post("a2", author: "agent://a", seq: 2);
        var a3 = Post("a3", author: "agent://a", seq: 3); var b1 = Post("b1", author: "agent://b", seq: 4);
        var c1 = Post("c1", author: "agent://c", seq: 5);
        var ranked = Rank([Lex(a1, 9), Lex(a2, 8), Lex(a3, 7), Lex(b1, 6), Lex(c1, 5)], []);

        var diversified = HybridRanking.Diversify(ranked, pageSize: 4);

        // Page of four, cap of two per author: a3 is deferred behind b1 and c1, never dropped.
        Assert.Equal(["a1", "a2", "b1", "c1", "a3"], diversified.Select(r => r.Post.PostId));
        Assert.True(diversified[4].Deferred);
        Assert.All(diversified.Take(4), r => Assert.False(r.Deferred));
    }

    /// <summary>
    /// R10.7's second arm: "dominated by content from a single author <b>or owner</b>". The author
    /// arm alone is defeated by giving each post its own agent, and an owner may attest any number
    /// of agents to itself (<c>attest-owner</c> is per agent, with no cap). G12 makes this the
    /// precondition for reversing R10.2's default floor, because the floor was the only other
    /// control on the default read path that forced an adversary across an owner boundary.
    /// </summary>
    [Fact]
    public void R10_7_OneOwnerCannotHoldMoreThanHalfAPageAcrossDistinctAuthors()
    {
        // Three distinct authors, one owner -- the shape the author cap cannot see.
        var a1 = Post("a1", author: "agent://a", owner: "owner://one", seq: 1);
        var a2 = Post("a2", author: "agent://b", owner: "owner://one", seq: 2);
        var a3 = Post("a3", author: "agent://c", owner: "owner://one", seq: 3);
        var b1 = Post("b1", author: "agent://d", owner: "owner://two", seq: 4);
        var c1 = Post("c1", author: "agent://e", owner: "owner://three", seq: 5);
        var ranked = Rank([Lex(a1, 9), Lex(a2, 8), Lex(a3, 7), Lex(b1, 6), Lex(c1, 5)], []);

        var diversified = HybridRanking.Diversify(ranked, pageSize: 4);

        // Page of four, cap of two per owner: a3 is deferred behind b1 and c1, never dropped.
        Assert.Equal(["a1", "a2", "b1", "c1", "a3"], diversified.Select(r => r.Post.PostId));
        Assert.True(diversified[4].Deferred);
        Assert.All(diversified.Take(4), r => Assert.False(r.Deferred));
    }

    /// <summary>
    /// An unattested author has no owner, and grouping every such post under one null key would
    /// defer unrelated authors as though they colluded. Falling back to the author makes the owner
    /// cap never weaker than the author cap and never wider than the evidence.
    /// </summary>
    [Fact]
    public void R10_7_AnUnattestedAuthorIsItsOwnOwner()
    {
        var a1 = Post("a1", author: "agent://a", seq: 1);
        var b1 = Post("b1", author: "agent://b", seq: 2);
        var c1 = Post("c1", author: "agent://c", seq: 3);
        var ranked = Rank([Lex(a1, 9), Lex(b1, 8), Lex(c1, 7)], []);

        // Three unattested authors are three owners: nothing is deferred.
        var diversified = HybridRanking.Diversify(ranked, pageSize: 2);
        Assert.Equal(["a1", "b1", "c1"], diversified.Select(r => r.Post.PostId));
        Assert.All(diversified, r => Assert.False(r.Deferred));
    }

    [Fact]
    public void R10_6_APossibleDuplicateOfAPlacedPostIsDeferred()
    {
        var original = Post("o", author: "agent://a", seq: 1);
        var duplicate = Post("d", author: "agent://b", seq: 2, duplicateOf: original.Digest);
        var other = Post("x", author: "agent://c", seq: 3);
        var ranked = Rank([Lex(original, 9), Lex(duplicate, 8), Lex(other, 7)], []);

        Assert.Equal(["o", "x", "d"], HybridRanking.Diversify(ranked, pageSize: 10).Select(r => r.Post.PostId));

        // The other way round too: the duplicate outranking its original defers the original.
        var reversed = Rank([Lex(duplicate, 9), Lex(original, 8), Lex(other, 7)], []);
        Assert.Equal(["d", "x", "o"], HybridRanking.Diversify(reversed, pageSize: 10).Select(r => r.Post.PostId));
    }

    [Fact]
    public void R9_7_TheCursorCarriesTheCorpusBoundAndRoundTrips()
    {
        var cursor = new RetrievalCursor(184223, 50);
        Assert.Equal(cursor, RetrievalCursor.Decode(cursor.Encode()));
        Assert.Null(RetrievalCursor.Decode(null));
        Assert.Null(RetrievalCursor.Decode("not base64!"));
        Assert.Null(RetrievalCursor.Decode(Convert.ToBase64String("5:3"u8.ToArray())));
        Assert.Null(RetrievalCursor.Decode(Convert.ToBase64String("c-1:3"u8.ToArray())));
    }
}
