using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Curia.Client;
using Curia.Client.Cli;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// Errata G39 (R9.40) at the verb: <c>curia recheck</c> over a batch with an element that is not a digest prints a line for every element sent,
/// in the order sent, and then exits the code the errata names, ranked below a <c>FAILED</c> line and above a withheld or a disputed citation.
/// Driven through <see cref="Program.ReportRecheck"/> over the stub's batch route and <see cref="ConsoleCapture"/>, as <see cref="CliRecheckTests"/> is.
///
/// <para><b>Oracles.</b> The elements and digests the test sent, the lines it counted on standard output, the stub's own request list, and the exit
/// number and rank parsed from the errata (<see cref="PublishedCliRules"/>), never the verb's report of itself. Each negative probe has its control.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("console")]
public sealed class CliRecheckExitTests : IDisposable
{
    private static readonly string[] StateWords = ["current", "superseded", "withheld", "unknown", "malformed", "disputed", "FAILED"];

    private readonly StubLog _log = new();

    public void Dispose()
    {
        _log.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string PostIdOf(int n) => $"01ZQPB17QUESTION0000000000{n:00}";

    private static string Canon(int n) =>
        "{\"author\":\"urn:curia:agent:alice\",\"board\":\"b\",\"body\":\"A body.\",\"code_blocks\":[],\"content_type\":\"agent-authored/untrusted\","
        + "\"created_at\":\"2026-01-01T00:00:00.0000000+00:00\",\"kind\":\"question\",\"nonce\":\"n0\",\"refs\":[],\"tags\":[],\"title\":\"Question " + n + "\",\"v\":1}";

    private static string Sha(string canonical) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

    private sealed record Batch(string[] Sent, string[] Items);

    /// <summary>A batch from a pattern: <c>c</c> is a current post, <c>x</c> an element that is not a digest (carrying <paramref name="marker"/>).</summary>
    private Batch BatchOf(string pattern, string marker = "nope")
    {
        var sent = new List<string>();
        var items = new List<string>();
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == 'x')
            {
                sent.Add($"{marker}-{i}");
                items.Add(StubLog.BatchItem(null, "malformed"));
                continue;
            }

            var canonical = Canon(i + 1);
            var digest = Sha(canonical);
            sent.Add(digest);
            items.Add(StubLog.BatchItem(digest, "current", postJson: _log.BatchPostJson(PostIdOf(i + 1), canonical, digest)));
        }

