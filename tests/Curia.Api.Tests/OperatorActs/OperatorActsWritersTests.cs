#pragma warning disable CA1305, CA1308, CA1707, CA1822, CA1031, CA2007, CA1861, CA1859, CA1062, CA1307, CA1310, CA1002, CA1819
using System.Text.Json.Nodes;
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
}
