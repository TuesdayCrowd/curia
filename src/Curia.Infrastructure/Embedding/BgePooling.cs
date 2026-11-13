using System.Collections.Immutable;
using Curia.Domain.Primitives;
using Curia.Domain.Search;

namespace Curia.Infrastructure.Embedder;

/// <summary>
/// The vector of a run (errata G65, Ruling 1): row 0 of <c>last_hidden_state</c> (the <c>[CLS]</c> state), L2-normalised; a zero row or a non-finite component is a
/// <c>ModelFault</c>, not a text without features.
/// </summary>
internal static class BgePooling
{
    /// <param name="hiddenState">The output flattened: <paramref name="sequenceLength"/> rows of <paramref name="dimensions"/> floats.</param>
    internal static Result<ImmutableArray<float>> Pool(float[] hiddenState, int sequenceLength, int dimensions)
    {
        ArgumentNullException.ThrowIfNull(hiddenState);
        if (sequenceLength < 1 || dimensions < 1 || hiddenState.LongLength != (long)sequenceLength * dimensions)
            return Result<ImmutableArray<float>>.Fail(EmbeddingErrors.ModelFault("ShapeMismatch"));

        var sum = 0.0;
        for (var j = 0; j < dimensions; j++)
        {
            var v = hiddenState[j];
            if (!float.IsFinite(v)) return Result<ImmutableArray<float>>.Fail(EmbeddingErrors.ModelFault("NonFiniteOutput"));
            sum += (double)v * v;
        }

        var norm = Math.Sqrt(sum);
        if (!(norm > 0) || !double.IsFinite(norm)) return Result<ImmutableArray<float>>.Fail(EmbeddingErrors.ModelFault("ZeroNorm"));

        var vector = ImmutableArray.CreateBuilder<float>(dimensions);
        for (var j = 0; j < dimensions; j++) vector.Add((float)(hiddenState[j] / norm));
        return Result<ImmutableArray<float>>.Ok(vector.MoveToImmutable());
    }
}
