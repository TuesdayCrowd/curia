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
}
