using System.Text.Json;
using Xunit;

namespace Curia.Canon.Tests.Vectors;

/// <summary>
/// R6.45 — <c>conformance/index.json</c> names every top-level directory, says whether it is
/// a vector family, and for each family gives the profiles its vectors may declare and its
/// vector count. Every runner loads it and fails when it disagrees with what is on disk.
/// </summary>
/// <remarks>
/// This exists because both runners hard-code their family lists in source, so a family added
/// to the corpus is invisible to each implementation until that implementation is separately
/// edited — and its absence looks, in a passing test-run log, exactly like a family that ran.
/// That was not hypothetical when the requirement was written: <c>red-team/</c> was on disk,
/// absent from the README's own "Families" list, and loaded by neither runner. It is
/// legitimately not a vector family, which is the point: nothing on disk distinguished that
/// case from a lost one. Adding a directory without adding it to the index fails here; adding
/// it to the index without teaching this runner to load it fails in
/// <see cref="EveryDirectoryShapedFamilyIsEnumeratedByThisRunner"/>.
/// </remarks>
public sealed class ConformanceIndexTests
{
    private sealed record IndexEntry(
        string Name,
        bool IsFamily,
        string? Shape,
        IReadOnlyList<VectorProfile> Profiles,
        int Count,
        string? Note);

