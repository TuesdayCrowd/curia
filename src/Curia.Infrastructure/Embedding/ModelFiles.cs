using Curia.Domain.Primitives;

namespace Curia.Infrastructure.Embedder;

/// <summary>The fixed refusal types of the startup check (errata G65, R11.91): one per cause, none carrying a path, an exception message or an input.</summary>
internal static class EmbedderRefusals
{
    internal const string DirectoryMissing = "curia/embedding/directory-missing";
    internal const string ModelFileMissing = "curia/embedding/model-file-missing";
    internal const string VocabularyFileMissing = "curia/embedding/vocabulary-file-missing";
    internal const string ModelDigestMismatch = "curia/embedding/model-digest-mismatch";
    internal const string VocabularyDigestMismatch = "curia/embedding/vocabulary-digest-mismatch";
}

/// <summary>The two files of a model directory, once their digests equal the manifest's.</summary>
internal sealed record VerifiedModelFiles(string ModelPath, string VocabularyPath);

/// <summary>
/// Checks the model directory against the manifest before any session is built (errata G65, R11.91). <b>Seam stub (D10a Task 1):</b> reads nothing and accepts
/// any directory; Task 2 checks existence and both digests, in that order, and refuses with the types of <see cref="EmbedderRefusals"/>.
/// </summary>
internal static class ModelFiles
{
    internal static Result<VerifiedModelFiles> Verify(string? directory, EmbeddingManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return Result<VerifiedModelFiles>.Ok(new VerifiedModelFiles(
            Path.Combine(directory ?? string.Empty, manifest.Model.Name), Path.Combine(directory ?? string.Empty, manifest.Vocabulary.Name)));
    }
}
