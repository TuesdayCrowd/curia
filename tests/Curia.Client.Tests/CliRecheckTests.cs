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
/// Errata G35 (R9.34) at the verb: <c>curia recheck</c> prints the post id and spells a position zero-based, keeps the exit codes it had, and
/// refuses a response that answers other than what was sent. Driven through <see cref="Program.ReportRecheck"/> over the stub's batch route and
/// <see cref="ConsoleCapture"/>, as <see cref="RecheckDisputedTests"/> is.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("console")]
public sealed class CliRecheckTests : IDisposable
{
    private const string PostId = "01ZQPB17QUESTION000000000001";
    private static readonly int[] ExitsOfAnswers = [0, 1, 5, 9];
    private static readonly string[] StateWords = ["current", "superseded", "withheld", "unknown", "malformed", "disputed"];

    private readonly StubLog _log = new();

    public void Dispose()
    {
        _log.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string Canon(int n) =>
        "{\"author\":\"urn:curia:agent:alice\",\"board\":\"b\",\"body\":\"A body.\",\"code_blocks\":[],\"content_type\":\"agent-authored/untrusted\","
        + "\"created_at\":\"2026-01-01T00:00:00.0000000+00:00\",\"kind\":\"question\",\"nonce\":\"n0\",\"refs\":[],\"tags\":[],\"title\":\"Question " + n + "\",\"v\":1}";

    private static string Sha(string canonical) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

    private async Task<(int Exit, string Stdout, string Stderr)> RecheckAsync(string[] sent, params string[] items)
    {
        _log.BatchAnswer = _ => items;
        var result = await _log.Client().BatchAsync(sent, MarkingMode.None, TestContext.Current.CancellationToken);
        Assert.True(result.TryGetValue(out var served, out var refusal), refusal?.Summary);
        return await ConsoleCapture.CaptureAsync(() => Task.FromResult(Program.ReportRecheck(sent, served)));
    }

    [Theory]
    [InlineData("current", 0)]
    [InlineData("malformed", 3)]
    [InlineData("withheld", 5)]
    [InlineData("disputed", 9)]
    public async Task R9_34_CuriaRecheckPrintsThePostIdAndSpellsAPositionZeroBased(string kind, int exit)
    {
        var canonical = Canon(1);
        var digest = Sha(canonical);
        var other = Sha(Canon(2));

        // A malformed element is second, so a zero-based position is 1 and a one-based one is 2.
        var (sent, items) = kind switch
        {
            "malformed" => (new[] { digest, "nope" }, new[] { StubLog.BatchItem(digest, "current", postJson: _log.BatchPostJson(PostId, canonical, digest)), StubLog.BatchItem(null, "malformed") }),
            "withheld" => (new[] { digest, other }, new[] { StubLog.BatchItem(digest, "current", postJson: _log.BatchPostJson(PostId, canonical, digest)), StubLog.BatchItem(other, "withheld") }),
            "disputed" => (new[] { digest }, new[] { StubLog.BatchItem(digest, "current", postJson: _log.BatchPostJson(PostId, canonical, digest, disputedBy: 42)) }),
            _ => (new[] { digest }, new[] { StubLog.BatchItem(digest, "current", postJson: _log.BatchPostJson(PostId, canonical, digest)) }),
        };

        var (actual, stdout, _) = await RecheckAsync(sent, items);

        Assert.Equal(exit, actual);
        Assert.Contains(PostId, stdout, StringComparison.Ordinal);
        if (kind == "current")
        {
            Assert.Contains($"current     \"{digest}\"  the Forum reports no later revision", stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("cite as-is", stdout, StringComparison.Ordinal);
        }
        else if (kind == "malformed")
        {
            Assert.Contains("digests[1]", stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("[#", stdout, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task R9_34_CuriaRecheckRefusesAResponseThatAnswersOtherThanWhatWasSent()
    {
        var digests = new[] { Sha(Canon(1)), Sha(Canon(2)), Sha(Canon(3)) };
        var items = digests.Take(2).Select((d, i) => StubLog.BatchItem(d, "current", postJson: _log.BatchPostJson($"01ZQPB18QUESTION00000000000{i}", Canon(i + 1), d))).ToArray();

        var (exit, stdout, stderr) = await RecheckAsync(digests, items);

        var stateLines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => StateWords.Any(w => l.StartsWith(w, StringComparison.Ordinal))).ToArray();
        Assert.Empty(stateLines);
        Assert.False(string.IsNullOrWhiteSpace(stderr), "a response that answers other than what was sent was refused without a word on stderr");
        Assert.DoesNotContain(exit, ExitsOfAnswers);
    }

    /// <summary>
    /// A FAILED citation is a check that ran and did not hold (the served canonical form does not hash to the digest asked about), so it exits 6,
    /// as a signature that does not verify does, and not 8, which is a check that could not be run. It outranks a withheld citation (5) and a disputed one (9).
    /// </summary>
    [Fact]
    public async Task R9_34_AFailedCitationExitsSixAndOutranksTheOthers()
    {
        var asked = Sha(Canon(1));
        var other = Sha(Canon(2));
        string[] sent = [asked, other];
        var failing = StubLog.BatchItem(asked, "current", postJson: _log.BatchPostJson(PostId, Canon(9), asked));
        var withheld = StubLog.BatchItem(other, "withheld");

        var (exit, stdout, _) = await RecheckAsync(sent, failing, withheld);

        Assert.Contains("FAILED", stdout, StringComparison.Ordinal);
        Assert.Equal(6, exit);
    }

    /// <summary>
    /// R10.83 (errata G42): a fork branch, and a FORKED original, are information about which wording to cite and not a failure of the
    /// citation, so the verb's exit is the one it gave before (decision 3): 0 for both.
    /// </summary>
    [Fact]
    public async Task R10_83_AForkBranchDoesNotMoveTheExitCode()
    {
        var branch = Canon(1);
        var branchDigest = Sha(branch);
        var original = Canon(2);
        var originalDigest = Sha(original);
        var siblings = new[] { "sha256:" + new string('b', 64), "sha256:" + new string('c', 64) };

        var (branchExit, branchOut, _) = await RecheckAsync(
            [branchDigest],
            StubLog.BatchItem(branchDigest, "current", postJson: _log.BatchPostJson(PostId, branch, branchDigest), forkJson: StubLog.ForkJson((siblings[0], "01ZQPB19SIBLING0000000000001"))));
        var (forkedExit, forkedOut, _) = await RecheckAsync(
            [originalDigest],
            StubLog.BatchItem(originalDigest, "superseded", siblings, true, _log.BatchPostJson(PostId, original, originalDigest, [new Successor(siblings[0], null), new Successor(siblings[1], null)]), StubLog.ForkJson()));

        Assert.Equal(0, branchExit);
        Assert.Equal(0, forkedExit);

        // Controls: both lines were printed, and the original's is the FORKED one.
        Assert.Contains("current", branchOut, StringComparison.Ordinal);
        Assert.Contains("FORKED", forkedOut, StringComparison.Ordinal);
    }
}
