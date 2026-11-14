using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.Canon.Json;
using Curia.Client;
using Curia.Domain;
using Curia.OperatorTool;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;
using JsonObject = System.Text.Json.Nodes.JsonObject;

namespace Curia.Api.Tests;

/// <summary>
/// Errata G68 (rulings 8, 12, 13): the operator hears of a skipped row in three places. LRV15 is the head signer's stop on the verifier's verdict, LRV16 <c>lifecycle-audit</c>, LRV17 EventId 7007. The
/// fixtures are HSU37 to HSU40's, adapted: rows appended by SQL under the app role and through no use case.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public static class VerifiedStopSupport
{
    internal sealed record Run(int Exit, string Out, string Err);

    internal static readonly int[] StartIds = [7006, 7007];

    internal static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    [SuppressMessage("Design", "CA1068:CancellationToken parameters must come last", Justification = "A test helper with an optional parameter: the token is the first thing a call names and the optional one follows.")]
    internal static async Task<Run> OperatorAsync(ForumFixture forum, CancellationToken ct, string[] args, Func<string?>? logKey = null)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            var exit = await OperatorCommands.RunAsync(
                args, forum.ConnectionString, forum.Clock, forum.PermissiveGate(), OperatorCommands.LoadDeclarationSigner, stdout, stderr, ct,
                readLogSigningKeyPem: logKey, readDeclarationSigningKeyPem: null);
            return new Run(exit, stdout.ToString(), stderr.ToString());
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return new Run(-1, stdout.ToString(), "THREW " + e.GetType().Name + ": " + e.Message);
        }
    }

    internal static async Task<long> CountAsync(ForumFixture forum, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM events;", connection);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    /// <summary>A row appended by SQL under the app role and through no use case.</summary>
    internal static async Task InsertRowAsync(ForumFixture forum, string type, string aggregate, string payloadJson, CancellationToken ct)
    {
        var id = new Curia.Domain.Primitives.UlidGenerator(forum.Clock).Next().Match(u => u.ToString(), e => throw new InvalidOperationException(e.Type));
        await using var connection = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(forum.ConnectionString) { Username = forum.RoleName, Password = forum.RolePassword }.ConnectionString);
        await connection.OpenAsync(ct);
        await using var insert = new NpgsqlCommand(
            "INSERT INTO events (event_id, event_type, aggregate_id, actor_id, payload, server_ts) VALUES (@id, @type, @aggregate, 'operator:ops', @payload::jsonb, @ts);", connection);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("type", type);
        insert.Parameters.AddWithValue("aggregate", aggregate);
        insert.Parameters.AddWithValue("payload", payloadJson);
        insert.Parameters.AddWithValue("ts", forum.Now);
        Assert.Equal(1, await insert.ExecuteNonQueryAsync(ct));
    }

    /// <summary>
    /// A retirement with no signature. <paramref name="prev"/> is the log index of the identity's last honoured lifecycle entry; the default, 1, is the enrolment's own binding on a log the identity
    /// enrolled at the start of, and a probe on a log other agents have written to passes the real one, so that the row fails on the header <c>typ</c> and not on the link before it (R6.64's order).
    /// </summary>
    internal static string ForgedRetirement(string agent, string kid, long prev = 1) => new JsonObject
    {
        ["agent_id"] = agent,
        ["authorization"] = "this-is-not-a-signature-qq7",
        ["kid"] = kid,
        ["prev"] = prev,
        ["statement"] = "{\"agent_id\":\"" + agent + "\",\"kid\":\"" + kid + "\",\"prev\":" + prev.ToString(CultureInfo.InvariantCulture) + ",\"typ\":\"curia.key-retirement/1\"}",
    }.ToJsonString();

    internal static async Task<ForumAgent> EnrolAsync(ForumFixture forum, string stem, CancellationToken ct)
    {
        var agent = ForumAgent.Create($"https://agents.example/{stem}-{Suffix()}", $"{stem}-{Suffix()}");
        using var response = await agent.EnrollAsync(forum.Client, ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return agent;
    }

    internal static async Task<ForumFixture> NewAsync()
    {
        var forum = new ForumFixture();
        await forum.InitializeAsync();
        return forum;
    }
}

