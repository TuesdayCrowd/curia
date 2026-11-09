using Curia.Domain.Search;

namespace Curia.Infrastructure.Embedder;

/// <summary>A file the manifest names: its name in the model directory, its size and its sha256 (lower-case hex).</summary>
internal sealed record ManifestFile(string Name, long Bytes, string Sha256);

/// <summary>
/// A text whose token ids and vector were produced by the Python reference (<c>tools/model/reference.py</c>), never by this adapter (errata G65, Ruling 3):
/// the Forum embeds it at start and refuses to run when the ids differ or a component of the vector differs by more than 1e-4 (R11.91).
/// </summary>
internal sealed record Canary(string Id, string Text, EmbeddingRole Role, IReadOnlyList<int> TokenIds, IReadOnlyList<float> Vector);

/// <summary>
/// The identity of <c>bge-small-en-v1.5@1</c> as the build carries it (errata G65, R9.63): the two file digests, the prefix, the head length and the canary set.
/// <b>Seam stub (D10a Task 1):</b> <see cref="Embedded"/> reads nothing and answers <see cref="Empty"/>; Task 2 embeds <c>models/bge-small-en-v1.5/manifest.json</c>.
/// </summary>
internal sealed record EmbeddingManifest(
    string Id,
    int Dimensions,
    ManifestFile Model,
    ManifestFile Vocabulary,
    string QueryPrefix,
    int MaxSequenceLength,
    IReadOnlyList<Canary> Canaries,
    IReadOnlyDictionary<string, string> ReferenceTools)
{
    /// <summary>What a manifest that was not read says: no identity, no files, no canaries.</summary>
    internal static EmbeddingManifest Empty { get; } = new(
        string.Empty, 0, new ManifestFile(string.Empty, 0, string.Empty), new ManifestFile(string.Empty, 0, string.Empty),
        string.Empty, 0, [], new Dictionary<string, string>());

    /// <summary>The manifest embedded in this assembly. Seam stub: <see cref="Empty"/>.</summary>
    internal static EmbeddingManifest Embedded() => Empty;
}
