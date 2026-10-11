using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Client.Cli;
using Curia.Domain.Serving;
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
        Assert.Contains("NOT A DELIMITED SPAN:", entry, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every line EXIT CODES 6 names as a failed span's marker is one a failed span's passage writes, and
    /// each is written only by the failure it names. The markers are taken from the help, not written
    /// here, so the help and the frame code check each other (FA29's target).
    /// </summary>
    [Fact]
    public void R10_71_EveryLineExitSixNamesIsOneAFailedSpanWrites()
    {
        var markers = Regex.Matches(ExitCodeEntry(6), "'([^']+:)'")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(
            markers.SetEquals(["content   FAILED:", "NOT A DELIMITED SPAN:"]),
            "EXIT CODES 6 names these markers: " + string.Join(" | ", markers));

        var verdict = new SignatureVerdict(true, "k", "d");
        var delimited = SpanCheckTests.Post(
            SpanCheckTests.Envelope("The words the author signed."),
            Datamarking.Render(SpanCheckTests.Envelope("zyzzyva"), MarkingMode.None),
            MarkingMode.None);
        var undelimited = delimited with { Rendered = "undelimited" };

        var passages = new Dictionary<string, Passage>(StringComparer.Ordinal)
        {
            ["delimited"] = new Passage(delimited, verdict),
            ["undelimited"] = new Passage(undelimited, verdict),
        };
        foreach (var passage in passages.Values)
            Assert.Equal(CheckOutcome.Failed, passage.Outcome);

        var owner = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["content   FAILED:"] = "delimited",
            ["NOT A DELIMITED SPAN:"] = "undelimited",
        };
        foreach (var marker in markers)
        {
            foreach (var (name, passage) in passages)
            {
                var writes = passage.Render().Split('\n').Any(l => l.TrimStart(' ').StartsWith(marker, StringComparison.Ordinal));
                Assert.True(
                    writes == (owner[marker] == name),
                    $"the {name} passage {(writes ? "writes" : "does not write")} a line beginning '{marker}'");
            }
        }
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
