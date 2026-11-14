using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Client;
using Curia.Domain;
using Curia.Domain.Acta;
using Curia.Domain.Keys;
using Curia.Domain.Primitives;
using Curia.Domain.Serving;
using Curia.Infrastructure;
using Curia.Infrastructure.Lifecycle;
using Curia.OperatorTool;
using Curia.Tests.Shared;
using Npgsql;
using Xunit;
using JsonArray = System.Text.Json.Nodes.JsonArray;
using JsonObject = System.Text.Json.Nodes.JsonObject;

namespace Curia.Api.Tests;

/// <summary>
/// PR H (errata G67: R6.62, R6.63, R11.94, R12.18): a head signed unattended, a declaration key of its own, and the words that say when the latest head was signed. Each test
/// builds the Forum it needs (a database of its own, a host where it reads a served route), so that one log is not another's: growth is a fact about the whole log.
/// The oracles are the events table, read by SQL and not through the code under test, and the operator tool's exit codes and words as the verbs print them.
/// HSU8 to HSU17, HSU26, HSU28, HSU31 and HSU35 to HSU38 live here; HSU36 is D121's Forum half as the Forum now answers it: a forged row is skipped and marks nothing.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage(
    "Reliability",
    "CA2000:Dispose objects before losing scope",
    Justification = "Each fixture is an await using declaration; the analyzer loses the ownership through the generic factory.")]
[SuppressMessage(
    "Security",
    "CA2100:Review SQL queries for security vulnerabilities",
    Justification = "Every statement is a literal or is built from names this test generated itself (a role and a database it created); nothing reaches it from outside.")]
public sealed partial class HeadsSignedUnattendedTests
{
    private sealed record Run(int Exit, string Out, string Err);

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>Compares two strings and, where they differ, shows both whole: xUnit cuts a long string short at the first difference, and a red here is read by what it says.</summary>
    private static void Same(string expected, string actual) =>
        Assert.True(string.Equals(expected, actual, StringComparison.Ordinal), $"expected [{expected}] but got [{actual}]");

    /// <summary>Text with nothing in common with another call's: two posts that are near-duplicates carry a non-integer cosine, which the client refuses (its ADMIT profile), and a probe of the lifecycle is not about that.</summary>
    private static string Words() => string.Join(' ', Enumerable.Range(0, 8).Select(_ => Guid.NewGuid().ToString("N")[..10]));

    private static async Task<T> NewAsync<T>() where T : ForumFixture, new()
    {
        var forum = new T();
        await forum.InitializeAsync();
        return forum;
    }

