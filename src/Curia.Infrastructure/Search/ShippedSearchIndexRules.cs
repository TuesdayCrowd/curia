using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Curia.Application.Retrieval;
using Curia.Domain.Search;

namespace Curia.Infrastructure.Search;

/// <summary>
/// The rules of the index version this build writes (errata G88): the committed tag-fold table and the committed tokenizer digest, read from the files <c>conformance/retrieval</c> holds and this assembly
/// embeds. A test regenerates both from the runtime and compares them byte for byte, so a file edited by hand, or a runtime that moved, is red before it is deployed.
/// </summary>
public static class ShippedSearchIndexRules
{
    private const string Prefix = "Curia.Infrastructure.Search.";

    /// <summary>The rules this build runs under. Read once: the files are the build's.</summary>
    public static SearchIndexRules Shipped { get; } = Load();

    private static SearchIndexRules Load()
    {
        var fold = ReadResource("tagfold-v1.tsv");
        var table = TagFoldTable.Parse(Encoding.UTF8.GetString(fold)).Match(t => t, e => throw new InvalidOperationException($"The embedded tag-fold table is unusable ({e.Type})."));

        var committed = Encoding.UTF8.GetString(ReadResource("tokenizer-units.sha256")).Trim();
        if (committed.Length != 64 || !committed.All(Uri.IsHexDigit))
            throw new InvalidOperationException("The embedded tokenizer digest is not 64 hex digits.");

        return new SearchIndexRules(table, [.. SHA256.HashData(fold)], [.. Convert.FromHexString(committed)]);
    }

    private static byte[] ReadResource(string name)
    {
        var assembly = typeof(ShippedSearchIndexRules).Assembly;
        using var stream = assembly.GetManifestResourceStream(Prefix + name)
            ?? throw new InvalidOperationException($"Embedded resource '{Prefix + name}' was not found in {assembly.FullName}; check Curia.Infrastructure.csproj.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
