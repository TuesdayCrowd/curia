#pragma warning disable CA1305, CA1308, CA1707, CA1822, CA1031, CA2007, CA1861, CA1859, CA1062, CA1307, CA1310, CA1002, CA1819
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Curia.OperatorTool;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// PR N2 (errata G70, R11.97, R4.43, R6.67), Task 1: the operator's side of the probes. OAS7 (the verb that publishes the key), OAS16 (a mixed environment), OAS21 (the audit), OAS22 (the head signer's stop),
/// OAS23 (EventId 7010), OAS27 (the review queue) and OAS37 (the operator's own verbs decided over honoured facts). The process runs are real processes of <c>curia-operator.dll</c>, as the Forum's operator runs
/// it; the rest call <see cref="OperatorCommands.RunAsync"/> against a database of their own. Every probe has the control that holds on the tree.
/// </summary>
[Collection("forum")]
public sealed class OperatorActsOperatorTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Pem() => LogSigningKey.GeneratePem();

    private static async Task<(int Exit, string Out, string Err)> ProcessAsync(ForumFixture on, Dictionary<string, string> env, params string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        // The operator's own output folder: the test's copy of its dll cannot load the logging assemblies the operator alone references.
        var operatorDirectory = AppContext.BaseDirectory.Replace(Path.Combine("tests", "Curia.Api.Tests"), Path.Combine("src", "Curia.Operator"), StringComparison.Ordinal);
        start.ArgumentList.Add(Path.Combine(operatorDirectory, "curia-operator.dll"));
        foreach (var a in args) start.ArgumentList.Add(a);
        start.Environment["CURIA_EVENTS_POSTGRES"] = on.ConnectionString;
        foreach (var (k, v) in env) start.Environment[k] = v;
        using var process = Process.Start(start)!;
        var so = process.StandardOutput.ReadToEndAsync(Ct);
        var se = process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        return (process.ExitCode, await so, await se);
    }

    // ---- OAS7 ----------------------------------------------------------------------------------

    /// <summary>OAS7 (control of the whole genuine path, red until it exists): <c>publish-operator-key</c> with the key in the environment publishes it once. On the tree the verb does not exist.</summary>
    [Fact]
    public async Task OAS7_TheOperatorKeyCanBePublished()
    {
        var run = await ProcessAsync(forum, new() { ["CURIA_OPERATOR_ACTS_SIGNING_KEY_PEM"] = Pem() }, "publish-operator-key", "--by", "ops");
        OperatorActsApi.Log($"OAS7 publish-operator-key: exit {run.Exit}; stderr[{run.Err.Trim().Replace('\n', '|')}]");
        WholeText.Same("exit 0", $"exit {run.Exit}");
    }

    // ---- OAS16 ---------------------------------------------------------------------------------

    [Fact]
    public async Task OAS16_AVerbThatSignsRefusesAnEnvironmentThatHoldsAnotherKey()
    {
        var api = new OperatorActsApi(forum);
        var agent = await api.GenuineAsync("attested", Ct);
        var post = await api.QuestionAsync("moderated", Ct);
        var both = new Dictionary<string, string> { ["CURIA_OPERATOR_ACTS_SIGNING_KEY_PEM"] = Pem(), ["CURIA_LOG_SIGNING_KEY_PEM"] = Pem() };
        var viaDeclaration = new Dictionary<string, string> { ["CURIA_OPERATOR_ACTS_SIGNING_KEY_PEM"] = Pem(), ["CURIA_DECLARATION_SIGNING_KEY_PEM"] = DeclarationSigningKey.GeneratePem() };

        // Control: the verb with one key in the environment runs (and appends).
        var before = await api.EventCountAsync(Ct);
        var control = await ProcessAsync(forum, new() { ["CURIA_OPERATOR_ACTS_SIGNING_KEY_PEM"] = Pem() }, "attest-owner", "--agent", agent.Agent.AgentId, "--owner", "owner:control", "--by", "ops");
        Assert.True(control.Exit == 0, $"the control attest-owner did not run: exit {control.Exit} {control.Err}");
        Assert.Equal(before + 1, await api.EventCountAsync(Ct));

        var results = new List<string>();
        foreach (var (name, env, args) in new (string, Dictionary<string, string>, string[])[]
        {
            ("attest-owner with the log key", both, ["attest-owner", "--agent", (await api.GenuineAsync("t1", Ct)).Agent.AgentId, "--owner", "owner:x", "--by", "ops"]),
            ("moderate with the log key", both, ["moderate", "--post", post.Id, "--category", "spam", "--effect", "withhold", "--reason", "probe", "--by", "ops"]),
            ("publish-operator-key with the declaration key", viaDeclaration, ["publish-operator-key", "--by", "ops"]),
            ("sign-head with the operator key", new() { ["CURIA_OPERATOR_ACTS_SIGNING_KEY_PEM"] = Pem(), ["CURIA_LOG_SIGNING_KEY_PEM"] = Pem() }, ["sign-head", "--by", "ops"]),
        })
        {
            var rows = await api.EventCountAsync(Ct);
            var run = await ProcessAsync(forum, env, args);
            results.Add($"{name}: exit {run.Exit}, {(await api.EventCountAsync(Ct) == rows ? "nothing appended" : "appended")}");
        }

        WholeText.Same(
            "attest-owner with the log key: exit 3, nothing appended; moderate with the log key: exit 3, nothing appended; publish-operator-key with the declaration key: exit 3, nothing appended; sign-head with the operator key: exit 3, nothing appended",
            string.Join("; ", results));
    }

    // ---- OAS21 ---------------------------------------------------------------------------------

    private const string Canary = "canary-7c1e9a52";

    [Fact]
    public async Task OAS21_TheAuditListsASkippedActRowAndCountsIt()
    {
        await using var own = await VerifiedStopSupport.NewAsync();
        var api = new OperatorActsApi(own);
        var agent = await api.GenuineAsync("audited", Ct);
        var post = await api.QuestionAsync("audited-post", Ct);

        // Control, the golden: a log with no act row prints the line it printed before this PR (the count line, exit 0).
        var clean = await VerifiedStopSupport.OperatorAsync(own, Ct, ["lifecycle-audit"]);
        OperatorActsApi.Log($"OAS21 golden: exit {clean.Exit}; stdout[{clean.Out.Replace("\n", "|")}]; stderr[{clean.Err.Trim()}]");
        Assert.Equal(0, clean.Exit);
        Assert.Matches(@"^honoured \d+  skipped 0\n$", clean.Out);

        await api.InsertRowAsync("agent.owner-attested", agent.Agent.AgentId, $"{{\"agent_id\":\"{agent.Agent.AgentId}\",\"method\":\"manual\",\"owner_id\":\"owner:{Canary}\",\"owner_verified\":true,\"reason\":\"{Canary}\"}}", "operator:ops", own.Now, Ct);
        await api.ForgeModerationAsync(post.Id, post.Digest, "withhold", Ct);
        var run = await VerifiedStopSupport.OperatorAsync(own, Ct, ["lifecycle-audit"]);
        var listed = run.Out.Split('\n').Count(l => l.StartsWith("skipped  log_index=", StringComparison.Ordinal));
        var leaked = (run.Out + run.Err).Contains(Canary, StringComparison.Ordinal);
        WholeText.Same("exit 7; 2 skipped rows listed; no payload text", $"exit {run.Exit}; {listed} skipped rows listed; {(leaked ? "a payload member leaked" : "no payload text")}");
    }

    /// <summary>
    /// OAS21g, the golden: a lifecycle-only log prints the bytes it printed before N2, whatever this PR adds to the audit. The golden below was captured from the operator as it stood before any act row was
    /// listed (stdout, stderr and exit code) over a fixed log: one enrolment (log index 0), its key binding (1) and a retirement with no signature (2), the last skipped. The ids are fixed so that the
    /// bytes are.
    /// </summary>
    [Fact]
    public async Task OAS21g_ALifecycleOnlyAuditPrintsTheBytesItPrintedBefore()
    {
        await using var own = await VerifiedStopSupport.NewAsync();
        const string Agent = "https://agents.example/golden-agent";
        const string Kid = "golden-kid-1";
        const string Jwk = "{\"alg\":\"ES256\",\"crv\":\"P-256\",\"kid\":\"" + Kid + "\",\"kty\":\"EC\",\"use\":\"sig\",\"x\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"y\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\"}";
        await VerifiedStopSupport.InsertRowAsync(own, "agent.enrolled", Agent, $"{{\"agent_id\":\"{Agent}\",\"kid\":\"{Kid}\",\"reason\":\"golden\"}}", Ct);
        await VerifiedStopSupport.InsertRowAsync(own, "agent.key-bound", Agent, $"{{\"agent_id\":\"{Agent}\",\"jwk\":{Jwk},\"kid\":\"{Kid}\"}}", Ct);
        await VerifiedStopSupport.InsertRowAsync(own, "agent.key-retired", Agent, VerifiedStopSupport.ForgedRetirement(Agent, Kid), Ct);

        var run = await VerifiedStopSupport.OperatorAsync(own, Ct, ["lifecycle-audit"]);
        OperatorActsApi.Log($"OAS21g golden: exit {run.Exit}; stdout[{run.Out.Replace("\n", "|")}]; stderr[{run.Err.Replace("\n", "|")}]");

        WholeText.Same(
            "exit 7\nstdout[honoured 2  skipped 1\nskipped  log_index=2  type=agent.key-retired  aggregate=\"https://agents.example/golden-agent\"  predicate=lifecycle-typ-mismatch\n]\nstderr[]",
            $"exit {run.Exit}\nstdout[{run.Out}]\nstderr[{run.Err}]");
    }

    // ---- OAS22 ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("moderation")]
    [InlineData("attestation")]
    public async Task OAS22_TheHeadSignerWithholdsOverASkippedActRow(string kind)
    {
        await using var own = await VerifiedStopSupport.NewAsync();
        var api = new OperatorActsApi(own);
        var agent = await api.GenuineAsync("stopped", Ct);
        var post = await api.QuestionAsync("stopped-post", Ct);
        var pem = Pem();
        Assert.Equal(0, (await VerifiedStopSupport.OperatorAsync(own, Ct, ["sign-head", "--by", "ops"], () => pem)).Exit);

        // Control: a genuine act row past the head does not stop the signer.
        await api.GenuineWithhold(post.Id, Ct);
        var control = await VerifiedStopSupport.OperatorAsync(own, Ct, ["sign-head", "--by", "ops", "--if-grown"], () => pem);
        Assert.Equal(0, control.Exit);

        var second = await api.QuestionAsync("stopped-post-2", Ct);
        if (kind == "moderation") await api.ForgeModerationAsync(second.Id, second.Digest, "withhold", Ct);
        else await api.ForgeAttestationNowAsync(agent.Agent.AgentId, "owner:forged", Ct);
        var before = await VerifiedStopSupport.CountAsync(own, Ct);
        var run = await VerifiedStopSupport.OperatorAsync(own, Ct, ["sign-head", "--by", "ops", "--if-grown"], () => pem);
        var after = await VerifiedStopSupport.CountAsync(own, Ct);
        WholeText.Same("exit 6; stdout empty; nothing appended", $"exit {run.Exit}; stdout {(run.Out.Length == 0 ? "empty" : "[" + run.Out.Trim() + "]")}; {(after == before ? "nothing appended" : "a head was appended")}");
    }

    // ---- OAS23 ---------------------------------------------------------------------------------

    [Fact]
    public async Task OAS23_AForumThatStartsOverASkippedActRowLogsEventId7010()
    {
        await using var own = await VerifiedStopSupport.NewAsync();
        var api = new OperatorActsApi(own);
        var agent = await api.GenuineAsync("logged", Ct);
        var post = await api.QuestionAsync("logged-post", Ct);

        // Control: a start over a clean log logs no 7010 (and the host starts).
        await using (var clean = new CapturedForum(own))
        {
            Assert.DoesNotContain(clean.Log.All, e => e.EventId == 7010);
        }

        await api.ForgeAttestationNowAsync(agent.Agent.AgentId, "owner:" + Canary, Ct);
        await api.ForgeModerationAsync(post.Id, post.Digest, "withhold", Ct);
        await using var captured = new CapturedForum(own);
        var logged = captured.Log.All.Where(e => e.EventId == 7010).ToArray();
        WholeText.Same(
            "one event 7010, Error, naming the count and the predicates, and no payload text",
            logged.Length == 1
                ? $"one event 7010, {logged[0].Level}, naming {(logged[0].Message.Contains("act-unsigned", StringComparison.Ordinal) ? "the count and the predicates" : "no predicate")}, and {(logged[0].Message.Contains(Canary, StringComparison.Ordinal) ? "payload text" : "no payload text")}"
                : $"{logged.Length} events 7010: a log entry with EventId 7010 was expected");
        _ = LogLevel.Error;
    }

    // ---- OAS27 ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("dismiss")]
    [InlineData("upholding")]
    public async Task OAS27_TheReviewQueueIsComputedOverHonouredRecords(string leg)
    {
        var api = new OperatorActsApi(forum);
        var post = await api.QuestionAsync("flagged", Ct);
        var flagger = await api.GenuineAsync("flagger", Ct);
        var flagId = await api.RaiseFlagAsync(flagger, post.Id, Ct);
        var open = await api.FlagStateAsync(post.Id, Ct);
        Assert.Contains("open", open, StringComparison.Ordinal);

        await api.ForgeModerationAsync(post.Id, post.Digest, leg == "dismiss" ? "dismiss" : "withhold", Ct, adjudicates: [flagId]);
        var after = await api.FlagStateAsync(post.Id, Ct);
        OperatorActsApi.Log($"OAS27 {leg}: before [{open}] after [{after}]");
        WholeText.Same(open, after);
    }

    // ---- OAS37 ---------------------------------------------------------------------------------

    [Fact]
    public async Task OAS37_TheOperatorsOwnVerbsAreDecidedOverHonouredFacts()
    {
        var api = new OperatorActsApi(forum);

        // Control: the genuine withhold of a post nothing forged touched is recorded.
        var clean = await api.QuestionAsync("clean", Ct);
        var control = await api.OperatorAsync(Ct, "moderate", "--post", clean.Id, "--category", "spam", "--effect", "withhold", "--reason", "genuine", "--by", "ops");
        Assert.Equal(0, control.Exit);

        var post = await api.QuestionAsync("forged-held", Ct);
        await api.ForgeModerationAsync(post.Id, post.Digest, "withhold", Ct);
        var withhold = await api.OperatorAsync(Ct, "moderate", "--post", post.Id, "--category", "spam", "--effect", "withhold", "--reason", "genuine", "--by", "ops");
        var restore = await api.OperatorAsync(Ct, "moderate", "--post", post.Id, "--category", "spam", "--effect", "restore", "--reason", "genuine restore", "--by", "ops");
        using var get = await api.Http().GetAsync(new Uri($"/v1/posts/{post.Id}", UriKind.Relative), Ct);
        OperatorActsApi.Log($"OAS37 withhold: exit {withhold.Exit} err[{withhold.Err.Trim()}]; restore: exit {restore.Exit} err[{restore.Err.Trim()}]; post {(int)get.StatusCode}");
        WholeText.Same(
            "withhold exit 0 (recorded); restore exit 0; post 200",
            $"withhold exit {withhold.Exit}{(withhold.Exit == 0 ? " (recorded)" : " " + ErrorType(withhold.Err))}; restore exit {restore.Exit}; post {(int)get.StatusCode}");
        _ = HttpStatusCode.OK;
        _ = JsonDocument.Parse("{}");
    }

    private static string ErrorType(string stderr)
    {
        var m = System.Text.RegularExpressions.Regex.Match(stderr, @"\((curia/[^)]+)\)");
        return m.Success ? m.Groups[1].Value : stderr.Trim();
    }
}