[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class VerifiedStopTests
{
    private static string Pem() => LogSigningKey.GeneratePem();

    private static async Task<(ForumFixture Forum, ForumAgent Victim, string Pem)> SignedAnchorAsync(CancellationToken ct)
    {
        var forum = await VerifiedStopSupport.NewAsync();
        var victim = await VerifiedStopSupport.EnrolAsync(forum, "lrv15", ct);
        var pem = Pem();
        Assert.Equal(0, (await VerifiedStopSupport.OperatorAsync(forum, ct, ["sign-head", "--by", "ops"], () => pem)).Exit);
        return (forum, victim, pem);
    }

    /// <summary>
    /// LRV15, the forged-row half (a control on the seam, which keeps today's structural stop): <c>sign-head --if-grown</c> withholds over a forged row past the anchor, exit 6, empty stdout, the events count
    /// unchanged, and the stderr line names the row's index and type.
    /// </summary>
    [Fact]
    public async Task LRV15_AForgedRowPastTheAnchorWithholdsTheHead_Control()
    {
        var ct = TestContext.Current.CancellationToken;
        var (forum, victim, pem) = await SignedAnchorAsync(ct);
        await using var _ = forum;
        var index = await VerifiedStopSupport.CountAsync(forum, ct);
        await VerifiedStopSupport.InsertRowAsync(forum, "agent.key-retired", victim.AgentId, VerifiedStopSupport.ForgedRetirement(victim.AgentId, victim.Kid), ct);
        var before = await VerifiedStopSupport.CountAsync(forum, ct);

        var run = await VerifiedStopSupport.OperatorAsync(forum, ct, ["sign-head", "--by", "ops", "--if-grown"], () => pem);
        var after = await VerifiedStopSupport.CountAsync(forum, ct);

        WholeText.Same(
            "exit 6; stdout empty; 0 rows appended; names the row",
            $"exit {run.Exit}; stdout {(run.Out.Length == 0 ? "empty" : "'" + run.Out.Trim() + "'")}; {after - before} rows appended; "
            + (run.Err.Contains($"withheld  log_index={index.ToString(CultureInfo.InvariantCulture)}  type=agent.key-retired", StringComparison.Ordinal) ? "names the row" : "does not name the row: " + run.Err));
    }

    /// <summary>LRV15: the withheld line states the predicate of the row, <c>withheld  log_index=&lt;i&gt;  type=&lt;t&gt;  predicate=&lt;slug&gt;</c> (ruling 12). Red on the seam: the line carries no predicate.</summary>
    [Fact]
    public async Task LRV15_TheWithheldLineNamesThePredicate()
    {
        var ct = TestContext.Current.CancellationToken;
        var (forum, victim, pem) = await SignedAnchorAsync(ct);
        await using var _ = forum;
        var index = await VerifiedStopSupport.CountAsync(forum, ct);
        await VerifiedStopSupport.InsertRowAsync(forum, "agent.key-retired", victim.AgentId, VerifiedStopSupport.ForgedRetirement(victim.AgentId, victim.Kid), ct);

        var run = await VerifiedStopSupport.OperatorAsync(forum, ct, ["sign-head", "--by", "ops", "--if-grown"], () => pem);

        var want = $"withheld  log_index={index.ToString(CultureInfo.InvariantCulture)}  type=agent.key-retired  predicate=lifecycle-typ-mismatch";
        Assert.True(run.Err.Contains(want, StringComparison.Ordinal), $"Expected: stderr containing [{want}]  Actual: [{run.Err}]");
    }

    /// <summary>
    /// LRV15, the genuine-row half: a genuine rotation past the anchor does not withhold; <c>--if-grown</c> signs a head covering it (the lift requires this: item 259). Red on the seam: the structural stop,
    /// <c>Expected: exit 0  Actual: exit 6</c>.
    /// </summary>
    [Fact]
    public async Task LRV15_AGenuineRotationPastTheAnchorDoesNotWithholdTheHead()
    {
        var ct = TestContext.Current.CancellationToken;
        var (forum, victim, pem) = await SignedAnchorAsync(ct);
        await using var _ = forum;
        var (dpop, token) = (DpopClient.For(victim, victim.AssertionKey), string.Empty);
        token = await dpop.GetTokenAsync(forum.Client, KeyActRequests.TokenEndpoint, forum.Now, ct);
        var prev = await KeyActRequests.LatestLifecycleAsync(forum.Client, victim.AgentId, ct);
        using var rotated = await dpop.PostAsync(
            forum.Client, KeyActRequests.RotateUrl(victim.AgentId), token, KeyActRequests.RotationBody(victim, KeyActRequests.KeyPair.New("next-" + VerifiedStopSupport.Suffix()), prev), forum.Now, ct, contentType: "application/json");
        Assert.True(rotated.StatusCode == HttpStatusCode.Created, "the control rotation was refused: " + await rotated.Content.ReadAsStringAsync(ct));

        var run = await VerifiedStopSupport.OperatorAsync(forum, ct, ["sign-head", "--by", "ops", "--if-grown"], () => pem);

        WholeText.Same("exit 0; signed head", $"exit {run.Exit}; {(run.Out.Contains("signed head", StringComparison.Ordinal) ? "signed head" : "no head: " + run.Err.Split('\n')[0])}");
    }

    /// <summary>LRV15: after a person's plain <c>sign-head</c> covers a forged row, a later <c>--if-grown</c> on a grown log runs (the stall clears by one plain head).</summary>
    [Fact]
    public async Task LRV15_AfterAPlainSignHeadCoversAForgedRowTheNextIfGrownRuns_Control()
    {
        var ct = TestContext.Current.CancellationToken;
        var (forum, victim, pem) = await SignedAnchorAsync(ct);
        await using var _ = forum;
        await VerifiedStopSupport.InsertRowAsync(forum, "agent.key-retired", victim.AgentId, VerifiedStopSupport.ForgedRetirement(victim.AgentId, victim.Kid), ct);
        var covered = await VerifiedStopSupport.OperatorAsync(forum, ct, ["sign-head", "--by", "ops"], () => pem);
        await VerifiedStopSupport.EnrolAsync(forum, "lrv15n", ct);
        var resumed = await VerifiedStopSupport.OperatorAsync(forum, ct, ["sign-head", "--by", "ops", "--if-grown"], () => pem);

        // The resume is tied to a head it signed, as the genuine-row half above ties its exit: an exit 0 with no head would pass a bare exit check.
        WholeText.Same(
            "plain 0; resumed 0 signed head",
            $"plain {covered.Exit}; resumed {resumed.Exit} {(resumed.Out.Contains("signed head", StringComparison.Ordinal) ? "signed head" : "no head: " + resumed.Err.Split('\n')[0])}");
    }
}

/// <summary>LRV16: <c>curia-operator lifecycle-audit</c> (ruling 13): read-only, exit 0 and <c>honoured &lt;h&gt;  skipped 0</c> on a clean log, exit 7 and one line per skipped row otherwise. Red on the seam: the verb does not exist.</summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class LifecycleAuditTests
{
    [GeneratedRegex(@"^honoured \d+  skipped 0$", RegexOptions.CultureInvariant, 2000)]
    private static partial Regex CleanLine();

    [Fact]
    public async Task LRV16_AuditOfACleanLogExitsZero()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var forum = await VerifiedStopSupport.NewAsync();
        await VerifiedStopSupport.EnrolAsync(forum, "lrv16c", ct);
        var before = await VerifiedStopSupport.CountAsync(forum, ct);

        var run = await VerifiedStopSupport.OperatorAsync(forum, ct, ["lifecycle-audit"]);
        var after = await VerifiedStopSupport.CountAsync(forum, ct);

        WholeText.Same(
            "exit 0; honoured <h>  skipped 0; 0 rows appended",
            $"exit {run.Exit}; {(CleanLine().IsMatch(run.Out.Trim()) ? "honoured <h>  skipped 0" : "'" + run.Out.Trim() + "' / '" + run.Err.Trim().Split('\n')[0] + "'")}; {after - before} rows appended");
    }

    [Fact]
    public async Task LRV16_AuditOfALogWithASkippedRowExitsSevenAndNamesTheRowWithoutItsPayload()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var forum = await VerifiedStopSupport.NewAsync();
        var victim = await VerifiedStopSupport.EnrolAsync(forum, "lrv16f", ct);
        var index = await VerifiedStopSupport.CountAsync(forum, ct);
        await VerifiedStopSupport.InsertRowAsync(forum, "agent.key-retired", victim.AgentId, VerifiedStopSupport.ForgedRetirement(victim.AgentId, victim.Kid), ct);

        var run = await VerifiedStopSupport.OperatorAsync(forum, ct, ["lifecycle-audit"]);

        var want = $"skipped  log_index={index.ToString(CultureInfo.InvariantCulture)}  type=agent.key-retired  aggregate={DisplayLiteral.Of(victim.AgentId)}  predicate=lifecycle-typ-mismatch";
        WholeText.Same(
            "exit 7; the line; no payload member",
            $"exit {run.Exit}; {(run.Out.Contains(want, StringComparison.Ordinal) ? "the line" : "no line [" + want + "] in [" + run.Out.Trim() + "]")}; "
            + (run.Out.Contains("this-is-not-a-signature-qq7", StringComparison.Ordinal) ? "LEAKS" : "no payload member"));
    }

    [Fact]
    public async Task LRV16_AuditListsAHundredRowsAndSaysHowManyMore()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var forum = await VerifiedStopSupport.NewAsync();
        var victim = await VerifiedStopSupport.EnrolAsync(forum, "lrv16m", ct);
        for (var i = 0; i < 103; i++)
            await VerifiedStopSupport.InsertRowAsync(forum, "agent.key-retired", victim.AgentId, VerifiedStopSupport.ForgedRetirement(victim.AgentId, victim.Kid), ct);

        var run = await VerifiedStopSupport.OperatorAsync(forum, ct, ["lifecycle-audit"]);

        var listed = run.Out.Split('\n').Count(l => l.StartsWith("skipped  log_index=", StringComparison.Ordinal));
        WholeText.Same("exit 7; 100 listed; and 3 more", $"exit {run.Exit}; {listed} listed; {(run.Out.Contains("and 3 more", StringComparison.Ordinal) ? "and 3 more" : "no more line")}");
    }

    [Fact]
    public async Task LRV16_AuditReadsNoKeyAndAStoreFaultIsExitFive()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var forum = await VerifiedStopSupport.NewAsync();

        // No key is read (the verb is given none and asks for none); a connection that cannot be made is a store fault, exit 5.
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await OperatorCommands.RunAsync(
            ["lifecycle-audit"], "Host=localhost;Port=1;Database=nothing;Username=nobody;Timeout=1", forum.Clock, forum.PermissiveGate(), OperatorCommands.LoadDeclarationSigner, stdout, stderr, ct,
            readLogSigningKeyPem: () => throw new InvalidOperationException("the audit asked for the log key"), readDeclarationSigningKeyPem: () => throw new InvalidOperationException("the audit asked for the declaration key"));

        // Exit 5 is also what any thrown exception maps to, so the cause is asserted: the store fault is named, and neither key was asked for (Amendment 7, ruling 88).
        var said = stderr.ToString();
        WholeText.Same(
            "exit 5; stderr names the store fault; no key asked for",
            $"exit {exit}; stderr {(said.Contains("store", StringComparison.OrdinalIgnoreCase) ? "names the store fault" : "does not name it: [" + said.Trim() + "]")}; "
            + (said.Contains("asked for the log key", StringComparison.Ordinal) || said.Contains("asked for the declaration key", StringComparison.Ordinal) ? "a key was asked for" : "no key asked for"));
    }
}

