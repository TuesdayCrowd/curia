using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Curia.Application.Tests.Retrieval;

/// <summary>
/// Errata G81, plan KWC16 (spec): the three documents' cross-reference checks pass with R9.72, R10.102, R11.108 and the <c>(rev.)</c> rows indexed, and the sweep for "the search tokenizer" finds
/// each use either reworded (it says <c>Tokenize</c>, which is what R8.18 (rev.) says the dedupe's overlap is over) or deliberately kept and recorded here with its reason.
///
/// <para>After this stage "the search's terms" (R9.72's, the list removed) and "the search tokenizer's terms" (<c>Tokenize</c>'s) differ, and the dedupe's R8.18, R8.61 and
/// <c>THRESHOLD-RULE.md</c> all say the second in words that now read as the first. The sweep is the proof that none was left saying the wrong one. The G81 entry itself is excluded: it quotes the
/// phrase to revise it.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class StopwordSpecTests
{
    private static string Root => StopwordMeasure.RepoRoot();

    private static string Errata => "curia-whitepaper-ERRATA-AND-ADDENDUM.md";

    /// <summary>A use deliberately kept as written, by file, with the reason. Task 8 fills it from the sweep; a use not here and not reworded fails <see cref="KWC16_EveryUseOfTheSearchTokenizerIsRewordedOrRecorded"/>.</summary>
    private static readonly Dictionary<string, string> Kept = new(StringComparer.Ordinal);

    private static string Python(string script)
    {
        var start = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = Root };
        start.ArgumentList.Add("-I");
        start.ArgumentList.Add(Path.Combine(Root, "tools", "spec-checks", script));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("python3 did not start");
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? stdout : throw new InvalidOperationException($"{script} exited {process.ExitCode}: {stdout} {stderr.GetAwaiter().GetResult()}");
    }

    /// <summary>KWC16. <c>check-spec.py</c> and <c>falsify-spec-checks.py</c> exit 0, and the errata's index carries a row for each requirement G81 adds or revises.</summary>
    [Fact]
    public void KWC16_TheSpecChecksPassAndTheIndexCarriesG81sRows()
    {
        Python("check-spec.py");
        Python("falsify-spec-checks.py");

        var errata = File.ReadAllText(Path.Combine(Root, Errata), Encoding.UTF8);
        var index = errata[errata.IndexOf("# Consolidated proposed-requirements index", StringComparison.Ordinal)..];
        foreach (var row in new[] { "R9.72", "R10.102", "R11.108", "R9.67 (rev.)", "R10.99 (rev.)", "R11.93 (rev.)", "R8.18 (rev.)" })
            Assert.True(index.Contains($"| {row} |", StringComparison.Ordinal), $"Expected: the index holds a row for {row}  Actual: none");
    }

    private static IEnumerable<(string File, int Line, string Text)> Occurrences()
    {
        var files = new List<string>
        {
            Errata, "curia-agent-forum-WHITEPAPER.md", "curia-csharp-scoping.md",
        };
        foreach (var dir in new[] { "conformance/retrieval", "tools/retrieval", "src" })
            files.AddRange(Directory.EnumerateFiles(Path.Combine(Root, dir), "*", SearchOption.AllDirectories)
                .Where(f => !f.Contains("/bin/", StringComparison.Ordinal) && !f.Contains("/obj/", StringComparison.Ordinal) && !f.EndsWith(".jsonl", StringComparison.Ordinal) && !f.EndsWith(".pyc", StringComparison.Ordinal))
                .Select(f => Path.GetRelativePath(Root, f)));

        foreach (var file in files)
        {
            var lines = File.ReadAllLines(Path.Combine(Root, file), Encoding.UTF8);
            var inG81 = false;
            for (var i = 0; i < lines.Length; i++)
            {
                if (file == Errata)
                {
                    if (lines[i].StartsWith("## G81 ", StringComparison.Ordinal)) inG81 = true;
                    else if (lines[i].StartsWith("# ", StringComparison.Ordinal) || (inG81 && lines[i].StartsWith("## ", StringComparison.Ordinal))) inG81 = false;
                }

                if (!inG81 && SearchTokenizer().IsMatch(lines[i])) yield return (file, i + 1, lines[i]);
            }
        }
    }

    [GeneratedRegex("search('s)? tokeni[sz]er", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SearchTokenizer();

    /// <summary>
    /// KWC16. Every use of "the search tokenizer" over the three documents, <c>conformance/retrieval/</c>, <c>tools/retrieval/</c> and <c>src/</c> (the G81 entry apart) either names <c>Tokenize</c> on the
    /// line, which is the reworded form, or is a file recorded in <see cref="Kept"/> with its reason. The failure names each use by file and line, never its text.
    /// </summary>
    [Fact]
    public void KWC16_EveryUseOfTheSearchTokenizerIsRewordedOrRecorded()
    {
        var unresolved = Occurrences()
            .Where(o => !o.Text.Contains("Tokenize", StringComparison.Ordinal) && !Kept.ContainsKey(o.File))
            .Select(o => $"{o.File}:{o.Line}")
            .ToList();

        Assert.True(unresolved.Count == 0, $"Expected: every use reworded to name Tokenize or recorded as kept  Actual: {unresolved.Count} unresolved: {string.Join(", ", unresolved)}");
    }
}
