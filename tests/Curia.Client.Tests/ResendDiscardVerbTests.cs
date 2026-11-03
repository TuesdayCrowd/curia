#pragma warning disable CA1416 // modes are set and read as PrivateFiles does

using System.Diagnostics.CodeAnalysis;
using Curia.Domain.Content;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// ASA47 to ASA49 at the real command (errata G58, R11.79, plan rulings 40 and 41): <c>curia acts --discard</c> and the qualified verb names, in process against a scripted Forum. Every word is read
/// from the errata (<see cref="PublishedResendWords"/>), folded for whitespace; what a probe says of the files is read from the directory.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Each kit is a using declaration; the analyzer loses the ownership across the awaits.")]
[Collection("console")]
public sealed class ResendDiscardVerbTests
{
    private static string Fold(string text) => CliRun.Fold(text);

    /// <summary>
    /// ASA47, the command's half. A file that cannot be removed (a read-only directory): <c>KeptActs.Discard</c> answers a failed outcome and does not throw, and <c>curia acts --discard</c> prints the published
    /// <c>NOT DISCARDED</c> words with the reason as a display literal and exits 1, the file still there. Skipped where mode bits do not bind. Predicted red: an unhandled <c>UnauthorizedAccessException</c>.
    /// </summary>
    [Fact]
    public async Task R11_79_ASA47_AFileThatCannotBeRemovedIsNotDiscardedAndExitsOne()
    {
        using var kit = new SessionKit();
        var act = kit.Build();
        var path = kit.Plant(act);
        var mode = File.GetUnixFileMode(kit.ActsDirectory);
        File.SetUnixFileMode(kit.ActsDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var binds = false;
            try { using var probe = File.Open(Path.Combine(kit.ActsDirectory, "probe.tmp"), FileMode.CreateNew); }
            catch (UnauthorizedAccessException) { binds = true; }
            Assert.SkipUnless(binds, "mode bits do not bind on this host (a superuser or a host without them)");

            var outcome = new KeptActs(kit.Store, "alice").Discard(act.PrefixedDigest);
            var run = await CliKit.RunAsync(kit, "acts", "--discard", act.PrefixedDigest);

            var expected = Fold(PublishedResendWords.RemovalFailedWords.Replace("<reason as a display literal>", "\"UnauthorizedAccessException\"", StringComparison.Ordinal));
            var problems = new List<string>();
            if (outcome.Status != DiscardStatus.Failed) problems.Add($"the library's outcome is {outcome.Status}, expected Failed");
            if (run.Exit != 1) problems.Add($"exit {run.Exit}, expected 1");
            if (!run.Text.Contains(expected, StringComparison.Ordinal)) problems.Add($"lacks \"{expected}\" (\"{run.Text}\")");
            if (!File.Exists(path)) problems.Add("the file is gone");
            Red.None(problems, "a failed outcome without a throw, the published NOT DISCARDED words with the reason, exit 1, the file kept");
        }
        finally
        {
            File.SetUnixFileMode(kit.ActsDirectory, mode);
        }
    }

