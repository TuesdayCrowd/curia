using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.Domain.Primitives;
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
