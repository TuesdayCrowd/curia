using System.Diagnostics.CodeAnalysis;
using Curia.Client;
using Curia.Client.Cli;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R6.59 (errata G33, closing D60): a line that reports a verification check says what the check did and no more. The
/// scenarios are the ones <see cref="PostVerifierTests"/> builds, each rendered and read line by line; what is asserted is
/// derived from the outcome the verifier produced (an inclusion that was not verified, a consistency that compared two equal
/// heads, a verdict that stands with nothing to compare against) and never from the words of a fix.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class VerificationWordingTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("curia-wording-").FullName;
    private readonly List<StubLog> _logs = [];

    public void Dispose()
    {
        foreach (var log in _logs) log.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private StubLog Log()
    {
        var log = new StubLog(5, 2, 1);
        _logs.Add(log);
        return log;
    }

    private static async Task<PostVerification> VerifyAsync(StubLog log, HeadStore heads)
    {
        var verifier = new PostVerifier(log.Client(), heads);
        var result = await verifier.VerifyAsync(StubLog.PostId, MarkingMode.None, TestContext.Current.CancellationToken);

        Assert.True(result.TryGetValue(out var verification, out var refusal), refusal?.Summary);
        return verification!;
    }

    private HeadStore Heads(string name) => new(Path.Combine(_home, name));

    /// <summary>A scenario's name, the verification it produced, and the verdict and exit code the unmodified tree gave it.</summary>
    private sealed record Scenario(string Name, PostVerification Verification, CheckOutcome Overall, int Exit);

    /// <summary>
    /// Six scenarios, each from a fresh retained-head directory: a first verification; the same head verified again; a post
    /// newer than the signed head; a log with no signed head; a fork at a size already seen; an altered entry payload.
    /// The verdicts and exits are written by hand from <see cref="PostVerifierTests"/> and <c>ExitCodeTests</c> (0, 8 and 6),
    /// not read back from the verifier.
    /// </summary>
    private async Task<IReadOnlyList<Scenario>> ScenariosAsync()
    {
        var scenarios = new List<Scenario>();

        var first = Heads("first");
        var intact = Log();
        scenarios.Add(new("first verification", await VerifyAsync(intact, first), CheckOutcome.Verified, 0));
        scenarios.Add(new("equal head", await VerifyAsync(intact, first), CheckOutcome.Verified, 0));

        var newer = Log();
        newer.HeadTreeSize = newer.PostIndex;
        scenarios.Add(new("post newer than the head", await VerifyAsync(newer, Heads("newer")), CheckOutcome.CouldNotCheck, 8));

        var unsigned = Log();
        unsigned.NoSignedHead = true;
        scenarios.Add(new("no signed head", await VerifyAsync(unsigned, Heads("unsigned")), CheckOutcome.CouldNotCheck, 8));

        var forkHeads = Heads("fork");
        await VerifyAsync(Log(), forkHeads);
        scenarios.Add(new("fork at a size already seen", await VerifyAsync(Log(), forkHeads), CheckOutcome.Failed, 6));

        var altered = Log();
        altered.AlterTheEntryPayload();
        scenarios.Add(new("altered entry payload", await VerifyAsync(altered, Heads("altered")), CheckOutcome.Failed, 6));

        return scenarios;
    }

    private static string[] Lines(PostVerification verification) =>
        verification.Render().Split('\n', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Derived from the outcome: for every verification whose inclusion was not verified, no line says the post is in the log.
    /// The scenarios must hold both kinds of non-verified inclusion, or the quantifier would be over nothing.
    /// </summary>
    [Fact]
    public async Task R6_59_NoInclusionLineSaysInTheLogUnlessInclusionVerified()
    {
        var scenarios = await ScenariosAsync();
        var notVerified = scenarios.Where(s => s.Verification.Inclusion.Outcome is not CheckOutcome.Verified).ToList();

        Assert.Contains(notVerified, s => s.Verification.Inclusion.Outcome is CheckOutcome.CouldNotCheck);
        Assert.Contains(notVerified, s => s.Verification.Inclusion.Outcome is CheckOutcome.Failed);
        Assert.NotEmpty(Lines(notVerified[0].Verification));

        foreach (var scenario in notVerified)
            foreach (var line in Lines(scenario.Verification))
                Assert.DoesNotContain("is in the log", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R6_59_ConsistencyOfAnEqualHeadSaysTwoHeadsWereComparedAndANoEarlierTreeWasExtended()
    {
        var scenarios = await ScenariosAsync();
        var equal = scenarios.Single(s => s.Name == "equal head").Verification;

        Assert.Equal(CheckOutcome.Verified, equal.Consistency.Outcome);
        Assert.Contains("equals", equal.Consistency.Detail, StringComparison.Ordinal);
        Assert.Contains("no earlier tree", equal.Consistency.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The not-checked sentence is on the closing text exactly when the verdict is VERIFIED and consistency was marked as
    /// having nothing to compare. The condition is read from the verification's own outcome and its
    /// <see cref="Check.NothingToCompare"/> mark, over every scenario, so a closing text that carries it always, or never,
    /// or on the wrong verdict, is each a different failure.
    /// </summary>
    [Fact]
    public async Task R6_59_AVerifiedClosingTextSaysConsistencyWasNotCheckedExactlyWhenNothingWasRetained()
    {
        var scenarios = await ScenariosAsync();

        Assert.Contains(scenarios, s => s.Verification.Overall is CheckOutcome.Verified && s.Verification.Consistency.NothingToCompare);
        Assert.Contains(scenarios, s => s.Verification.Overall is CheckOutcome.Verified && !s.Verification.Consistency.NothingToCompare);
        Assert.Contains(scenarios, s => s.Verification.Overall is not CheckOutcome.Verified);

        foreach (var scenario in scenarios)
        {
            var carries = scenario.Verification.Render().Contains("Consistency was not checked", StringComparison.Ordinal);
            var expected = scenario.Verification.Overall is CheckOutcome.Verified && scenario.Verification.Consistency.NothingToCompare;

            Assert.True(expected == carries, $"{scenario.Name}: closing text carries the not-checked sentence = {carries}, the outcome says it should be {expected}");
        }
    }

    /// <summary>Green first, held by the pair FSW10 and FSW14: the wording moves and the verdict and the exit code do not.</summary>
    [Fact]
    public async Task R6_59_NoVerdictAndNoExitMoved()
    {
        var scenarios = await ScenariosAsync();

        Assert.Equal(6, scenarios.Count);
        foreach (var scenario in scenarios)
        {
            Assert.Equal(scenario.Overall, scenario.Verification.Overall);
            Assert.Equal(scenario.Exit, ExitCode.ForOutcomes(CheckOutcome.Verified, CheckOutcome.Verified, scenario.Verification.Overall));
        }
    }
}
