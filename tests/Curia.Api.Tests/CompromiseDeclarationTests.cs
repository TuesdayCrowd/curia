using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Curia.Application.Credentials;
using Curia.Client;
using Curia.Application.Ports;
using Curia.Domain.Primitives;
using Curia.Domain.Serving;
using Curia.Infrastructure.Lifecycle;
using Curia.OperatorTool;
using Npgsql;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// What <c>curia-operator compromise</c> does to the running Forum (R6.26 rev., R6.57, R5.22; errata G21,
/// spec sections 4.5, 4.8): a post under a compromised key is refused 403, a token naming one is refused
/// 401, and a log key the log has not published signs nothing. The declaration is seeded through the
/// operator tool itself, behind the permissive gate the fixture composes.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class CompromiseDeclarationTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private static string Unique(string stem) => $"https://agents.example/{stem}-{Guid.NewGuid().ToString("N")[..8]}";

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    internal static async Task<long> EventCountAsync(string connectionString, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM events;", connection);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    private async Task<(int Exit, string Out, string Err)> OperatorAsync(string pem, CancellationToken ct, params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await OperatorCommands.RunAsync(
            args, forum.ConnectionString, forum.Clock, forum.PermissiveGate(), OperatorCommands.LoadLogSigner, stdout, stderr, ct,
            readLogSigningKeyPem: () => pem);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private Task<(int Exit, string Out, string Err)> SignHeadAsync(string pem, CancellationToken ct) =>
        OperatorAsync(pem, ct, "sign-head", "--by", "ops");

    private Task<(int Exit, string Out, string Err)> CompromiseAsync(string pem, string agent, string kid, long fromIndex, CancellationToken ct) =>
        OperatorAsync(pem, ct, "compromise", "--agent", agent, "--kid", kid, "--from-index", fromIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--reason", "owner-reported", "--operator", "ops");

    [Fact]
    public async Task R6_57_NewPostsUnderACompromisedKeyAreRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var first = ForumAgent.Create(Unique("compromised"), "first-" + Suffix());
        var (firstDpop, firstToken) = await first.AuthenticateAsync(client, KeyActRequests.TokenEndpoint, forum.Now, ct);

        // A second key, rotated in under the first, is the one declared compromised; the first stays live and carries the token.
        var second = ForumAgent.Create(first.AgentId, "second-" + Suffix());
        var prev = await KeyActRequests.LatestLifecycleAsync(client, first.AgentId, ct);
        using (var rotated = await firstDpop.PostAsync(
            client, KeyActRequests.RotateUrl(first.AgentId), firstToken,
            KeyActRequests.RotationBody(first, new KeyActRequests.KeyPair(second.Kid, second.AssertionKey), prev),
            forum.Now, ct, contentType: "application/json"))
        {
            Assert.Equal(HttpStatusCode.Created, rotated.StatusCode);
        }

        var boundAt = await KeyActRequests.LatestLifecycleAsync(client, first.AgentId, ct);
        var pem = LogSigningKey.GeneratePem();
        var published = await SignHeadAsync(pem, ct);
        var declared = await CompromiseAsync(pem, first.AgentId, second.Kid, boundAt, ct);

        var board = "board-" + Suffix();
        using var refused = await firstDpop.PostAsync(
            client, "http://localhost/v1/posts", firstToken, second.SignQuestion(board, "Signed by a compromised key?", "Refused", forum.Now), forum.Now, ct);
        var problem = await KeyActRequests.ProblemOfAsync(refused, ct);

        using var control = await firstDpop.PostAsync(
            client, "http://localhost/v1/posts", firstToken, first.SignQuestion(board, "Signed by the live key?", "Accepted", forum.Now), forum.Now, ct);

        Assert.Equal(
            "0; 0; 403 curia/keys/compromised; 201",
            $"{published.Exit}; {declared.Exit}; {(int)refused.StatusCode} {problem.Type}; {(int)control.StatusCode}");
    }

    [Fact]
    public async Task R5_22_ACompromisedKeysTokenIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var postId = await TokenKeyLivenessTests.PostedQuestionAsync(forum, client, ct);
        var agent = ForumAgent.Create(Unique("compromised-token"), "only-" + Suffix());
        var (dpop, token) = await agent.AuthenticateAsync(client, KeyActRequests.TokenEndpoint, forum.Now, ct);
        var boundAt = await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct);

        // Control: before the declaration the token works.
        using var before = await dpop.PostAsync(
            client, $"http://localhost/v1/posts/{postId}/flags", token, TokenKeyLivenessTests.FlagBody(), forum.Now, ct, contentType: "application/json");

        var pem = LogSigningKey.GeneratePem();
        var published = await SignHeadAsync(pem, ct);
        var declared = await CompromiseAsync(pem, agent.AgentId, agent.Kid, boundAt, ct);

        using var after = await dpop.PostAsync(
            client, $"http://localhost/v1/posts/{postId}/accept", token, TokenKeyLivenessTests.AcceptBody, forum.Now, ct, contentType: "application/json");
        var problem = await KeyActRequests.ProblemOfAsync(after, ct);

        Assert.Equal(
            "0; 0; True; 401 curia/authn/token-key-not-live",
            $"{published.Exit}; {declared.Exit}; {before.IsSuccessStatusCode}; {(int)after.StatusCode} {problem.Type}");
    }

    [Fact]
    public async Task R6_26_TheOperatorRefusesALogKeyTheLogHasNotPublished()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var agent = ForumAgent.Create(Unique("unpublished"), "only-" + Suffix());
        await agent.AuthenticateAsync(client, KeyActRequests.TokenEndpoint, forum.Now, ct);
        var boundAt = await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct);
        var pem = LogSigningKey.GeneratePem();
        var rows = await EventCountAsync(forum.ConnectionString, ct);

        var refused = await CompromiseAsync(pem, agent.AgentId, agent.Kid, boundAt, ct);
        var afterRefusal = await EventCountAsync(forum.ConnectionString, ct);

        // Control: the same call once sign-head has published the key appends.
        var published = await SignHeadAsync(pem, ct);
        var beforeControl = await EventCountAsync(forum.ConnectionString, ct);
        var control = await CompromiseAsync(pem, agent.AgentId, agent.Kid, boundAt, ct);
        var afterControl = await EventCountAsync(forum.ConnectionString, ct);

        Assert.Equal(
            $"{ExitCode.Refused}, curia/keys/log-key-not-published: True; {rows}; {ExitCode.Ok}; {ExitCode.Ok}, {beforeControl + 1}",
            $"{refused.Exit}, curia/keys/log-key-not-published: {refused.Err.Contains("curia/keys/log-key-not-published", StringComparison.Ordinal)}; {afterRefusal}; {published.Exit}; {control.Exit}, {afterControl}");
    }

    [Fact]
    public async Task R6_57_TheKeySetPublishesACompromisedKeyAsCompromised()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var agent = ForumAgent.Create(Unique("published-status"), "only-" + Suffix());
        await agent.AuthenticateAsync(client, KeyActRequests.TokenEndpoint, forum.Now, ct);
        var boundAt = await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct);
        var pem = LogSigningKey.GeneratePem();
        await SignHeadAsync(pem, ct);

        var declared = await CompromiseAsync(pem, agent.AgentId, agent.Kid, boundAt, ct);
        using var keySet = await KeyActRequests.KeySetAsync(client, agent.AgentId, ct);
        var status = keySet.RootElement.GetProperty("keys").EnumerateArray().Single().GetProperty("curia_status").GetString();

        Assert.Equal($"{ExitCode.Ok}; compromised", $"{declared.Exit}; {status}");
    }

    /// <summary>
    /// R6.57: the key set names the declaration for every kid it lists, a cascaded key as well as the first, so a reader can
    /// fetch it for a post under either (<c>compromise-post-under-cascaded-key</c>'s shape: k1 compromised from its binding,
    /// k2 rotated in under it). Both keys are served <c>compromised</c>, with the declaration's log index as
    /// <c>curia_compromised_log_index</c> and the index the compromise is stated from as <c>curia_disputed_from</c>. Red when
    /// the key set names the declaration for the declaration's first kid alone.
    /// </summary>
    [Fact]
    public async Task R6_57_ACascadedKeysKeySetEntryCarriesTheDeclarationIndex()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var first = ForumAgent.Create(Unique("cascade"), "first-" + Suffix());
        var (firstDpop, firstToken) = await first.AuthenticateAsync(client, KeyActRequests.TokenEndpoint, forum.Now, ct);
        var boundAt = await KeyActRequests.LatestLifecycleAsync(client, first.AgentId, ct);

        var second = ForumAgent.Create(first.AgentId, "second-" + Suffix());
        using (var rotated = await firstDpop.PostAsync(
            client, KeyActRequests.RotateUrl(first.AgentId), firstToken,
            KeyActRequests.RotationBody(first, new KeyActRequests.KeyPair(second.Kid, second.AssertionKey), boundAt),
            forum.Now, ct, contentType: "application/json"))
        {
            Assert.Equal(HttpStatusCode.Created, rotated.StatusCode);
        }

        var pem = LogSigningKey.GeneratePem();
        await SignHeadAsync(pem, ct);
        var declared = await CompromiseAsync(pem, first.AgentId, first.Kid, boundAt, ct);
        var declarationAt = await KeyActRequests.LatestLifecycleAsync(client, first.AgentId, ct);

        using var keySet = await KeyActRequests.KeySetAsync(client, first.AgentId, ct);
        var keys = keySet.RootElement.GetProperty("keys").EnumerateArray().ToDictionary(k => k.GetProperty("kid").GetString()!, k => k, StringComparer.Ordinal);
        string Of(string kid) =>
            $"{keys[kid].GetProperty("curia_status").GetString()} at {keys[kid].GetProperty("curia_compromised_log_index").GetInt64()} from {keys[kid].GetProperty("curia_disputed_from").GetInt64()}";

        Assert.Equal(
            $"{ExitCode.Ok}; compromised at {declarationAt} from {boundAt}; compromised at {declarationAt} from {boundAt}",
            $"{declared.Exit}; {Of(first.Kid)}; {Of(second.Kid)}");
    }

    /// <summary>
    /// R6.57, against a real Forum: a post accepted before the operator declared its key compromised from the key's binding
    /// is reported disputed by the reference reader, through the key set's <c>curia_compromised_log_index</c> and the
    /// declaration fetched, held to the chain and verified under the log's key set. The signature, the inclusion and the key's
    /// binding all hold; the lifecycle line is disputed; the summary is disputed and never verified.
    /// </summary>
    [Fact]
    public async Task R6_57_APostAcceptedBeforeADeclarationIsReportedDisputedByTheReader()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var agent = ForumAgent.Create(Unique("disputed-post"), "only-" + Suffix());
        var (dpop, token) = await agent.AuthenticateAsync(client, KeyActRequests.TokenEndpoint, forum.Now, ct);
        var boundAt = await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct);

        string postId;
        using (var posted = await dpop.PostAsync(
            client, "http://localhost/v1/posts", token, agent.SignQuestion("board-" + Suffix(), "Signed before the declaration?", "Yes", forum.Now), forum.Now, ct))
        {
            Assert.Equal(HttpStatusCode.Created, posted.StatusCode);
            postId = (await posted.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("post_id").GetString()!;
        }

        var pem = LogSigningKey.GeneratePem();
        Assert.Equal(ExitCode.Ok, (await SignHeadAsync(pem, ct)).Exit);
        Assert.Equal(ExitCode.Ok, (await CompromiseAsync(pem, agent.AgentId, agent.Kid, boundAt, ct)).Exit);

        // A head that covers the declaration: the reader fetches it only under a head it holds.
        Assert.Equal(ExitCode.Ok, (await SignHeadAsync(pem, ct)).Exit);

        var home = Directory.CreateTempSubdirectory("curia-disputed-heads-").FullName;
        try
        {
            var verifier = new PostVerifier(new ForumClient(client, client.BaseAddress!), new HeadStore(home));
            var verified = await verifier.VerifyAsync(postId, MarkingMode.None, ct);
            Assert.True(verified.TryGetValue(out var result, out var refusal), refusal?.Summary);

            Assert.Equal(
                $"{CheckOutcome.Verified}; {CheckOutcome.Verified}; {CheckOutcome.Verified}; {CheckOutcome.Disputed}; {CheckOutcome.Disputed}",
                $"{result!.Signature.Outcome}; {result.Inclusion.Outcome}; {result.KeyBinding.Outcome}; {result.Lifecycle.Outcome}; {result.Overall}");
            Assert.Contains("\nDISPUTED. ", result.Render(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>
    /// D49 / R6.57 / R6.28, the Forum's half: <c>POST /v1/posts/batch</c> serves a post a declaration disputes as
    /// <c>current</c>, marked -- <c>key_status</c> <c>compromised</c> and <c>disputed_by_log_index</c> on its provenance.
    /// Disputed is a mark on a served post, never a withholding. This pins the wire the client's <c>recheck</c> reads;
    /// the client's half is <c>RecheckDisputedTests</c>.
    /// </summary>
    [Fact]
    public async Task D49_ABatchServesADisputedPostAsCurrentMarkedWithTheDeclarationsIndex()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var agent = ForumAgent.Create(Unique("disputed-batch"), "only-" + Suffix());
        var (dpop, token) = await agent.AuthenticateAsync(client, KeyActRequests.TokenEndpoint, forum.Now, ct);
        var boundAt = await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct);

        string digest;
        using (var posted = await dpop.PostAsync(
            client, "http://localhost/v1/posts", token, agent.SignQuestion("board-" + Suffix(), "Batched after the declaration?", "Yes", forum.Now), forum.Now, ct))
        {
            Assert.Equal(HttpStatusCode.Created, posted.StatusCode);
            digest = (await posted.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("digest").GetString()!;
        }

        var pem = LogSigningKey.GeneratePem();
        Assert.Equal(ExitCode.Ok, (await SignHeadAsync(pem, ct)).Exit);
        Assert.Equal(ExitCode.Ok, (await CompromiseAsync(pem, agent.AgentId, agent.Kid, boundAt, ct)).Exit);
        var declarationAt = await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct);

        using var response = await client.PostAsJsonAsync("/v1/posts/batch", new { digests = new[] { digest } }, ct);
        var item = (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("items")[0];
        var provenance = item.GetProperty("post").GetProperty("provenance");

        Assert.Equal(
            $"current; compromised; {declarationAt}",
            $"{item.GetProperty("state").GetString()}; {provenance.GetProperty("key_status").GetString()}; {provenance.GetProperty("disputed_by_log_index").GetInt64()}");
    }

    /// <summary>
    /// Task 2.5's dry run, through the test composition (never the production binary): a post by k1, then a rotation that
    /// binds k2, then the operator's declaration of k1 from its binding. Both readers render the post disputed -- the
    /// reference client (<see cref="PostVerifier"/>) and the independent <c>curia-testis log author</c> (exit 4) -- with the
    /// rotation between the post and the declaration. CP-D: the database is the one this run's fixture created (its name
    /// carries the generated <c>curia_e2e_</c> prefix and the fixture's permissive gate accepts only that name), asserted
    /// before the first append. There is no production declaration until after the owner's lift.
    /// </summary>
    [Fact]
    public async Task R6_57_BothReadersRenderAPostDisputedWithARotationBetweenItAndTheDeclaration()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;

        // CP-D: the name came from this run's create step. PermissiveGate() throws for any other database.
        Assert.StartsWith("curia_e2e_", new NpgsqlConnectionStringBuilder(forum.ConnectionString).Database, StringComparison.Ordinal);
        _ = forum.PermissiveGate();

        var first = ForumAgent.Create(Unique("dry-run"), "first-" + Suffix());
        var (dpop, token) = await first.AuthenticateAsync(client, KeyActRequests.TokenEndpoint, forum.Now, ct);
        var boundAt = await KeyActRequests.LatestLifecycleAsync(client, first.AgentId, ct);

        string postId;
        using (var posted = await dpop.PostAsync(
            client, "http://localhost/v1/posts", token, first.SignQuestion("board-" + Suffix(), "Signed before the rotation?", "Yes", forum.Now), forum.Now, ct))
        {
            Assert.Equal(HttpStatusCode.Created, posted.StatusCode);
            postId = (await posted.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("post_id").GetString()!;
        }

        var second = ForumAgent.Create(first.AgentId, "second-" + Suffix());
        using (var rotated = await dpop.PostAsync(
            client, KeyActRequests.RotateUrl(first.AgentId), token,
            KeyActRequests.RotationBody(first, new KeyActRequests.KeyPair(second.Kid, second.AssertionKey), boundAt),
            forum.Now, ct, contentType: "application/json"))
        {
            Assert.Equal(HttpStatusCode.Created, rotated.StatusCode);
        }

        var pem = LogSigningKey.GeneratePem();
        Assert.Equal(ExitCode.Ok, (await SignHeadAsync(pem, ct)).Exit);
        Assert.Equal(ExitCode.Ok, (await CompromiseAsync(pem, first.AgentId, first.Kid, boundAt, ct)).Exit);
        Assert.Equal(ExitCode.Ok, (await SignHeadAsync(pem, ct)).Exit);

        var home = Directory.CreateTempSubdirectory("curia-dry-run-heads-").FullName;
        var dir = Directory.CreateTempSubdirectory("curia-dry-run-").FullName;
        try
        {
            var verifier = new PostVerifier(new ForumClient(client, client.BaseAddress!), new HeadStore(home));
            var verified = await verifier.VerifyAsync(postId, MarkingMode.None, ct);
            Assert.True(verified.TryGetValue(out var result, out var refusal), refusal?.Summary);
            Assert.Equal(
                $"{CheckOutcome.Verified}; {CheckOutcome.Disputed}; {CheckOutcome.Disputed}",
                $"{result!.Signature.Outcome}; {result.Lifecycle.Outcome}; {result.Overall}");

            async Task<string> SaveAsync(string url, string name)
            {
                using var response = await client.GetAsync(new Uri(url, UriKind.Relative), ct);
                Assert.True(response.IsSuccessStatusCode, $"{url}: {response.StatusCode}");
                var path = Path.Combine(dir, name);
                await File.WriteAllBytesAsync(path, await response.Content.ReadAsByteArrayAsync(ct), ct);
                return path;
            }

            var postIndex = (await client.GetFromJsonAsync<JsonElement>($"/v1/posts/{postId}", ct)).GetProperty("log_index").GetInt64();
            var treeSize = (await client.GetFromJsonAsync<JsonElement>("/v1/log/head", ct)).GetProperty("head").GetProperty("tree_size").GetInt64();
            using var keySet = await KeyActRequests.KeySetAsync(client, first.AgentId, ct);
            var keyIndex = keySet.RootElement.GetProperty("keys").EnumerateArray()
                .Single(k => k.GetProperty("kid").GetString() == first.Kid).GetProperty("curia_log_index").GetInt64();

            // Every lifecycle entry the key set lists below the head, the rotation and the declaration included.
            var args = new System.Text.StringBuilder(
                $"log author --entry \"{await SaveAsync($"/v1/log/entries/{postIndex}", "entry.json")}\"" +
                $" --proof \"{await SaveAsync($"/v1/log/proof/{postIndex}", "proof.json")}\"" +
                $" --key-entry \"{await SaveAsync($"/v1/log/entries/{keyIndex}", "key-entry.json")}\"" +
                $" --key-proof \"{await SaveAsync($"/v1/log/proof/{keyIndex}", "key-proof.json")}\"" +
                $" --head \"{await SaveAsync("/v1/log/head", "head.json")}\"" +
                $" --log-jwks \"{await SaveAsync("/v1/log/jwks", "log-jwks.json")}\"");
            foreach (var index in KeyActRequests.LifecycleOf(keySet).Where(i => i < treeSize).Order())
            {
                args.Append(System.Globalization.CultureInfo.InvariantCulture, $" --lifecycle-entry \"{await SaveAsync($"/v1/log/entries/{index}", $"lc-{index}-entry.json")}\"");
                args.Append(System.Globalization.CultureInfo.InvariantCulture, $" --lifecycle-proof \"{await SaveAsync($"/v1/log/proof/{index}?tree_size={treeSize}", $"lc-{index}-proof.json")}\"");
            }

            var (code, stdout, stderr) = TestisBinary.Run(TestisBinary.Locate(), args.ToString());
            Assert.True(code == 4, $"exit {code}\n{stdout}\n{stderr}");
            Assert.Contains("disputed", stdout + stderr, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
            Directory.Delete(dir, recursive: true);
        }
    }
}

/// <summary>
/// R4.38 (add.), GF2 and its companions: against the build as <c>Main</c> composes it (the embedded gate over
/// the empty approval record), <c>curia-operator compromise</c> refuses with exit 4 before it reads the log
/// key or signs anything, and says which setting would enable it.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class CompromiseDeclarationGuardTests(GuardedForumFixture forum) : IClassFixture<GuardedForumFixture>
{
    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>The embedded gate as <c>Main</c> composes it: the shipped value (none) over the embedded, empty approval record.</summary>
    private static ILifecycleAppendGate EmbeddedGate() =>
        EmbeddedLifecycleAppendGate.FromEmbedded("none").Match(g => (ILifecycleAppendGate)g, e => throw new InvalidOperationException(e.Type));

    private sealed class Probes
    {
        public int KeyReads { get; private set; }

        public int SignerBuilds { get; private set; }

        public string? Read()
        {
            KeyReads++;
            return LogSigningKey.GeneratePem();
        }

        public Result<ILifecycleStatementSigner> Build(string pem)
        {
            SignerBuilds++;
            throw new InvalidOperationException("the guard must refuse before a signer is built");
        }
    }

    private static string[] Compromise(string agent, string kid) =>
        ["compromise", "--agent", agent, "--kid", kid, "--from-index", "1", "--reason", "owner-reported", "--operator", "ops"];

    private async Task<(int Exit, string Out, string Err)> RunGuardedAsync(Probes probes, string[] args, CancellationToken ct)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await OperatorCommands.RunAsync(
            args, forum.ConnectionString, forum.Clock, EmbeddedGate(), probes.Build, stdout, stderr, ct, readLogSigningKeyPem: probes.Read);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task R4_38_TheOperatorVerbRefusesBeforeItReadsTheLogKey()
    {
        var ct = TestContext.Current.CancellationToken;
        var probes = new Probes();
        var rows = await CompromiseDeclarationTests.EventCountAsync(forum.ConnectionString, ct);

        var refused = await RunGuardedAsync(probes, Compromise("https://agents.example/guarded-" + Suffix(), "k-" + Suffix()), ct);

        Assert.Equal(
            $"{ExitCode.LifecycleNotEnabled}; 0; 0; {rows}",
            $"{refused.Exit}; {probes.KeyReads}; {probes.SignerBuilds}; {await CompromiseDeclarationTests.EventCountAsync(forum.ConnectionString, ct)}");
    }

    [Fact]
    public async Task R4_38_TheOperatorVerbDeclaresOnceTheGateIsPermissive()
    {
        // The positive control for the fact above: same verb, permissive gate, the real signing seam.
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var agent = ForumAgent.Create("https://agents.example/control-" + Suffix(), "only-" + Suffix());
        await agent.AuthenticateAsync(client, KeyActRequests.TokenEndpoint, forum.Now, ct);
        var boundAt = await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct);
        var pem = LogSigningKey.GeneratePem();

        async Task<int> RunAsync(string[] args)
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            return await OperatorCommands.RunAsync(
                args, forum.ConnectionString, forum.Clock, forum.PermissiveGate(), OperatorCommands.LoadLogSigner, stdout, stderr, ct,
                readLogSigningKeyPem: () => pem);
        }

        var published = await RunAsync(["sign-head", "--by", "ops"]);
        var declared = await RunAsync(["compromise", "--agent", agent.AgentId, "--kid", agent.Kid, "--from-index",
            boundAt.ToString(System.Globalization.CultureInfo.InvariantCulture), "--reason", "owner-reported", "--operator", "ops"]);

        Assert.Equal($"{ExitCode.Ok}; {ExitCode.Ok}", $"{published}; {declared}");
    }

    [Fact]
    public async Task R4_38_TheOperatorRefusalNamesTheSetting()
    {
        var ct = TestContext.Current.CancellationToken;
        var probes = new Probes();
        var rows = await CompromiseDeclarationTests.EventCountAsync(forum.ConnectionString, ct);

        var refused = await RunGuardedAsync(probes, Compromise("https://agents.example/guarded-" + Suffix(), "k-" + Suffix()), ct);

        Assert.Equal(
            $"{ExitCode.LifecycleNotEnabled}; True; True; {rows}",
            $"{refused.Exit}; {refused.Err.Contains("Curia__Lifecycle__Enabled", StringComparison.Ordinal)}; "
                + $"{refused.Err.Contains("curia/keys/lifecycle-not-enabled", StringComparison.Ordinal)}; {await CompromiseDeclarationTests.EventCountAsync(forum.ConnectionString, ct)}");
    }

    [Theory]
    [InlineData("compromise", "--help")]
    [InlineData("help", "compromise")]
    public async Task R12_11_TheCompromiseHelpStatesTheFromIndexRule(string first, string second)
    {
        var ct = TestContext.Current.CancellationToken;
        var probes = new Probes();

        var help = await RunGuardedAsync(probes, [first, second], ct);

        Assert.Equal(
            $"{ExitCode.Ok}; True; True; True; 0",
            $"{help.Exit}; {help.Out.Contains("--from-index", StringComparison.Ordinal)}; {help.Out.Contains("FOREIGN", StringComparison.Ordinal)}; "
                + $"{help.Out.Contains("no later than", StringComparison.Ordinal)}; {probes.KeyReads}");
    }
}