    [Fact]
    public void EveryDirectoryOnDiskIsNamedByTheIndexAndEveryIndexEntryExistsOnDisk()
    {
        var indexed = Index().Select(e => e.Name).ToHashSet(StringComparer.Ordinal);
        var onDisk = Directory.EnumerateDirectories(VectorLoader.ConformanceRoot)
            .Select(d => Path.GetFileName(d))
            .ToHashSet(StringComparer.Ordinal);

        var unaccountedFor = onDisk.Except(indexed, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var missingFromDisk = indexed.Except(onDisk, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            unaccountedFor.Count == 0,
            $"conformance/ directories absent from index.json: {string.Join(", ", unaccountedFor)} -- "
                + "an unindexed directory is indistinguishable from a lost vector family (R6.45)");
        Assert.True(
            missingFromDisk.Count == 0,
            $"index.json names directories that are not on disk: {string.Join(", ", missingFromDisk)}");
    }

    [Fact]
    public void EveryFamilysDeclaredCountEqualsTheVectorsActuallyPresent()
    {
        var families = Index().Where(e => e.IsFamily).ToList();
        Assert.NotEmpty(families);
        Assert.All(families, e =>
        {
            var actual = VectorNames(e).Count;
            Assert.True(
                actual == e.Count,
                $"index.json says {e.Name}/ holds {e.Count} vectors; {actual} are on disk");
        });
    }

    [Fact]
    public void EveryVectorDeclaresAProfileItsFamilyPermits()
    {
        var families = Index().Where(e => e.IsFamily).ToList();
        Assert.NotEmpty(families);
        Assert.All(families, e =>
        {
            Assert.NotEmpty(e.Profiles);

            if (e.Shape == "file-pairs")
            {
                // The rfc8785/ family carries its profile implicitly — it is the RFC author's
                // own data, vendored unmodified as input/output file pairs rather than as
                // directories, so it has no meta.json. A family whose vectors cannot declare a
                // profile can only have one, or nothing selects among them.
                Assert.True(
                    e.Profiles.Count == 1,
                    $"index.json gives {e.Name}/ {e.Profiles.Count} profiles, but its vectors carry no meta.json to choose between them");
                return;
            }

            foreach (var vector in VectorNames(e))
            {
                var declared = VectorLoader.ReadProfile(
                    Path.Combine(VectorLoader.ConformanceRoot, e.Name, vector, "meta.json"));
                Assert.True(
                    e.Profiles.Contains(declared),
                    $"{e.Name}/{vector} declares profile \"{VectorLoader.ProfileName(declared)}\", which index.json does not list for that family "
                        + $"(it permits: {string.Join(", ", e.Profiles.Select(VectorLoader.ProfileName))})");
            }
        });
    }

    /// <summary>
    /// The half of R6.45 that points at this runner rather than at the corpus: a family in the
    /// index that <see cref="VectorLoaderTests.AllFamilies"/> does not name is loaded by
    /// nothing here, and contributes no assurance while looking exactly like one that ran.
    /// Only directory-shaped families are in scope — <c>rfc8785/</c> is enumerated by
    /// <see cref="Rfc8785VectorTests"/> from its file pairs, <c>envelope/</c> by the fixture
    /// generator's own verification pass, and <c>merkle/</c> by
    /// <see cref="MerkleVectorLoader"/> (whose count
    /// <c>Acta.MerkleTreeTests.R6_45_ThisRunnerLoadsEveryMerkleVectorTheIndexDeclares</c> checks
    /// against the index), none through <c>VectorLoader.Load</c>.
    /// </summary>
    [Fact]
    public void EveryDirectoryShapedFamilyIsEnumeratedByThisRunner()
    {
        var indexed = Index()
            .Where(e => e.IsFamily && e.Shape == "directory")
            .Select(e => e.Name)
            .ToHashSet(StringComparer.Ordinal);
        var enumerated = VectorLoaderTests.AllFamilies.ToHashSet(StringComparer.Ordinal);

        var notLoaded = indexed.Except(enumerated, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var notIndexed = enumerated.Except(indexed, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            notLoaded.Count == 0,
            $"index.json lists vector families this suite never loads: {string.Join(", ", notLoaded)} -- "
                + "add them to VectorLoaderTests' family lists (R6.45)");
        Assert.True(
            notIndexed.Count == 0,
            $"this suite loads families index.json does not list as directory-shaped: {string.Join(", ", notIndexed)}");
    }

    [Fact]
    public void EveryNonFamilyDirectoryRecordsWhyItIsNotOne()
    {
        var others = Index().Where(e => !e.IsFamily).ToList();
        Assert.All(others, e => Assert.False(
            string.IsNullOrWhiteSpace(e.Note),
            $"index.json marks {e.Name}/ as not a vector family without saying why -- the decision has to be recorded, not merely true"));
    }

    /// <summary>
    /// Vector names within a family, by the shape the index declares: one subdirectory per
    /// vector for <c>directory</c>, <c>envelope</c> and <c>merkle</c>, one <c>input-*.json</c>
    /// per vector for <c>file-pairs</c>. An unrecognized shape fails rather than silently
    /// counting nothing.
    /// </summary>
    private static IReadOnlyList<string> VectorNames(IndexEntry entry)
    {
        var dir = Path.Combine(VectorLoader.ConformanceRoot, entry.Name);
        return entry.Shape switch
        {
            "directory" or "envelope" or "merkle" => [.. Directory.EnumerateDirectories(dir)
                .Select(d => Path.GetFileName(d))
                .Order(StringComparer.Ordinal)],
            "file-pairs" => [.. Directory.EnumerateFiles(dir, "input-*.json")
                .Select(f => Path.GetFileName(f))
                .Order(StringComparer.Ordinal)],
            _ => throw new InvalidOperationException(
                $"index.json gives {entry.Name}/ the unrecognized shape \"{entry.Shape}\""),
        };
    }

    private static List<IndexEntry> Index()
    {
        var path = Path.Combine(VectorLoader.ConformanceRoot, "index.json");
        var root = JsonDocument.Parse(File.ReadAllBytes(path)).RootElement;
        var entries = new List<IndexEntry>();

        foreach (var entry in root.GetProperty("directories").EnumerateArray())
        {
            var name = entry.GetProperty("name").GetString()
                ?? throw new InvalidOperationException("index.json has a directory entry with no name");
            var isFamily = entry.GetProperty("family").GetBoolean();

            // A family entry that omits its shape, profiles or count states nothing checkable,
            // which is the condition R6.45 exists to make impossible.
            var profiles = isFamily
                ? entry.GetProperty("profiles").EnumerateArray()
                    .Select(p => VectorLoader.ParseProfile(p.GetString()
                        ?? throw new InvalidOperationException($"index.json: {name}/ lists a non-string profile")))
                    .ToList()
                : [];

            entries.Add(new IndexEntry(
                Name: name,
                IsFamily: isFamily,
                Shape: isFamily ? entry.GetProperty("shape").GetString() : null,
                Profiles: profiles,
                Count: isFamily ? entry.GetProperty("count").GetInt32() : 0,
                Note: entry.TryGetProperty("note", out var note) ? note.GetString() : null));
        }

        Assert.NotEmpty(entries);
        return entries;
    }
}
