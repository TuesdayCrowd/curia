using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Curia.Canon.Json;
using Curia.Client;
using Curia.Domain.Serving;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R6.54 (add.): <c>conformance/lifecycle/</c>, run by the client's reader.
///
/// <para>Each case is a small whole log: the entries and proofs a reader fetches, a signed head over the log, and the
/// log's key set. <see cref="LifecycleCheck"/> folds the author's lifecycle for the post and must reach the case's
/// verdict and name its predicate. <c>curia-testis</c> runs the same cases (<c>rust/curia-testis/tests/lifecycle.rs</c>);
/// the two are the stage's cross-implementation check, since R14.6's differential comparison takes no lifecycle input
/// in this stage.</para>
///
/// <para><b>The heads are real.</b> Every case's head is checked against the log key set the case carries before
/// the case is judged, so a case whose head does not verify fails here as a broken vector and never as a verdict.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class LifecycleConformanceTests
{
    private static string Root
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, "conformance", "lifecycle");
                if (Directory.Exists(candidate)) return candidate;
                directory = directory.Parent;
            }

            throw new InvalidOperationException(
                $"conformance/lifecycle/ is not above {AppContext.BaseDirectory}. A vector family no runner can find is "
                + "indistinguishable from a lost one (trap 5).");
        }
    }

    private static List<string> CaseNames() =>
        [.. Directory.EnumerateDirectories(Root).OrderBy(d => d, StringComparer.Ordinal).Select(d => Path.GetFileName(d))];

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var name in CaseNames()) data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void R6_54_EveryLifecycleCaseReachesItsVerdictAndNamesItsPredicate(string name)
    {
        var (verdict, predicate) = Judge(name);
        var expected = Expected(name);

        Assert.Equal(expected.Verdict, verdict);
        Assert.Equal(expected.Predicate, predicate);
    }

    /// <summary>
    /// R6.45: this runner loads every case the index declares. A family the index counts and this suite does not run
    /// contributes no assurance while looking exactly like one that does.
    /// </summary>
    [Fact]
    public void R6_45_ThisRunnerLoadsEveryLifecycleCaseTheIndexDeclares()
    {
        using var index = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, "..", "index.json")));
        var declared = index.RootElement.GetProperty("directories").EnumerateArray()
            .Single(d => d.GetProperty("name").GetString() == "lifecycle")
            .GetProperty("count").GetInt32();

        Assert.Equal(declared, CaseNames().Count);
        Assert.Equal(30, declared);
    }

    [Fact]
    public void R6_54_TheFamilyCoversEachVerdict()
    {
        var verdicts = CaseNames().Select(c => Expected(c).Verdict).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("verified", verdicts);
        Assert.Contains("failed", verdicts);
        Assert.Contains("could-not-check", verdicts);
    }

    private static (string Verdict, string Predicate) Expected(string name)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, name, "expected.json")));
        return (document.RootElement.GetProperty("verdict").GetString()!, document.RootElement.GetProperty("predicate").GetString()!);
    }

    /// <summary>The verdict and short predicate <see cref="LifecycleCheck"/> reaches for a case.</summary>
    internal static (string Verdict, string Predicate) Judge(string name)
    {
        var dir = Path.Combine(Root, name);
        using var meta = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "meta.json")));
        var postIndex = meta.RootElement.GetProperty("post_index").GetInt64();

        var head = Read(Path.Combine(dir, "head.json"), ActaDocuments.ReadHead);
        var keys = Read(Path.Combine(dir, "log-jwks.json"), ActaDocuments.ReadLogJwks);
        Assert.Equal(CheckOutcome.Verified, ActaCheck.HeadSignature(head, keys).Outcome);

        var indices = Directory.EnumerateFiles(Path.Combine(dir, "entries"), "*.json")
            .Select(f => long.Parse(Path.GetFileNameWithoutExtension(f), System.Globalization.CultureInfo.InvariantCulture))
            .Order()
            .ToList();

        LogEntryDocument Entry(long index) => Read(Path.Combine(dir, "entries", $"{index}.json"), ActaDocuments.ReadEntry);
        var postEntry = Entry(postIndex);
        var chain = indices
            .Where(i => i != postIndex)
            .Select(i => (Entry(i), Read(Path.Combine(dir, "proofs", $"{i}.json"), ActaDocuments.ReadInclusionProof)))
            .ToList();

        var verdict = LifecycleCheck.Fold(PostOf(postEntry), postEntry, chain, head);
        return (
            verdict.Check.Outcome switch
            {
                CheckOutcome.Verified => "verified",
                CheckOutcome.Failed => "failed",
                CheckOutcome.CouldNotCheck => "could-not-check",
                _ => throw new InvalidOperationException("an outcome the vectors do not name"),
            },
            verdict.Predicate);
    }

    internal static SignedHeadDocument ReadHead(string path) => Read(path, ActaDocuments.ReadHead);

    internal static ImmutableArray<LogJwk> ReadLogKeys(string path) => Read(path, ActaDocuments.ReadLogJwks);

    private static T Read<T>(string path, Func<JsonValue, Curia.Domain.Primitives.Result<T>> parse)
    {
        var parsed = JsonReader.Parse(File.ReadAllBytes(path), AdmitLimits.Default);
        Assert.True(parsed.TryGetValue(out var value, out var error), $"{path}: {error?.Type}");
        Assert.True(parse(value!).TryGetValue(out var read, out var readError), $"{path}: {readError?.Type}");
        return read!;
    }

    /// <summary>The post a reader holds, built from the log's own record of it.</summary>
    private static ProvenancePost PostOf(LogEntryDocument entry)
    {
        var payload = (JsonValue.Object)entry.Entry.Members.Single(m => m.Key == "payload").Value;
        string Text(string name) => ((JsonValue.String)payload.Members.Single(m => m.Key == name).Value).Value;

        return new ProvenancePost(
            new Provenance(
                "agent-authored/untrusted", Provenance.StandardWarning, Text("author"), OwnerVerified: false, SignatureValid: true,
                "V0", [], MarkingMode.None, null, string.Empty, null, null, [], []),
            Text("post_id"), Text("board"), Text("kind"), null, Text("server_ts"), Text("digest"),
            Text("canonical"), Text("signature"), string.Empty,
            LogIndex: entry.LogIndex);
    }
}
