using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Curia.Canon.Json;
using Curia.Client.Cli;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// The facts that capture the process's standard streams. They swap <see cref="Console.Out"/> and
/// <see cref="Console.Error"/>, which every test in the assembly shares, so they run apart from the rest.
/// </summary>
[CollectionDefinition("console", DisableParallelization = true)]
public sealed class ConsoleGroup;

/// <summary>
/// R10.71 on the refusal path: a duplicate refusal's frame is written only from the posting path, with
/// answers this client verified; on any other route a duplicate-shaped problem is a refusal and nothing
/// more.
/// </summary>
[Collection("console")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class RefusalOutputTests
{
    /// <summary>
    /// <c>Program.RefusedAsync</c> over a duplicate refusal with one honest answer and one whose span is
    /// another envelope's rendering: one passage per answer on stdout, the mismatched one as R10.71's
    /// FAILED line and never its served word, the refusal on stderr (FA27's target).
    /// </summary>
    [Fact]
    public async Task R10_71_APostingRefusalShowsItsVerifiedAnswers()
    {
        var ct = TestContext.Current.CancellationToken;
        using var log = new StubLog { RenderedContent = SpanCheckTests.Envelope("zyzzyva, the Forum's word") };

        // The second answer: the same signed answer under another id, its span honest.
        var honest = JsonNode.Parse(log.AnswerJson())!.AsObject();
        honest["post_id"] = "01TESTPOSTID0000000000000Z";
        honest["rendered"] = Datamarking.Render((string)honest["canonical"]!, MarkingMode.None);

        var refusal = ReaderFrameTests.DuplicateRefusal(log, RefusalKind.Conflict, 409, honest);
        var duplicate = refusal.AsDuplicate;
        Assert.NotNull(duplicate);
        Assert.Equal(2, duplicate.Answers.Length);

        // Both kinds are present: two verified signatures, one span that renders its covered bytes and
        // one that does not.
        var (passages, outcomes) = await Program.VerifyAllAsync(log.Client(), duplicate.Answers, ct);
        Assert.All(passages, p => Assert.Equal(CheckOutcome.Verified, p.Verdict.Outcome));
        Assert.Equal(2, outcomes.Length);
        Assert.Single(outcomes, o => o == CheckOutcome.Verified);
        Assert.Single(outcomes, o => o == CheckOutcome.Failed);

        var (exit, stdout, stderr) = await CaptureAsync(() => Program.RefusedAsync(log.Client(), refusal, ct));

        Assert.Equal(ExitCode.For(refusal), exit);
        Assert.Equal(duplicate.Answers.Length, Count(stdout, "\npost      "));

        var lines = stdout.Split('\n');
        Assert.Equal(1, lines.Count(l => l.TrimStart(' ').StartsWith("content   FAILED:", StringComparison.Ordinal)));
        Assert.DoesNotContain("zyzzyva", stdout, StringComparison.Ordinal);

        Assert.StartsWith("duplicate of ", stdout, StringComparison.Ordinal);
        var overrideLine = stdout.IndexOf("\noverride     ", StringComparison.Ordinal);
        Assert.True(overrideLine > stdout.LastIndexOf("\npost      ", StringComparison.Ordinal), $"the override line is not last:\n{DisplayLiteral.Of(stdout)}");
        Assert.Equal(-1, stdout.IndexOf("\npost      ", overrideLine, StringComparison.Ordinal));

        Assert.StartsWith("error: ", stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// A duplicate-shaped problem served on a route other than posting (here a 404) prints its title,
    /// status and problem type on stderr and nothing on stdout: no count of answers this client did not
    /// check, and no frame (FA30's target).
    /// </summary>
    [Fact]
    public async Task R10_71_ADuplicateShapedRefusalOffThePostingPathPrintsNoFrame()
    {
        using var log = new StubLog();
        var refusal = ReaderFrameTests.DuplicateRefusal(log, RefusalKind.NotFound, 404);
        Assert.NotNull(refusal.AsDuplicate);

        var (exit, stdout, stderr) = await CaptureAsync(() => Task.FromResult(Output.Fail(refusal)));

        Assert.Equal(string.Empty, stdout);
        Assert.StartsWith("error: ", stderr, StringComparison.Ordinal);
        Assert.Contains("curia/posts/duplicate-question", stderr, StringComparison.Ordinal);
        Assert.Equal(ExitCode.For(refusal), exit);
    }

    private static Task<(int Exit, string Stdout, string Stderr)> CaptureAsync(Func<Task<int>> action) =>
        ConsoleCapture.CaptureAsync(action);

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal))
            count++;
        return count;
    }
}
