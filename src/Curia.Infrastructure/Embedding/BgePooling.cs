using System.Collections.Immutable;
using Curia.Domain.Primitives;

namespace Curia.Infrastructure.Embedder;

/// <summary>
/// The vector of a run (errata G65, Ruling 1): row 0 of <c>last_hidden_state</c> (the <c>[CLS]</c> state), L2-normalised; a zero row or a non-finite component is a
/// <c>ModelFault</c>. <b>Seam stub (D10a Task 1):</b> answers <c>ModelNotReady</c>.
/// </summary>
internal static class BgePooling
{
    /// <param name="hiddenState">The output flattened: <paramref name="sequenceLength"/> rows of <paramref name="dimensions"/> floats.</param>
    internal static Result<ImmutableArray<float>> Pool(float[] hiddenState, int sequenceLength, int dimensions)
    {
        ArgumentNullException.ThrowIfNull(hiddenState);
        return Result<ImmutableArray<float>>.Fail(Curia.Domain.Search.EmbeddingErrors.ModelNotReady());
    }
}
