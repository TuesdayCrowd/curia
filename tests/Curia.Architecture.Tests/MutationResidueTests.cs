using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// Trap 14: a restored file that is not the file you restored.
///
/// <para><b>Why this exists.</b> A falsification patch was rolled back imperfectly and left
/// <c>string.Equals(recomputed, recomputed, …)</c> — a value compared with itself — in a verifier.
/// The check was silently dead: the suite was green, the build clean at 0 warnings, and that very
/// comparison had been <i>successfully</i> falsified an hour earlier, which is what made it look
/// safe. Falsifying a check proves it worked at that moment; it says nothing about whether the
/// restore put it back. Only a new test found it.</para>
///
/// <para><b>And this file exists because the claim came first.</b> The stage that found the residue
/// wrote in its own record that "a scan for self-comparisons and mutation residue now runs over
/// <c>src/</c> before a commit". It did not: the scan was run by hand and never committed, so the
/// sentence was false the moment it merged — this project's named failure mode, in the write-up
/// about that failure mode. This is the scan, and it runs where every other gate does.</para>
///
/// <para><b>Deliberately crude.</b> It matches text, not semantics, and it will not catch a residue
/// spelled differently. <c>LayeringTests</c> makes the same trade in the same words — a grep gate
/// "honest about being crude ... beats a sophisticated check nobody wrote" — and this one would have
/// caught the defect that prompted it.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class MutationResidueTests
{
    /// <summary>
    /// A comparison of an expression with itself — the shape the restore left behind. Matches the
    /// two-argument comparison helpers this codebase actually uses.
    /// </summary>
    private static readonly Regex SelfComparison = new(
        @"(?:string\.Equals|Equals|SequenceEqual|CompareTo)\(\s*([A-Za-z_][\w.]*)\s*,\s*\1\s*[,)]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Short-circuits and markers a falsification patch leaves when it is not fully rolled back.
    /// <c>if (false</c> and <c>true ||</c> disable a check while compiling cleanly; the word
    /// FALSIFICATION is what this project's own patches write into the code they break.
    /// </summary>
    private static readonly Regex Residue = new(
        @"\btrue\s*\|\||\bfalse\s*&&|\bif\s*\(\s*false\b|FALSIFICATION",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// No shipped source compares a value with itself or carries a disabled check.
    ///
    /// <para>Run over <c>src/</c>, <c>tests/</c> and <c>tools/</c> alike: a falsification breaks
    /// production code, and the patch that restores it can miss either side.</para>
    ///
    /// <para><b>One fact rather than a theory per file.</b> A row per source file would report a
    /// failure more precisely and would add some 350 cases to a published count this project treats
    /// as a measurement. Every offender is named in the message instead, so precision costs nothing
    /// and the baseline stays comparable across stages.</para>
    /// </summary>
    [Fact]
    public void NoSourceFileCarriesMutationResidue()
    {
        var offenders = new List<string>();

        foreach (var file in Sources())
        {
            // This file necessarily contains the patterns it looks for.
            if (file.EndsWith("MutationResidueTests.cs", StringComparison.Ordinal)) continue;

            var lines = File.ReadAllText(file).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (SelfComparison.IsMatch(lines[i]))
                    offenders.Add($"{file}:{i + 1} compares a value with itself: {lines[i].Trim()}");

                if (Residue.IsMatch(lines[i]))
                    offenders.Add($"{file}:{i + 1} carries a disabled check: {lines[i].Trim()}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Mutation residue — a check that compiles cleanly and can never fail (trap 14). Restore "
            + "the intended operand or remove the short-circuit:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The scan is not vacuous: it found the real tree, and its patterns match the residue that
    /// prompted it.
    ///
    /// <para>Both halves matter. A scan enumerating nothing passes every theory row above by having
    /// no rows; a scan whose patterns match nothing passes by never firing. This asserts the
    /// enumeration is real and that the exact line the restore left behind would be caught.</para>
    /// </summary>
    [Fact]
    public void TheScanEnumeratesTheTreeAndItsPatternsBite()
    {
        var files = Sources();

        Assert.True(
            files.Length > 100,
            $"the scan found only {files.Length} source files, which is not this repository. A scan "
            + "over nothing reports that everything it saw was clean.");

        // The line that was actually left behind, verbatim.
        Assert.Matches(SelfComparison, "if (!string.Equals(recomputed, recomputed, StringComparison.Ordinal))");
        Assert.Matches(Residue, "        return true || string.Equals(a, b);");
        Assert.Matches(Residue, "        if (false && !EntryDescribes(entry, post))");

        // And they do not fire on ordinary code, or the gate would be unusable.
        Assert.DoesNotMatch(SelfComparison, "if (!string.Equals(recomputed, entry.LeafHash, StringComparison.Ordinal))");
        Assert.DoesNotMatch(Residue, "        return verified || retained is null;");
    }

    private static readonly string[] ScannedDirectories = ["src", "tests", "tools"];

    private static string[] Sources()
    {
        var root = FindRepositoryRoot();

        return
        [
            .. ScannedDirectories
                .Select(d => Path.Combine(root, d))
                .Where(Directory.Exists)
                .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .OrderBy(f => f, StringComparer.Ordinal),
        ];
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Curia.sln"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException($"Curia.sln is not above {AppContext.BaseDirectory}");
    }
}
