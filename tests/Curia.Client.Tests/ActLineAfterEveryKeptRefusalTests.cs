#pragma warning disable CA1416 // modes are read as PrivateFiles sets them

using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// ASA51 (errata G58, R11.80 as amended by plan ruling 43), at the command. A refusal of a signed post that leaves the act kept is followed by the act line whatever the status and whether or not the Forum named a
/// problem type; one the client removed the act for is not; a resend's refusal has none. The phrases <c>nothing was recorded</c> and <c>nothing was stored</c> occur 0 times in a refusal the client cannot
/// say that of (a <c>502</c> says nothing of what the Forum stored). The digest is the one the stub computes from the bytes it received.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Each kit is a using declaration; the analyzer loses the ownership across the awaits.")]
[Collection("console")]
public sealed class ActLineAfterEveryKeptRefusalTests
{
    private static string Fold(string text) => CliRun.Fold(text);

    private static HttpResponseMessage Typeless(int status) => new((HttpStatusCode)status) { Content = new StringContent("upstream said no", Encoding.UTF8, "text/plain") };

    /// <summary>
    /// ASA51. First sends answered <c>500</c>, <c>502</c>, <c>504</c>, <c>413</c> and <c>429</c>, with a typeless body and with a problem document each: one act line naming the digest computed from the bytes signed, the file kept, and no
    /// "nothing was recorded" or "nothing was stored". A first send answered <c>400</c> with a problem document removes the file and prints no act line (the control). A resend answered <c>500</c> prints
    /// R11.81 (a) and no act line. Predicted red: <c>no act line</c> for every status the act line did not name.
    /// </summary>
    [Fact]
    public async Task R11_80_ASA51_AnActLineFollowsEveryRefusalThatLeavesTheActKept()
    {
        var problems = new List<string>();
        var actLine = Fold(PublishedResendWords.ActWords);
        var marker = actLine[..actLine.IndexOf("<digest>", StringComparison.Ordinal)];

        foreach (var status in new[] { 500, 502, 504, 413, 429 })
        {
            foreach (var typeless in new[] { true, false })
            {
                var name = $"{status} {(typeless ? "typeless" : "a problem document")}";
                using var kit = new SessionKit();
                kit.Forum.OnPost = (_, _, _) => Task.FromResult(typeless ? Typeless(status) : ScriptedForum.Problem(status, "curia/server/fault", "A fault"));
                var run = await CliKit.RunAsync(kit, "ask", "--board", "b", "--title", "A title?", "--body", "A body.");
                if (kit.Forum.Posts.Count != 1) { problems.Add($"{name}: {kit.Forum.Posts.Count} requests"); continue; }

                var digest = ReceivedAct.DigestOf(Encoding.UTF8.GetString(kit.Forum.Posts[0].Body));
                var lines = Regex.Count(run.Text, Regex.Escape(marker), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
                if (lines != 1) problems.Add($"{name}: {lines} act lines, expected 1 (\"{run.Text}\")");
                else if (!run.Text.Contains(Fold(PublishedResendWords.ActWords.Replace("<digest>", digest, StringComparison.Ordinal)), StringComparison.Ordinal)) problems.Add($"{name}: the act line does not name the digest of the bytes signed");
                if (kit.KeptFiles().Length != 1) problems.Add($"{name}: {kit.KeptFiles().Length} kept files, expected 1");
                var said = Regex.Count(run.Text, "nothing was (?:recorded|stored)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
                if (said != 0) problems.Add($"{name}: says nothing was recorded or stored {said} time(s)");
            }
        }

        using (var removed = new SessionKit())
        {
            removed.Forum.OnPost = (_, _, _) => Task.FromResult(ScriptedForum.Problem(400, "curia/admit/invalid-envelope", "A refusal"));
            var run = await CliKit.RunAsync(removed, "ask", "--board", "b", "--title", "A title?", "--body", "A body.");
            if (removed.KeptFiles().Length != 0) problems.Add("400: the file was kept");
            if (run.Text.Contains(marker, StringComparison.Ordinal)) problems.Add("400: an act line follows a refusal the client removed the act for");
        }

        using (var resent = new SessionKit())
        {
            var act = resent.Build();
            resent.Plant(act);
            resent.Forum.OnPost = (_, _, _) => Task.FromResult(ScriptedForum.Problem(500, "curia/server/fault", "A fault"));
            var run = await CliKit.RunAsync(resent, "resend", act.PrefixedDigest);
            if (run.Text.Contains(marker, StringComparison.Ordinal)) problems.Add("a resend answered 500: an act line follows it");
            if (!run.Text.Contains(Fold(PublishedResendWords.ResendRefusalWords.Replace("<digest>", act.PrefixedDigest, StringComparison.Ordinal)), StringComparison.Ordinal)) problems.Add("a resend answered 500: R11.81 (a) is absent");
        }

        Red.None(problems, "one act line naming the digest of the bytes signed after every refusal that leaves the act kept, none after a removal or a resend, and no phrase of nothing recorded");
    }
}
