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
    internal static string Root
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

        Assert.Null(Mismatch(expected, verdict, predicate));
    }

    /// <summary>
    /// The comparison this runner makes for every case, on the verdict <i>and</i> the predicate. Null when they agree, else
    /// what differs. One function, so the fact below can hand it a wrong expectation and see it refuse: a runner that
    /// compared the verdict alone would make every falsify row that goes red "on predicate" vacuous (Task 2.4).
    /// </summary>
    internal static string? Mismatch((string Verdict, string Predicate) expected, string verdict, string predicate)
    {
        if (!string.Equals(expected.Verdict, verdict, StringComparison.Ordinal))
            return $"verdict: expected {expected.Verdict}, got {verdict}";

        return string.Equals(expected.Predicate, predicate, StringComparison.Ordinal)
            ? null
            : $"predicate: expected {expected.Predicate}, got {predicate}";
    }

    /// <summary>
    /// R6.57: this runner compares <c>predicate</c> as well as <c>verdict</c> for every compromise case. A case run against an
    /// expectation whose verdict is right and whose predicate is wrong is refused, for each distinct verdict the PR 2 cases reach.
    /// </summary>
    [Theory]
    [InlineData("compromised-before-post")]
    [InlineData("compromise-kid-of-other-identity")]
    [InlineData("compromise-unknown-reason")]
    public void R6_57_TheRunnerComparisonFailsOnAWrongPredicate(string name)
    {
        var (verdict, predicate) = Judge(name);
        var expected = Expected(name);

        Assert.Null(Mismatch(expected, verdict, predicate));
        Assert.NotNull(Mismatch((expected.Verdict, "not-" + expected.Predicate), verdict, predicate));
        Assert.Contains("predicate:", Mismatch((expected.Verdict, "not-" + expected.Predicate), verdict, predicate), StringComparison.Ordinal);
    }

    /// <summary>
    /// R6.57 and R6.62: the compromise cases (the fifteen of PR 2 that remain, the seven PR H adds under the declaration key, the six of Amendment 3, the six of Amendment 4 and the four of Amendment 5: errata G67) are all present and reach
    /// the verdict and predicate the vocabulary names, one string at a time. They are named here so that a case dropped from the family is a red fact and not a smaller count.
    /// </summary>
    [Fact]
    public void R6_57_TheCompromiseCasesAreAllPresentWithTheirVocabulary()
    {
        var expected = new Dictionary<string, (string Verdict, string Predicate)>(StringComparer.Ordinal)
        {
            ["compromised-before-post"] = ("disputed", "disputed"),
            ["compromised-after-intervening-rotation"] = ("disputed", "disputed"),
            ["compromise-post-under-cascaded-key"] = ("disputed", "disputed"),
            ["compromise-sibling-declared-alone"] = ("disputed", "disputed"),
            ["declaration-signed-as-head"] = ("failed", "lifecycle-typ-mismatch"),
            ["compromise-kids-omits-cascade"] = ("failed", "declaration-mismatch"),
            ["compromise-kids-reordered"] = ("failed", "declaration-mismatch"),
            ["compromise-kids-siblings-reordered"] = ("failed", "declaration-mismatch"),
            ["compromise-from-index-after-declaration"] = ("failed", "declaration-mismatch"),
            ["compromise-from-index-at-declaration"] = ("failed", "declaration-mismatch"),
            ["compromise-from-index-before-binding"] = ("failed", "declaration-mismatch"),
            ["compromise-kid-of-other-identity"] = ("failed", "kid-not-bound-to-agent"),
            ["compromise-declaration-kid-not-valid-at-position"] = ("failed", "declaration-key-unpublished"),
            ["compromise-unknown-reason"] = ("could-not-check", "unknown-reason"),
            ["compromise-declaration-kid-position-unknown"] = ("could-not-check", "declaration-key-position-unknown"),
            ["compromise-v1-statement"] = ("could-not-check", "unknown-lifecycle-kind"),
            ["compromise-signed-by-log-key"] = ("failed", "declaration-key-unpublished"),
            ["compromise-declaration-key-is-log-key"] = ("failed", "declaration-key-is-log-key"),
            ["compromise-second-declaration-key"] = ("failed", "declaration-key-not-first"),
            ["compromise-first-of-two-declaration-keys"] = ("disputed", "disputed"),
            ["compromise-declaration-key-set-absent"] = ("could-not-check", "declaration-key-set-not-supplied"),
            ["compromise-declaration-keys-empty"] = ("failed", "declaration-key-unpublished"),
            ["compromise-declaration-keys-null"] = ("could-not-check", "declaration-key-set-not-supplied"),
            ["compromise-declaration-keys-object"] = ("could-not-check", "declaration-key-set-not-supplied"),
            ["compromise-log-key-kid-position-unknown"] = ("could-not-check", "declaration-key-position-unknown"),
            ["compromise-declaration-key-is-log-key-under-another-kid"] = ("failed", "declaration-key-is-log-key"),
            ["compromise-another-declaration-key-position-unknown"] = ("could-not-check", "declaration-key-position-unknown"),
            ["compromise-declaration-kid-published-twice"] = ("failed", "declaration-signature-invalid"),
            ["compromise-declaration-kid-twice-one-unpositioned"] = ("could-not-check", "declaration-key-position-unknown"),
            ["compromise-declaration-key-without-kid"] = ("could-not-check", "declaration-key-set-not-supplied"),
            ["compromise-log-key-without-kid"] = ("could-not-check", "declaration-key-set-not-supplied"),
            ["compromise-declaration-keys-share-position"] = ("failed", "declaration-key-not-first"),
            ["compromise-declaration-keys-entry-not-an-object"] = ("could-not-check", "declaration-key-set-not-supplied"),
            ["compromise-declaration-keys-entry-without-alg"] = ("could-not-check", "declaration-key-set-not-supplied"),
            ["compromise-declaration-key-without-y"] = ("could-not-check", "declaration-key-set-not-supplied"),
            ["compromise-declaration-key-x-not-base64url"] = ("could-not-check", "declaration-key-set-not-supplied"),
            ["compromise-log-key-kty-rsa"] = ("could-not-check", "declaration-key-set-not-supplied"),
            ["compromise-declaration-kid-twice-at-one-position"] = ("could-not-check", "declaration-key-position-unknown"),
        };

        Assert.Equal(38, expected.Count);
        foreach (var (name, want) in expected)
        {
            Assert.Equal(want, Expected(name));
            Assert.Equal(want, Judge(name));
        }
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
        Assert.Equal(75, declared);
    }

    [Fact]
    public void R6_54_TheFamilyCoversEachVerdict()
    {
        var verdicts = CaseNames().Select(c => Expected(c).Verdict).ToHashSet(StringComparer.Ordinal);

        Assert.Contains("verified", verdicts);
        Assert.Contains("failed", verdicts);
        Assert.Contains("could-not-check", verdicts);
        Assert.Contains("disputed", verdicts);
    }

    internal static (string Verdict, string Predicate) Expected(string name)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Root, name, "expected.json")));
        return (document.RootElement.GetProperty("verdict").GetString()!, document.RootElement.GetProperty("predicate").GetString()!);
    }

    /// <summary>The documents of one case, as a reader fetches them: the post's entry, the lifecycle entries with their proofs, the head and the log's key set.</summary>
    internal sealed record CaseDocuments(
        LogEntryDocument PostEntry,
        List<(LogEntryDocument Entry, InclusionProofDocument Proof)> Chain,
        SignedHeadDocument Head,
        LogKeySet LogKeys);

    internal static CaseDocuments Load(string name) => Load(name, File.ReadAllBytes(Path.Combine(Root, name, "log-jwks.json")));

    /// <summary>A case read against a key set other than its own (HSU50 mutates one entry of a key set and reads the case again).</summary>
    internal static CaseDocuments Load(string name, byte[] logJwks)
    {
        var dir = Path.Combine(Root, name);
        using var meta = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "meta.json")));
        var postIndex = meta.RootElement.GetProperty("post_index").GetInt64();

        var head = Read(Path.Combine(dir, "head.json"), ActaDocuments.ReadHead);
        var keys = Read("the key set of " + name, logJwks, ActaDocuments.ReadLogJwks);
        Assert.Equal(CheckOutcome.Verified, ActaCheck.HeadSignature(head, keys.Keys).Outcome);

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

        return new CaseDocuments(postEntry, chain, head, keys);
    }

    /// <summary>The verdict and short predicate <see cref="LifecycleCheck"/> reaches for a case.</summary>
    internal static (string Verdict, string Predicate) Judge(string name) => Judge(Load(name));

    internal static (string Verdict, string Predicate) Judge(CaseDocuments documents)
    {
        var verdict = LifecycleCheck.Fold(PostOf(documents.PostEntry), documents.PostEntry, documents.Chain, documents.Head, documents.LogKeys);
        return (
            verdict.Check.Outcome switch
            {
                CheckOutcome.Verified => "verified",
                CheckOutcome.Failed => "failed",
                CheckOutcome.CouldNotCheck => "could-not-check",
                CheckOutcome.Disputed => "disputed",
                _ => throw new InvalidOperationException("an outcome the vectors do not name"),
            },
            verdict.Predicate);
    }

    internal static SignedHeadDocument ReadHead(string path) => Read(path, ActaDocuments.ReadHead);

    internal static LogEntryDocument ReadEntryFile(string path) => Read(path, ActaDocuments.ReadEntry);

    internal static InclusionProofDocument ReadProofFile(string path) => Read(path, ActaDocuments.ReadInclusionProof);

    /// <summary>The <c>keys</c> member of a case's key set: the log keys, which sign heads.</summary>
    internal static ImmutableArray<LogJwk> ReadLogKeys(string path) => Read(path, ActaDocuments.ReadLogJwks).Keys;

    private static T Read<T>(string path, Func<JsonValue, Curia.Domain.Primitives.Result<T>> parse) => Read(path, File.ReadAllBytes(path), parse);

    private static T Read<T>(string path, byte[] bytes, Func<JsonValue, Curia.Domain.Primitives.Result<T>> parse)
    {
        var parsed = JsonReader.Parse(bytes, AdmitLimits.Default);
        Assert.True(parsed.TryGetValue(out var value, out var error), $"{path}: {error?.Type}");
        Assert.True(parse(value!).TryGetValue(out var read, out var readError), $"{path}: {readError?.Type}");
        return read!;
    }

    /// <summary>The post a reader holds, built from the log's own record of it.</summary>
    internal static ProvenancePost PostOf(LogEntryDocument entry)
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
