using System.Diagnostics.CodeAnalysis;
using Curia.Client.Cli;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>What <c>curia help</c> publishes, read from the text it prints.</summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class HelpTextTests
{
    [Fact]
    public void R10_71_ExitCodeSixNamesTheSpanCheck()
    {
        var entry = ExitCodeEntry(6);

        Assert.Contains("R10.71", entry, StringComparison.Ordinal);
        Assert.Contains("content   FAILED:", entry, StringComparison.Ordinal);
    }

    /// <summary>
    /// The EXIT CODES entry for <paramref name="code"/>: its first line, beginning with the number, and
    /// every continuation line indented beneath it, up to the next entry or section.
    /// </summary>
    internal static string ExitCodeEntry(int code)
    {
        var lines = Help.Usage.Split('\n');
        var section = Array.FindIndex(lines, l => string.Equals(l.Trim(), "EXIT CODES", StringComparison.Ordinal));
        Assert.True(section >= 0, "the help text has no EXIT CODES section");

        var number = code.ToString(System.Globalization.CultureInfo.InvariantCulture) + "  ";
        var first = Array.FindIndex(lines, section + 1, l => l.TrimStart().StartsWith(number, StringComparison.Ordinal));
        Assert.True(first > section, $"EXIT CODES has no entry {code}");

        var indent = lines[first].Length - lines[first].TrimStart().Length;
        var entry = new List<string> { lines[first].Trim() };
        for (var i = first + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0 || line.Length - line.TrimStart().Length <= indent) break;
            entry.Add(line.Trim());
        }

        return string.Join(' ', entry);
    }
}