/// <summary>LRV17: EventId 7007 fires regardless of the guard (ruling 8 (a)). Red on the seam: the scan has no skipped row to report.</summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class RowsSkippedAtStartGuardOffTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    [Fact]
    public async Task LRV17_ASkippedRowIsReportedAtStartWithTheGuardOff()
    {
        var ct = TestContext.Current.CancellationToken;
        var victim = await VerifiedStopSupport.EnrolAsync(forum, "lrv17off", ct);
        await VerifiedStopSupport.InsertRowAsync(forum, "agent.key-retired", victim.AgentId, VerifiedStopSupport.ForgedRetirement(victim.AgentId, victim.Kid), ct);

        await using var captured = new CapturedForum(forum);
        var logged = captured.Log.All.Where(e => e.EventId == 7007).ToArray();

        // The structured Count the event carries is asserted (one row skipped), not a literal that is on both sides.
        var count = logged.Length == 1 ? logged[0].Values.FirstOrDefault(v => v.Key == "Count").Value?.ToString() : null;
        WholeText.Same(
            "one event 7007, Error, with Count 1 and the predicate",
            logged.Length == 1
                ? $"one event 7007, {(logged[0].Level == LogLevel.Error ? "Error" : logged[0].Level.ToString())}, with Count {count ?? "none"} and {(logged[0].Message.Contains("lifecycle-typ-mismatch", StringComparison.Ordinal) ? "the predicate" : "no predicate")}"
                : $"{logged.Length} events 7007: a log entry with EventId 7007 was expected, none");
    }
}

