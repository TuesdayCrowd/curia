using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Curia.Client.Cli;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// WAF4 to WAF8, WAF6's help half (errata G53, R10.93): what <c>recheck</c> prints for a digest the Forum does not know, at the unit, over a stub's batch answer.
/// Every clause, order and figure is read from the errata's <c>R10.93 words</c> and <c>R10.93 wait</c> (<see cref="PublishedClientWords"/>), folded for whitespace;
/// the oracles of the instant are the instants the probe passes in, never the line's statement of itself. The probes that follow the line across the seam to the
/// act it speaks of are the Api suite's (<c>WordsAfterFaultKestrelTests</c>).
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("console")]
public sealed class RecheckUnknownWordsTests : IDisposable
{
    private static readonly string Digest = "sha256:" + new string('d', 64);

    private readonly StubLog _log = new();

    public void Dispose()
    {
        _log.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<CitationReport> ReportAsync(DateTimeOffset answeredAt, string[] sent, params string[] items)
    {
        _log.BatchAnswer = _ => items;
        var result = await _log.Client().BatchAsync(sent, MarkingMode.None, TestContext.Current.CancellationToken);
        Assert.True(result.TryGetValue(out var served, out var refusal), refusal?.Summary);
        return CitationLines.For(sent, served, answeredAt);
    }

    private static string InstantOf(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>The printed line for an unknown digest, whitespace folded.</summary>
    private async Task<string> UnknownLineAsync(DateTimeOffset at)
    {
        var report = await ReportAsync(at, [Digest], StubLog.BatchItem(Digest, "unknown"));
        Assert.Null(report.Error);
        return PublishedClientWords.Fold(Assert.Single(report.Lines).Text.Text);
    }

    /// <summary>
    /// WAF4. A digest that is a typo: the line holds, after the digest, the three clauses of R10.93 in the order the entry gives them (the resend clause, the wait
    /// clause, the spelling clause), each by its published start, and the whole published line with the instant the reader's clock took.
    /// </summary>
    [Fact]
    public async Task R10_93_WAF4_TheUnknownLineHoldsTheThreeClausesInTheirPublishedOrder()
    {
        var at = new DateTimeOffset(2026, 11, 1, 9, 30, 0, TimeSpan.Zero);
        var line = await UnknownLineAsync(at);
        var problems = new List<string>();

        var starts = PublishedClientWords.UnknownClauses().Select(PublishedClientWords.Fold).ToArray();
        var positions = starts.Select(start => line.IndexOf(start, StringComparison.Ordinal)).ToArray();
        for (var i = 0; i < starts.Length; i++)
        {
            if (positions[i] < 0) problems.Add($"lacks \"{starts[i]}\"");
        }

        if (positions.All(p => p >= 0) && !(positions[0] < positions[1] && positions[1] < positions[2])) problems.Add($"the clauses are out of order (at {string.Join(", ", positions)})");

        var published = PublishedClientWords.Fold(PublishedClientWords.UnknownLineAt(InstantOf(at)));
        if (!line.Contains(published, StringComparison.Ordinal)) problems.Add($"lacks the published line \"{published}\"");
        if (!Regex.IsMatch(line, "^unknown \"?" + Regex.Escape(Digest) + "\"? ", RegexOptions.CultureInvariant)) problems.Add("does not begin with the state word and the digest");

        Assert.True(problems.Count == 0, $"{string.Join("; ", problems)}. Printed: \"{line}\"");
    }

    /// <summary>
    /// WAF5. The instant is the reader's clock and not a constant: at two instants (sub-second parts included, which the form drops) the line holds each, to the
    /// second, in the published form, and a line at one instant does not hold the other.
    /// </summary>
    [Fact]
    public async Task R10_93_WAF5_TheLineStatesTheReadersOwnInstantToTheSecond()
    {
        var first = new DateTimeOffset(2026, 11, 1, 9, 30, 7, 912, TimeSpan.Zero);
        var second = new DateTimeOffset(2027, 2, 3, 22, 4, 59, 1, TimeSpan.Zero);
        var problems = new List<string>();

        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$", PublishedClientWords.SecondInstantExample);

        var lineAtFirst = await UnknownLineAsync(first);
        var lineAtSecond = await UnknownLineAsync(second);
        foreach (var (at, line) in new[] { (first, lineAtFirst), (second, lineAtSecond) })
        {
            var expected = $"(answered at {InstantOf(at)}, by this client's clock)";
            if (!line.Contains(expected, StringComparison.Ordinal)) problems.Add($"lacks \"{expected}\"");
        }

        if (lineAtFirst.Contains(InstantOf(second), StringComparison.Ordinal)) problems.Add("the line at the first instant holds the second");
        if (lineAtFirst == lineAtSecond) problems.Add("the two lines are equal");

        Assert.True(problems.Count == 0, $"{string.Join("; ", problems)}. At the first instant: \"{lineAtFirst}\"");
    }

    /// <summary>
    /// WAF6, the help's half. The help of <c>curia recheck</c> says that <c>unknown</c> names no post, carries the wait, and does not say that <c>unknown</c> means the
    /// citation is to be dropped. (The description of <c>curia_recheck</c> is held in <c>Curia.Mcp.Tests</c>.)
    /// </summary>
    [Fact]
    public void R10_93_WAF6_TheHelpOfRecheckSaysUnknownNamesNoPostAndDoesNotSayDrop()
    {
        var help = PublishedClientWords.Fold(Help.For("recheck") ?? string.Empty);
        var problems = new List<string>();

        if (Regex.IsMatch(help, @"\bunknown\b['""`]?\s+(says|means)\s+(to\s+)?drop", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)) problems.Add("says that unknown means drop the citation");
        if (!help.Contains("names no post", StringComparison.Ordinal)) problems.Add("lacks \"names no post\"");
        var clause = $"{PublishedClientWords.UnknownClauses()[1]} {PublishedClientWords.Wait} old";
        if (!help.Contains(clause, StringComparison.Ordinal)) problems.Add($"lacks the wait in its clause \"{clause}\"");

        Assert.True(problems.Count == 0, $"{string.Join("; ", problems)}. Help: \"{help}\"");
    }

    private static string Canon(int n) =>
        "{\"author\":\"urn:curia:agent:alice\",\"board\":\"b\",\"body\":\"A body.\",\"code_blocks\":[],\"content_type\":\"agent-authored/untrusted\","
        + "\"created_at\":\"2026-01-01T00:00:00.0000000+00:00\",\"kind\":\"question\",\"nonce\":\"n0\",\"refs\":[],\"tags\":[],\"title\":\"Question " + n + "\",\"v\":1}";

    private static string Sha(string canonical) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

    /// <summary>
    /// WAF7, a control (green on the seam tree; red under FWAF4 and FWAF5). The neighbours of <c>unknown</c> are as before: <c>withheld</c> and the two
    /// <c>malformed</c> lines byte for byte (the <c>withheld</c> line's "drop this citation" is the status quo, G53 non-property g), the <c>current</c> line carries
    /// none of R10.93's clauses, and <c>unknown</c> still exits 5 with the citation in the group the caller drops, while <c>current</c> exits 0.
    /// </summary>
    [Fact]
    public async Task R10_93_WAF7_TheNeighboursAndTheExitOfUnknownAreAsBefore()
    {
        var canonical = Canon(1);
        var current = Sha(canonical);
        var withheld = "sha256:" + new string('c', 64);
        string[] sent = [current, withheld, Digest, "nope"];
        var report = await ReportAsync(
            DateTimeOffset.UnixEpoch,
            sent,
            StubLog.BatchItem(current, "current", postJson: _log.BatchPostJson("01ZQPB0QUESTION000000000001", canonical, current)),
            StubLog.BatchItem(withheld, "withheld"),
            StubLog.BatchItem(Digest, "unknown"),
            StubLog.BatchItem(null, "malformed"));
        var problems = new List<string>();

        string[] starts = [.. PublishedClientWords.UnknownClauses().Select(PublishedClientWords.Fold)];
        var lines = report.Lines.Select(l => PublishedClientWords.Fold(l.Text.Text)).ToArray();
        if (lines.Length != 4) problems.Add($"{lines.Length} lines, not four");
        else
        {
            if (lines[1] != $"withheld \"{withheld}\" drop this citation; it is no longer served (withholding can be reversed -- re-check later)") problems.Add($"the withheld line moved: \"{lines[1]}\"");
            if (lines[3] != "malformed digests[3] not a digest; expected sha256:<64 lowercase hex>") problems.Add($"the malformed line moved: \"{lines[3]}\"");
            foreach (var start in starts)
            {
                if (lines[0].Contains(start, StringComparison.Ordinal)) problems.Add($"the current line carries \"{start}\"");
                if (lines[1].Contains(start, StringComparison.Ordinal)) problems.Add($"the withheld line carries \"{start}\"");
            }
        }

        if (!report.Lines[2].Gone || report.Lines[2].State != "unknown") problems.Add("the unknown line is not a citation to change, as before");

        _log.BatchAnswer = _ => [StubLog.BatchItem(Digest, "unknown")];
        var unknownOnly = await _log.Client().BatchAsync([Digest], MarkingMode.None, TestContext.Current.CancellationToken);
        Assert.True(unknownOnly.TryGetValue(out var unknownItems, out var unknownRefusal), unknownRefusal?.Summary);
        var (unknownExit, _, _) = await ConsoleCapture.CaptureAsync(() => Task.FromResult(Program.ReportRecheck([Digest], unknownItems)));
        if (unknownExit != ExitCode.NotFound) problems.Add($"unknown exits {unknownExit}, not {ExitCode.NotFound}");

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// WAF8, a control (green on the seam tree; red only under FWAF2's errata form). <c>PublishedClientWords</c> reads R10.93: three clause starts, each found in the
    /// published line, in the order the entry gives them; the line holds one <c>&lt;instant&gt;</c>; the wait is published once in R10.93 wait and the same figure
    /// is in the line.
    /// </summary>
    [Fact]
    public void R10_93_WAF8_ThePublishedClauseStartsAreFoundInThePublishedLineInOrder()
    {
        var line = PublishedClientWords.Fold(PublishedClientWords.UnknownLine);
        var starts = PublishedClientWords.UnknownClauses().Select(PublishedClientWords.Fold).ToArray();

        Assert.Equal(3, starts.Length);
        var positions = starts.Select(s => line.IndexOf(s, StringComparison.Ordinal)).ToArray();
        Assert.True(positions.All(p => p >= 0), $"a clause start is not in the line: {string.Join(", ", positions)}");
        Assert.True(positions[0] < positions[1] && positions[1] < positions[2], $"the clauses are out of order: {string.Join(", ", positions)}");
        Assert.Equal(1, Regex.Count(line, "<instant>", RegexOptions.CultureInvariant));
        Assert.Contains(PublishedClientWords.Wait, line, StringComparison.Ordinal);
    }
}
