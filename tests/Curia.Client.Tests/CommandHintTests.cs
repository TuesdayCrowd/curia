using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Canon.Json;
using Curia.Client.Cli;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R10.65 (errata G17) in the command-line client: every command it prints for its reader to run is
/// written by <see cref="Hints"/>, holds a Forum's values only as shell words, and is run by a shell
/// with exactly those values as its arguments.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class CommandHintTests : IDisposable
{
    private const string PostId = "01M0572TG0RAWZ1W6J2SZ5ZQ4E";

    private readonly string _home = Directory.CreateTempSubdirectory("curia-hints-").FullName;

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Sentinel => Path.Combine(_home, "ran");

    /// <summary>
    /// Each hint, with a Forum's values that are words, is a command a shell runs with exactly those
    /// values as its arguments -- an entity tag's <c>$(…)</c> included, which runs nothing. Printed as
    /// a display literal, as this plan first printed it, the same tag ran.
    /// </summary>
    [Fact]
    public void R10_65_AHintIsACommandAShellRunsWithTheValuesAsItsArguments()
    {
        var etag = "W/\"$(touch " + Sentinel + ")\"";
        var recheck = Hints.ReCheck(PostId, etag).Text;
        Assert.StartsWith("(re-check cheaply: curia read ", recheck, StringComparison.Ordinal);
        Assert.Equal(["read", PostId, "--if-none-match", etag], PosixShell.Argv(recheck["(re-check cheaply: ".Length..^1]));

        Assert.Equal(["thread", PostId], PosixShell.Argv(Hints.Thread(PostId).Text));
        Assert.Equal(["inbox", "...", "--cursor", "YzE6MTA="], PosixShell.Argv(Hints.More("curia inbox ...", "YzE6MTA=").Text));

        Assert.False(File.Exists(Sentinel), "a shell ran a command a Forum's value held");
    }

    public static TheoryData<string> ValuesNoWordHolds()
    {
        var data = new TheoryData<string>();
        foreach (var value in ShellWordTests.NotWords())
        {
            if (value is { Length: > 0 }) data.Add(value);
        }

        return data;
    }

    /// <summary>
    /// A value that is not a word leaves the command unprinted, and the line says so; a cursor, which
    /// is printed nowhere else, is printed as a display literal outside the command.
    /// </summary>
    [Theory]
    [MemberData(nameof(ValuesNoWordHolds))]
    public void R10_65_AHintWithAValueThatIsNotAWordPrintsNoCommand(string value)
    {
        var more = Hints.More("curia inbox ...", value).Text;

        foreach (var hint in new[] { Hints.ReCheck(PostId, value).Text, Hints.Thread(value).Text, more })
        {
            Assert.Contains(Hints.Withheld, hint, StringComparison.Ordinal);
            Assert.DoesNotContain("'" + value, hint, StringComparison.Ordinal);
            Assert.DoesNotContain('\n', hint);
        }

        Assert.Contains(DisplayLiteral.Of(value), more, StringComparison.Ordinal);
    }

    /// <summary>
    /// The rule holds where it is written: no interpolated string in the command-line client outside
    /// <see cref="Hints"/> writes a value after <c>curia</c> and a verb. Without this, the next hint
    /// written the old way prints a display literal into a command, as the five lines this stage found
    /// did. It reads a line at a time, so a command split across lines is past what it can see.
    /// </summary>
    [Fact]
    public void R10_65_EveryCommandTheCliPrintsWithAValueIsWrittenByHints()
    {
        var cli = Path.Combine(SourceRoot(), "Curia.Client.Cli");
        var command = new Regex("""\$"(?:[^"\\]|\\.)*\bcuria [a-z]+\b(?:[^"\\{]|\\.)*\{""", RegexOptions.CultureInvariant);

        var inHints = File.ReadLines(Path.Combine(cli, "Hints.cs")).Count(command.IsMatch);
        Assert.True(inHints >= 3, $"the pattern finds {inHints} lines in Hints.cs, where four put a command beside a value; a defect in this fact");

        var elsewhere = Directory.EnumerateFiles(cli, "*.cs")
            .Where(file => Path.GetFileName(file) != "Hints.cs")
            .SelectMany(file => File.ReadLines(file).Select((line, i) => (File: Path.GetFileName(file), Line: i + 1, Text: line)))
            .Where(site => command.IsMatch(site.Text))
            .Select(site => $"{site.File}:{site.Line}: {site.Text.Trim()}")
            .ToArray();

        Assert.True(elsewhere.Length == 0, "commands written with a value outside Hints, where no ShellWord guards it:\n" + string.Join('\n', elsewhere));
    }

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new InvalidOperationException($"src/ is not above {AppContext.BaseDirectory}");
    }
}
