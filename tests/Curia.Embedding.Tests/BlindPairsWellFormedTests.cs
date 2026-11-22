using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Curia.Embedding.Tests;

/// <summary>
/// NDT1 (D10b). The well-formedness of <c>conformance/retrieval/blind-pairs.jsonl</c> and of its provenance record. It needs no model and constructs no fixture. Nothing it prints is a text: a failure names a
/// check and an id.
///
/// On the tree this is a control, green because the files exist (the plan's "red: no file" predates their commit); it carries information only through a mutation of the file (FNDT9: one pair's class edited).
/// Its own red is shown by <see cref="NDT1_TheCheckFailsOnASyntheticBadFile"/>, the same checks run over literal lines.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class BlindPairsWellFormedTests
{
    private static readonly (string Prefix, string Class, int Count)[] Expected =
    [
        ("bp", "paraphrase", 36), ("bn", "near-miss", 36), ("bc", "near-copy", 18), ("qo", "off-topic", 12), ("qm", "made-up", 12),
    ];

    private static string Retrieval(string name) => Path.Combine(ModelDirectory.RepositoryRoot(), "conformance", "retrieval", name);

    private static IEnumerable<string> StringMembers(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String: yield return e.GetString()!; break;
            case JsonValueKind.Object: foreach (var m in e.EnumerateObject()) foreach (var s in StringMembers(m.Value)) yield return s; break;
            default: break;
        }
    }

    /// <summary>The texts the blind file may not repeat: every title, body, joined text and query of the legacy pairs, the corpus and the canaries.</summary>
    private static HashSet<string> KnownTexts()
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in new[] { "dedupe-pairs.jsonl", "corpus.jsonl", "canaries.jsonl" })
            foreach (var line in File.ReadLines(Retrieval(file)).Where(l => l.Length > 0))
            {
                using var json = JsonDocument.Parse(line);
                foreach (var s in StringMembers(json.RootElement)) known.Add(s);
                foreach (var side in new[] { "a", "b" })
                    if (json.RootElement.TryGetProperty(side, out var text)) known.Add(text.GetProperty("title").GetString() + "\n" + text.GetProperty("body").GetString());
                if (json.RootElement.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String && json.RootElement.TryGetProperty("body", out var body))
                    known.Add(title.GetString() + "\n" + body.GetString());
            }

        return known;
    }

    /// <summary>The checks of the file over its lines, as problems named by check and id (never by text). An empty list is a well-formed file.</summary>
    internal static List<string> Problems(IReadOnlyList<string> lines, ISet<string> known)
    {
        var problems = new List<string>();
        var ids = new List<string>();
        var prefixes = Expected.ToDictionary(e => e.Prefix, e => e);
        foreach (var (line, number) in lines.Select((l, i) => (l, i + 1)))
        {
            JsonElement root;
            try { root = JsonDocument.Parse(line).RootElement; }
            catch (JsonException) { problems.Add($"parse:line {number}"); continue; }
            var id = root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String ? idElement.GetString()! : $"line {number}";
            ids.Add(id);
            var prefix = id.Length >= 2 ? id[..2] : string.Empty;
            if (!prefixes.TryGetValue(prefix, out var spec)) { problems.Add($"id-prefix:{id}"); continue; }
            var cls = root.TryGetProperty("class", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            if (cls != spec.Class) problems.Add($"class:{id}");

            var wanted = prefix switch
            {
                "qo" => new[] { "id", "class", "title", "body" },
                "qm" => ["id", "class", "text"],
                _ => ["id", "class", "a", "b", "reason"],
            };
            var keys = root.EnumerateObject().Select(m => m.Name).Order(StringComparer.Ordinal).ToArray();
            if (!keys.SequenceEqual(wanted.Order(StringComparer.Ordinal))) problems.Add($"extra-key:{id}");

            var texts = new List<string>();
            if (prefix is "qo") texts.Add(root.GetProperty("title").GetString() + "\n" + root.GetProperty("body").GetString());
            else if (prefix is "qm") texts.Add(root.GetProperty("text").GetString()!);
            else if (root.TryGetProperty("a", out var a) && root.TryGetProperty("b", out var b) && a.ValueKind == JsonValueKind.Object && b.ValueKind == JsonValueKind.Object)
            {
                foreach (var side in new[] { a, b })
                {
                    var sideKeys = side.EnumerateObject().Select(m => m.Name).Order(StringComparer.Ordinal).ToArray();
                    if (!sideKeys.SequenceEqual(["body", "title"])) problems.Add($"extra-key:{id}");
                }

                var textA = a.GetProperty("title").GetString() + "\n" + a.GetProperty("body").GetString();
                var textB = b.GetProperty("title").GetString() + "\n" + b.GetProperty("body").GetString();
                if (textA == textB) problems.Add($"identical-side:{id}");
                texts.AddRange([textA, textB]);
            }

            foreach (var s in StringMembers(root).Where(s => s != cls && s != id).Concat(texts))
                if (known.Contains(s)) { problems.Add($"known-text:{id}"); break; }
        }

        var expectedIds = Expected.SelectMany(e => Enumerable.Range(1, e.Count).Select(i => $"{e.Prefix}-{i:00}")).ToHashSet(StringComparer.Ordinal);
        if (ids.Count != ids.Distinct(StringComparer.Ordinal).Count() || !expectedIds.SetEquals(ids)) problems.Add($"id-set:{ids.Count} lines, {expectedIds.Count} expected");
        return problems;
    }

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex Sha1Hex();

    private static string Sha256Hex(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public void R8_89_NDT1_TheBlindFileIsWellFormed()
    {
        var lines = File.ReadAllLines(Retrieval("blind-pairs.jsonl")).Where(l => l.Length > 0).ToList();
        Assert.Equal(114, lines.Count);
        Assert.Empty(Problems(lines, KnownTexts()));
    }

    [Fact]
    public void R8_89_NDT1_TheProvenanceRecordMatchesTheFilesAsTheyAreNow()
    {
        using var provenance = JsonDocument.Parse(File.ReadAllText(Retrieval("blind-pairs.provenance.json")));
        var p = provenance.RootElement;
        Assert.Equal(Sha256Hex(Retrieval("AUTHOR-BRIEF.md")), p.GetProperty("brief_sha").GetString());
        Assert.Equal(Sha256Hex(Retrieval("blind-pairs.jsonl")), p.GetProperty("file_sha").GetString());

        var ruleSha = p.GetProperty("rule_sha").GetString()!;
        Assert.Matches(Sha1Hex(), ruleSha);

        var calls = p.GetProperty("author_tool_calls");
        Assert.True(calls.GetArrayLength() > 0, "the provenance record lists no tool call of the author");
        foreach (var call in calls.EnumerateArray())
            foreach (var target in call.GetProperty("targets").EnumerateArray())
            {
                var t = target.GetString()!;
                Assert.True(t.StartsWith("its own", StringComparison.Ordinal) || t.StartsWith("the output file", StringComparison.Ordinal), "an author tool call names a target that is neither the author's own script nor the output file");
            }

        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = ModelDirectory.RepositoryRoot() };
        foreach (var a in new[] { "merge-base", "--is-ancestor", ruleSha, "HEAD" }) start.ArgumentList.Add(a);
        try
        {
            using var process = Process.Start(start)!;
            process.WaitForExit();
            if (process.ExitCode == 1) Assert.Fail("rule_sha is not an ancestor of HEAD: the rule was not committed before the pairs were measured");
            TestContext.Current.SendDiagnosticMessage($"NDT1: git merge-base --is-ancestor exit {process.ExitCode} (0 = ancestor; 128 = git does not hold the commit)");
        }
        catch (Win32Exception)
        {
            TestContext.Current.SendDiagnosticMessage("NDT1: git is unavailable; the ancestry of rule_sha was not checked");
        }
    }

    /// <summary>The checks' own red: a pair with a cosine member, a pair with a duplicated side, an id out of range, a text of the legacy set, and a class that does not match its prefix.</summary>
    [Fact]
    public void R8_89_NDT1_TheCheckFailsOnASyntheticBadFile()
    {
        string[] bad =
        [
            """{"id":"bp-01","class":"paraphrase","a":{"title":"t1","body":"b1"},"b":{"title":"t2","body":"b2"},"reason":"r","cosine":0.5}""",
            """{"id":"bp-02","class":"paraphrase","a":{"title":"t3","body":"b3"},"b":{"title":"t3","body":"b3"},"reason":"r"}""",
            """{"id":"bp-37","class":"paraphrase","a":{"title":"t4","body":"b4"},"b":{"title":"t5","body":"b5"},"reason":"r"}""",
            """{"id":"bn-01","class":"near-copy","a":{"title":"t6","body":"b6"},"b":{"title":"t7","body":"b7"},"reason":"r"}""",
            """{"id":"bn-02","class":"near-miss","a":{"title":"legacy title","body":"legacy body"},"b":{"title":"t8","body":"b8"},"reason":"r"}""",
        ];
        var problems = Problems(bad, new HashSet<string>(StringComparer.Ordinal) { "legacy title\nlegacy body" });
        Assert.Contains("extra-key:bp-01", problems);
        Assert.Contains("identical-side:bp-02", problems);
        Assert.Contains(problems, x => x.StartsWith("id-set", StringComparison.Ordinal));
        Assert.Contains("class:bn-01", problems);
        Assert.Contains("known-text:bn-02", problems);
    }
}
