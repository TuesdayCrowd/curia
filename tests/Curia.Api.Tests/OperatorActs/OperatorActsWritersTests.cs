#pragma warning disable CA1305, CA1308, CA1707, CA1822, CA1031, CA2007, CA1861, CA1859, CA1062, CA1307, CA1310, CA1002, CA1819
using System.Reflection;
using System.Text.Json.Nodes;
using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.Domain;
using Curia.Domain.Primitives;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Curia.OperatorTool;
using Npgsql;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// PR N2 (errata G70, R6.66, R11.97), Task 4: the writers' side of the probes that need a database of their own. OAS19c is register item 300: <c>sign-head</c> appends its first log key at the number of
/// events the <c>log:keys</c> aggregate holds, and not at the number of keys that hold a position.
/// </summary>
public sealed class OperatorActsWritersTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<long> AggregateCountAsync(ForumFixture forum, string aggregate)
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM events WHERE aggregate_id = @a;", connection);
        command.Parameters.AddWithValue("a", aggregate);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static string KeyEntry(string kid, JsonObject jwk, DateTimeOffset at)
    {
        var copy = JsonNode.Parse(jwk.ToJsonString())!.AsObject();
        copy["kid"] = kid;
        return new JsonObject { ["jwk"] = copy, ["kid"] = kid, ["valid_from"] = at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture) }.ToJsonString();
    }

    /// <summary>
    /// OAS19c: the head signer publishes its first log key and signs a head whatever rows of <c>log:keys</c> hold no position (R6.66 rule (a) and rule (c)). Legs: a <c>log.key</c> row that republishes the
    /// operator key's public key under another <c>kid</c> (rule (c): no position; the form this PR adds); a row that repeats a log key's <c>kid</c> (rule (a): no position in the verifier's list; a characterization,
    /// GREEN before the fix, because <c>ActaLog.Keys</c> still lists a repeated <c>kid</c>, so the old version equalled the aggregate's count: Amendment 3's claim that this form already stalled the
    /// head signer is wrong for log keys); and the control, with neither. The counts are the aggregates' own events: the rows inserted, the key the run publishes, and the heads.
    /// </summary>
    [Theory]
    [InlineData("a log.key row republishing the operator key under another kid", 2, 1)]
    [InlineData("a log.key row repeating a published log key's kid", 3, 2)]
    [InlineData("no such row (control)", 1, 1)]
    public async Task OAS19c_TheHeadSignerCountsTheAggregatesEventsNotItsKeys(string leg, int expectedKeyEvents, int expectedHeads)
    {
        await using var own = await VerifiedStopSupport.NewAsync();
        var api = new OperatorActsApi(own);

        // The operator key is a key no head or declaration ever uses; the legs that republish it say so: they are the ones whose point is that two keys are equal.
        using var operatorKey = DeclarationSigningKey.FromPem(DeclarationSigningKey.GeneratePem());
        var operatorJwk = JsonNode.Parse(OperatorActsApi.ToJson(operatorKey.PublicJwk()))!.AsObject();
        await api.InsertRowAsync("log.operator-key", "log:operator-keys", KeyEntry(operatorKey.Kid, operatorJwk, own.Now), "operator:ops", own.Now, Ct);

        if (leg.StartsWith("a log.key row republishing", StringComparison.Ordinal))
        {
            await api.InsertRowAsync("log.key", "log:keys", KeyEntry("operator-key-as-log-key", operatorJwk, own.Now), "operator:ops", own.Now, Ct);
        }
        else if (leg.StartsWith("a log.key row repeating", StringComparison.Ordinal))
        {
            var first = LogSigningKey.GeneratePem();
            var firstRun = await VerifiedStopSupport.OperatorAsync(own, Ct, ["sign-head", "--by", "ops"], () => first);
            Assert.Equal(0, firstRun.Exit);
            var firstKid = System.Text.RegularExpressions.Regex.Match(firstRun.Out, @"^published key\s+(\S+)", System.Text.RegularExpressions.RegexOptions.Multiline).Groups[1].Value;
            using var held = LogSigningKey.FromPem(first);
            var heldJwk = JsonNode.Parse(OperatorActsApi.ToJson(held.PublicJwk()))!.AsObject();
            await api.InsertRowAsync("log.key", "log:keys", KeyEntry(firstKid, heldJwk, own.Now), "operator:ops", own.Now, Ct);
        }

        var pem = LogSigningKey.GeneratePem();
        var run = await VerifiedStopSupport.OperatorAsync(own, Ct, ["sign-head", "--by", "ops"], () => pem);
        var published = run.Out.Contains("published key", StringComparison.Ordinal);
        var signed = run.Out.Contains("signed head", StringComparison.Ordinal);
        OperatorActsApi.Log($"OAS19c [{leg}]: exit {run.Exit}; stdout[{run.Out.Trim().Replace('\n', '|')}]; stderr[{run.Err.Trim().Replace('\n', '|')}]");

        WholeText.Same(
            $"exit 0; key published; head signed; log:keys holds {expectedKeyEvents}; log:heads holds {expectedHeads}",
            $"exit {run.Exit}; {(published ? "key published" : "no key published")}; {(signed ? "head signed" : "no head signed")}; log:keys holds {await AggregateCountAsync(own, "log:keys")}; log:heads holds {await AggregateCountAsync(own, "log:heads")}");
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------
    // Task 4 (re-cut): the tool signs and refuses. OAS16b, OAS16c, OAS19b, OAS19e, OAS23b and the RefuseAsync mapping.
    // ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------

    private static string[] AttestArgs(string agent) => ["attest-owner", "--agent", agent, "--owner", "owner:probe", "--by", "ops"];

    private static string[] ModerateArgs(string post) => ["moderate", "--post", post, "--category", "spam", "--effect", "withhold", "--reason", "probe", "--by", "ops"];

    private static string ErrorTypeOf(string stderr)
    {
        var m = System.Text.RegularExpressions.Regex.Match(stderr, @"\((curia/[^)]+)\)");
        return m.Success ? m.Groups[1].Value : stderr.Trim();
    }

    /// <summary>
    /// OAS16b: <c>attest-owner</c> and <c>moderate</c> with a key the log has not published refuse before anything is signed (exit 2, <c>curia/keys/operator-key-not-published</c>) and append nothing; the
    /// control publishes that key and runs the same verb (exit 0, one row).
    /// </summary>
    [Theory]
    [InlineData("attest-owner")]
    [InlineData("moderate")]
    public async Task OAS16b_AVerbRefusesAKeyTheLogHasNotPublished(string verb)
    {
        ArgumentNullException.ThrowIfNull(verb);
        await using var own = await VerifiedStopSupport.NewAsync();
        var api = new OperatorActsApi(own);
        var agent = await api.GenuineAsync("keyed", Ct);
        var post = await api.QuestionAsync("keyed-post", Ct);
        var args = verb == "attest-owner" ? AttestArgs(agent.Agent.AgentId) : ModerateArgs(post.Id);
        var pem = OperatorActsSigningKey.GeneratePem();

        var before = await api.EventCountAsync(Ct);
        var refused = await api.OperatorWithKeysAsync(args, Ct, operatorActsKey: () => pem);
        var after = await api.EventCountAsync(Ct);
        Assert.Equal(0, (await api.OperatorWithKeysAsync(["publish-operator-key", "--by", "ops"], Ct, operatorActsKey: () => pem)).Exit);
        var published = await api.EventCountAsync(Ct);
        var control = await api.OperatorWithKeysAsync(args, Ct, operatorActsKey: () => pem);
        var ran = await api.EventCountAsync(Ct);

        WholeText.Same(
            "unpublished: exit 2 (curia/keys/operator-key-not-published), nothing appended; control: exit 0, 1 row appended",
            $"unpublished: exit {refused.Exit} ({ErrorTypeOf(refused.Err)}), {(after == before ? "nothing appended" : "appended")}; control: exit {control.Exit}, {ran - published} row appended");
    }

    /// <summary>
    /// OAS16c: <c>publish-declaration-key</c> and <c>compromise</c> refuse (exit 3) an environment that holds the operator-acts key beside the declaration key, and append nothing; the controls run each
    /// without it. <c>compromise</c> refuses exit 4 under the lifecycle guard before it reads either key (R4.38), so this reaches the mixed-environment refusal with the guard lifted: the in-process runs
    /// are handed the fixture's permissive gate, as <c>CompromiseDeclarationTests</c> does.
    /// </summary>
    [Fact]
    public async Task OAS16c_ADeclarationVerbRefusesAnEnvironmentThatHoldsTheOperatorActsKey()
    {
        await using var own = await VerifiedStopSupport.NewAsync();
        var api = new OperatorActsApi(own);
        var agent = await api.GenuineAsync("declared", Ct);
        var bound = await api.EventCountAsync(Ct) - 1;
        var operatorPem = OperatorActsSigningKey.GeneratePem();
        var declarationPem = DeclarationSigningKey.GeneratePem();
        string[] compromise = ["compromise", "--agent", agent.Agent.AgentId, "--kid", agent.Agent.Kid, "--from-index", bound.ToString(System.Globalization.CultureInfo.InvariantCulture), "--reason", "owner-reported", "--operator", "ops"];

        var results = new List<string>();
        foreach (var (name, args) in new (string, string[])[] { ("publish-declaration-key", ["publish-declaration-key", "--by", "ops"]), ("compromise", compromise) })
        {
            var rows = await api.EventCountAsync(Ct);
            var mixed = await api.OperatorWithKeysAsync(args, Ct, operatorActsKey: () => operatorPem, declarationKey: () => declarationPem);
            var refused = $"{name} with the operator key: exit {mixed.Exit}, {(await api.EventCountAsync(Ct) == rows ? "nothing appended" : "appended")}";

            // The control: the same verb without it. compromise needs the declaration key published, which publish-declaration-key's control, the leg before it, has done.
            var before = await api.EventCountAsync(Ct);
            var control = await api.OperatorWithKeysAsync(args, Ct, declarationKey: () => declarationPem);
            results.Add($"{refused}; control: exit {control.Exit}, {await api.EventCountAsync(Ct) - before} row appended");
        }

        WholeText.Same(
            "publish-declaration-key with the operator key: exit 3, nothing appended; control: exit 0, 1 row appended; compromise with the operator key: exit 3, nothing appended; control: exit 0, 1 row appended",
            string.Join("; ", results));
    }

    /// <summary>
    /// OAS19b: <c>sign-head</c> refuses to publish a log key that is a published operator key (exit 2, <c>curia/keys/log-key-is-operator-key</c>), before the <c>log.key</c> append and the head: no
    /// <c>log:keys</c> row and no head. The control, a different log key, runs. The refused leg's two keys are equal by design: the log key is the very key <c>publish-operator-key</c> published.
    /// </summary>
    [Fact]
    public async Task OAS19b_TheHeadSignerRefusesALogKeyThatIsAnOperatorKey()
    {
        await using var own = await VerifiedStopSupport.NewAsync();
        var api = new OperatorActsApi(own);
        var operatorPem = OperatorActsSigningKey.GeneratePem();
        Assert.Equal(0, (await api.OperatorWithKeysAsync(["publish-operator-key", "--by", "ops"], Ct, operatorActsKey: () => operatorPem)).Exit);

        var refused = await api.OperatorWithKeysAsync(["sign-head", "--by", "ops"], Ct, logKey: () => operatorPem);
        var afterRefusal = $"exit {refused.Exit} ({ErrorTypeOf(refused.Err)}); log:keys holds {await AggregateCountAsync(own, "log:keys")}; log:heads holds {await AggregateCountAsync(own, "log:heads")}";

        var other = LogSigningKey.GeneratePem();
        Assert.NotEqual(operatorPem, other);
        var control = await api.OperatorWithKeysAsync(["sign-head", "--by", "ops"], Ct, logKey: () => other);
        var afterControl = $"exit {control.Exit}; log:keys holds {await AggregateCountAsync(own, "log:keys")}; log:heads holds {await AggregateCountAsync(own, "log:heads")}";

        WholeText.Same(
            "exit 2 (curia/keys/log-key-is-operator-key); log:keys holds 0; log:heads holds 0\nexit 0; log:keys holds 1; log:heads holds 1",
            afterRefusal + "\n" + afterControl);
    }

    /// <summary>
    /// OAS19e (register item 300, ruling 62): <c>sign-head</c> appends a head at the number of events <c>log:heads</c> holds, and not at the number of entries that read as a head. A log holding an operator key
    /// and a first head; leg one adds a well-formed <c>log.head</c> row in another aggregate, a head the Acta counts and <c>log:heads</c> does not hold; then <c>sign-head</c> must run (exit 0) and leave
    /// <c>log:heads</c> holding 2. Leg two is the control, with no such row.
    /// </summary>
    [Theory]
    [InlineData("a well-formed log.head row in another aggregate")]
    [InlineData("no such row (control)")]
    public async Task OAS19e_TheHeadSignerCountsTheHeadsAggregatesEventsNotTheHeadsItReads(string leg)
    {
        ArgumentNullException.ThrowIfNull(leg);
        await using var own = await VerifiedStopSupport.NewAsync();
        var api = new OperatorActsApi(own);
        var operatorPem = OperatorActsSigningKey.GeneratePem();
        Assert.Equal(0, (await api.OperatorWithKeysAsync(["publish-operator-key", "--by", "ops"], Ct, operatorActsKey: () => operatorPem)).Exit);
        var logPem = LogSigningKey.GeneratePem();
        Assert.Equal(0, (await api.OperatorWithKeysAsync(["sign-head", "--by", "ops"], Ct, logKey: () => logPem)).Exit);

        if (leg.StartsWith("a well-formed", StringComparison.Ordinal))
        {
            string shape;
            await using (var connection = new NpgsqlConnection(own.ConnectionString))
            {
                await connection.OpenAsync(Ct);
                await using var command = new NpgsqlCommand("SELECT payload::text FROM events WHERE event_type = 'log.head' ORDER BY seq LIMIT 1;", connection);
                shape = (string)(await command.ExecuteScalarAsync(Ct))!;
            }

            await api.InsertRowAsync("log.head", "log:stray-heads", shape, "operator:ops", own.Now, Ct);
        }

        var run = await api.OperatorWithKeysAsync(["sign-head", "--by", "ops"], Ct, logKey: () => logPem);
        OperatorActsApi.Log($"OAS19e [{leg}]: exit {run.Exit}; stdout[{run.Out.Trim().Replace('\n', '|')}]; stderr[{run.Err.Trim().Replace('\n', '|')}]");

        WholeText.Same(
            "exit 0; a second head signed; log:heads holds 2",
            $"exit {run.Exit}{(run.Exit == 0 ? string.Empty : " (" + ErrorTypeOf(run.Err) + ")")}; {(run.Out.Contains("signed head", StringComparison.Ordinal) ? "a second head signed" : "no head signed")}; log:heads holds {await AggregateCountAsync(own, "log:heads")}");
    }

    /// <summary>A reader whose every read is a store fault, as a database that cannot be reached throws (R11.71).</summary>
    private sealed class UnreachableReader : IEventReader
    {
        public Task<Result<IReadOnlyList<AppendedEvent>>> ReadByAggregateAsync(AggregateId aggregateId, CancellationToken cancellationToken = default) =>
            throw new LogUnreadableException("08006");

        public Task<Result<IReadOnlyList<AppendedEvent>>> ReadForwardAsync(EventSequence afterSeq, int? maxCount = null, CancellationToken cancellationToken = default) =>
            throw new LogUnreadableException("08006");
    }

    /// <summary>
    /// OAS23b: a store fault when the Forum starts logs a Warning (EventId 7011, naming the cause and no payload) and the Forum starts: the scan is a report, and a report is not a guard (it does not copy
    /// <c>LifecycleEntryScan</c>, which throws, because that scan holds back what the guard keeps out). The host is the real one, with the scan's reader replaced by one that cannot reach the store.
    /// </summary>
    [Fact]
    public async Task OAS23b_AStoreFaultAtStartIsAWarningAndTheForumStarts()
    {
        await using var own = await VerifiedStopSupport.NewAsync();
        using var log = new LogCapture();
        await using var host = own.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.Remove(services.Single(d => d.ImplementationType == typeof(Curia.Api.Adapters.OperatorActEntryScan)));
            services.AddSingleton<ILoggerProvider>(log);
            services.AddSingleton<IHostedService>(sp => new Curia.Api.Adapters.OperatorActEntryScan(
                new UnreachableReader(), sp.GetRequiredService<LifecycleVerifier>(), sp.GetRequiredService<ILogger<Curia.Api.Adapters.OperatorActEntryScan>>()));
        }));
        using var client = host.CreateClient();
        using var served = await client.GetAsync(new Uri("/v1/log/jwks", UriKind.Relative), Ct);

        var scan = log.All.Where(e => e.Category.EndsWith("OperatorActEntryScan", StringComparison.Ordinal)).ToArray();
        WholeText.Same(
            "the Forum serves 200; one Warning 7011 naming the cause 08006; no 7010",
            $"the Forum serves {(int)served.StatusCode}; {(scan.Count(e => e.EventId == 7011 && e.Level == LogLevel.Warning && e.Message.Contains("08006", StringComparison.Ordinal)) == 1 ? "one" : "not one")} Warning 7011 naming the cause 08006; {(scan.Any(e => e.EventId == 7010) ? "a 7010" : "no 7010")}");
    }

    /// <summary>
    /// The refusal handler and the route map, for the two new refusals' words: <c>RefuseAsync</c> given <c>curia/acts/not-honoured</c> exits 7 and writes the row's index and predicate and no payload; the
    /// routes' map answers <c>curia/acts/not-honoured</c> 500 (no route raises it, so an unlisted type is the Forum's fault and never a 4xx) and the two new <c>KeyActErrors</c> types 409.
    /// </summary>
    [Fact]
    public async Task RefuseAsync_ANotHonouredActExitsSevenAndTheRoutesMapTheNewTypes()
    {
        var refuse = typeof(OperatorCommands).GetMethod("RefuseAsync", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Curia.OperatorTool.OperatorCommands.RefuseAsync is not found: the refusal handler this probe reads was renamed");
        using var stderr = new StringWriter();
        var exit = await (Task<int>)refuse.Invoke(null, [stderr, ActErrors.NotHonoured(7, "act-signature-invalid")])!;

        var map = typeof(ForumEndpoints).GetMethod("StatusForKeyAct", BindingFlags.NonPublic | BindingFlags.Static)!;
        int Status(string type) => (int)map.Invoke(null, [new Error(type, "t")])!;

        WholeText.Same(
            "exit 7; stderr names index 7 and act-signature-invalid: True; not-honoured 500; operator-key-not-published 409; log-key-is-operator-key 409",
            $"exit {exit}; stderr names index 7 and act-signature-invalid: {stderr.ToString().Contains("log index 7", StringComparison.Ordinal) && stderr.ToString().Contains("act-signature-invalid", StringComparison.Ordinal)}; "
            + $"not-honoured {Status(ActErrors.NotHonouredType)}; operator-key-not-published {Status(KeyActErrors.OperatorKeyNotPublishedType)}; log-key-is-operator-key {Status(KeyActErrors.LogKeyIsOperatorKeyType)}");
    }
}