[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class RowsSkippedAtStartGuardOnTests(GuardedForumFixture forum) : IClassFixture<GuardedForumFixture>
{
    [Fact]
    public async Task LRV17_ASkippedRowIsReportedBy7006And7007WithTheGuardOn()
    {
        var ct = TestContext.Current.CancellationToken;
        var victim = await VerifiedStopSupport.EnrolAsync(forum, "lrv17on", ct);
        await VerifiedStopSupport.InsertRowAsync(forum, "agent.key-retired", victim.AgentId, VerifiedStopSupport.ForgedRetirement(victim.AgentId, victim.Kid), ct);

        await using var captured = new CapturedForum(forum);

        // 7007 is an Error that names the predicate, as the guard-off arm asserts, and not only an event that was logged.
        var skipped = captured.Log.All.Where(e => e.EventId == 7007).ToArray();
        WholeText.Same(
            "7006 and 7007; 7007 Error naming lifecycle-typ-mismatch",
            string.Join(" and ", VerifiedStopSupport.StartIds.Where(id => captured.Log.All.Any(e => e.EventId == id)).Select(id => id.ToString(CultureInfo.InvariantCulture)).DefaultIfEmpty("neither"))
            + $"; 7007 {(skipped.Length == 1 && skipped[0].Level == LogLevel.Error ? "Error" : "not one Error")} {(skipped.Length == 1 && skipped[0].Message.Contains("lifecycle-typ-mismatch", StringComparison.Ordinal) ? "naming lifecycle-typ-mismatch" : "naming no predicate")}");
    }
}

[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class RowsSkippedAtStartNoneTests(GuardedForumFixture forum) : IClassFixture<GuardedForumFixture>
{
    [Fact]
    public async Task LRV17_NoSkippedRowAndNoRowBeyondTheEnrolmentPairSaysNothing_Control()
    {
        var ct = TestContext.Current.CancellationToken;
        await VerifiedStopSupport.EnrolAsync(forum, "lrv17none", ct);

        await using var captured = new CapturedForum(forum);

        WholeText.Same("neither", string.Join(" and ", VerifiedStopSupport.StartIds.Where(id => captured.Log.All.Any(e => e.EventId == id)).Select(id => id.ToString(CultureInfo.InvariantCulture)).DefaultIfEmpty("neither")));
    }
}