        return new Batch([.. sent], [.. items]);
    }

    private async Task<(int Exit, string Stdout, string Stderr)> RecheckAsync(string[] sent, params string[] items)
    {
        _log.BatchAnswer = _ => items;
        var result = await _log.Client().BatchAsync(sent, MarkingMode.None, TestContext.Current.CancellationToken);
        Assert.True(result.TryGetValue(out var served, out var refusal), refusal?.Summary);
        return await ConsoleCapture.CaptureAsync(() => Task.FromResult(Program.ReportRecheck(sent, served)));
    }

    private static string[] StateLines(string stdout) =>
        [.. stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => StateWords.Any(w => l.StartsWith(w, StringComparison.Ordinal)))];

    [Theory]
    [InlineData("xcc")]
    [InlineData("cxc")]
    [InlineData("ccx")]
    [InlineData("xcx")]
    [InlineData("xx")]
    [InlineData("ccxcc")]
    public async Task R9_40_AMalformedElementExitsThreeAfterEveryLineIsPrinted(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        var batch = BatchOf(pattern);

        var (exit, stdout, _) = await RecheckAsync(batch.Sent, batch.Items);

        var lines = StateLines(stdout);
        Assert.Equal(PublishedCliRules.R940Exit(), exit);
        Assert.Equal(pattern.Length, lines.Length);
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == 'x') Assert.Contains($"digests[{i}]", lines[i], StringComparison.Ordinal);
            else Assert.Contains(batch.Sent[i], lines[i], StringComparison.Ordinal);
        }

        Assert.Single(_log.BatchRequests);
    }

    /// <summary>The control: the same valid elements without the malformed one exit 0 and print the same lines.</summary>
    [Fact]
    public async Task R9_40_AllValidExitsZeroAndOneMalformedElementIsTheOnlyDifference()
    {
        var without = BatchOf("cc");

        var (exit, stdout, _) = await RecheckAsync(without.Sent, without.Items);

        Assert.Equal(0, exit);
        Assert.Equal(2, StateLines(stdout).Length);
        Assert.Single(_log.BatchRequests);
    }

    [Fact]
    public async Task R9_40_TheRankIsFailedThenMalformedThenGoneThenDisputed()
    {
        var (below, above) = PublishedCliRules.R940Rank();
        var malformedExit = PublishedCliRules.R940Exit();
        var canonical = Canon(1);
        var digest = Sha(canonical);
        var other = Sha(Canon(2));
        var third = Sha(Canon(3));
        string Current(string postId, string text, string d, long? disputed = null) =>
            StubLog.BatchItem(d, "current", postJson: _log.BatchPostJson(postId, text, d, disputedBy: disputed));

        var rows = new (string Name, string[] Sent, string[] Items, int Expected)[]
        {
            // a served form that does not hash to the digest asked about is a check that ran and did not hold
            ("FAILED+malformed", [digest, "nope"], [StubLog.BatchItem(digest, "current", postJson: _log.BatchPostJson(PostIdOf(1), Canon(9), digest)), StubLog.BatchItem(null, "malformed")], below),
            ("malformed+withheld", ["nope", other], [StubLog.BatchItem(null, "malformed"), StubLog.BatchItem(other, "withheld")], malformedExit),
            ("malformed+disputed", ["nope", third], [StubLog.BatchItem(null, "malformed"), Current(PostIdOf(3), Canon(3), third, 42)], malformedExit),
            ("withheld+disputed", [other, third], [StubLog.BatchItem(other, "withheld"), Current(PostIdOf(3), Canon(3), third, 42)], above[0]),
        };

        Assert.Equal(6, below);
        Assert.Equal([5, 9], above);
        var mismatches = new List<string>();
        foreach (var (name, sent, items, expected) in rows)
        {
            var (exit, _, _) = await RecheckAsync(sent, items);
            if (expected != exit) mismatches.Add($"{name}: expected exit {expected}, got {exit}");
        }

        Assert.True(mismatches.Count == 0, string.Join("; ", mismatches));
    }

    /// <summary>The malformed line names a zero-based position and echoes nothing of the element (a marker in each is found nowhere on standard output).</summary>
    [Fact]
    public async Task R9_40_TheMalformedLineNamesAZeroBasedPositionAndEchoesNothing()
    {
        const string marker = "ZXQ-MARKER-7f3a";
        var batch = BatchOf("xcxx", marker);

        var (_, stdout, stderr) = await RecheckAsync(batch.Sent, batch.Items);

        var lines = StateLines(stdout);
        foreach (var position in new[] { 0, 2, 3 })
            Assert.Contains($"digests[{position}]", lines[position], StringComparison.Ordinal);
        Assert.DoesNotContain(marker, stdout + stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// A whole-batch refusal (more than the Forum's cap) prints no line and exits 3 already: the exit the verb gives a refusal of content, which is
    /// <see cref="Output.Fail(Refusal)"/> at the arm of <c>RecheckAsync</c> that precedes <see cref="Program.ReportRecheck"/> (the live run is the
    /// end-to-end probe PF22; the verb builds its own <c>HttpClient</c>, so this one drives the arm over the stub's cap answer).
    /// </summary>
    [Fact]
    public async Task R9_40_AWholeBatchRefusalStillExitsThreeAndPrintsNoLine()
    {
        _log.BatchCap = 64;
        var sent = Enumerable.Range(0, 65).Select(i => Sha(Canon(i))).ToArray();

        var result = await _log.Client().BatchAsync(sent, MarkingMode.None, TestContext.Current.CancellationToken);
        Assert.False(result.TryGetValue(out _, out var refusal));
        Assert.Equal(RefusalKind.Content, refusal!.Kind);
        var (exit, stdout, stderr) = await ConsoleCapture.CaptureAsync(() => Task.FromResult(Output.Fail(refusal)));

        Assert.Equal(PublishedCliRules.R940Exit(), exit);
        Assert.Empty(StateLines(stdout));
        Assert.Contains("batch-too-large", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R9_40_AMalformedElementIsStillSentToTheForum()
    {
        var batch = BatchOf("cxc");

        await RecheckAsync(batch.Sent, batch.Items);

        var request = Assert.Single(_log.BatchRequests);
        Assert.True(batch.Sent.SequenceEqual(request), "the request body did not hold every element, malformed included, in order");
    }
}
