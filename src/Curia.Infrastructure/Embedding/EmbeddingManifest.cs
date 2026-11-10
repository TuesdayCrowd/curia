using System.Collections.ObjectModel;
using System.Text.Json;
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
/// The identity of <c>bge-small-en-v1.5@1</c> as the build carries it (errata G65, R9.63): the two file digests, the prefix, the head length and the canary set, read from
/// <c>models/bge-small-en-v1.5/manifest.json</c>, which is embedded in this assembly and is also the input of <c>tools/model/fetch-model.py</c>: the digest the fetch checks and the
/// digest the Forum checks are one string in one file. A manifest that does not parse is a refusal to start, never an empty identity.
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
    /// <summary>The name the manifest is embedded under.</summary>
    internal const string ResourceName = "Curia.Infrastructure.Embedding.manifest.json";

    /// <summary>The reference tools the manifest may name (<c>tools/model/reference.py</c> writes them).</summary>
    private static readonly string[] ReferenceToolNames = ["python", "transformers", "tokenizers", "onnxruntime", "numpy", "torch"];

    private static readonly Lazy<EmbeddingManifest> EmbeddedManifest = new(() => ReadEmbedded(), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>What a manifest that was not read says: no identity, no files, no canaries.</summary>
    internal static EmbeddingManifest Empty { get; } = new(
        string.Empty, 0, new ManifestFile(string.Empty, 0, string.Empty), new ManifestFile(string.Empty, 0, string.Empty),
        string.Empty, 0, [], new Dictionary<string, string>());

    /// <summary>The manifest embedded in this assembly.</summary>
    internal static EmbeddingManifest Embedded() => EmbeddedManifest.Value;

    /// <summary>Parses a manifest document. Throws <see cref="InvalidOperationException"/> for a member that is absent or of the wrong shape, naming the member.</summary>
    internal static EmbeddingManifest Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var files = Member(root, "files");
        var canaries = new List<Canary>();
        foreach (var c in Member(root, "canaries").EnumerateArray())
        {
            var role = Text(c, "role") switch
            {
                "query" => EmbeddingRole.Query,
                "passage" => EmbeddingRole.Passage,
                _ => throw new InvalidOperationException("The embedding manifest holds a canary with a role that is neither query nor passage."),
            };
            canaries.Add(new Canary(
                Text(c, "id"),
                Text(c, "text"),
                role,
                [.. Member(c, "token_ids").EnumerateArray().Select(e => e.GetInt32())],
                [.. Member(c, "vector").EnumerateArray().Select(e => e.GetSingle())]));
        }

        var tools = new Dictionary<string, string>();
        var named = Member(root, "reference_tools");
        foreach (var tool in ReferenceToolNames)
            if (named.TryGetProperty(tool, out _)) tools[tool] = Text(named, tool);

        return new EmbeddingManifest(
            Text(root, "id"),
            Member(root, "dimensions").GetInt32(),
            FileOf(files, "model.onnx"),
            FileOf(files, "vocab.txt"),
            Text(root, "query_prefix"),
            Member(root, "max_sequence_length").GetInt32(),
            new ReadOnlyCollection<Canary>(canaries),
            tools);
    }

    private static EmbeddingManifest ReadEmbedded()
    {
        using var stream = typeof(EmbeddingManifest).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The embedding manifest is not embedded in Curia.Infrastructure.");
        using var reader = new StreamReader(stream);
        try
        {
            return Parse(reader.ReadToEnd());
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("The embedding manifest embedded in Curia.Infrastructure is not valid JSON (" + ex.GetType().Name + ").", ex);
        }
    }

    private static JsonElement Member(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
            ? value
            : throw new InvalidOperationException($"The embedding manifest has no '{name}' member.");

    private static string Text(JsonElement element, string name) =>
        Member(element, name).GetString() ?? throw new InvalidOperationException($"The embedding manifest's '{name}' member is not a string.");

    private static ManifestFile FileOf(JsonElement files, string name)
    {
        var entry = Member(files, name);
        return new ManifestFile(name, Member(entry, "bytes").GetInt64(), Text(entry, "sha256"));
    }
}
