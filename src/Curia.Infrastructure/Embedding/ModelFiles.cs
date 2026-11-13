using System.Security.Cryptography;
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
/// Checks the model directory against the manifest before any session is built (errata G65, R11.91): the directory, then each file, then each file's size and sha256, the model first.
/// A refusal is one of <see cref="EmbedderRefusals"/>'s fixed types; its text names the file and the cause and never the path.
/// </summary>
internal static class ModelFiles
{
    internal static Result<VerifiedModelFiles> Verify(string? directory, EmbeddingManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return Refuse(EmbedderRefusals.DirectoryMissing, "The model directory does not exist");

        var model = Path.Combine(directory, manifest.Model.Name);
        var vocabulary = Path.Combine(directory, manifest.Vocabulary.Name);
        if (!File.Exists(model))
            return Refuse(EmbedderRefusals.ModelFileMissing, $"{manifest.Model.Name} is missing from the model directory");
        if (!File.Exists(vocabulary))
            return Refuse(EmbedderRefusals.VocabularyFileMissing, $"{manifest.Vocabulary.Name} is missing from the model directory");

        if (!Matches(model, manifest.Model))
            return Refuse(EmbedderRefusals.ModelDigestMismatch, $"{manifest.Model.Name} does not match the digest the build carries");
        if (!Matches(vocabulary, manifest.Vocabulary))
            return Refuse(EmbedderRefusals.VocabularyDigestMismatch, $"{manifest.Vocabulary.Name} does not match the digest the build carries");

        return Result<VerifiedModelFiles>.Ok(new VerifiedModelFiles(model, vocabulary));
    }

    private static Result<VerifiedModelFiles> Refuse(string type, string title) => Result<VerifiedModelFiles>.Fail(new Error(type, title, null));

    private static bool Matches(string path, ManifestFile expected)
    {
        try
        {
            if (new FileInfo(path).Length != expected.Bytes) return false;
            using var stream = File.OpenRead(path);
            return string.Equals(Convert.ToHexStringLower(SHA256.HashData(stream)), expected.Sha256, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