    /// <summary>
    /// ASA48, the damaged leg at the command. A kept file whose header cannot be read, named by its digest, is listed with the digest and how to drop it, and <c>acts --discard</c> of that digest removes it (the file nothing can
    /// name was a slot held for good). A file whose name is no digest says nothing of dropping it. Predicted red: <c>the damaged line names no digest</c>.
    /// </summary>
    [Fact]
    public async Task R11_79_ASA48_ADamagedFileIsListedByItsDigestAndCanBeDropped()
    {
        using var kit = new SessionKit();
        kit.Plant(kit.Build());
        var hex = new string('c', 64);
        await File.WriteAllTextAsync(Path.Combine(kit.ActsDirectory, hex + ".act"), "not a header\n", Ct);
        await File.WriteAllTextAsync(Path.Combine(kit.ActsDirectory, "other.act"), "not a header\n", Ct);

        var listing = await CliKit.RunAsync(kit, "acts");
        var problems = new List<string>();
        var line = listing.Text;
        if (!line.Contains($"`curia acts --discard sha256:{hex}` (in `curia-mcp`, `curia_discard`) drops it", StringComparison.Ordinal)) problems.Add($"the damaged line names no digest and how to drop it (\"{line}\")");
        if (line.Contains("other.act\"  its first line", StringComparison.Ordinal) && line.Contains("sha256:other", StringComparison.Ordinal)) problems.Add("a name that is no digest was given a digest");

        var discard = await CliKit.RunAsync(kit, "acts", "--discard", "sha256:" + hex);
        if (discard.Exit != 0 || File.Exists(Path.Combine(kit.ActsDirectory, hex + ".act"))) problems.Add($"the damaged file was not dropped by its digest (exit {discard.Exit})");
        Red.None(problems, "the damaged file's line names its digest and the discard, and the discard removes it");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// ASA49, the command's half (plan ruling 41). The published sentences, as <c>curia</c> prints them: (a), the stop sentence, the 64 refusal, the act not kept, the discard words, the listing's last line and a damaged
    /// file's line each name every verb with its <c>(in `curia-mcp`, `curia_x`)</c> and no tool name outside one; and the published sentence is present with the digest filled in. Predicted red: a bare verb.
    /// </summary>
    [Fact]
    public async Task R11_81_ASA49_EverySentenceNamesBothClientsVerbsInOneText()
    {
        var problems = new List<string>();
        void Check(string where, string text, string? published = null)
        {
            foreach (var p in PublishedResendWords.VerbQualificationProblems(text)) problems.Add($"{where}: {p}");
            if (published is not null && !text.Contains(published, StringComparison.Ordinal)) problems.Add($"{where}: lacks the published words \"{published}\"");
        }

        static string Sub(string words, string digest) => Fold(words.Replace("<digest>", digest, StringComparison.Ordinal));

        using (var kit = new SessionKit())
        {
            var act = kit.Build();
            kit.Plant(act);
            kit.Forum.OnPost = (_, _, _) => Task.FromResult(ScriptedForum.Problem(503, "curia/store/unavailable", "A store fault"));
            var first = await CliKit.RunAsync(kit, "resend", act.PrefixedDigest);
            Check("curia resend, (a)", first.Text, Sub(PublishedResendWords.ResendRefusalWords, act.PrefixedDigest));
            var second = await CliKit.RunAsync(kit, "resend", act.PrefixedDigest);
            Check("curia resend, the stop sentence", second.Text, Sub(PublishedResendWords.StopWords.Replace("<n>", "2", StringComparison.Ordinal), act.PrefixedDigest));

            var notKept = await CliKit.RunAsync(kit, "resend", "sha256:" + new string('a', 64));
            Check("curia resend, no act kept", notKept.Text);

            var listing = await CliKit.RunAsync(kit, "acts");
            Check("curia acts, the last line", listing.Text, Fold(PublishedResendWords.ListingFooterWords.Replace("<n>", "1", StringComparison.Ordinal)));

            var removed = await CliKit.RunAsync(kit, "acts", "--discard", act.PrefixedDigest);
            Check("curia acts --discard", removed.Text, Sub(PublishedResendWords.DiscardedWords.After, act.PrefixedDigest));
        }

        using (var kit = new SessionKit())
        {
            for (var i = 0; i < PublishedResendWords.KeptActBound - 1; i++) kit.Plant(kit.Build(body: "act " + i));
            await File.WriteAllTextAsync(Path.Combine(kit.ActsDirectory, new string('c', 64) + ".act"), "not a header\n", Ct);
            var full = await CliKit.RunAsync(kit, "ask", "--board", "b", "--title", "A title?", "--body", "A body.");
            Check("the 64 refusal", full.Text);
            Check("the damaged line", (await CliKit.RunAsync(kit, "acts")).Text);
        }

        Red.None(problems, "every verb with its qualifier, no tool name outside one, the published sentences present");
    }
}
