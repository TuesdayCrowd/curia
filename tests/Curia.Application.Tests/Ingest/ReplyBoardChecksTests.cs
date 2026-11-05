#pragma warning disable CA2007 // test code: there is no SynchronizationContext to resume on (as the other suites of this project do, per await, with ConfigureAwait(true))
using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ingest;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Application.Tests.Ingest;

/// <summary>
/// R8.87 (errata G61), RPB3: <see cref="SubmissionChecks.RevisionBoardRefusal"/> over facts, against hand-written answers. A revision's board is compared with the board stored for
/// the post whose digest is its <c>prev</c>, on the facts the route already folded; a log in which that post cannot be found is <c>revision-check-unavailable</c>, a failure of the
/// Forum's and never a pass (fail closed). The type, title and detail key are parsed from R8.87 words (<see cref="PublishedReplyBoardWords"/>).
/// Predicted red: the seam returns none for every row.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ReplyBoardChecksTests
{
    private const string Author = "https://agents.example/author";

    // Post ids as the log stores them (ULID-shaped): the refusal echoes a post id under R8.70's shape rule, so a fixture id that is not one would be answered "(not a post id)".
    private const string P1 = "01M5V0000000000000000000A1";
    private const string P2 = "01M5V0000000000000000000A2";

    private static string Word(Curia.Domain.Primitives.Error? e) => e is null ? "none" : $"{e.Type} {e.Detail}";

    [Fact]
    public async Task R8_87_RPB3_ARevisionIsComparedWithTheBoardOfThePostItRevisesAndAnAbsentOriginalFailsClosed()
    {
        var log = new LateCommitLog();
        await log.AppendPostAsync(P1, "question", Author, "{\"kind\":\"question\"}", board: "rust");
        await log.AppendPostAsync(P2, "question", Author, "{\"kind\":\"question\"}", board: "Meta");
        var facts = LogFacts.Of(await log.ReadAsync());
        var (_, originalKey) = PublishedReplyBoardWords.DetailKeys();
        var refusal = PublishedReplyBoardWords.Type();

        var said = string.Join(" | ",
        [
            "board of P1 itself: " + Word(SubmissionChecks.RevisionBoardRefusal(facts, LateCommitLog.Digest(P1), "rust")),
            "another board than P1's: " + Word(SubmissionChecks.RevisionBoardRefusal(facts, LateCommitLog.Digest(P1), "dotnet")),
            "P1's board in another case: " + Word(SubmissionChecks.RevisionBoardRefusal(facts, LateCommitLog.Digest(P1), "Rust")),
            "the stored board Meta, itself: " + Word(SubmissionChecks.RevisionBoardRefusal(facts, LateCommitLog.Digest(P2), "Meta")),
            "the stored board Meta, lower-cased: " + Word(SubmissionChecks.RevisionBoardRefusal(facts, LateCommitLog.Digest(P2), "meta")),
            "a prev no post bears: " + Word(SubmissionChecks.RevisionBoardRefusal(facts, "sha256:unknown", "rust")),
        ]);

        var expected = string.Join(" | ",
        [
            "board of P1 itself: none",
            $"another board than P1's: {refusal} {originalKey}{P1}",
            $"P1's board in another case: {refusal} {originalKey}{P1}",
            "the stored board Meta, itself: none",
            $"the stored board Meta, lower-cased: {refusal} {originalKey}{P2}",
            "a prev no post bears: curia/ingest/revision-check-unavailable ",
        ]);

        if (!string.Equals(expected, said, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {said}");
    }
}
