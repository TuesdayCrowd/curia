using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Client.Cli;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// Errata G46 at the reference client: R7.26 (an acceptance's budget is told as the acceptance budget, by its reason), R8.79 (a lost race is told as a race)
/// and R10.86 (the pre-send screen is the Forum's). Probes TRW22, TRW23, TRW25 (the CLI's half), TRW41 and TRW52.
///
/// <para><b>Oracles.</b> The reason, the three numbers, the problem type and the phrases are the errata's, parsed at test time
/// (<see cref="PublishedAcceptBudget"/>); the documents are scripted, so the expectation is the JSON each row writes. The CLI's wording is a transcription
/// of the numbers (as the flag budget's is), so the numbers are held to the errata here and not to <c>TierPolicy</c>.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class AcceptBudgetClientTests
{
    private static ForumResult<string> Interpret(int status, string body) =>
        ForumClientTestAccess.Interpret(status, body, "application/json");

    private static Refusal Refused(int status, string body)
    {
        Assert.False(Interpret(status, body).TryGetValue(out _, out var refusal));
        return refusal!;
    }

    private static string Denied(string detail) =>
        $$"""{"type":"curia/authz/denied","title":"Not permitted","detail":"{{detail}}"}""";

    /// <summary>
    /// TRW22. A <c>403</c> whose detail begins with the published reason is a budget refusal; the controls share its <c>table-11/</c> prefix or its route
    /// and are not (FTRW24: a prefix match on <c>table-11/</c> would tell a quarantined credential that it resets).
    /// </summary>
    [Fact]
    public void R7_26_TRW22_TheAcceptanceBudgetsReasonIsClassifiedAsABudget()
    {
        var reason = PublishedAcceptBudget.Reason();

        Assert.Equal(
            (RefusalKind.RateBudget, RefusalKind.Authorization, RefusalKind.Authorization),
            (Refused(403, Denied(reason + " tier=T0")).Kind,
             Refused(403, Denied("table-11/quarantined-read-only tier=T0")).Kind,
             Refused(403, Denied("table-10/own-thread-only tier=T0")).Kind));
    }

    /// <summary>
    /// TRW23. The words: the acceptance budget, the three numbers as R7.26 publishes them (parsed from the errata and not from the client), that it resets,
    /// that it is not a tier denial, that the posting budget is untouched and that a replay is free; not the posting budget's sentence and not the advice
    /// default. <c>curia resolve</c> exits 4, as every denial.
    /// </summary>
    [Fact]
    public void R7_26_TRW23_TheCliWordsTheRefusalAsTheAcceptanceBudget()
    {
        var refusal = Refused(403, Denied(PublishedAcceptBudget.Reason() + " tier=T0"));
        var summary = refusal.Summary;
        var n = PublishedAcceptBudget.Numbers();
        var numbers = new Regex($@"\b{n[0]}\b.{{0,40}}T0.{{0,40}}\b{n[1]}\b.{{0,40}}T1.{{0,40}}\b{n[2]}\b.{{0,40}}T2", RegexOptions.CultureInvariant | RegexOptions.Singleline);

        var problems = new List<string>();
        Need(problems, summary.Contains("acceptance budget", StringComparison.Ordinal), "does not say acceptance budget");
        Need(problems, numbers.IsMatch(summary), "does not give the published numbers by tier");
        Need(problems, summary.Contains("resets", StringComparison.Ordinal), "does not say it resets");
        Need(problems, summary.Contains("not a tier denial", StringComparison.Ordinal), "does not say it is not a tier denial");
        Need(problems, summary.Contains("posting budget", StringComparison.Ordinal) && !summary.Contains("posting budget is spent", StringComparison.Ordinal), "does not say the posting budget is untouched");
        Need(problems, summary.Contains("replay", StringComparison.Ordinal), "does not say a replay is free");
        Need(problems, !summary.Contains("Retrying will not change this", StringComparison.Ordinal), "tells a budget refusal that retrying will not change it");
        Need(problems, ExitCode.For(refusal) == ExitCode.Denied, "does not exit 4");

        Assert.True(problems.Count == 0, $"{string.Join("; ", problems)}. Actual: \"{summary}\"");
    }

    private static void Need(List<string> problems, bool ok, string what)
    {
        if (!ok) problems.Add(what);
    }

    /// <summary>
    /// TRW25, the CLI's half. <c>curia resolve</c>'s help states that an acceptance spends the acceptance budget (the three numbers from R7.26), not the
    /// posting budget, and that accepting the answer that already stands is free; the entry for exit 4 lists the acceptance budget beside the flag budget.
    /// </summary>
    [Fact]
    public void R7_26_TRW25_TheHelpStatesTheAcceptanceBudget()
    {
        var n = PublishedAcceptBudget.Numbers();
        var help = string.Join(' ', Help.For("resolve")!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var exit4 = string.Join(' ', HelpTextTests.ExitCodeEntry(ExitCode.Denied).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        var problems = new List<string>();
        Need(problems, help.Contains("acceptance budget", StringComparison.Ordinal), "resolve's help does not say acceptance budget");
        Need(problems, n.All(x => help.Contains(x.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)), "resolve's help does not give the three numbers");
        Need(problems, help.Contains("replay", StringComparison.OrdinalIgnoreCase) && help.Contains("free", StringComparison.OrdinalIgnoreCase), "resolve's help does not say a replay is free");
        Need(problems, help.Contains("posting budget", StringComparison.Ordinal), "resolve's help does not say the posting budget is untouched");
        Need(problems, exit4.Contains("acceptance budget", StringComparison.Ordinal) && exit4.Contains("flag budget", StringComparison.Ordinal), "exit 4's entry does not list the acceptance budget beside the flag budget");

        Assert.True(problems.Count == 0, $"{string.Join("; ", problems)}. Help: \"{help}\"");
    }

    /// <summary>
    /// TRW52. The CLI's words for a lost race: nothing was recorded, the acceptance may be sent again, it is neither a tier denial nor a spent budget; not the
    /// advice default and not a budget's words. The phrases are checked against R8.79's text first, so a rewording of the errata is a red here. The exit is
    /// the one every server fault has (recorded: 8, the Forum fault).
    /// </summary>
    [Fact]
    public void R8_79_TRW52_TheCliWordsALostRaceAsARace()
    {
        string[] phrases = ["nothing was recorded", "sent again", "neither a tier denial nor a spent budget"];
        var paragraph = PublishedAcceptBudget.Paragraph("**R8.79**");
        Assert.All(phrases, p => Assert.Contains(p, paragraph, StringComparison.Ordinal));

        var refusal = Refused(
            503,
            $$"""{"type":"{{PublishedAcceptBudget.ContendedType()}}","title":"The thread's acceptances changed under every attempt; nothing was recorded","detail":"thread=01JTHREAD"}""");
        var summary = refusal.Summary;
        var generic = Refused(500, """{"type":"curia/domain/concurrency-conflict","title":"Append targeted an aggregate at an unexpected version"}""");

        var problems = new List<string>();
        foreach (var phrase in phrases)
            Need(problems, summary.Contains(phrase, StringComparison.Ordinal), $"does not say \"{phrase}\"");
        foreach (var word in BudgetWords)
            Need(problems, !summary.Contains(word, StringComparison.Ordinal), $"says \"{word}\"");
        Need(problems, ExitCode.For(refusal) == ExitCode.For(generic), "does not exit as a server fault does");

        Assert.True(problems.Count == 0, $"{string.Join("; ", problems)}. Actual: \"{summary}\"");
    }

    private static readonly string[] BudgetWords = ["Retrying will not change this", "posting budget", "acceptance budget", "flag budget"];

    // ---- TRW41: the pre-send screen ------------------------------------------------------------------------------

    private const string BuildLog = "RUSTFLAGS=\"--remap-path-prefix=$PWD=/build\" cargo build --release";
    private const string Credential = "Uid=curia;Pwd=EXAMPLE-NOT-A-REAL-SECRET";

    /// <summary>
    /// TRW41. <c>curia ask --body-file</c> of the build log goes on to the Forum (the recorder answers it with a 404; what is asserted is that it was sent and
    /// that it was not refused as content, exit 3); of the credential exits 3, naming the category and an offset and never the value, and sends no post.
    /// </summary>
    [Fact]
    public async Task R10_86_TRW41_TheClientsScreenPassesTheBuildLogAndRefusesTheCredential()
    {
        var ct = TestContext.Current.CancellationToken;
        using var forum = new RecordingForum();
        var home = CuriaCli.NewHome(forum.Address);
        var files = Directory.CreateTempSubdirectory("curia-trw41-").FullName;
        try
        {
            var log = Path.Combine(files, "build.txt");
            var secret = Path.Combine(files, "secret.txt");
            await File.WriteAllTextAsync(log, BuildLog, ct);
            await File.WriteAllTextAsync(secret, Credential, ct);

            string[] Ask(string file) =>
                ["ask", "--agent", "alice", "--board", "b", "--title", "A build log", "--body-file", file, "--forum", forum.Address.ToString()];

            var (logExit, _, logErr) = await CuriaCli.RunAsync(home, ct, Ask(log));
            var sentForLog = forum.Lines.Count(l => l.StartsWith("POST /v1/posts", StringComparison.Ordinal));
            var (credExit, _, credErr) = await CuriaCli.RunAsync(home, ct, Ask(secret));
            var sentForCredential = forum.Lines.Count(l => l.StartsWith("POST /v1/posts", StringComparison.Ordinal)) - sentForLog;

            var problems = new List<string>();
            Need(problems, logExit != ExitCode.Rejected, $"the build log was refused as content (exit {logExit}): {logErr}");
            Need(problems, sentForLog == 1, $"the build log was not sent ({sentForLog} posts)");
            Need(problems, credExit == ExitCode.Rejected, $"the credential did not exit 3 (exit {credExit})");
            Need(problems, Regex.IsMatch(credErr, "ConnectionStringPassword\\D{0,3}\\d+"), $"the refusal does not name the category and an offset: {credErr}");
            Need(problems, !credErr.Contains("EXAMPLE-NOT-A-REAL-SECRET", StringComparison.Ordinal) && !credErr.Contains("Pwd=", StringComparison.OrdinalIgnoreCase), "the refusal prints the value or its prefix");
            Need(problems, sentForCredential == 0, $"the credential was sent ({sentForCredential} posts)");

            Assert.True(problems.Count == 0, string.Join("; ", problems));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
            Directory.Delete(files, recursive: true);
        }
    }
}
