using System.Diagnostics.CodeAnalysis;
using Curia.Client;
using Curia.Client.Cli;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R6.52 as the CLI's exit code: which of "this post is not sound" and "I could not check" the
/// caller is told, and the fact that they are different numbers.
///
/// <para><b>Why this is worth a test at all.</b> An exit code is the only thing a script reads, and
/// the collapse R6.52 forbids is invisible in prose output that nobody parses. Before this stage
/// <c>curia read</c> returned 6 — "a signature did not verify. The post exists; its authorship is
/// not established" — for a JWKS host that was merely down, which is a claim about the author on
/// the evidence of a network fault.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ExitCodeTests
{
    [Fact]
    public void R6_52_EverythingVerifiedIsSuccess()
    {
        Assert.Equal(
            ExitCode.Ok,
            ExitCode.ForOutcomes(CheckOutcome.Verified, CheckOutcome.Verified, CheckOutcome.Verified));
    }

    /// <summary>
    /// A check that could not run is the Forum's problem (8), never the post's (6). The three codes
    /// are asserted as distinct first, because an assertion that two calls return the same number
    /// is satisfied by a function that returns one number.
    /// </summary>
    [Fact]
    public void R6_52_ACheckThatCouldNotRunIsNotAFailedVerification()
    {
        Assert.NotEqual(ExitCode.Ok, ExitCode.Unverified);
        Assert.NotEqual(ExitCode.Unverified, ExitCode.ForumFault);

        Assert.Equal(
            ExitCode.ForumFault,
            ExitCode.ForOutcomes(CheckOutcome.Verified, CheckOutcome.CouldNotCheck));
    }

    /// <summary>
    /// A failure outranks an absence. A post whose signature is forged and whose log is also
    /// unreachable is reported as forged: the alarming claim is the one a caller must not miss.
    /// </summary>
    [Fact]
    public void R6_52_AFailureOutranksACheckThatCouldNotRun()
    {
        Assert.Equal(
            ExitCode.Unverified,
            ExitCode.ForOutcomes(CheckOutcome.Failed, CheckOutcome.CouldNotCheck, CheckOutcome.Verified));
    }

    /// <summary>
    /// Nothing to report is success. The read verbs call this with one outcome per passage, and a
    /// read that returned no posts must not invent a verdict about them.
    /// </summary>
    [Fact]
    public void AnEmptySetOfOutcomesIsSuccess() => Assert.Equal(ExitCode.Ok, ExitCode.ForOutcomes());

    /// <summary>
    /// The rule holds for every combination, not just the three above. Enumerated rather than
    /// sampled, because there are only nine and a rule stated for the cases somebody thought of is
    /// a rule with a hole in it.
    /// </summary>
    [Theory]
    [InlineData(CheckOutcome.Verified, CheckOutcome.Verified, 0)]
    [InlineData(CheckOutcome.Verified, CheckOutcome.CouldNotCheck, 8)]
    [InlineData(CheckOutcome.Verified, CheckOutcome.Failed, 6)]
    [InlineData(CheckOutcome.CouldNotCheck, CheckOutcome.Verified, 8)]
    [InlineData(CheckOutcome.CouldNotCheck, CheckOutcome.CouldNotCheck, 8)]
    [InlineData(CheckOutcome.CouldNotCheck, CheckOutcome.Failed, 6)]
    [InlineData(CheckOutcome.Failed, CheckOutcome.Verified, 6)]
    [InlineData(CheckOutcome.Failed, CheckOutcome.CouldNotCheck, 6)]
    [InlineData(CheckOutcome.Failed, CheckOutcome.Failed, 6)]
    public void R6_52_EveryPairOfOutcomesMapsToOneCode(CheckOutcome first, CheckOutcome second, int expected) =>
        Assert.Equal(expected, ExitCode.ForOutcomes(first, second));

    /// <summary>
    /// Every digest this CLI hands a user is in the form its own verbs accept.
    ///
    /// <para><b>The defect this pins.</b> <c>SignedSubmission</c> carries the digest twice — bare
    /// hex and the wire's <c>sha256:</c> form — and the post receipt printed the bare one under the
    /// label "digest". Every digest-consuming verb in the same CLI gates on
    /// <see cref="EnvelopeDigest.IsPrefixedForm"/>, which rejects bare hex on the length check
    /// alone, so <c>curia post</c> handed its user the right number in a spelling
    /// <c>curia endorse</c> answers with a usage error.</para>
    ///
    /// <para>The second assertion is what makes the first mean something: the bare form is not
    /// merely different, it is <i>unusable</i>, so printing it is not a stylistic choice.</para>
    /// </summary>
    [Fact]
    public void EveryDigestThePostReceiptPrintsIsInTheFormThisClientsOwnVerbsAccept()
    {
        using var root = new TemporaryRoot();
        var signed = root.Sign();

        Assert.True(EnvelopeDigest.IsPrefixedForm(signed.PrefixedDigest));
        Assert.False(EnvelopeDigest.IsPrefixedForm(signed.Digest));
    }

    /// <summary>
    /// And the receipt uses it. Crude on purpose: the receipt line sits inside a submit path that
    /// needs a live Forum, so the alternative to reading the source is no gate at all. This
    /// repository already argues for that trade where it applies — a grep gate "honest about being
    /// crude ... beats a sophisticated check nobody wrote" — and it would have caught the defect.
    /// </summary>
    [Fact]
    public void ThePostReceiptDoesNotPrintTheBareHexMember()
    {
        var program = Path.Combine(SourceRoot(), "Curia.Client.Cli", "Program.cs");
        var text = File.ReadAllText(program);

        // Non-vacuity: the file really is the one that prints the receipt.
        Assert.Contains("posted    ", text, StringComparison.Ordinal);
        Assert.Contains("submission.PrefixedDigest", text, StringComparison.Ordinal);

        Assert.DoesNotContain("submission.Digest", text, StringComparison.Ordinal);
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

    /// <summary>A throwaway identity, for signing one submission.</summary>
    private sealed class TemporaryRoot : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("curia-exit-code-").FullName;

        internal SignedSubmission Sign()
        {
            var store = new ProfileStore(_root);
            Assert.True(store
                .Create("alice", "https://agents.example/alice", "alice-1", new Uri("http://forum.test"))
                .TryGetValue(out var agent, out _));

            using (agent)
            {
                var draft = new PostDraft
                {
                    Kind = PostKind.Question,
                    Board = "b",
                    Title = "t",
                    Body = "A question, signed so its digest is a real one.",
                };

                Assert.True(SubmissionBuilder.Build(agent!, draft, DateTimeOffset.UnixEpoch)
                    .TryGetValue(out var signed, out _));

                return signed!;
            }
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// The numbers above are the ones the published help text names. Read from the constants rather
    /// than transcribed, so a renumbering moves both together instead of leaving this theory
    /// asserting yesterday's contract.
    /// </summary>
    [Fact]
    public void TheCodesInTheTheoryAreThePublishedOnes()
    {
        Assert.Equal(0, ExitCode.Ok);
        Assert.Equal(6, ExitCode.Unverified);
        Assert.Equal(8, ExitCode.ForumFault);
    }
}
