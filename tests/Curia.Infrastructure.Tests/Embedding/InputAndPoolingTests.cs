using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Curia.Domain.Search;
using Curia.Infrastructure.Embedder;
using Curia.Infrastructure.Embedder.Tokenization;
using Xunit;

namespace Curia.Infrastructure.Tests.Embedder;

/// <summary>
/// Errata G65 (D10a), SEM6 to SEM8, model-free: the canary check's two sides, the pooling and the input (R9.63, R11.91). All three run over the synthetic 50-word vocabulary and a fake runner,
/// so a red here is about the adapter's arithmetic and not about a model.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class InputAndPoolingTests
{
    private static string Show(IEnumerable<long> ids) => string.Join(' ', ids);

    private static IEnumerable<long> Ids(params string[] words) => words.Select(w => (long)EmbedderRig.IdOf(w));

    private static WordPieceTokenizer Tokenizer() => new(EmbedderRig.Vocabulary);

    // ---- SEM6: the canary's two sides ----

    private static Canary CanaryFor(EmbedderRig rig, string id, string text, int[] tokenIds, float[]? vector = null)
    {
        // The vector the fake runner produces for these ids, pooled and normalised by the test's own arithmetic (row 0, L2).
        var hidden = FakeRunner.HiddenStateFor(new ModelRunInput([.. tokenIds.Select(i => (long)i)], [], []));
        var row = hidden[..384];
        var norm = Math.Sqrt(row.Sum(v => (double)v * v));
        var expected = vector ?? [.. row.Select(v => (float)(v / norm))];
        _ = rig;
        return new Canary(id, text, EmbeddingRole.Passage, tokenIds, expected);
    }

    private static int[] HelloWorldIds() => [EmbedderRig.Cls, EmbedderRig.IdOf("hello"), EmbedderRig.IdOf("world"), EmbedderRig.Sep];

    /// <summary>
    /// SEM6. The check has both sides of its tolerance and an id mismatch: a reference that differs from what the runner produced by +2e-4 on one component is refused; +5e-5 is accepted;
    /// one token id different is refused; the ids and the vector right are accepted; a refusal names the canary's id. Predicted red on the seam: <c>Assert.Throws() Failure: No exception was thrown</c>
    /// for the refused rows (the seam's check compares nothing).
    /// </summary>
    [Theory]
    [InlineData("vector-plus-2e-4", true)]
    [InlineData("vector-plus-5e-5", false)]
    [InlineData("one-id-different", true)]
    [InlineData("all-right", false)]
    public void R11_91_SEM6_TheCanaryIsHeldToTheTolerancesBothSidesAndToTheIds(string variant, bool refused)
    {
        using var rig = EmbedderRig.Create();
        var ids = HelloWorldIds();
        var canary = CanaryFor(rig, "sem6-hello", "hello world", ids);
        canary = variant switch
        {
            "vector-plus-2e-4" => canary with { Vector = [.. canary.Vector.Select((v, i) => i == 0 ? v + 2e-4f : v)] },
            "vector-plus-5e-5" => canary with { Vector = [.. canary.Vector.Select((v, i) => i == 0 ? v + 5e-5f : v)] },
            "one-id-different" => canary with { TokenIds = [ids[0], ids[1], EmbedderRig.IdOf("fox"), ids[3]] },
            _ => canary,
        };
        var embedder = rig.WithCanaries([canary]);

        if (refused)
        {
            var refusal = Assert.Throws<InvalidOperationException>(embedder.Check);
            Assert.Contains("sem6-hello", refusal.Message, StringComparison.Ordinal);
        }
        else
        {
            embedder.Check();
        }
    }

    // ---- SEM7: pooling ----

    private static float[] Hidden(int rows, Func<int, int, float> at)
    {
        var hidden = new float[rows * 384];
        for (var i = 0; i < rows; i++)
            for (var j = 0; j < 384; j++) hidden[(i * 384) + j] = at(i, j);
        return hidden;
    }

    /// <summary>
    /// SEM7. A <c>[1,4,384]</c> state whose CLS row and whose mean differ gives the normalised CLS row (norm 1 within 1e-6, equal to the row divided by its norm); a zero CLS row and a NaN are
    /// <c>ModelFault</c>, not <c>NoFeatures</c>. Predicted red on the seam: <c>pooling failed: curia/embedding/model-not-ready</c>.
    /// </summary>
    [Fact]
    public void R9_63_SEM7_TheVectorIsTheNormalisedClsRowAndNotTheMeanAndAZeroOrNaNRowIsAFault()
    {
        var cls = Enumerable.Range(0, 384).Select(j => (float)((j % 5) + 1)).ToArray();
        var hidden = Hidden(4, (i, j) => i == 0 ? cls[j] : 100f * (i + 1));
        var pooled = BgePooling.Pool(hidden, 4, 384);
        Assert.True(pooled.IsOk, "pooling failed: " + pooled.Match(_ => string.Empty, e => e.Type));
        var vector = pooled.Match(v => v, _ => ImmutableArray<float>.Empty);

        var clsNorm = Math.Sqrt(cls.Sum(v => (double)v * v));
        Assert.Equal(384, vector.Length);
        Assert.Equal(1.0, Math.Sqrt(vector.Sum(v => (double)v * v)), 1e-6);
        for (var j = 0; j < 384; j++) Assert.Equal(cls[j] / clsNorm, vector[j], 1e-6);

        var zero = BgePooling.Pool(Hidden(4, (i, j) => i == 0 ? 0f : 1f), 4, 384);
        Assert.Equal(EmbeddingErrors.ModelFault().Type, zero.Match(_ => string.Empty, e => e.Type));

        var nan = BgePooling.Pool(Hidden(4, (i, j) => i == 0 && j == 7 ? float.NaN : 1f), 4, 384);
        Assert.Equal(EmbeddingErrors.ModelFault().Type, nan.Match(_ => string.Empty, e => e.Type));
    }

    // ---- SEM8: the input ----

    private static string Words(int count, params string[] vocabulary) =>
        string.Join(' ', Enumerable.Range(0, count).Select(i => vocabulary[i % vocabulary.Length]));

    private static readonly string[] Plain = ["alpha", "beta", "gamma", "delta", "one", "two", "three", "four"];

    /// <summary>
    /// SEM8. Over the synthetic vocabulary the query role puts the prefix first and the passage role does not; a 700-word text is exactly 512 ids, <c>[CLS]</c> first and <c>[SEP]</c> last, the 510
    /// between them the text's first 510 tokens; the prefix's tokens count against the 510; the boundary is held on both sides (509, 510 and 511 tokens). Predicted red on the seam:
    /// <c>expected 512 ids, the input holds 0</c>.
    /// </summary>
    [Fact]
    public void R9_63_SEM8_TheHeadIs512TokensTheQueryRolePrefixesAndThePrefixCountsAgainstIt()
    {
        var tokenizer = Tokenizer();
        var prefixIds = Ids("represent", "this", "sentence", "for", "searching", "relevant", "passages", ":").ToArray();

        var passage = BgeInput.Build(tokenizer, "hello world", EmbeddingRole.Passage, EmbedderRig.QueryPrefix, 512);
        Assert.Equal(Show(Ids("[CLS]", "hello", "world", "[SEP]")), Show(passage.InputIds));

        var query = BgeInput.Build(tokenizer, "hello world", EmbeddingRole.Query, EmbedderRig.QueryPrefix, 512);
        Assert.Equal(Show([EmbedderRig.Cls, .. prefixIds, EmbedderRig.IdOf("hello"), EmbedderRig.IdOf("world"), EmbedderRig.Sep]), Show(query.InputIds));

        var long700 = Words(700, Plain);
        var head = BgeInput.Build(tokenizer, long700, EmbeddingRole.Passage, EmbedderRig.QueryPrefix, 512);
        Assert.True(head.InputIds.Length == 512, $"expected 512 ids, the input holds {head.InputIds.Length}");
        Assert.Equal(EmbedderRig.Cls, head.InputIds[0]);
        Assert.Equal(EmbedderRig.Sep, head.InputIds[^1]);
        var first510 = Enumerable.Range(0, 510).Select(i => (long)EmbedderRig.IdOf(Plain[i % Plain.Length]));
        Assert.Equal(Show(first510), Show(head.InputIds[1..^1]));
        Assert.Equal(512, head.AttentionMask.Length);
        Assert.All(head.AttentionMask, m => Assert.Equal(1L, m));
        Assert.All(head.TokenTypeIds, t => Assert.Equal(0L, t));

        foreach (var (count, expected) in new[] { (509, 511), (510, 512), (511, 512) })
        {
            var edge = BgeInput.Build(tokenizer, Words(count, Plain), EmbeddingRole.Passage, EmbedderRig.QueryPrefix, 512);
            Assert.True(edge.InputIds.Length == expected, string.Create(CultureInfo.InvariantCulture, $"a {count}-token text gave {edge.InputIds.Length} ids, expected {expected}"));
        }

        var prefixed = BgeInput.Build(tokenizer, long700, EmbeddingRole.Query, EmbedderRig.QueryPrefix, 512);
        Assert.True(prefixed.InputIds.Length == 512, $"the query role gave {prefixed.InputIds.Length} ids, expected 512");
        Assert.Equal(Show(prefixIds), Show(prefixed.InputIds[1..(1 + prefixIds.Length)]));
        var textPart = prefixed.InputIds[(1 + prefixIds.Length)..^1];
        Assert.True(textPart.Length == 510 - prefixIds.Length, $"the prefix's {prefixIds.Length} tokens must count against the 510: the text keeps {textPart.Length}, expected {510 - prefixIds.Length}");
    }
}
