using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Curia.Domain.Primitives;
using Curia.Domain.Search;
using Xunit;

namespace Curia.Domain.Tests.Search;

[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class HashedNGramEmbeddingTests
{
    private static ImmutableArray<float> Embed(string text) =>
        HashedNGramEmbedding.Embed(text).Match(v => v, e => throw new InvalidOperationException(e.Type));

    [Fact]
    public void R9_5_TheModelIsNamedVersionedAndFixedInDimension()
    {
        Assert.Equal("hashed-ngram@1", HashedNGramEmbedding.Model.Id);
        Assert.Equal(256, HashedNGramEmbedding.Model.Dimensions);
        Assert.Equal(256, Embed("a postgres connection reset").Length);
    }

    [Fact]
    public void TheVectorIsUnitLengthAndDeterministic()
    {
        var a = Embed("ECONNRESET talking to postgres on port 5432");
        var b = Embed("ECONNRESET talking to postgres on port 5432");

        Assert.Equal(a, b);
        Assert.Equal(1.0, HashedNGramEmbedding.Cosine(a, a), 1e-6);
    }

    /// <summary>
    /// Stored vectors must mean the same thing tomorrow: a change to any constant here is a new
    /// model version (R9.5, R11.10), and this digest is what makes an accidental one fail.
    /// </summary>
    [Fact]
    public void R9_5_AKnownTextEmbedsToAKnownVector()
    {
        var vector = Embed("Why does my DPoP proof fail with iat outside the window?");
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector.ToArray(), 0, bytes, 0, bytes.Length);

        Assert.Equal(KnownDigest, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    /// <summary>Pinned from the first implementation; see the test above for why it is pinned at all.</summary>
    private const string KnownDigest = "3ce5bfc740df8ededd0e7b7b0bbf93da2731fe0de0e57a67a31714374aaac4e8";

    [Fact]
    public void SharedVocabularyIsCloserThanUnrelatedText()
    {
        var question = Embed("npgsql throws ECONNRESET after the pooler idles the connection");
        var paraphrase = Embed("connection reset (ECONNRESET) from npgsql once the pool has idled");
        var unrelated = Embed("rotate an ed25519 signing key without breaking published heads");

        Assert.True(
            HashedNGramEmbedding.Cosine(question, paraphrase) > HashedNGramEmbedding.Cosine(question, unrelated),
            "shared words and trigrams must pull texts together");
        Assert.True(HashedNGramEmbedding.Cosine(question, unrelated) < 0.3);
    }

    [Fact]
    public void FoldingIsAnalysisOnADerivedCopy()
    {
        // Case and compatibility forms fold; the vectors agree exactly.
        Assert.Equal(Embed("Postgres ECONNRESET"), Embed("postgres econnreset"));
        Assert.Equal(Embed("ﬁle"), Embed("file"));
    }

    [Fact]
    public void TextWithNoFeaturesHasNoEmbedding()
    {
        var result = HashedNGramEmbedding.Embed("... !!! ---");
        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/embedding/no-features", error!.Type);
    }

    /// <summary>
    /// Register D24: features can cancel. <c>dk</c> and <c>j</c> + U+00E0 are two words of equal
    /// count whose features hash to one bucket with opposite signs, so the vector is zero and has no
    /// direction. Before D24 its zero norm divided it into NaN, which pgvector refuses: an anonymous
    /// search answered 503, and a question 500 after PERSIST. It has no features, as a text of
    /// punctuation has none.
    /// </summary>
    [Fact]
    public void FeaturesThatCancelHaveNoEmbedding()
    {
        var result = HashedNGramEmbedding.Embed("dk j\u00E0");
        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/embedding/no-features", error!.Type);
    }

    /// <summary>
    /// Register D23: .NET's normalizer refuses U+FFFE outright, so a query holding it threw out of the
    /// vector channel and an anonymous <c>GET /v1/search</c> answered 500. A text that is only a
    /// noncharacter has no features, as a text of punctuation has none; it is not an error.
    /// </summary>
    [Fact]
    public void ANoncharacterAloneHasNoFeaturesAndDoesNotThrow()
    {
        var result = HashedNGramEmbedding.Embed("\uFFFE");
        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/embedding/no-features", error!.Type);
    }

    /// <summary>
    /// A noncharacter is read as U+FFFD, which separates words: <c>jcs</c> and <c>hash</c> either side
    /// of one embed as they do either side of U+FFFD. U+FFFE threw before D23's fix; the other three
    /// did not. This fact holds how they embed now, not how they embedded before the mapping; the
    /// digest pin below holds that, for U+FFFF.
    /// </summary>
    [Fact]
    public void ANoncharacterSeparatesWordsAsTheReplacementCharacterDoes()
    {
        var replaced = Embed("jcs\uFFFDhash");

        Assert.Equal(replaced, Embed("jcs\uFFFEhash"));
        Assert.Equal(replaced, Embed("jcs\uFFFFhash"));
        Assert.Equal(replaced, Embed("jcs\uFDD0hash"));
        Assert.Equal(replaced, Embed("jcs\U0010FFFFhash"));
    }

    /// <summary>
    /// An unpaired surrogate, which the normalizer also refuses, is read as U+FFFD too: a high one with
    /// no low one after it, a low one alone, a low one before a high one, which are two unpaired halves
    /// and not a pair, and a high one that ends the text.
    /// </summary>
    [Fact]
    public void AnUnpairedSurrogateIsReadAsTheReplacementCharacter()
    {
        var replaced = Embed("jcs\uFFFDhash");

        Assert.Equal(replaced, Embed("jcs\uD800hash"));
        Assert.Equal(replaced, Embed("jcs\uDC00hash"));
        Assert.Equal(replaced, Embed("jcs\uDC00\uD800hash"));
        Assert.Equal(Embed("jcs hash\uFFFD"), Embed("jcs hash\uD800"));
    }

    /// <summary>
    /// D23's mapping moves no feature of any text the normalizer accepted: such a text holds no
    /// ill-formed sequence, and a noncharacter in it split words exactly as U+FFFD does. So no vector
    /// that could be computed before it changed, and <c>hashed-ngram@1</c> keeps its version (R9.5,
    /// R11.10). Pinned before the mapping existed, over
    /// a text holding what the mapping sits beside: a compatibility ligature, a combining accent, a
    /// zero-width space between two words, U+FFFF between two more, which the normalizer accepts, and
    /// U+FFFD.
    /// </summary>
    [Fact]
    public void R9_5_AVectorThatCouldBeComputedBeforeD23IsUnchanged()
    {
        var vector = Embed("Canonical \uFB01le hash: cafe\u0301 and JCS\u200Bhash, jcs\uFFFFhash, not \uFFFD.");
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector.ToArray(), 0, bytes, 0, bytes.Length);

        Assert.Equal(ComputableBeforeD23Digest, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    /// <summary>Pinned at ccf200e, before D23's mapping; see the test above.</summary>
    private const string ComputableBeforeD23Digest = "042da00bbfaf5edec954d766767fae622f6f49ccefb31287fe044dd54f4c4e8e";
}