    [SuppressMessage("Design", "CA1068:CancellationToken parameters must come last", Justification = "A test helper with optional parameters: the token is the first thing a call names and the optional ones follow.")]
    private static async Task<Run> OperatorAsync(
        ForumFixture forum,
        CancellationToken ct,
        string[] args,
        Func<string?>? logKey = null,
        Func<string?>? declarationKey = null,
        ILifecycleAppendGate? gate = null,
        string? connection = null,
        TimeProvider? clock = null)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            var exit = await OperatorCommands.RunAsync(
                args, connection ?? forum.ConnectionString, clock ?? forum.Clock, gate ?? forum.PermissiveGate(), OperatorCommands.LoadDeclarationSigner, stdout, stderr, ct,
                readLogSigningKeyPem: logKey, readDeclarationSigningKeyPem: declarationKey);
            return new Run(exit, stdout.ToString(), stderr.ToString());
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // An unhandled exception is what a verb that had no word for a fault did; the probe reads it as a result so that its assertion, and not a stack trace, is the red.
            return new Run(-1, stdout.ToString(), "THREW " + e.GetType().Name + ": " + e.Message);
        }
    }

    private static Func<string?> Pem(string? pem) => () => pem;

    private static async Task<long> CountAsync(ForumFixture forum, string? type, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(type is null ? "SELECT count(*) FROM events;" : "SELECT count(*) FROM events WHERE event_type = @t;", connection);
        if (type is not null) command.Parameters.AddWithValue("t", type);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    private static string AppRoleConnection(ForumFixture forum) =>
        new NpgsqlConnectionStringBuilder(forum.ConnectionString) { Username = forum.RoleName, Password = forum.RolePassword }.ConnectionString;

    /// <summary>One entry that is neither a head nor a key, appended through the Forum's own event store adapter: the log has grown.</summary>
    private static async Task GrowAsync(ForumFixture forum, CancellationToken ct)
    {
        static T Require<T>(Result<T> result) => result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} ({e.Detail})"));

        var adapters = new PostgresAdapters(forum.ConnectionString, forum.Clock, forum.PermissiveGate());
        await using (adapters.ConfigureAwait(false))
        {
            Require(await adapters.EventStore.AppendAsync(
                Require(AggregateId.Create("noise:" + Suffix())),
                AggregateVersion.New,
                [new DomainEvent(
                    Require(EventId.Create(Require(new UlidGenerator(forum.Clock).Next()).ToString())),
                    Require(EventType.Create("test.noise")),
                    Require(ActorId.Create("operator:test")),
                    new JsonValue.Object([new("n", new JsonValue.Number(1))]))],
                ct));
        }
    }

    /// <summary>A row appended by SQL under the app role and through no use case: what a holder of the Forum's database credential can do.</summary>
    private static async Task InsertRowAsync(ForumFixture forum, string type, string aggregate, JsonObject payload, CancellationToken ct)
    {
        var id = new UlidGenerator(forum.Clock).Next().Match(u => u.ToString(), e => throw new InvalidOperationException(e.Type));
        await using var connection = new NpgsqlConnection(AppRoleConnection(forum));
        await connection.OpenAsync(ct);
        await using var insert = new NpgsqlCommand(
            "INSERT INTO events (event_id, event_type, aggregate_id, actor_id, payload, server_ts) VALUES (@id, @type, @aggregate, 'operator:ops', @payload::jsonb, @ts);", connection);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("type", type);
        insert.Parameters.AddWithValue("aggregate", aggregate);
        insert.Parameters.AddWithValue("payload", payload.ToJsonString());
        insert.Parameters.AddWithValue("ts", forum.Now);
        Assert.Equal(1, await insert.ExecuteNonQueryAsync(ct));
    }

    private static async Task<IReadOnlyList<AppendedEvent>> ReadLogAsync(ForumFixture forum, CancellationToken ct)
    {
        var adapters = new PostgresAdapters(forum.ConnectionString, forum.Clock, forum.PermissiveGate());
        await using (adapters.ConfigureAwait(false))
        {
            return (await adapters.EventStore.ReadAllAsync(ct)).Match(v => v, e => throw new InvalidOperationException(e.Type));
        }
    }

    private static async Task<(ForumAgent Agent, DpopClient Dpop, string Token)> EnrolAsync(ForumFixture forum, string stem, CancellationToken ct)
    {
        var agent = ForumAgent.Create($"https://agents.example/{stem}-{Suffix()}", $"{stem}-{Suffix()}");
        var (dpop, token) = await agent.AuthenticateAsync(forum.Client, KeyActRequests.TokenEndpoint, forum.Now, ct);
        return (agent, dpop, token);
    }

    private static async Task<(string PostId, long LogIndex)> AskAsync(
        ForumFixture forum, ForumAgent agent, DpopClient dpop, string token, string board, string title, string body, CancellationToken ct)
    {
        using var response = await dpop.PostAsync(forum.Client, "http://localhost/v1/posts", token, agent.SignQuestion(board, body, title, forum.Now), forum.Now, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, text);
        using var accepted = JsonDocument.Parse(text);
        var postId = accepted.RootElement.GetProperty("post_id").GetString()!;
        var served = await forum.Client.GetFromJsonAsync<JsonElement>($"/v1/posts/{postId}", ct);
        return (postId, served.GetProperty("log_index").GetInt64());
    }

    // ---------------------------------------------------------------------------------------------------------------------------------- --if-grown

    /// <summary>
    /// HSU8. <c>sign-head --if-grown</c> on a log whose only entries after the latest head are its own appends nothing, publishes no key on a log that has none yet, says
    /// <c>unchanged</c> and exits 0; the same call without the flag appends one head (the control that the flag is what stopped it). Red on the seam: <c>Expected: 0  Actual: 1</c>.
    /// </summary>
    [Fact]
    public async Task R6_63_HSU8_IfGrownSignsNothingOnALogThatHasNotGrown()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var forum = await NewAsync<ForumFixture>();
        var pem = LogSigningKey.GeneratePem();

        // (a) an empty log: nothing to sign, and no key published for it.
        var emptyBefore = await CountAsync(forum, null, ct);
        var empty = await OperatorAsync(forum, ct, ["sign-head", "--by", "ops", "--if-grown"], logKey: Pem(pem));
        var emptyAfter = await CountAsync(forum, null, ct);

        // (b) a log that has grown and been signed, then signed again by the flag.
        await GrowAsync(forum, ct);
        var first = await OperatorAsync(forum, ct, ["sign-head", "--by", "ops"], logKey: Pem(pem));
        var signedAt = await CountAsync(forum, null, ct);
        var quiet = await OperatorAsync(forum, ct, ["sign-head", "--by", "ops", "--if-grown"], logKey: Pem(pem));
        var quietAfter = await CountAsync(forum, null, ct);

        // (c) the control: the same call without the flag appends one head.
        var control = await OperatorAsync(forum, ct, ["sign-head", "--by", "ops"], logKey: Pem(pem));
        var controlAfter = await CountAsync(forum, null, ct);

        var keys = await CountAsync(forum, LogEntries.KeyType, ct);
        string Says(Run run) => run.Out.StartsWith("unchanged", StringComparison.Ordinal) ? "unchanged" : "'" + run.Out.Trim() + "'";

        Same(
            "empty log: 0 rows, exit 0, unchanged | grown log signed once: exit 0, 1 key | quiet: 0 rows, exit 0, unchanged | control: exit 0, 1 row",
            $"empty log: {emptyAfter - emptyBefore} rows, exit {empty.Exit}, {Says(empty)} | grown log signed once: exit {first.Exit}, {keys} key"
            + $" | quiet: {quietAfter - signedAt} rows, exit {quiet.Exit}, {Says(quiet)} | control: exit {control.Exit}, {controlAfter - quietAfter} row");
    }

    /// <summary>
    /// HSU9. A post, then <c>--if-grown</c>: one <c>log.key</c> and one <c>log.head</c>; a second at once: <c>unchanged</c>, 0 rows; a post and a third run: one <c>log.head</c> and no second key.
    /// Red on the seam: the second run appends.
    /// </summary>
    [Fact]
    public async Task R6_63_HSU9_AGrownLogIsSignedOnceAndThenLeftAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var forum = await NewAsync<ForumFixture>();
        var pem = LogSigningKey.GeneratePem();
        string[] args = ["sign-head", "--by", "ops", "--if-grown"];

        await GrowAsync(forum, ct);
        var first = await OperatorAsync(forum, ct, args, logKey: Pem(pem));
        var afterFirst = (Keys: await CountAsync(forum, LogEntries.KeyType, ct), Heads: await CountAsync(forum, LogEntries.HeadType, ct), All: await CountAsync(forum, null, ct));
        var second = await OperatorAsync(forum, ct, args, logKey: Pem(pem));
        var afterSecond = await CountAsync(forum, null, ct);

        await GrowAsync(forum, ct);
        var third = await OperatorAsync(forum, ct, args, logKey: Pem(pem));
        var afterThird = (Keys: await CountAsync(forum, LogEntries.KeyType, ct), Heads: await CountAsync(forum, LogEntries.HeadType, ct));

        Same(
            "first: exit 0, published key, signed head; 1 key 1 head | second: exit 0, unchanged, 0 rows | third: exit 0, signed head; 1 key 2 heads",
            $"first: exit {first.Exit}, {(first.Out.Contains("published key", StringComparison.Ordinal) ? "published key" : "no key line")}, {(first.Out.Contains("signed head", StringComparison.Ordinal) ? "signed head" : "no head line")}; {afterFirst.Keys} key {afterFirst.Heads} head"
            + $" | second: exit {second.Exit}, {(second.Out.StartsWith("unchanged", StringComparison.Ordinal) ? "unchanged" : "'" + second.Out.Split('\n')[0].Trim() + "'")}, {afterSecond - afterFirst.All} rows"
            + $" | third: exit {third.Exit}, {(third.Out.Contains("signed head", StringComparison.Ordinal) ? "signed head" : "no head line")}; {afterThird.Keys} key {afterThird.Heads} heads");
    }

    /// <summary>
    /// HSU10. The miniature timer: a manual clock, 288 ticks of five minutes (a day), <c>--if-grown</c> each tick, one post at tick 100 and one at tick 200: exactly 2 heads and 1 key;
    /// the same ticks without the flag: 288 heads. Counted by SQL over <c>events</c>. Red on the seam: <c>Expected: 2  Actual: 288</c>.
    /// </summary>
    [Fact]
    public async Task R6_63_HSU10_ADayOfTicksSignsTwoHeadsWhenTheLogGrewTwice()
    {
        var ct = TestContext.Current.CancellationToken;
        var pem = LogSigningKey.GeneratePem();

        async Task<(long Heads, long Keys)> DayAsync(bool ifGrown)
        {
            await using var forum = await NewAsync<ForumFixture>();
            for (var tick = 0; tick < 288; tick++)
            {
                if (tick is 100 or 200) await GrowAsync(forum, ct);
                string[] args = ifGrown ? ["sign-head", "--by", "ops", "--if-grown"] : ["sign-head", "--by", "ops"];
                var run = await OperatorAsync(forum, ct, args, logKey: Pem(pem));
                Assert.True(run.Exit == 0, $"tick {tick}: exit {run.Exit}: {run.Err}");
                forum.Clock.Advance(TimeSpan.FromMinutes(5));
            }

            return (await CountAsync(forum, LogEntries.HeadType, ct), await CountAsync(forum, LogEntries.KeyType, ct));
        }

        var withFlag = await DayAsync(ifGrown: true);
        var withoutFlag = await DayAsync(ifGrown: false);

        Same("2 heads 1 key with the flag; 288 heads without", $"{withFlag.Heads} heads {withFlag.Keys} key with the flag; {withoutFlag.Heads} heads without");
    }

    /// <summary>
    /// HSU11. Arguments: <c>--if-grown</c> alone is exit 1 (<c>--by</c> is required); <c>--if-grownn</c> is exit 1 <c>unexpected argument</c>; <c>--if-grown</c> twice is exit 1; nothing appended in any.
    /// Red on the seam for the twice case (the seam parses the flag and ignores it, so it signs).
    /// </summary>
    [Fact]
    public async Task R6_63_HSU11_TheFlagIsParsedStrictly()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var forum = await NewAsync<ForumFixture>();
        var pem = LogSigningKey.GeneratePem();
        await GrowAsync(forum, ct);
        var before = await CountAsync(forum, null, ct);

        var alone = await OperatorAsync(forum, ct, ["sign-head", "--if-grown"], logKey: Pem(pem));
        var typo = await OperatorAsync(forum, ct, ["sign-head", "--by", "ops", "--if-grownn"], logKey: Pem(pem));
        var twice = await OperatorAsync(forum, ct, ["sign-head", "--by", "ops", "--if-grown", "--if-grown"], logKey: Pem(pem));
        var after = await CountAsync(forum, null, ct);

        Same(
            "1 | 1 unexpected argument '--if-grownn' | 1 | 0 rows",
            $"{alone.Exit} | {typo.Exit} {(typo.Err.Contains("unexpected argument '--if-grownn'", StringComparison.Ordinal) ? "unexpected argument '--if-grownn'" : "'" + typo.Err.Trim() + "'")} | {twice.Exit} | {after - before} rows");
    }

    // ---------------------------------------------------------------------------------------------------------------------- the two keys, one tool

    /// <summary>
    /// HSU12. The environment rule (R12.18, Amendment 3 ruling 41): <c>sign-head</c> with a declaration key in its environment is exit 3, naming the variable, saying to unset it, holding no part of
    /// the value, and saying <c>No key was parsed or used, and nothing was signed or written.</c>; <c>publish-declaration-key</c> and <c>compromise</c> with a log key in theirs are exit 3 the same way;
    /// each succeeds with only its own variable (the control). With both variables holding an invalid PEM the refusal is still the environment rule's and never <c>not a usable P-256 key</c>: no key is
    /// parsed in an environment that holds both. The sentence is written here, a restatement of the words. Red on the tip: the sentence says <c>Nothing was read, signed or written.</c>
    /// </summary>
    [Fact]
    public async Task R12_18_HSU12_NoVerbRunsWithBothKeysInItsEnvironment()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var forum = await NewAsync<ForumFixture>();
        const string Secret = "NOT-A-PEM-SECRET-qzx81";
        const string Sentence = "No key was parsed or used, and nothing was signed or written.";
        var logPem = LogSigningKey.GeneratePem();
        var declarationPem = DeclarationSigningKey.GeneratePem();
        await GrowAsync(forum, ct);
        var before = await CountAsync(forum, null, ct);

        var signWithBoth = await OperatorAsync(forum, ct, ["sign-head", "--by", "ops"], logKey: Pem(logPem), declarationKey: Pem(Secret));
        var publishWithBoth = await OperatorAsync(forum, ct, ["publish-declaration-key", "--by", "ops"], logKey: Pem(Secret), declarationKey: Pem(declarationPem));
        string[] compromise = ["compromise", "--agent", "https://agents.example/nobody", "--kid", "k", "--from-index", "1", "--reason", "owner-reported", "--operator", "ops"];
        var compromiseWithBoth = await OperatorAsync(forum, ct, compromise, logKey: Pem(Secret), declarationKey: Pem(declarationPem));

        // Both variables hold an invalid PEM: the environment rule decides, and no key is parsed.
        var signInvalid = await OperatorAsync(forum, ct, ["sign-head", "--by", "ops"], logKey: Pem(Secret), declarationKey: Pem(Secret));
        var publishInvalid = await OperatorAsync(forum, ct, ["publish-declaration-key", "--by", "ops"], logKey: Pem(Secret), declarationKey: Pem(Secret));
        var compromiseInvalid = await OperatorAsync(forum, ct, compromise, logKey: Pem(Secret), declarationKey: Pem(Secret));
        var after = await CountAsync(forum, null, ct);

        string Refusal(Run run, string variable) =>
            $"{run.Exit} {(run.Err.Contains(variable, StringComparison.Ordinal) ? "names " + variable : "silent")} {(run.Err.Contains("unset", StringComparison.OrdinalIgnoreCase) ? "unset" : "no-unset")} "
            + $"{(run.Err.Contains(Secret, StringComparison.Ordinal) ? "LEAKS" : "no-value")} {(run.Err.Contains(Sentence, StringComparison.Ordinal) ? "sentence" : "no-sentence")} "
            + (run.Err.Contains("not a usable", StringComparison.OrdinalIgnoreCase) ? "PARSED" : "not-parsed");

        // The controls: each verb succeeds with only its own variable.
        var signAlone = await OperatorAsync(forum, ct, ["sign-head", "--by", "ops"], logKey: Pem(logPem));
        var publishAlone = await OperatorAsync(forum, ct, ["publish-declaration-key", "--by", "ops"], declarationKey: Pem(declarationPem));

        const string Of = " unset no-value sentence not-parsed";
        Same(
            $"3 names CURIA_DECLARATION_SIGNING_KEY_PEM{Of}; 3 names CURIA_LOG_SIGNING_KEY_PEM{Of}; 3 names CURIA_LOG_SIGNING_KEY_PEM{Of}; "
            + $"invalid: 3 names CURIA_DECLARATION_SIGNING_KEY_PEM{Of}; 3 names CURIA_LOG_SIGNING_KEY_PEM{Of}; 3 names CURIA_LOG_SIGNING_KEY_PEM{Of}; 0 rows | controls 0 0",
            $"{Refusal(signWithBoth, "CURIA_DECLARATION_SIGNING_KEY_PEM")}; {Refusal(publishWithBoth, "CURIA_LOG_SIGNING_KEY_PEM")}; {Refusal(compromiseWithBoth, "CURIA_LOG_SIGNING_KEY_PEM")}; "
            + $"invalid: {Refusal(signInvalid, "CURIA_DECLARATION_SIGNING_KEY_PEM")}; {Refusal(publishInvalid, "CURIA_LOG_SIGNING_KEY_PEM")}; {Refusal(compromiseInvalid, "CURIA_LOG_SIGNING_KEY_PEM")}; "
            + $"{after - before} rows | controls {signAlone.Exit} {publishAlone.Exit}");
    }

    /// <summary>
    /// HSU53 (Amendment 7 ruling 65). The environment refusal claims only keys the environment holds. Each of the three verbs is run with only the key it refuses set and with both set, and the refusal is read
    /// against the environment this test built: the keys it says the environment holds are the one key the verb refuses and none that is not set, and it never says "holds both". The live run found
    /// <c>compromise</c> with only the log key refusing "in an environment that holds both" (trap 243). HSU12 holds the variable's name, "unset", the absence of the value and the last sentence, all of which the
    /// false sentence kept, and every one of its refusal rows sets both variables, the one environment in which that sentence is true. Red on the tip: all six rows.
    /// </summary>
    [Fact]
    public async Task R12_18_HSU53_TheEnvironmentRefusalClaimsOnlyWhatTheEnvironmentHolds()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var forum = await NewAsync<ForumFixture>();
        var logPem = LogSigningKey.GeneratePem();
        var declarationPem = DeclarationSigningKey.GeneratePem();
        await GrowAsync(forum, ct);
        var before = await CountAsync(forum, null, ct);
        string[] compromise = ["compromise", "--agent", "https://agents.example/nobody", "--kid", "k", "--from-index", "1", "--reason", "owner-reported", "--operator", "ops"];

        // The key each verb refuses in its environment: sign-head the declaration key, the other two the log key.
        var verbs = new (string Name, string[] Args, string Refused)[]
        {
            ("sign-head", ["sign-head", "--by", "ops"], "declaration"),
            ("publish-declaration-key", ["publish-declaration-key", "--by", "ops"], "log"),
            ("compromise", compromise, "log"),
        };

        var expected = new List<string>();
        var actual = new List<string>();
        foreach (var (name, args, refused) in verbs)
        {
            foreach (var both in new[] { false, true })
            {
                var logSet = refused == "log" || both;
                var declarationSet = refused == "declaration" || both;
                var run = await OperatorAsync(forum, ct, args, logKey: logSet ? Pem(logPem) : null, declarationKey: declarationSet ? Pem(declarationPem) : null);

                var held = new List<string>();
                if (logSet) held.Add("log");
                if (declarationSet) held.Add("declaration");
                var claimed = Regex.Matches(run.Err, @"holds (?:the )?(log|declaration) key", RegexOptions.None, TimeSpan.FromSeconds(5))
                    .Select(m => m.Groups[1].Value).Distinct().Order().ToList();

                var row = $"{name} {(both ? "both" : "alone")}";
                expected.Add($"{row}: 3, claims {refused}");
                actual.Add($"{row}: {run.Exit}, claims {string.Join('+', claimed)}"
                    + (claimed.Except(held).Any() ? " NOT-HELD" : string.Empty)
                    + (run.Err.Contains("holds both", StringComparison.Ordinal) ? " SAYS-BOTH" : string.Empty));
            }
        }

        var after = await CountAsync(forum, null, ct);
        Same(string.Join("; ", expected) + " | 0 rows", string.Join("; ", actual) + $" | {after - before} rows");
    }

    /// <summary>
    /// HSU13. The exemption is exactly one entry (the production gate; Amendment 2 ruling 29): <c>publish-declaration-key</c> exits 0 and appends one <c>log.declaration-key</c>; <c>compromise</c> in the
    /// same environment exits 4 with <c>lifecycle-not-enabled</c> before the key reader is called; <c>GET /v1/log/jwks</c> serves the key in <c>declaration_keys</c> with its <c>log_index</c> and <c>keys</c>
    /// does not carry it; <c>curia_lifecycle_kinds</c> is still empty. Red on the seam: <c>declaration_keys</c> is absent.
    /// </summary>
    [Fact]
    public async Task R6_62_HSU13_PublishingTheDeclarationKeyIsOutsideTheGuardAndCompromisingIsNot()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var forum = await NewAsync<GuardedForumFixture>();
        var production = EmbeddedLifecycleAppendGate.FromEmbedded("none").Match(g => g, e => throw new InvalidOperationException(e.Type));
        var declarationPem = DeclarationSigningKey.GeneratePem();
        var kid = DeclarationSigningKey.FromPem(declarationPem).Kid;
        var (agent, _, _) = await EnrolAsync(forum, "hsu13", ct);

        var declarationReads = 0;
        var before = await CountAsync(forum, null, ct);
        var published = await OperatorAsync(forum, ct, ["publish-declaration-key", "--by", "ops"], declarationKey: Pem(declarationPem), gate: production);
        var afterPublish = await CountAsync(forum, null, ct);
        var entries = await CountAsync(forum, LogEntries.DeclarationKeyType, ct);

        var compromised = await OperatorAsync(
            forum, ct,
            ["compromise", "--agent", agent.AgentId, "--kid", agent.Kid, "--from-index", "1", "--reason", "owner-reported", "--operator", "ops"],
            declarationKey: () => { declarationReads++; return declarationPem; }, gate: production);
        var afterCompromise = await CountAsync(forum, null, ct);

        var jwks = await forum.Client.GetFromJsonAsync<JsonElement>("/v1/log/jwks", ct);
        var served = jwks.TryGetProperty("declaration_keys", out var declarationKeys) ? declarationKeys : default;
        var inDeclarationKeys = served.ValueKind == JsonValueKind.Array
            ? served.EnumerateArray().Any(k => k.TryGetProperty("kid", out var k1) && k1.GetString() == kid && k.TryGetProperty("log_index", out _))
            : false;
        var inKeys = jwks.GetProperty("keys").EnumerateArray().Any(k => k.GetProperty("kid").GetString() == kid);
        var kinds = (await forum.Client.GetFromJsonAsync<JsonElement>($"/v1/jwks?agent={Uri.EscapeDataString(agent.AgentId)}", ct)).GetProperty("curia_lifecycle_kinds").GetRawText();

        Same(
            "publish exit 0, 1 row, 1 entry | compromise exit 4 lifecycle-not-enabled, key read 0 times, 0 rows | declaration_keys carries the key True, keys carries it False | {\"approved\":[],\"enabled\":[]}",
            $"publish exit {published.Exit}, {afterPublish - before} row, {entries} entry | compromise exit {compromised.Exit} {(compromised.Err.Contains("lifecycle-not-enabled", StringComparison.Ordinal) ? "lifecycle-not-enabled" : "'" + compromised.Err.Trim() + "'")}, key read {declarationReads} times, {afterCompromise - afterPublish} rows"
            + $" | declaration_keys carries the key {inDeclarationKeys}, keys carries it {inKeys} | {Regex.Replace(kinds, @"\s+", string.Empty, RegexOptions.None, TimeSpan.FromSeconds(5))}");
    }

    /// <summary>
    /// HSU14. Publishing: the log key's own PEM as the declaration key is refused <c>declaration-key-is-log-key</c> (exit 2); the right key publishes, and <c>declaration_keys</c> carries it with its
    /// <c>log_index</c> while <c>keys</c> does not; a second publish is refused <c>declaration-key-already-published</c> (exit 2); a refusal writes no event. Red on the seam: the refusals exit 0.
    /// </summary>
    [Fact]
    public async Task R6_62_HSU14_AKeyThatIsALogKeyAndASecondKeyAreRefusedAndWriteNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var forum = await NewAsync<ForumFixture>();
        var logPem = LogSigningKey.GeneratePem();
        var declarationPem = DeclarationSigningKey.GeneratePem();
        var secondPem = DeclarationSigningKey.GeneratePem();
        await GrowAsync(forum, ct);
        var signed = await OperatorAsync(forum, ct, ["sign-head", "--by", "ops"], logKey: Pem(logPem));
        string[] publish = ["publish-declaration-key", "--by", "ops"];

        var rows0 = await CountAsync(forum, null, ct);
        var asLogKey = await OperatorAsync(forum, ct, publish, declarationKey: Pem(logPem));
        var rows1 = await CountAsync(forum, null, ct);
        var first = await OperatorAsync(forum, ct, publish, declarationKey: Pem(declarationPem));
        var rows2 = await CountAsync(forum, null, ct);
        var second = await OperatorAsync(forum, ct, publish, declarationKey: Pem(secondPem));
        var rows3 = await CountAsync(forum, null, ct);

        var jwks = await forum.Client.GetFromJsonAsync<JsonElement>("/v1/log/jwks", ct);
        var kid = DeclarationSigningKey.FromPem(declarationPem).Kid;
        var declared = jwks.TryGetProperty("declaration_keys", out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().Select(k => k.GetProperty("kid").GetString()).ToArray() : [];
        var inKeys = jwks.GetProperty("keys").EnumerateArray().Any(k => k.GetProperty("kid").GetString() == kid);

        string Type(Run run, string slug) => run.Err.Contains(slug, StringComparison.Ordinal) ? slug : "'" + run.Err.Trim() + "'";
        Same(
            "signed 0 | exit 2 curia/keys/declaration-key-is-log-key, 0 rows | first exit 0, 1 row | exit 2 curia/keys/declaration-key-already-published, 0 rows | declared [kid] in keys False",
            $"signed {signed.Exit} | exit {asLogKey.Exit} {Type(asLogKey, "curia/keys/declaration-key-is-log-key")}, {rows1 - rows0} rows | first exit {first.Exit}, {rows2 - rows1} row"
            + $" | exit {second.Exit} {Type(second, "curia/keys/declaration-key-already-published")}, {rows3 - rows2} rows | declared [{string.Join(",", declared.Select(d => d == kid ? "kid" : d))}] in keys {inKeys}");
    }

    /// <summary>
    /// HSU15. <c>compromise</c> signs with the declaration key: the appended entry's header <c>kid</c> is the declaration key's, the statement <c>typ</c> is <c>curia.key-compromise/2</c>, and with the log key's
    /// PEM alone it exits 3 (HSU12). Red on the seam, where the verb reads the log key's variable.
    /// </summary>
    [Fact]
    public async Task R6_62_HSU15_CompromiseSignsWithTheDeclarationKey()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var forum = await NewAsync<ForumFixture>();
        var logPem = LogSigningKey.GeneratePem();
        var declarationPem = DeclarationSigningKey.GeneratePem();
        var declarationKid = DeclarationSigningKey.FromPem(declarationPem).Kid;
        var (agent, _, _) = await EnrolAsync(forum, "hsu15", ct);
        var boundAt = await KeyActRequests.LatestLifecycleAsync(forum.Client, agent.AgentId, ct);

        Assert.Equal(0, (await OperatorAsync(forum, ct, ["sign-head", "--by", "ops"], logKey: Pem(logPem))).Exit);
        Assert.Equal(0, (await OperatorAsync(forum, ct, ["publish-declaration-key", "--by", "ops"], declarationKey: Pem(declarationPem))).Exit);
        string[] compromise = ["compromise", "--agent", agent.AgentId, "--kid", agent.Kid, "--from-index", boundAt.ToString(CultureInfo.InvariantCulture), "--reason", "owner-reported", "--operator", "ops"];

        var withLogKeyAlone = await OperatorAsync(forum, ct, compromise, logKey: Pem(logPem));
        var declared = await OperatorAsync(forum, ct, compromise, declarationKey: Pem(declarationPem));

        string observed = "no entry";
        if (declared.Exit == 0)
        {
            await using var connection = new NpgsqlConnection(forum.ConnectionString);
            await connection.OpenAsync(ct);
            await using var select = new NpgsqlCommand("SELECT payload::text FROM events WHERE event_type = 'agent.key-compromised' ORDER BY seq DESC LIMIT 1;", connection);
            using var payload = JsonDocument.Parse((string)(await select.ExecuteScalarAsync(ct))!);
            var jws = payload.RootElement.GetProperty("declaration").GetString()!;
            var header = Encoding.UTF8.GetString(Convert.FromBase64String(Pad(jws[..jws.IndexOf('.', StringComparison.Ordinal)].Replace('-', '+').Replace('_', '/'))));
            using var headerJson = JsonDocument.Parse(header);
            var statement = payload.RootElement.GetProperty("statement").GetString()!;
            observed = $"header kid is the declaration key's {headerJson.RootElement.GetProperty("kid").GetString() == declarationKid}, typ /2 {statement.Contains("\"typ\":\"curia.key-compromise/2\"", StringComparison.Ordinal)}";
        }

        Same(
            "log key alone exit 3 | declared exit 0, header kid is the declaration key's True, typ /2 True",
            $"log key alone exit {withLogKeyAlone.Exit} | declared exit {declared.Exit}, {observed}");

        static string Pad(string base64) => base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
    }

    /// <summary>
    /// HSU16, a control on the seam: separation, the other direction. A head signed by the declaration key and appended by hand is not a head of this log: the Forum's <c>signature_valid</c> is false and
    /// the client's check of it fails, because its <c>kid</c> is in no <c>keys</c> entry. Red if <c>declaration_keys</c> ever leaked into <c>keys</c>.
    /// </summary>
    [Fact]
    public async Task R6_62_HSU16_AHeadSignedByTheDeclarationKeyIsNoHeadOfThisLog()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var forum = await NewAsync<ForumFixture>();
        var logPem = LogSigningKey.GeneratePem();
        var declarationPem = DeclarationSigningKey.GeneratePem();
        await GrowAsync(forum, ct);
        Assert.Equal(0, (await OperatorAsync(forum, ct, ["sign-head", "--by", "ops"], logKey: Pem(logPem))).Exit);
        Assert.Equal(0, (await OperatorAsync(forum, ct, ["publish-declaration-key", "--by", "ops"], declarationKey: Pem(declarationPem))).Exit);

        // A head over the log as it stands, signed by the declaration key and appended by hand.
        var log = await ReadLogAsync(forum, ct);
        var acta = ActaLog.Fold(log).Match(a => a, e => throw new InvalidOperationException(e.Type));
        using var ecdsa = System.Security.Cryptography.ECDsa.Create();
        ecdsa.ImportFromPem(declarationPem);
        var kid = DeclarationSigningKey.FromPem(declarationPem).Kid;
        var head = LogEntries.HeadDocument(acta.Root, LogLeaf.RenderServerTimestamp(ServerTimestamp.At(forum.Now)), acta.TreeSize);
        var canonical = LogEntries.HeadCanonical(head).Match(c => c, e => throw new InvalidOperationException(e.Type));
        var es256 = new Curia.Canon.Sodium.Es256Adapter();
        var jws = new Curia.Canon.Jws.DetachedJws(
            new Dictionary<string, Curia.Canon.Jws.IContentSigner>(StringComparer.Ordinal) { ["ES256"] = es256 },
            new Dictionary<string, Curia.Canon.Jws.IContentVerifier>(StringComparer.Ordinal) { ["ES256"] = es256 },
            Curia.Canon.Jws.DetachedJws.HeadTyp);
        var signature = jws.Sign(canonical, new Curia.Canon.Jws.SigningKey("ES256", kid, ecdsa.ExportECPrivateKey())).Match(s => s, e => throw new InvalidOperationException(e.Type));

        var adapters = new PostgresAdapters(forum.ConnectionString, forum.Clock, forum.PermissiveGate());
        await using (adapters.ConfigureAwait(false))
        {
            (await adapters.EventStore.AppendAsync(
                AggregateId.Create(LogEntries.HeadsAggregate).Match(a => a, e => throw new InvalidOperationException(e.Type)),
                AggregateVersion.From(acta.Heads.Length).Match(v => v, e => throw new InvalidOperationException(e.Type)),
                [new DomainEvent(
                    EventId.Create(new UlidGenerator(forum.Clock).Next().Match(u => u.ToString(), e => throw new InvalidOperationException(e.Type))).Match(i => i, e => throw new InvalidOperationException(e.Type)),
                    EventType.Create(LogEntries.HeadType).Match(t => t, e => throw new InvalidOperationException(e.Type)),
                    ActorId.Create("operator:ops").Match(a => a, e => throw new InvalidOperationException(e.Type)),
                    new JsonValue.Object(
                    [
                        new(LogEntries.HeadMember, head),
                        new(LogEntries.KidMember, new JsonValue.String(kid)),
                        new(LogEntries.SignatureMember, new JsonValue.String(signature.Compact)),
                    ]))],
                ct)).Match(a => a, e => throw new InvalidOperationException(e.Type));
        }

        var served = await forum.Client.GetFromJsonAsync<JsonElement>("/v1/log/head", ct);
        var client = new ForumClient(forum.Client, forum.Client.BaseAddress!);
        Assert.True((await client.GetLogHeadAsync(ct)).TryGetValue(out var latest, out var headRefusal), headRefusal?.Summary);
        Assert.True((await client.GetLogJwksAsync(ct)).TryGetValue(out var keys, out var keysRefusal), keysRefusal?.Summary);
        var verdict = ActaCheck.HeadSignature(latest!, keys!.Keys);

        Same(
            "signature_valid False, client Failed",
            $"signature_valid {served.GetProperty("signature_valid").GetBoolean()}, client {verdict.Outcome}");
    }

    /// <summary>
    /// HSU17, a control on the seam: compatibility. The same log served before and after a declaration key is published has a byte-identical <c>keys</c> array, so a reader that reads <c>keys</c> only (the
    /// client of before this PR) is unchanged. Red under FHSU7.
    /// </summary>
    [Fact]
    public async Task R6_62_HSU17_TheKeysArrayIsByteIdenticalWithAndWithoutADeclarationKey()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var forum = await NewAsync<ForumFixture>();
        await GrowAsync(forum, ct);
        Assert.Equal(0, (await OperatorAsync(forum, ct, ["sign-head", "--by", "ops"], logKey: Pem(LogSigningKey.GeneratePem()))).Exit);

        var before = (await forum.Client.GetFromJsonAsync<JsonElement>("/v1/log/jwks", ct)).GetProperty("keys").GetRawText();
        Assert.Equal(0, (await OperatorAsync(forum, ct, ["publish-declaration-key", "--by", "ops"], declarationKey: Pem(DeclarationSigningKey.GeneratePem()))).Exit);
        var after = (await forum.Client.GetFromJsonAsync<JsonElement>("/v1/log/jwks", ct)).GetProperty("keys").GetRawText();

        Assert.Equal(before, after);
    }

    /// <summary>
    /// HSU26. <c>GET /v1/log/jwks</c> of a Forum with no declaration key serves <c>"declaration_keys":[]</c> (present, empty), and with one serves it with <c>log_index</c>, <c>valid_from</c> and the JWK members;
    /// the client's own reader of the document reads the key set beside it. Red on the seam: the member is absent.
    /// </summary>
    [Fact]
    public async Task R6_62_HSU26_TheKeySetServesDeclarationKeysPresentAndEmptyOrWithTheKey()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var forum = await NewAsync<ForumFixture>();
        await GrowAsync(forum, ct);
        Assert.Equal(0, (await OperatorAsync(forum, ct, ["sign-head", "--by", "ops"], logKey: Pem(LogSigningKey.GeneratePem()))).Exit);
        var client = new ForumClient(forum.Client, forum.Client.BaseAddress!);

        string Shape(JsonElement jwks) => jwks.TryGetProperty("declaration_keys", out var list) && list.ValueKind == JsonValueKind.Array
            ? "[" + string.Join(";", list.EnumerateArray().Select(k => string.Join(",", k.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)))) + "]"
            : "absent";

        var empty = Shape(await forum.Client.GetFromJsonAsync<JsonElement>("/v1/log/jwks", ct));
        var readEmpty = await KeyCountAsync(client, ct);

        Assert.Equal(0, (await OperatorAsync(forum, ct, ["publish-declaration-key", "--by", "ops"], declarationKey: Pem(DeclarationSigningKey.GeneratePem()))).Exit);
        var present = Shape(await forum.Client.GetFromJsonAsync<JsonElement>("/v1/log/jwks", ct));
        var readPresent = await KeyCountAsync(client, ct);

        Same(
            "[] read 1 key | [alg,crv,kid,kty,log_index,use,valid_from,x,y] read 1 key",
            $"{empty} read {readEmpty} key | {present} read {readPresent} key");
    }

    private static async Task<string> KeyCountAsync(ForumClient client, CancellationToken ct) =>
        (await client.GetLogJwksAsync(ct)).TryGetValue(out var keys, out var refusal) ? keys!.Keys.Length.ToString(CultureInfo.InvariantCulture) : refusal!.Summary;

    // ---------------------------------------------------------------------------------------------------------------------------------- the words

    /// <summary>The instant as the words render it: UTC to the millisecond.</summary>
    private static string Instant(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static async Task<PostVerification> VerifyAsync(ForumFixture forum, string postId, DateTimeOffset checkBegan, HeadStore heads, CancellationToken ct)
    {
        var verifier = new PostVerifier(new ForumClient(forum.Client, forum.Client.BaseAddress!), heads, new TimerManualTimeProvider(checkBegan), TimeSpan.FromSeconds(30));
        var result = await verifier.VerifyAsync(postId, MarkingMode.None, ct);
        Assert.True(result.TryGetValue(out var verification, out var refusal), refusal?.Summary);
        return verification!;
    }

    private static string FoldWhitespace(string text) => Regex.Replace(text, @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(5));

    /// <summary>
    /// HSU28, end to end. A head signed at T, a post appended 90 s later, <c>curia verify</c> run at T plus 317 s by a client on the same clock: the inclusion line holds <c>That head was signed at &lt;T&gt;, 317 s before
    /// this check began.</c>; the closing holds the same clause after <c>(tree size &lt;n&gt;, </c>; the instant is the <b>head's</b> timestamp and not the post's <c>server_ts</c>. Red on the seam: the closing ends
    /// <c>(tree size 4), so its inclusion</c>.
    /// </summary>
    [Fact]
    public async Task R11_94_HSU28_AVerificationPastTheHeadStatesWhenTheHeadWasSigned()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var forum = await NewAsync<ForumFixture>();
        var home = Directory.CreateTempSubdirectory("curia-hsu28-").FullName;
        try
        {
            var pem = LogSigningKey.GeneratePem();
            var (first, dpopA, tokenA) = await EnrolAsync(forum, "hsu28a", ct);
            await AskAsync(forum, first, dpopA, tokenA, "board-" + Suffix(), Words(), Words(), ct);
            var signedAt = forum.Now;
            Assert.Equal(0, (await OperatorAsync(forum, ct, ["sign-head", "--by", "ops"], logKey: Pem(pem))).Exit);

            forum.Clock.Advance(TimeSpan.FromSeconds(90));
            var (second, dpopB, tokenB) = await EnrolAsync(forum, "hsu28b", ct);
            var (postId, _) = await AskAsync(forum, second, dpopB, tokenB, "board-" + Suffix(), Words(), Words(), ct);
            var postServedAt = forum.Now;

            var verification = await VerifyAsync(forum, postId, signedAt.AddSeconds(317), new HeadStore(home), ct);
            var inclusion = FoldWhitespace(verification.Inclusion.Detail);
            var rendered = FoldWhitespace(verification.Render());

            var clause = $"signed at {Instant(signedAt)}, 317 s before this check began";
            var problems = new List<string>();
            if (!inclusion.Contains($"That head was {clause}.", StringComparison.Ordinal)) problems.Add($"the inclusion line lacks \"That head was {clause}.\"");
            if (!rendered.Contains($"(tree size {verification.PastHeadTreeSize?.ToString(CultureInfo.InvariantCulture)}, {clause}), so its inclusion could not be checked", StringComparison.Ordinal)) problems.Add($"the closing lacks \"(tree size {verification.PastHeadTreeSize}, {clause}), so its inclusion\"");
            if (rendered.Contains($"signed at {Instant(postServedAt)}", StringComparison.Ordinal)) problems.Add("the words state the post's server_ts and not the head's timestamp");
            Assert.True(problems.Count == 0, string.Join("; ", problems) + " :: " + rendered);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>
    /// HSU31. Stopping the timer: heads signed at T, no further signing, the clock advanced three hours, two posts: both verifications say <c>10800 s before this check began</c>; a fresh signing then makes both
    /// <c>VERIFIED</c> and the words disappear (the other side of the words). Red on the seam: the first side.
    /// </summary>
    [Fact]
    public async Task R11_94_HSU31_AStoppedSignerShowsAsAnAgeThatGrows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var forum = await NewAsync<ForumFixture>();
        var home = Directory.CreateTempSubdirectory("curia-hsu31-").FullName;
        try
        {
            var pem = LogSigningKey.GeneratePem();
            var (first, dpopA, tokenA) = await EnrolAsync(forum, "hsu31a", ct);
            await AskAsync(forum, first, dpopA, tokenA, "board-" + Suffix(), Words(), Words(), ct);
            var signedAt = forum.Now;
            Assert.Equal(0, (await OperatorAsync(forum, ct, ["sign-head", "--by", "ops"], logKey: Pem(pem))).Exit);

            forum.Clock.Advance(TimeSpan.FromHours(3));
            var posts = new List<string>();
            for (var i = 0; i < 2; i++)
            {
                var (agent, dpop, token) = await EnrolAsync(forum, "hsu31x", ct);
                posts.Add((await AskAsync(forum, agent, dpop, token, "board-" + Suffix(), Words(), Words(), ct)).PostId);
            }

            var heads = new HeadStore(home);
            var stopped = new List<string>();
            foreach (var post in posts)
            {
                var verification = await VerifyAsync(forum, post, signedAt.AddHours(3), heads, ct);
                stopped.Add(FoldWhitespace(verification.Render()).Contains($"signed at {Instant(signedAt)}, 10800 s before this check began", StringComparison.Ordinal) ? "10800 s" : "no age");
            }

            Assert.Equal(0, (await OperatorAsync(forum, ct, ["sign-head", "--by", "ops"], logKey: Pem(pem))).Exit);
            var after = new List<string>();
            foreach (var post in posts)
            {
                var verification = await VerifyAsync(forum, post, forum.Now, heads, ct);
                after.Add($"{verification.Overall}{(FoldWhitespace(verification.Render()).Contains("signed at", StringComparison.Ordinal) ? " with words" : " no words")}");
            }

            Same(
                "10800 s, 10800 s | Verified no words, Verified no words",
                $"{string.Join(", ", stopped)} | {string.Join(", ", after)}");
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    // ------------------------------------------------------------------------------------------------------------------------------ the store fault

    /// <summary>
    /// HSU35. A store fault is a worded refusal (Amendment 1 ruling 23): <c>sign-head --if-grown</c> and <c>attest-owner</c> under a role that holds <c>SELECT</c> on <c>events</c> and no <c>INSERT</c> exit 5, with
    /// <c>error: curia/store/unavailable</c> and the cause in the form of <c>PostgresStoreFaults.Cause</c>, no stack line and none of the driver's message, and the events count unchanged; the same call under
    /// the app role exits 0; a refused connection exits 5; a domain refusal is still exit 2. Red on the seam: the call throws <c>PostgresException</c> where exit 5 was expected.
    /// </summary>
    [Fact]
    public async Task R12_18_HSU35_AStoreFaultIsAWordedRefusalAndNotAStackTrace()
    {
        var ct = TestContext.Current.CancellationToken;
        var role = "curia_e2e_ro_" + Suffix();
        var password = Guid.NewGuid().ToString("N");
        string adminDatabaseConnection;
        try
        {
            await using (var forum = await NewAsync<ForumFixture>())
            {
                adminDatabaseConnection = new NpgsqlConnectionStringBuilder(forum.ConnectionString) { Database = "postgres" }.ConnectionString;
                var database = new NpgsqlConnectionStringBuilder(forum.ConnectionString).Database!;
                await using (var admin = new NpgsqlConnection(forum.ConnectionString))
                {
                    await admin.OpenAsync(ct);
                    foreach (var sql in new[]
                    {
                        $"CREATE ROLE \"{role}\" LOGIN PASSWORD '{password}' NOSUPERUSER NOCREATEDB NOCREATEROLE",
                        $"GRANT CONNECT ON DATABASE \"{database}\" TO \"{role}\"",
                        $"GRANT USAGE ON SCHEMA public TO \"{role}\"",
                        $"GRANT SELECT ON events TO \"{role}\"",
                    })
                    {
                        await using var command = new NpgsqlCommand(sql, admin);
                        await command.ExecuteNonQueryAsync(ct);
                    }
                }

                var logPem = LogSigningKey.GeneratePem();
                var (agent, _, _) = await EnrolAsync(forum, "hsu35", ct);
                await GrowAsync(forum, ct);
                var readOnly = new NpgsqlConnectionStringBuilder(forum.ConnectionString) { Username = role, Password = password }.ConnectionString;
                var before = await CountAsync(forum, null, ct);

                var sign = await OperatorAsync(forum, ct, ["sign-head", "--by", "ops", "--if-grown"], logKey: Pem(logPem), connection: readOnly);
                var attest = await OperatorAsync(forum, ct, ["attest-owner", "--agent", agent.AgentId, "--owner", "owner-1", "--by", "ops"], connection: readOnly);
                var after = await CountAsync(forum, null, ct);

                var control = await OperatorAsync(forum, ct, ["sign-head", "--by", "ops", "--if-grown"], logKey: Pem(logPem), connection: AppRoleConnection(forum));

                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var closedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                var refusedConnection = new NpgsqlConnectionStringBuilder(forum.ConnectionString) { Host = "127.0.0.1", Port = closedPort, Timeout = 3, Pooling = false }.ConnectionString;
                var refused = await OperatorAsync(forum, ct, ["sign-head", "--by", "ops", "--if-grown"], logKey: Pem(logPem), connection: refusedConnection);

                var domain = await OperatorAsync(forum, ct, ["moderate", "--post", "01NOSUCHPOST000000000000000", "--category", "spam", "--effect", "withhold", "--reason", "Reviewed.", "--by", "ops"]);

                string Fault(Run run) => run.Exit == 5
                    ? $"5 {(run.Err.StartsWith("error: curia/store/unavailable", StringComparison.Ordinal) ? "worded" : "unworded")} {(run.Err.Contains("PostgresException sqlstate=42501", StringComparison.Ordinal) ? "cause" : "no-cause")} {(run.Err.Contains("\n   at ", StringComparison.Ordinal) || run.Err.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ? "LEAKS" : "clean")}"
                    : $"{run.Exit} {run.Err.Split('\n')[0].Trim()}";

                Same(
                    "sign: 5 worded cause clean | attest: 5 worded cause clean | 0 rows | control exit 0 | refused connection: 5 | domain refusal: 2",
                    $"sign: {Fault(sign)} | attest: {Fault(attest)} | {after - before} rows | control exit {control.Exit} | refused connection: {refused.Exit} | domain refusal: {domain.Exit}");
            }
        }
        finally
        {
            await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CURIA_TEST_POSTGRES") is { Length: > 0 } env ? env : new NpgsqlConnectionStringBuilder { Host = "localhost", Port = 5432, Username = Environment.UserName, Database = "postgres" }.ConnectionString).ConnectionString);
            await admin.OpenAsync(ct);
            await using var drop = new NpgsqlCommand($"DROP ROLE IF EXISTS \"{role}\"", admin);
            await drop.ExecuteNonQueryAsync(ct);
        }
    }

    // ---------------------------------------------------------------------------------------------------- D121: the Forum honours a row only if it verifies (errata G68)

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>A compromise row the way the forgery probe wrote it: a statement under <see cref="KeyStatements.CompromiseStatementTyp"/>, a declaration whose signature is 64 zero bytes.</summary>
    private static JsonObject ForgedDeclaration(string agent, string kid, long prev, long fromIndex)
    {
        var statement = Encoding.UTF8.GetString(CanonicalJson.CanonicalizeWithNfc(new JsonValue.Object(
        [
            new("agent_id", new JsonValue.String(agent)),
            new("from_index", new JsonValue.Number(fromIndex)),
            new("kids", new JsonValue.Array([new JsonValue.String(kid)])),
            new("prev", new JsonValue.Number(prev)),
            new("reason", new JsonValue.String("owner-reported")),
            new("typ", new JsonValue.String(KeyStatements.CompromiseStatementTyp)),
        ])).Match(c => c.Span.ToArray(), e => throw new InvalidOperationException(e.Type)));
        var header = Base64Url(Encoding.UTF8.GetBytes($"{{\"alg\":\"ES256\",\"kid\":\"{kid}\",\"typ\":\"curia-key-compromise+jws\"}}"));
        return new JsonObject
        {
            ["agent_id"] = agent,
            ["declaration"] = header + ".." + Base64Url(new byte[64]),
            ["from_index"] = fromIndex,
            ["kids"] = new JsonArray(kid),
            ["prev"] = prev,
            ["reason"] = "owner-reported",
            ["statement"] = statement,
        };
    }

    /// <summary>The log index of the last event of <paramref name="aggregate"/> in <paramref name="log"/>.</summary>
    private static long LastIndexOf(IReadOnlyList<AppendedEvent> log, string aggregate)
    {
        for (var i = log.Count - 1; i >= 0; i--)
            if (string.Equals(log[i].AggregateId.Value, aggregate, StringComparison.Ordinal)) return i;

        throw new InvalidOperationException($"{aggregate} has no event in the log");
    }

    /// <summary>
    /// HSU36 (D121's Forum half, as errata G68 closes it; Amendment 1 ruling 20, Amendment 5 ruling 69 item 6). Two agents A and B, a post each and a head covering both. A row of type
    /// <c>agent.key-compromised</c> is appended by SQL under the app role and through no use case, naming B's real kid, with a zero signature. The Forum skips it: B's post is served
    /// <c>key_status</c> <c>active</c> with no <c>disputed_by_log_index</c>, is found by search (<c>excluded_disputed</c> 0), and B's token request is <c>200</c>; A is unchanged. The expectation
    /// replaces the one that pinned the Forum honouring the row by its shape; what <c>curia verify</c> says of B's post before and after a head covers the row is the client's and is Task 5's
    /// to pin, and D121, G67's non-property (c) and the beta notes are edited by Task 8.
    /// </summary>
    [Fact]
    public async Task R6_57_HSU36_TheForumSkipsAForgedDeclaration()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var forum = await NewAsync<ForumFixture>();
        var board = "board-" + Suffix();
        var term = "zforge" + Suffix();
        var (a, dpopA, tokenA) = await EnrolAsync(forum, "hsu36a", ct);
        var (b, dpopB, tokenB) = await EnrolAsync(forum, "hsu36b", ct);
        var postA = await AskAsync(forum, a, dpopA, tokenA, board, $"{Words()} {term}", $"{Words()} {term}", ct);
        var postB = await AskAsync(forum, b, dpopB, tokenB, board, $"{Words()} {term}", $"{Words()} {term}", ct);
        var pem = LogSigningKey.GeneratePem();
        Assert.Equal(0, (await OperatorAsync(forum, ct, ["sign-head", "--by", "ops"], logKey: Pem(pem))).Exit);

        var log = await ReadLogAsync(forum, ct);
        var bindingOfB = log.Select((e, i) => (e, i)).First(x => x.e.AggregateId.Value == b.AgentId && x.e.Event.Type.Value == AgentStandingProjector.KeyBoundType).i;
        await InsertRowAsync(forum, LifecycleAppendGuard.KeyCompromisedType, b.AgentId, ForgedDeclaration(b.AgentId, b.Kid, LastIndexOf(log, b.AgentId), bindingOfB), ct);

        var servedB = (await forum.Client.GetFromJsonAsync<JsonElement>($"/v1/posts/{postB.PostId}", ct)).GetProperty("provenance");
        var servedA = (await forum.Client.GetFromJsonAsync<JsonElement>($"/v1/posts/{postA.PostId}", ct)).GetProperty("provenance");
        using var search = JsonDocument.Parse(await forum.Client.GetStringAsync(new Uri($"/v1/search?q={term}&board={board}", UriKind.Relative), ct));
        var found = search.RootElement.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("post").GetProperty("post_id").GetString()).ToArray();
        var (tokenStatusB, _) = await dpopB.RequestTokenAsync(forum.Client, KeyActRequests.TokenEndpoint, forum.Now, b.AgentId, ct);
        var (tokenStatusA, _) = await dpopA.RequestTokenAsync(forum.Client, KeyActRequests.TokenEndpoint, forum.Now, a.AgentId, ct);

        var problems = new List<string>();
        void Hold(bool holds, string what) { if (!holds) problems.Add(what); }
        Hold(servedB.GetProperty("key_status").GetString() == "active", "B's post is not served key_status active");
        Hold(!servedB.TryGetProperty("disputed_by_log_index", out _), "B's post is served a disputed_by_log_index");
        Hold(found.Contains(postB.PostId) && found.Contains(postA.PostId), "search does not find both posts");
        Hold(search.RootElement.GetProperty("floor").GetProperty("excluded_disputed").GetInt32() == 0, "search does not state excluded_disputed 0");
        Hold(tokenStatusB == HttpStatusCode.OK, $"B's token request is not 200 ({(int)tokenStatusB})");
        Hold(servedA.GetProperty("key_status").GetString() == "active" && !servedA.TryGetProperty("disputed_by_log_index", out _), "A's mark changed");
        Hold(tokenStatusA == HttpStatusCode.OK, $"A's token request is not 200 ({(int)tokenStatusA})");

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    // ------------------------------------------------------------------------------------------------------------------------------------- the stop

    private static JsonObject ForgedRetirement(string agent, string kid) => new()
    {
        ["agent_id"] = agent,
        ["authorization"] = "this-is-not-a-signature-qq7",
        ["kid"] = kid,
        ["prev"] = 1,
        ["statement"] = "{\"agent_id\":\"" + agent + "\",\"kid\":\"" + kid + "\",\"prev\":1,\"typ\":\"curia.key-retirement/1\"}",
    };

    private static JsonObject ForgedBinding(string agent, string kid) => new()
    {
        ["agent_id"] = agent,
        ["authorization"] = "this-is-not-a-signature-qq7",
        ["authorized_by"] = kid,
        ["jwk"] = new JsonObject { ["kty"] = "EC", ["crv"] = "P-256", ["x"] = "AAAA", ["y"] = "AAAA" },
        ["kid"] = "forged-" + kid,
        ["possession"] = "this-is-not-a-signature-qq7",
        ["prev"] = 1,
        ["statement"] = "{}",
    };

    /// <summary>
    /// HSU37. The stop (Amendment 2 ruling 28): after a head, a lifecycle row appended by SQL under the app role and through no use case (a retirement whose <c>authorization</c> is no signature, a compromise with 64 zero
    /// bytes, a statement-bearing key binding with non-signature proofs) makes <c>sign-head --if-grown</c> exit 6 with nothing on stdout, <c>error: curia/head/withheld</c> on stderr naming <c>withheld  log_index=&lt;i&gt;  type=&lt;t&gt;</c>
    /// and no payload member, and nothing appended; the plain <c>sign-head</c> then exits 0 and prints <c>covers  log_index=&lt;i&gt;  type=&lt;t&gt;</c>; a post and <c>--if-grown</c> then signs (the stop is not permanent); twenty-one
    /// rows are listed as 20 and <c>and 1 more</c>. Red on the seam: <c>Expected: 6  Actual: 0</c>.
    /// </summary>
    [Fact]
    public async Task R6_63_HSU37_ALifecycleRowInTheTailWithholdsTheHeadFromTheTimer()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var forum = await NewAsync<ForumFixture>();
        var pem = LogSigningKey.GeneratePem();
        var (v, _, _) = await EnrolAsync(forum, "hsu37v", ct);
        var (w, dpopW, tokenW) = await EnrolAsync(forum, "hsu37w", ct);
        await AskAsync(forum, w, dpopW, tokenW, "board-" + Suffix(), Words(), Words(), ct);
        Assert.Equal(0, (await OperatorAsync(forum, ct, ["sign-head", "--by", "ops"], logKey: Pem(pem))).Exit);

        var rows = new (string Name, string Type, Func<JsonObject> Payload)[]
        {
            ("retirement", LifecycleAppendGuard.KeyRetiredType, () => ForgedRetirement(v.AgentId, v.Kid)),
            ("compromise", LifecycleAppendGuard.KeyCompromisedType, () => ForgedDeclaration(v.AgentId, v.Kid, 1, 1)),
            ("binding", AgentStandingProjector.KeyBoundType, () => ForgedBinding(v.AgentId, v.Kid)),
        };

        var observed = new List<string>();
        foreach (var (name, type, payload) in rows)
        {
            var index = (await ReadLogAsync(forum, ct)).Count;
            await InsertRowAsync(forum, type, v.AgentId, payload(), ct);
            var before = await CountAsync(forum, null, ct);

            var stopped = await OperatorAsync(forum, ct, ["sign-head", "--by", "ops", "--if-grown"], logKey: Pem(pem));
            var afterStop = await CountAsync(forum, null, ct);
            var words = $"{stopped.Exit} {(stopped.Out.Length == 0 ? "no-stdout" : "STDOUT")} {(stopped.Err.StartsWith("error: curia/head/withheld", StringComparison.Ordinal) ? "worded" : "unworded")} "
                + $"{(stopped.Err.Contains($"withheld  log_index={index}  type={type}", StringComparison.Ordinal) ? "names-row" : "no-row")} {afterStop - before} rows "
                + $"{(stopped.Err.Contains("this-is-not-a-signature-qq7", StringComparison.Ordinal) ? "LEAKS" : "no-payload")}";

            var covered = await OperatorAsync(forum, ct, ["sign-head", "--by", "ops"], logKey: Pem(pem));
            var afterCover = await CountAsync(forum, null, ct);
            var covers = $"{covered.Exit} {(covered.Out.Contains($"covers  log_index={index}  type={type}", StringComparison.Ordinal) ? "covers" : "no-covers")} {afterCover - afterStop} row";

            await GrowAsync(forum, ct);
            var resumed = await OperatorAsync(forum, ct, ["sign-head", "--by", "ops", "--if-grown"], logKey: Pem(pem));
            observed.Add($"{name}: {words} | plain {covers} | resumed {resumed.Exit}");
        }

        // Twenty-one rows past the head: the listing holds 20 and says there is one more.
        for (var i = 0; i < 21; i++) await InsertRowAsync(forum, LifecycleAppendGuard.KeyRetiredType, v.AgentId, ForgedRetirement(v.AgentId, v.Kid), ct);
        var many = await OperatorAsync(forum, ct, ["sign-head", "--by", "ops", "--if-grown"], logKey: Pem(pem));
        var listed = Regex.Count(many.Err, "withheld  log_index=", RegexOptions.None, TimeSpan.FromSeconds(5));

        var one = "6 no-stdout worded names-row 0 rows no-payload | plain 0 covers 1 row | resumed 0";
        Same(
            string.Join("\n", $"retirement: {one}", $"compromise: {one}", $"binding: {one}", "21 rows: exit 6, 20 listed, and 1 more"),
            string.Join("\n", observed.Append($"21 rows: exit {many.Exit}, {listed} listed, {(many.Err.Contains("and 1 more", StringComparison.Ordinal) ? "and 1 more" : "no-more")}")));
    }

    /// <summary>
    /// HSU38, the stop is not a wall (the same file): a log of 50 enrolment pairs, posts, a <c>log.declaration-key</c> and a <c>log.key</c> is signed by <c>--if-grown</c> (exit 0) and an immediate second run says
    /// <c>unchanged</c>; a lifecycle-shaped row <b>before</b> the latest head does not stop a later run. Red on FHSU31 (an enrolment pair counted as a row: the timer would stop at every new agent) and on FHSU32 (the
    /// tail taken from the start of the log: the stop is permanent after one forged row); on the seam red only where the flag is ignored (the immediate second run signs).
    /// </summary>
    [Fact]
    public async Task R6_63_HSU38_FiftyEnrolmentsAreNotARowAndACoveredRowDoesNotStopALaterRun()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var forum = await NewAsync<ForumFixture>();
        var pem = LogSigningKey.GeneratePem();
        var agents = new List<(ForumAgent Agent, DpopClient Dpop, string Token)>();
        for (var i = 0; i < 50; i++) agents.Add(await EnrolAsync(forum, "hsu38", ct));
        for (var i = 0; i < 10; i++) await GrowAsync(forum, ct);
        Assert.Equal(0, (await OperatorAsync(forum, ct, ["publish-declaration-key", "--by", "ops"], declarationKey: Pem(DeclarationSigningKey.GeneratePem()))).Exit);

        var first = await OperatorAsync(forum, ct, ["sign-head", "--by", "ops", "--if-grown"], logKey: Pem(pem));
        var second = await OperatorAsync(forum, ct, ["sign-head", "--by", "ops", "--if-grown"], logKey: Pem(pem));

        // A forged row, then a plain sign-head that covers it, then growth: the row is before the latest head and does not stop the timer.
        var victim = agents[0].Agent;
        await InsertRowAsync(forum, LifecycleAppendGuard.KeyRetiredType, victim.AgentId, ForgedRetirement(victim.AgentId, victim.Kid), ct);
        var covering = await OperatorAsync(forum, ct, ["sign-head", "--by", "ops"], logKey: Pem(pem));
        await GrowAsync(forum, ct);
        var later = await OperatorAsync(forum, ct, ["sign-head", "--by", "ops", "--if-grown"], logKey: Pem(pem));

        Same(
            "first 0 signed | second 0 unchanged | covering 0 | later 0 signed",
            $"first {first.Exit} {(first.Out.Contains("signed head", StringComparison.Ordinal) ? "signed" : "no head")} | second {second.Exit} {(second.Out.StartsWith("unchanged", StringComparison.Ordinal) ? "unchanged" : "signed")}"
            + $" | covering {covering.Exit} | later {later.Exit} {(later.Out.Contains("signed head", StringComparison.Ordinal) ? "signed" : "no head")}");
    }

    private enum ForgedHead { GarbageSignature, AttackersOwnKey, AttackersKeyUnderTheToolsKid }

    /// <summary>
    /// What a holder of the Forum's credential can append and the signer must not take for its own: a head at its own index, claiming the log's true root at that size,
    /// differing from the signer's in nothing the log can show but the signature. The second and third forms first append a <c>log.key</c> (an unsigned entry) that
    /// publishes the attacker's key, under the attacker's own kid or under the signer's, and sign the head with it.
    /// </summary>
    private static async Task ForgeHeadAsync(ForumFixture forum, ForgedHead form, string toolPem, CancellationToken ct)
    {
        static T Require<T>(Result<T> result) => result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} ({e.Detail})"));

        var acta = Require(ActaLog.Fold(await ReadLogAsync(forum, ct)));
        using var tool = LogSigningKey.FromPem(toolPem);
        var attackerPem = LogSigningKey.GeneratePem();
        using var attacker = LogSigningKey.FromPem(attackerPem);
        using var attackerEcdsa = System.Security.Cryptography.ECDsa.Create();
        attackerEcdsa.ImportFromPem(attackerPem);
        var kid = form == ForgedHead.AttackersOwnKey ? attacker.Kid : tool.Kid;

        var adapters = new PostgresAdapters(forum.ConnectionString, forum.Clock, forum.PermissiveGate());
        await using (adapters.ConfigureAwait(false))
        {
            var ids = new UlidGenerator(forum.Clock);
            if (form != ForgedHead.GarbageSignature)
            {
                var jwk = new JsonValue.Object([.. attacker.PublicJwk().Members.Select(m => m.Key == "kid" ? new(m.Key, new JsonValue.String(kid)) : m)]);
                await AppendEntryAsync(adapters, ids, LogEntries.KeysAggregate, acta.Keys.Length, LogEntries.KeyType, new JsonValue.Object(
                [
                    new(LogEntries.KidMember, new JsonValue.String(kid)),
                    new(LogEntries.JwkMember, jwk),
                    new(LogEntries.ValidFromMember, new JsonValue.String(LogLeaf.RenderServerTimestamp(ServerTimestamp.At(forum.Now)))),
                ]), ct);
                acta = Require(ActaLog.Fold(await ReadLogAsync(forum, ct)));
            }

            var head = LogEntries.HeadDocument(acta.Root, LogLeaf.RenderServerTimestamp(ServerTimestamp.At(forum.Now)), acta.TreeSize);
            var signature = "garbage-not-a-signature";
            if (form != ForgedHead.GarbageSignature)
            {
                var es256 = new Curia.Canon.Sodium.Es256Adapter();
                var jws = new Curia.Canon.Jws.DetachedJws(
                    new Dictionary<string, Curia.Canon.Jws.IContentSigner>(StringComparer.Ordinal) { ["ES256"] = es256 },
                    new Dictionary<string, Curia.Canon.Jws.IContentVerifier>(StringComparer.Ordinal) { ["ES256"] = es256 },
                    Curia.Canon.Jws.DetachedJws.HeadTyp);
                signature = Require(jws.Sign(Require(LogEntries.HeadCanonical(head)), new Curia.Canon.Jws.SigningKey("ES256", kid, attackerEcdsa.ExportECPrivateKey()))).Compact;
            }

            await AppendEntryAsync(adapters, ids, LogEntries.HeadsAggregate, acta.Heads.Length, LogEntries.HeadType, new JsonValue.Object(
            [
                new(LogEntries.HeadMember, head),
                new(LogEntries.KidMember, new JsonValue.String(kid)),
                new(LogEntries.SignatureMember, new JsonValue.String(signature)),
            ]), ct);
        }
    }

    /// <summary>HSU16's append through the event store adapter, as a helper: one entry, one aggregate, the version the caller names.</summary>
    private static async Task AppendEntryAsync(PostgresAdapters adapters, UlidGenerator ids, string aggregate, int version, string type, JsonValue.Object payload, CancellationToken ct)
    {
        static T Require<T>(Result<T> result) => result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} ({e.Detail})"));
        Require(await adapters.EventStore.AppendAsync(
            Require(AggregateId.Create(aggregate)),
            Require(AggregateVersion.From(version)),
            [new DomainEvent(Require(EventId.Create(Require(ids.Next()).ToString())), Require(EventType.Create(type)), Require(ActorId.Create("operator:ops")), payload)],
            ct));
    }

    /// <summary>
    /// HSU40 (Amendment 3 ruling 35; errata G67, R6.63). A head the signer cannot verify as its own moves neither the stop nor the growth test. A forged lifecycle row, then a
    /// forged head over it (three forms: a garbage signature under the signer's kid; a signature under a key the attacker published under the attacker's own kid; a signature
    /// under a key the attacker published under the signer's kid), then one ordinary entry: <c>sign-head --if-grown</c> exits 6, writes nothing, and names the row. The control
    /// in each iteration is the person's resume (the plain run covers the row and its head is the new anchor), then growth, then <c>--if-grown</c>, which signs. Two more rows: a
    /// forged head after a genuine head and one ordinary entry suppresses nothing (the run signs); a forged head with nothing after the signer's own head leaves <c>unchanged</c>
    /// naming the signer's head. Red on the tip (69f001bc, the shape-keyed tail): each form reads <c>0 STDOUT no-row 1 rows</c>, the quiet-log row reads <c>0 unchanged</c>, the
    /// no-growth row names the forgery's index.
    /// </summary>
    [Fact]
    public async Task R6_63_HSU40_AHeadTheSignerCannotVerifyAsItsOwnMovesNeitherTheStopNorTheGrowthTest()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var forum = await NewAsync<ForumFixture>();
        var pem = LogSigningKey.GeneratePem();
        var (v, _, _) = await EnrolAsync(forum, "hsu40v", ct);
        var (w, dpopW, tokenW) = await EnrolAsync(forum, "hsu40w", ct);
        await AskAsync(forum, w, dpopW, tokenW, "board-" + Suffix(), Words(), Words(), ct);
        Assert.Equal(0, (await OperatorAsync(forum, ct, ["sign-head", "--by", "ops"], logKey: Pem(pem))).Exit); // a head the tool signed: the anchor

        var observed = new List<string>();
        foreach (var form in new[] { ForgedHead.GarbageSignature, ForgedHead.AttackersOwnKey, ForgedHead.AttackersKeyUnderTheToolsKid })
        {
            var rowIndex = (await ReadLogAsync(forum, ct)).Count;
            await InsertRowAsync(forum, LifecycleAppendGuard.KeyRetiredType, v.AgentId, ForgedRetirement(v.AgentId, v.Kid), ct);
            await ForgeHeadAsync(forum, form, pem, ct);
            await GrowAsync(forum, ct);

            var before = await CountAsync(forum, null, ct);
            var run = await OperatorAsync(forum, ct, ["sign-head", "--by", "ops", "--if-grown"], logKey: Pem(pem));
            var written = await CountAsync(forum, null, ct) - before;
            var names = run.Err.Contains($"withheld  log_index={rowIndex}  type={LifecycleAppendGuard.KeyRetiredType}", StringComparison.Ordinal) ? "names-row" : "no-row";

            Assert.Equal(0, (await OperatorAsync(forum, ct, ["sign-head", "--by", "ops"], logKey: Pem(pem))).Exit); // the person's resume: covers the row, and its head is the new anchor
            await GrowAsync(forum, ct);
            var resumed = await OperatorAsync(forum, ct, ["sign-head", "--by", "ops", "--if-grown"], logKey: Pem(pem));
            observed.Add($"{form}: {run.Exit} {(run.Out.Length == 0 ? "no-stdout" : "STDOUT")} {names} {written} rows | resumed {resumed.Exit} {(resumed.Out.Contains("signed head", StringComparison.Ordinal) ? "signed" : "no head")}");
        }

        // A forged head after the signer's own head and one ordinary entry suppresses nothing.
        await GrowAsync(forum, ct);
        await ForgeHeadAsync(forum, ForgedHead.GarbageSignature, pem, ct);
        var grown = await OperatorAsync(forum, ct, ["sign-head", "--by", "ops", "--if-grown"], logKey: Pem(pem));
        var signedAt = Regex.Match(grown.Out, @"^log_index\s+(\d+)$", RegexOptions.Multiline, TimeSpan.FromSeconds(5)).Groups[1].Value;

        // With nothing after the signer's own head, a forged head is not growth and the line names the signer's head, not the forgery's.
        await ForgeHeadAsync(forum, ForgedHead.GarbageSignature, pem, ct);
        var quiet = await OperatorAsync(forum, ct, ["sign-head", "--by", "ops", "--if-grown"], logKey: Pem(pem));

        var one = "6 no-stdout names-row 0 rows | resumed 0 signed";
        Same(
            string.Join("\n", $"GarbageSignature: {one}", $"AttackersOwnKey: {one}", $"AttackersKeyUnderTheToolsKid: {one}", "quiet log, forged head: 0 signed", "no growth, forged head: 0 unchanged names-the-signers-head"),
            string.Join("\n", observed
                .Append($"quiet log, forged head: {grown.Exit} {(grown.Out.Contains("signed head", StringComparison.Ordinal) ? "signed" : "unchanged")}")
                .Append($"no growth, forged head: {quiet.Exit} {(quiet.Out.StartsWith("unchanged", StringComparison.Ordinal) ? "unchanged" : "signed")} "
                    + (signedAt.Length > 0 && quiet.Out.TrimEnd().EndsWith($"latest_head_log_index={signedAt}", StringComparison.Ordinal) ? "names-the-signers-head" : "names-another-head"))));
    }

    /// <summary>
    /// HSU40, the first run: with no head the tool signed, the anchor is none and the tail is the whole log. A log of two enrolments, a forged retirement, a forged head over it
    /// (garbage signature) and one ordinary entry: <c>--if-grown</c> exits 6 and appends nothing. Red on the tip: the forged head is the latest head, the tail holds the
    /// ordinary entry only, the run signs (exit 0).
    /// </summary>
    [Fact]
    public async Task R6_63_HSU40_WithNoHeadTheSignerSignedTheTailIsTheWholeLog()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var forum = await NewAsync<ForumFixture>();
        var pem = LogSigningKey.GeneratePem();
        var (v, _, _) = await EnrolAsync(forum, "hsu40a", ct);
        await EnrolAsync(forum, "hsu40b", ct);
        await InsertRowAsync(forum, LifecycleAppendGuard.KeyRetiredType, v.AgentId, ForgedRetirement(v.AgentId, v.Kid), ct);
        await ForgeHeadAsync(forum, ForgedHead.GarbageSignature, pem, ct);
        await GrowAsync(forum, ct);

        var before = await CountAsync(forum, null, ct);
        var run = await OperatorAsync(forum, ct, ["sign-head", "--by", "ops", "--if-grown"], logKey: Pem(pem));
        Same("6 0 rows", $"{run.Exit} {await CountAsync(forum, null, ct) - before} rows");
    }

    /// <summary>A clock that runs one action at its first reading and is the fixture's clock otherwise: the way a probe puts a write between two points of one run.</summary>
    private sealed class FirstReadingClock(TimeProvider inner, Action onFirstReading) : TimeProvider
    {
        private int _reads;

        public override DateTimeOffset GetUtcNow()
        {
            if (Interlocked.Increment(ref _reads) == 1) onFirstReading();
            return inner.GetUtcNow();
        }
    }

    /// <summary>
    /// HSU42 (Amendment 3 ruling 40). The stop is re-evaluated on the fold the head is built from: a grown log with no log key and a clock whose first reading appends a forged
    /// <c>agent.key-retired</c> row by SQL (the first reading after the first fold is the key's <c>valid_from</c>, between the two folds). <c>sign-head --if-grown</c> exits 6, the events
    /// table holds one <c>log.key</c> and no <c>log.head</c>, and stderr names the row. The probe asserts a <c>log.key</c> exists, so that a reading before the first fold (which stops on
    /// the first fold's own tail and publishes nothing) reads as the probe not reaching its window and not as green. Red on the tip: exit 0, one <c>log.head</c> covering the row.
    /// </summary>
    [Fact]
    [SuppressMessage("Reliability", "CA2025:Ensure tasks using 'IDisposable' instances complete before the instances are disposed", Justification = "The insert is awaited synchronously inside the clock's first reading, which runs inside the awaited operator run, before the fixture is disposed.")]
    public async Task R6_63_HSU42_TheStopIsEvaluatedOnTheFoldTheHeadIsBuiltFrom()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var forum = await NewAsync<ForumFixture>();
        var pem = LogSigningKey.GeneratePem();
        var (v, _, _) = await EnrolAsync(forum, "hsu42v", ct);
        var (w, dpopW, tokenW) = await EnrolAsync(forum, "hsu42w", ct);
        await AskAsync(forum, w, dpopW, tokenW, "board-" + Suffix(), Words(), Words(), ct);

        var rowIndex = (await ReadLogAsync(forum, ct)).Count;
        var clock = new FirstReadingClock(forum.Clock, () => InsertRowAsync(forum, LifecycleAppendGuard.KeyRetiredType, v.AgentId, ForgedRetirement(v.AgentId, v.Kid), ct).GetAwaiter().GetResult());
        var run = await OperatorAsync(forum, ct, ["sign-head", "--by", "ops", "--if-grown"], logKey: Pem(pem), clock: clock);

        var keys = await CountAsync(forum, LogEntries.KeyType, ct);
        var heads = await CountAsync(forum, LogEntries.HeadType, ct);
        var forged = await CountAsync(forum, LifecycleAppendGuard.KeyRetiredType, ct);
        var names = run.Err.Contains($"withheld  log_index={rowIndex}  type={LifecycleAppendGuard.KeyRetiredType}", StringComparison.Ordinal) ? "names-row" : "no-row";

        Same("1 forged row reached; exit 6, 1 log.key, 0 log.head, names-row", $"{forged} forged row reached; exit {run.Exit}, {keys} log.key, {heads} log.head, {names}");
    }
}
