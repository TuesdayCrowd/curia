using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Tests.Shared;
using Xunit;

using Probe = Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>
/// AFC10 (over a real socket) and AFC18 (errata G55, R8.84, R11.76): the real <c>curia</c> against the Forum on Kestrel, with the decorated store armed. The oracle is the events
/// table by SQL (the digest recomputed from the act line, or the rows by board), never the status or the Forum's projection. The fixture's clock is moved forward to the real
/// time before each call, never back, because a child process signs its proofs with the real clock.
/// </summary>
[Collection("forum")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class FaultedAppendIsConfirmedSocketTests(ConfirmKestrelFixture forum) : IClassFixture<ConfirmKestrelFixture>, IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("curia-afc-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Address => forum.Client.BaseAddress!.GetLeftPart(UriPartial.Authority);

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    [GeneratedRegex(@"sha256:[0-9a-f]{64}", RegexOptions.CultureInvariant)]
    private static partial Regex Digest();

    private static string Fold(string text) => PublishedCheckWords.Fold(text);

    private async Task<(int Exit, string Text, string Stdout)> CuriaAsync(params string[] args)
    {
        var behind = DateTimeOffset.UtcNow - forum.Now;
        if (behind > TimeSpan.Zero) forum.Clock.Advance(behind);
        var run = await CuriaProcess.RunAsync(_home, Ct, args);
        return (run.Exit, Fold(run.Stdout + " " + run.Stderr), run.Stdout);
    }

    private async Task<string> EnrolAsync(string stem)
    {
        var slug = $"{stem}-{Guid.NewGuid().ToString("N")[..8]}";
        var enrolled = await CuriaAsync("enrol", "--agent", slug, "--forum", Address);
        Assert.True(enrolled.Exit == 0, enrolled.Text);
        return slug;
    }

    private Task<(int Exit, string Text, string Stdout)> AskAsync(string slug, string board) =>
        CuriaAsync("ask", "--agent", slug, "--board", board, "--title", $"About a quokka {Guid.NewGuid():N}?", "--body", "A question about the quokka.", "--forum", Address);

    /// <summary>
    /// AFC10, over a real socket. A post whose append commits and then throws, asked by the real <c>curia</c> over Kestrel, prints an ordinary receipt and exits 0; the
    /// table grew by exactly the post (count and highest sequence, read by SQL) and no check is owed: the confirm wrote nothing and the found post owes nothing.
    /// </summary>
    [Fact]
    public async Task R8_84_AFC10_AFoundPostOverARealSocketIsAnOrdinaryReceiptAndTheConfirmWritesNothing()
    {
        await using var host = forum.Itself();
        var slug = await EnrolAsync("afc10");
        var board = "afc10-" + Guid.NewGuid().ToString("N")[..8];
        var before = await host.LogShapeAsync(Ct);
        host.Store.Arm(EventStoreFault.AppendThenThrow);
        var asked = await AskAsync(slug, board);
        host.Store.Disarm();
        var after = await host.LogShapeAsync(Ct);
        var state = await host.StateAsync(Ct);
        var posted = asked.Stdout.Contains("posted", StringComparison.Ordinal);

        Probe.Said(
            "exit 0 with an ordinary receipt (posted); the table grew by 1 event and its highest sequence by 1; no check is owed",
            $"exit {asked.Exit} {(posted ? "with an ordinary receipt (posted)" : "without a receipt: \"" + asked.Text + "\"")}; the table grew by {after.Count - before.Count} event{(after.Count - before.Count == 1 ? string.Empty : "s")} and its highest sequence by {after.MaxSeq - before.MaxSeq}; {(state.Owed ? "a check is owed" : "no check is owed")}");
    }

    /// <summary>
    /// AFC18. The instruction followed across the seam (trap 89). From a late commit (the Forum did not find the post) through the real <c>curia ask</c>: the digest is taken from
    /// the act line; <c>curia recheck</c> of it says <c>unknown</c>; after the commit lands it says <c>current</c> and the table holds one post, so following the sentence stores
    /// nothing twice. From a commit that the Forum confirms, <c>curia ask</c> prints an ordinary receipt and nothing the sentence is about.
    /// </summary>
    [Fact]
    public async Task R11_76_AFC18_TheInstructionIsFollowedAcrossTheSeamAndAConfirmedPostPrintsNoSentence()
    {
        await using var host = forum.Itself();
        var slug = await EnrolAsync("afc18");

        // The not-confirmed leg (the late commit): the sentence's instruction, followed.
        host.Store.Arm(EventStoreFault.ThrowThenCommitWhenReleased);
        var late = await AskAsync(slug, "afc18-late-" + Guid.NewGuid().ToString("N")[..8]);
        host.Store.Disarm();
        var digest = Digest().Matches(late.Text).Select(m => m.Value).LastOrDefault();
        var unknown = digest is null ? string.Empty : (await CuriaAsync("recheck", digest, "--forum", Address)).Text;
        host.Store.ReleaseLateCommit();
        await host.Store.LateCommitted.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        var current = digest is null ? string.Empty : (await CuriaAsync("recheck", digest, "--forum", Address)).Text;
        var stored = digest is null ? 0 : (await Afc.StoredAsync(forum, digest, Ct)).Count;

        // The confirmed leg: nothing the sentence is about.
        host.Store.Arm(EventStoreFault.AppendThenThrow);
        var confirmed = await AskAsync(slug, "afc18-found-" + Guid.NewGuid().ToString("N")[..8]);
        host.Store.Disarm();
        var sentence = Fold(PublishedConfirmWords.StoreSentence);
        var confirmedClean = confirmed.Exit == 0 && confirmed.Stdout.Contains("posted", StringComparison.Ordinal) && !confirmed.Text.Contains(sentence, StringComparison.Ordinal) && !confirmed.Text.Contains("The act sent:", StringComparison.Ordinal);

        Probe.Said(
            "the late ask prints an act line with a digest; recheck says unknown, then current once the commit landed; the table holds 1 post of the digest; a confirmed ask exits 0 with an ordinary receipt and prints neither the sentence nor an act line",
            $"the late ask prints {(digest is null ? "no act line digest" : "an act line with a digest")}; recheck says {(unknown.Length == 0 ? "nothing (no digest)" : unknown.Contains("unknown", StringComparison.OrdinalIgnoreCase) ? "unknown" : "something else")}, " +
            $"then {(current.Length == 0 ? "nothing" : current.Contains("current", StringComparison.OrdinalIgnoreCase) ? "current" : "something else")} once the commit landed; the table holds {stored} post{(stored == 1 ? string.Empty : "s")} of the digest; " +
            (confirmedClean ? "a confirmed ask exits 0 with an ordinary receipt and prints neither the sentence nor an act line" : $"a confirmed ask exits {confirmed.Exit} and prints \"{confirmed.Text}\""));
    }
}
