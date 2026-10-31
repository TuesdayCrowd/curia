using System.Diagnostics.CodeAnalysis;
using Curia.Domain.Authorization;
using Curia.Domain.Primitives;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// PR 3 (errata G50, R7.30), Task 0's probes RLW6 and RLW7 at the reference client. <b>CONSISTENCY CHECK (RLW42): this file reads <c>AccessPolicy.</c> on
/// purpose</b>, to enumerate the denial reasons a client must word, so that a reason added later is red until it has its advice; the reason that matters
/// (R7.30's) and the words that must not appear are the errata's (<see cref="PublishedReadersLastWords"/>).
///
/// <para>RLW6 isolates the advice a client adds to the Forum's title by giving every refusal the same title, so what follows the detail is the client's own;
/// the neutral advice is whatever a reason no table knows is given, so the fact names no production sentence.</para>
/// </summary>
[Collection("console")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class OwnResourceFoundClientTests
{
    private const string Post = "01TESTPOSTID0000000000000A";

    /// <summary>The words a refusal adds after the Forum's own title and detail (<c>Title (detail). Advice</c>).</summary>
    private static string AdviceFor(string reason)
    {
        var kind = reason.Contains("budget", StringComparison.Ordinal) ? RefusalKind.RateBudget : RefusalKind.Authorization;
        var summary = new Refusal(kind, 403, new Error("curia/authz/denied", "T", $"{reason} post={Post}")).Summary;
        var at = summary.IndexOf("). ", StringComparison.Ordinal);
        return at < 0 ? summary : summary[(at + 3)..];
    }

    /// <summary>RLW6. Every reason a denial can carry is worded by its cause (never the neutral advice a reason no table knows gets); R7.30's says the resource exists and not that it may not; the shared reason keeps its clause.</summary>
    [Fact]
    public void R7_30_RLW6_TheClientsWordEachReasonByItsCause()
    {
        var neutral = AdviceFor("table-99/unheard-of");
        var found = PublishedReadersLastWords.OwnResourceFound();
        var only = PublishedReadersLastWords.OwnResourceOnly();

        var problems = new List<string>();
        foreach (var reason in AccessPolicy.DenialReasons.Where(r => !r.Contains("budget", StringComparison.Ordinal)))
        {
            var advice = AdviceFor(reason);
            if (advice == neutral) problems.Add($"Expected: advice for {reason} Actual: the neutral \"{advice}\"");
        }

        var foundAdvice = AdviceFor(found);
        if (!foundAdvice.Contains("exist", StringComparison.Ordinal) || foundAdvice.Contains("not exist", StringComparison.Ordinal))
            problems.Add($"{found}: Expected: advice that says the resource exists and not that it does not Actual: \"{foundAdvice}\"");
        if (!AdviceFor(only).Contains("not exist", StringComparison.Ordinal)) problems.Add($"{only}: the advice lost its clause: \"{AdviceFor(only)}\"");

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>RLW7. <c>curia flags &lt;post&gt;</c> against a Forum that answers each way: R7.30's reason is exit 4 with the advice, a 404 is exit 5 and the Forum's "No such post", a 200 is exit 0; the positives are in the same fact.</summary>
    [Fact]
    public async Task R7_30_RLW7_CuriaFlagsWordsTheNewReasonAndKeepsItsOtherExits()
    {
        var found = PublishedReadersLastWords.OwnResourceFound();
        var title = PublishedReadersLastWords.FoundTitle();
        using var home = new CliHome();
        using var forum = new ListenerForum();
        var problems = new List<string>();

        forum.Answer = (_, _) => (403, $$"""{"type":"curia/authz/denied","title":"{{title}}","detail":"{{found}} post={{Post}}"}""");
        var denied = await home.RunAsync("flags", Post, "--agent", "alice", "--forum", forum.Address.ToString());
        if (denied.Exit != 4) problems.Add($"403: Expected: exit 4 Actual: exit {denied.Exit} {denied.Stderr.Trim()}");
        if (!denied.Stderr.Contains(found, StringComparison.Ordinal) || !denied.Stderr.Contains(title, StringComparison.Ordinal)) problems.Add($"403: the Forum's reason and title are not printed: {denied.Stderr.Trim()}");
        var advice = denied.Stderr[(denied.Stderr.IndexOf("). ", StringComparison.Ordinal) + 3)..];
        if (denied.Stderr.Contains("). ", StringComparison.Ordinal) && (advice.Contains("not exist", StringComparison.Ordinal) || advice.Contains("Retrying will not change this", StringComparison.Ordinal)))
            problems.Add($"403: Expected: the new reason's own advice Actual: \"{advice.Trim()}\"");

        forum.Answer = (_, _) => (404, """{"type":"curia/post/not-found","title":"No such post","detail":null}""");
        var missing = await home.RunAsync("flags", Post, "--agent", "alice", "--forum", forum.Address.ToString());
        if (missing.Exit != 5 || !missing.Stderr.Contains("No such post", StringComparison.Ordinal)) problems.Add($"404: Expected: exit 5 and \"No such post\" Actual: exit {missing.Exit} {missing.Stderr.Trim()}");

        forum.Answer = (_, _) => (200, """{"flags":[]}""");
        var listed = await home.RunAsync("flags", Post, "--agent", "alice", "--forum", forum.Address.ToString());
        if (listed.Exit != 0) problems.Add($"200: Expected: exit 0 Actual: exit {listed.Exit} {listed.Stderr.Trim()}");

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }
}
