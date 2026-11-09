using System.Diagnostics.CodeAnalysis;
using Curia.Api.Tests.Fuzz;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R11.88 (errata G63), LOB39's helper half: <see cref="FirstAsk"/> asked twice on one board over the real Forum and the real command. The first ask makes the board and its output carries the board line for a
/// flag that was given (<c>--new-board was given, so this post makes the board</c>); the second names no flag, is not refused, and its output carries neither the refusal nor the words for a flag that was not
/// needed. A helper that passed the flag on every ask would print <c>was not needed</c> the second time, and that is this fact's red.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class FirstAskTests(KestrelForumFixture forum) : IClassFixture<KestrelForumFixture>, IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("curia-first-ask-").FullName;

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Address => forum.Client.BaseAddress!.GetLeftPart(UriPartial.Authority);

    [Fact]
    public async Task R11_88_LOB39_TheSecondAskOnABoardPassesNoFlagAndSaysNothingWasNotNeeded()
    {
        var ct = TestContext.Current.CancellationToken;
        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now);   // a child process signs its proofs with the real clock
        Assert.Equal(0, (await CuriaProcess.RunAsync(_home, ct, "enrol", "--agent", "first-asker", "--forum", Address)).Exit);
        var board = "first-ask-" + Guid.NewGuid().ToString("N")[..8];

        var first = await FirstAsk.AskAsync(args => CuriaProcess.RunAsync(_home, ct, args), "first-asker", board, $"A first question {Guid.NewGuid():N}?", $"About the first quokka {Guid.NewGuid():N}.", Address, makesBoard: true);
        var second = await FirstAsk.AskAsync(args => CuriaProcess.RunAsync(_home, ct, args), "first-asker", board, $"A second question {Guid.NewGuid():N}?", $"About the second wombat {Guid.NewGuid():N}.", Address, makesBoard: false);

        var made = PublishedBoardListWords.States().NewByFlag;
        var neverNeeded = PublishedBoardListWords.States().ListedWithFlag;
        var neededTail = neverNeeded[(neverNeeded.IndexOf("; ", StringComparison.Ordinal) + 2)..];

        NameProbe.Said(
            [
                "first: exit 0, the line for a flag that was given",
                "second: exit 0, no refusal, no words for a flag that was not needed",
            ],
            [
                $"first: exit {first.Exit}{(first.Exit == 0 ? string.Empty : " (" + first.Stderr + ")")}, {(first.Stdout.Contains(made, StringComparison.Ordinal) ? "the line for a flag that was given" : "not the line for a flag that was given: " + first.Stdout + first.Stderr)}",
                $"second: exit {second.Exit}, {(second.Stdout.Contains(neededTail, StringComparison.Ordinal) ? "the words for a flag that was not needed" : second.Stderr.Contains("NOT SENT", StringComparison.Ordinal) ? "a refusal: " + second.Stderr : "no refusal, no words for a flag that was not needed")}",
            ]);
    }
}
