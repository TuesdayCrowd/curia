using System.Diagnostics.CodeAnalysis;
using Curia.Canon.Json;
using Curia.Client;
using Curia.Client.Cli;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R11.61 (errata G45): <c>curia</c> words the Forum's three <c>409</c>s on a submission by their cause, never as a tier or an ownership denial
/// (probes RVO31, RVO32, RVO34 and the client half of RVO50). The golden phrases are <see cref="RevisionWording"/>'s constants, which the errata's
/// requirement text fixes and which the refusal, the help and the tool descriptions share; the display literal of the post id the Forum gave is
/// <see cref="DisplayLiteral.Of"/>'s. Each fact reports the phrases that are missing, so a red names what the reader does not say.
///
/// <para>The exit code (3, <c>RefusalKind.Conflict</c>) is already the one a 409 gets, so that half of each fact is green on the unmodified tree; the
/// wording is the half that is red. The Forum's slug, title and detail come from the same factory the route uses
/// (<c>IngestErrors</c>), written here as literals because this assembly does not reference <c>Curia.Application</c>; RVO36 holds the stub to the
/// factory.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("console")]
public sealed class RevisionRefusalWordsTests
{
    /// <summary>Asserts two descriptions are equal and says both in full when they are not: xUnit shortens a long string's difference, and a red text is what these probes leave behind.</summary>
    private static void Said(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    private const string SuccessorId = "01REVISIONID00000000000009";
    private const string RecordedId = "01RECORDEDID00000000000009";

    private static Refusal AlreadyRevised() =>
        new(RefusalKind.Conflict, 409, new Error(RevisionWording.AlreadyRevisedType, "This post already has a revision", $"successor={SuccessorId}"));

    private static Refusal InFlight() =>
        new(RefusalKind.Conflict, 409, new Error(RevisionWording.RevisionInFlightType, "Another revision of this post is being recorded", "retry once the other revision is recorded (R8.77)"));

    private static Refusal AlreadyRecorded() =>
        new(RefusalKind.Conflict, 409, new Error(RevisionWording.AlreadyRecordedType, "This post is already recorded", $"post={RecordedId}"));

    private static string Missing(string summary, params string[] phrases) =>
        string.Join("; ", phrases.Where(p => !summary.Contains(p, StringComparison.OrdinalIgnoreCase)).Select(p => $"missing \"{p}\""));

    private static string NotATierOrOwnership(Refusal refusal) =>
        refusal.Kind == RefusalKind.Authorization || refusal.Summary.Contains("trust tier", StringComparison.OrdinalIgnoreCase)
            || refusal.Summary.Contains("owner", StringComparison.OrdinalIgnoreCase)
            ? "worded as a tier or ownership denial"
            : string.Empty;

    /// <summary>RVO31.</summary>
    [Fact]
    public void R11_61_AnAlreadyRevisedRefusalNamesTheSuccessorAndSaysToReviseIt()
    {
        var refusal = AlreadyRevised();

        var problems = string.Join(
            "; ",
            new[]
            {
                Missing(refusal.Summary, RevisionWording.AlreadyRevisedCause, DisplayLiteral.Of(SuccessorId), RevisionWording.AlreadyRevisedRemedy, RevisionWording.AlreadyRevisedSameText),
                NotATierOrOwnership(refusal),
                ExitCode.For(refusal) == 3 ? string.Empty : $"exit {ExitCode.For(refusal)}",
            }.Where(p => p.Length > 0));

        Said(string.Empty, problems);
    }

    /// <summary>RVO32. Retryable, nothing stored or spent, read the post first; distinct from the other two.</summary>
    [Fact]
    public void R11_61_ARevisionInFlightRefusalIsRetryableAndNotFinal()
    {
        var refusal = InFlight();

        var problems = string.Join(
            "; ",
            new[]
            {
                Missing(refusal.Summary, RevisionWording.InFlightCause, RevisionWording.NothingStoredOrSpent, RevisionWording.InFlightRetry),
                NotATierOrOwnership(refusal),
                refusal.Summary == AlreadyRevised().Summary || refusal.Summary == AlreadyRecorded().Summary ? "not distinct from the other two" : string.Empty,
                refusal.Summary.Contains("Another flag", StringComparison.Ordinal) ? "worded as the flag gate's refusal" : string.Empty,
                ExitCode.For(refusal) == 3 ? string.Empty : $"exit {ExitCode.For(refusal)}",
            }.Where(p => p.Length > 0));

        Said(string.Empty, problems);
    }

    /// <summary>RVO50, the refusal half. The post is the one just sent; nothing was stored or spent; read it and do not send it again; not retryable as sent.</summary>
    [Fact]
    public void R11_61_AnAlreadyRecordedRefusalNamesThePostAndSaysNotToSendItAgain()
    {
        var refusal = AlreadyRecorded();

        var problems = string.Join(
            "; ",
            new[]
            {
                Missing(refusal.Summary, RevisionWording.AlreadyRecordedCause, DisplayLiteral.Of(RecordedId), RevisionWording.NothingStoredOrSpent, RevisionWording.AlreadyRecordedRemedy),
                NotATierOrOwnership(refusal),
                refusal.Summary.Contains("retry", StringComparison.OrdinalIgnoreCase) ? "worded as retryable" : string.Empty,
                ExitCode.For(refusal) == 3 ? string.Empty : $"exit {ExitCode.For(refusal)}",
            }.Where(p => p.Length > 0));

        Said(string.Empty, problems);
    }

    /// <summary>RVO34, the help half: the entry for <c>revision</c> states the refusal, and the entry for exit 3 names the retryable slug beside the flag gate's.</summary>
    [Fact]
    public void R11_61_TheHelpStatesTheRefusalAndTheRetryableSlug()
    {
        var revision = Help.For("revision");
        var exits = Help.Usage;

        var problems = string.Join(
            "; ",
            new[]
            {
                revision is null ? "no help for revision" : Missing(revision, RevisionWording.SecondRevisionRefused),
                Missing(exits, RevisionWording.RevisionInFlightType),
                (revision ?? string.Empty).Contains("not in", StringComparison.Ordinal) && (revision ?? string.Empty).Contains("landing", StringComparison.Ordinal) ? "says a revision landing after the read is not seen" : string.Empty,
            }.Where(p => p.Length > 0));

        Said(string.Empty, problems);
    }
}
