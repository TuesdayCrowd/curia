using Curia.Api.Issuer;
using Curia.Application.Credentials;
using Curia.Application.Moderation;
using Curia.Application.Ports;
using Curia.Application.Retrieval;
using Curia.Application.Projections;
using Curia.Canon;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Credentials;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;
using Curia.Infrastructure.Migrations;
using Curia.Tests.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// A settable clock, so a test can advance eight days without waiting eight days. Table 11's tier
/// criteria are elapsed-time conditions, and a test that could not move time could only ever
/// exercise T0.
/// </summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// The real Forum, hosted in process: the same <see cref="Program.Build"/> the deployed host runs,
/// with three things supplied -- the clock (so tier promotion is testable), the connection string
/// (so each run gets its own database), and the issuer's signing key (which the host requires and
/// deliberately does not generate for itself; see <see cref="IssuerSigningKey"/>).
///
/// <para><b>Nothing else is substituted.</b> The pipeline, the PDP, the screener and the Postgres
/// event store are the production ones. A fixture that swapped the store for an in-memory fake
/// would be testing a different system: R11.6's append-only guarantee is a database grant, and the
/// whole claim under test is that a real Forum behaves this way.</para>
///
/// <para>Fails loudly when no Postgres is reachable rather than skipping, for the reason
/// <c>Curia.Infrastructure.Tests</c> already records: a green suite that quietly ran nothing is
/// the exact failure R11.9 exists to prevent.</para>
/// </summary>
public class ForumFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string EnvVarName = "CURIA_TEST_POSTGRES";

    private readonly string _database = "curia_e2e_" + Guid.NewGuid().ToString("N")[..12];
    private readonly string _role = "curia_e2e_role_" + Guid.NewGuid().ToString("N")[..12];
    private readonly string _password = Guid.NewGuid().ToString("N");

    private string _connectionString = string.Empty;

    /// <summary>
    /// The issuer's signing key for this fixture's whole life, as a PEM. Public so a test that
    /// restarts the host can stand the second one up on the same key and check that what the
    /// first minted still verifies.
    /// </summary>
    internal string IssuerSigningKeyPem { get; } = IssuerSigningKey.GeneratePem();

    internal ManualTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero));

    internal DateTimeOffset Now => Clock.GetUtcNow();

    internal virtual HttpClient Client => CreateClient();

    private string? _reservedOrigin;
    private int _reservedPort;

    /// <summary>
    /// D41 (errata G82): the one name this fixture's Forum is configured with (<c>Curia:PublicOrigin</c>) and the one its clients dial: <c>http://localhost</c>, which is what the in-process test
    /// server addresses and what the suite's literals already say. A Kestrel fixture that signs over its own address reserves a port first (<see cref="UseReservedKestrelPort"/>) and its origin
    /// is that address; a fixture whose clients force a <c>Host</c> overrides this. <b>A fixture whose clients dial one name and whose host is configured with another is the defect the stage
    /// closes</b>: ORG15 holds the two equal for every fixture.
    /// </summary>
    internal virtual string PublicOrigin => _reservedOrigin ?? "http://localhost";

    /// <summary>
    /// D41 (ruling 8, amendment 1 #3): the Forum on a real socket at a port taken from the system, closed, and then bound by <c>UseKestrel(port)</c>, with <see cref="PublicOrigin"/> built from it. A
    /// bind failure fails by name (<see cref="CreateHost"/>); there is no retry, because a <c>WebApplicationFactory</c> cannot be rebuilt with another origin after its configuration is read.
    /// </summary>
    protected void UseReservedKestrelPort(Func<int, string>? originOf = null)
    {
        using var probe = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        probe.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        _reservedPort = ((System.Net.IPEndPoint)probe.LocalEndPoint!).Port;
        _reservedOrigin = originOf is null ? $"http://127.0.0.1:{_reservedPort}" : originOf(_reservedPort);
        UseKestrel(_reservedPort);
    }

    /// <summary>A reserved port taken before the bind fails the fixture by name and never by a hang.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Any start failure of a reserved-port host is re-thrown naming the port.")]
    protected override Microsoft.Extensions.Hosting.IHost CreateHost(Microsoft.Extensions.Hosting.IHostBuilder builder)
    {
        try
        {
            return base.CreateHost(builder);
        }
        catch (Exception e) when (_reservedPort != 0)
        {
            throw new InvalidOperationException($"port {_reservedPort} was reserved and taken before the bind; re-run ({e.GetType().Name}: {e.Message})", e);
        }
    }

    /// <summary>How this fixture's host is told its name: <c>Curia:PublicOrigin</c> set to <see cref="PublicOrigin"/>. A fixture that probes the start (ORG1 to ORG3) overrides it to say something else, or nothing.</summary>
    protected virtual void ConfigureOrigin(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseSetting("Curia:PublicOrigin", PublicOrigin);
    }

    /// <summary>Every client this fixture hands out is recorded against its clock, so that <see cref="ForumAgent.EnrollAsync(HttpClient, CancellationToken)"/> can make a proof's <c>iat</c> at the Forum's time (R4.46), and against the origin the host was configured with, so that a proof's <c>aud</c> is the Forum's name and never the address the client happens to dial.</summary>
    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        ForumAgent.RecordClock(client, Clock);
        ForumAgent.RecordOrigin(client, PublicOrigin);
    }

    /// <summary>
    /// R4.38 (add.): whether this fixture's host composes the permissive lifecycle gate. By default it
    /// does, so the lifecycle-shaped appends existing tests make by hand stay green; a fixture that
    /// overrides this to false registers nothing lifecycle-related, and the host runs
    /// <c>Program.cs</c>'s own composition (spec section 4.13).
    /// </summary>
    protected virtual bool ComposesPermissiveLifecycleGate => true;

    /// <summary>
    /// R4.38 (add.): the permissive gate for this fixture's database, which tests that drive
    /// <c>curia-operator</c> in process hand it.
    /// </summary>
    internal ILifecycleAppendGate PermissiveGate() => PermissiveGateFor(_connectionString);

    /// <summary>
    /// R4.38 (add.): the permissive gate, handed out only for the database this fixture created in this
    /// run (<c>curia_e2e_</c> plus a suffix generated at construction), checked by name before anything
    /// connects; any other database is refused.
    /// </summary>
    internal ILifecycleAppendGate PermissiveGateFor(string connectionString)
    {
        var database = new NpgsqlConnectionStringBuilder(connectionString).Database;
        if (!string.Equals(database, _database, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "the permissive lifecycle gate is only for the database this run created (curia_e2e_...); " +
                $"refusing '{database}'");
        }

        return new PermissiveLifecycleAppendGate();
    }

    /// <summary>The provisioned database, so a test can drive the operator tool against the same log the host serves from.</summary>
    internal string ConnectionString => _connectionString;

    /// <summary>
    /// The connection string the host itself connects with. Today's is the administrator's (<see cref="ConnectionString"/>), so the Api suite's hosts run as the
    /// database owner and a <c>REVOKE</c> from the application role changes nothing for them (the D95 PR's Task 0, ruling 13); a fixture that must exercise R11.6's
    /// grants overrides it (<c>RoleForumFixture</c>). Nothing else reads it.
    /// </summary>
    protected virtual string HostConnectionString => _connectionString;

    /// <summary>The application role this fixture's migrations created, for a fixture whose host connects as it.</summary>
    internal string RoleName => _role;

    /// <summary>The role's password, set explicitly by <c>SchemaMigrations.RenderAll</c>.</summary>
    internal string RolePassword => _password;

    /// <summary>
    /// How many events of <paramref name="eventType"/> the log holds on <paramref name="aggregateId"/>, read
    /// from the table itself and not through the host's event store, so a count is not the code under test
    /// reporting on its own appends.
    /// </summary>
    internal async Task<long> CountEventsAsync(string eventType, string aggregateId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var count = new NpgsqlCommand(
            "SELECT count(*) FROM events WHERE event_type = @type AND aggregate_id = @aggregate;", connection);
        count.Parameters.AddWithValue("type", eventType);
        count.Parameters.AddWithValue("aggregate", aggregateId);
        return (long)(await count.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// Withholds a post through R10.59's writer — the same <see cref="ApplyModeration"/> the operator
    /// tool runs — never by hand-building the event. A fixture written by the people who wrote the
    /// fold agrees with the fold (trap 16); this one has to agree with the writer.
    /// </summary>
    internal Task WithholdAsync(string postId, CancellationToken ct) => ModerateAsync(postId, ModerationEffect.Withhold, ct);

    /// <summary>Quarantines a post pending review through the same writer. A human record: <see cref="ApplyModeration"/> writes no other kind, and the fold treats the two states alike (R10.36).</summary>
    internal Task QuarantineAsync(string postId, CancellationToken ct) => ModerateAsync(postId, ModerationEffect.Quarantine, ct);

    /// <summary>Restores a post that was withheld or quarantined in the spam category, through the same writer.</summary>
    internal Task RestoreAsync(string postId, CancellationToken ct) => ModerateAsync(postId, ModerationEffect.Restore, ct);

    private readonly SemaphoreSlim _operatorActsGate = new(1, 1);
    private OperatorTool.OperatorActsSigningKey? _operatorActsKey;
    private string? _operatorActsPem;

    /// <summary>
    /// The operator-acts key every operator act in this suite is signed with (errata G70, R4.43), published to the log once, lazily, before the first act that needs it, under a lock so that two
    /// tests of one class racing to their first act publish it once. Returns the key's PEM: the seam a test hands <c>OperatorCommands.RunAsync</c> as <c>readOperatorActsSigningKeyPem</c>. The PEM is the
    /// test's own key, generated here; nothing in it guards anything.
    /// </summary>
    internal async Task<string> OperatorActsKeyAsync(CancellationToken ct)
    {
        await _operatorActsGate.WaitAsync(ct);
        try
        {
            if (_operatorActsPem is not null) return _operatorActsPem;

            var pem = OperatorTool.OperatorActsSigningKey.GeneratePem();
            var key = OperatorTool.OperatorActsSigningKey.FromPem(pem);
            var published = await new PublishOperatorKey(Services.GetRequiredService<IEventStore>(), Clock)
                .PublishAsync(key, Require(ActorId.Create("operator:fixture")), ct);
            _ = Require(published);
            (_operatorActsKey, _operatorActsPem) = (key, pem);
            return pem;
        }
        finally
        {
            _operatorActsGate.Release();
        }

        static T Require<T>(Result<T> result) =>
            result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} ({e.Detail})"));
    }

    /// <summary>
    /// The reader of the operator-acts key a test hands <c>OperatorCommands.RunAsync</c> for the verb <paramref name="args"/> names: the fixture's key for <c>attest-owner</c> and <c>moderate</c>, which refuse
    /// without one (exit 3, before the log is read), and none for any other verb, which must not run in an environment that holds it (R12.18: <c>sign-head</c>, <c>publish-declaration-key</c> and
    /// <c>compromise</c> refuse it). The key is published only when a verb asks for it.
    /// </summary>
    internal async Task<Func<string?>?> OperatorActsKeyForAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count == 0 || args[0] is not ("attest-owner" or "moderate")) return null;

        var pem = await OperatorActsKeyAsync(ct);
        return () => pem;
    }

    /// <summary>The key <see cref="OperatorActsKeyAsync"/> published, as the signer the writers take.</summary>
    private async Task<OperatorTool.OperatorActsSigningKey> OperatorActsSignerAsync(CancellationToken ct)
    {
        _ = await OperatorActsKeyAsync(ct);
        return _operatorActsKey!;
    }

    private async Task ModerateAsync(string postId, ModerationEffect effect, CancellationToken ct)
    {
        var signer = await OperatorActsSignerAsync(ct);
        using var scope = Services.CreateScope();
        var moderate = scope.ServiceProvider.GetRequiredService<ApplyModeration>();

        static T Require<T>(Result<T> result) =>
            result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} ({e.Detail})"));

        Require(await moderate.RecordAsync(
            PostId.Parse(postId),
            effect,
            FlagKind.Spam,
            Rationale.Parse("moderated by the test fixture"),
            Require(ActorId.Create("operator:fixture")),
            signer,
            ct));
    }

    /// <summary>
    /// Appends a <c>post.accepted</c> for <paramref name="wire"/> directly to the host's event store, with
    /// the members <c>IngestPipeline.PersistAsync</c> writes, and not through the pipeline: the shape of a
    /// post accepted before R8.64, the one way to put a <c>prev</c> on a comment in the log now.
    /// </summary>
    internal async Task<(string PostId, string Digest)> AppendStoredPostAsync(byte[] wire, CancellationToken ct)
    {
        static T Require<T>(Result<T> result) =>
            result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} ({e.Detail})"));

        var submission = (JsonValue.Object)Require(JsonReader.ParseUnrestricted(wire));
        JsonValue Member(JsonValue.Object o, string name) => o.Members.First(m => m.Key == name).Value;
        var envelope = (JsonValue.Object)Member(submission, "envelope");
        var signature = ((JsonValue.String)Member(submission, "signature")).Value;
        string Text(string name) => ((JsonValue.String)Member(envelope, name)).Value;

        var canonicalBytes = Require(CanonicalJson.CanonicalizeWithNfc(envelope));
        var canonical = canonicalBytes.ToArray();
        var digest = Digests.Sha256(canonicalBytes).ToPrefixed();
        var postId = Require(new UlidGenerator(Clock).Next()).ToString();
        var author = Text("author");

        var payload = new JsonValue.Object(
        [
            new("post_id", new JsonValue.String(postId)),
            new("canonical", new JsonValue.String(System.Text.Encoding.UTF8.GetString(canonical))),
            new("signature", new JsonValue.String(signature)),
            new("digest", new JsonValue.String(digest)),
            new("author", new JsonValue.String(author)),
            new("board", new JsonValue.String(Text("board"))),
            new("kind", new JsonValue.String(Text("kind"))),
            new("parent", envelope.Members.Any(m => m.Key == "parent") ? Member(envelope, "parent") : new JsonValue.Null()),
            new("server_ts", new JsonValue.String(Now.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture))),
            new("risk_flags", new JsonValue.Array([])),
        ]);

        using var scope = Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore>();
        Require(await store.AppendAsync(
            Require(AggregateId.Create(postId)),
            AggregateVersion.New,
            [new DomainEvent(
                Require(EventId.Create(postId)),
                Require(EventType.Create("post.accepted")),
                Require(ActorId.Create(author)),
                payload)],
            ct));
        return (postId, digest);
    }

    /// <summary>
    /// PR 4 (errata G44): an acceptance appended through the host's own use case (<see cref="AcceptAnswer"/>, the object the accept route calls), with none
    /// of the route's checks. It lets a probe read the state an acceptance of a revision leaves (or the one a crafted log holds, a thread root that is not the
    /// chain root's) apart from the route that would record it, so each is red for its own reason.
    /// </summary>
    internal async Task AppendStoredAcceptanceAsync(string threadRoot, string answerId, string acceptedBy, CancellationToken ct)
    {
        using var scope = Services.CreateScope();
        var accept = scope.ServiceProvider.GetRequiredService<AcceptAnswer>();

        var recorded = await accept.RecordAsync(Typed.Post(threadRoot), Typed.Post(answerId), Typed.Agent(acceptedBy), ct);
        _ = recorded.Match(
            v => v,
            e => throw new InvalidOperationException($"{e.Type}: {e.Title} ({e.Detail})"));
    }

    /// <summary>
    /// R4.30's attestation, through the host's own use case -- the same object the operator tool
    /// calls, so a bug in it fails this suite instead of hiding behind a fixture that builds the
    /// event correctly by hand. There is no HTTP route for this on purpose (errata G5).
    /// </summary>
    internal async Task AttestOwnerAsync(
        string agentId, CancellationToken ct, string owner = "owner:test", bool verified = true)
    {
        var signer = await OperatorActsSignerAsync(ct);
        using var scope = Services.CreateScope();
        var attest = scope.ServiceProvider.GetRequiredService<AttestOwner>();

        static T Require<T>(Result<T> result) =>
            result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} ({e.Detail})"));

        Require(await attest.RecordAsync(
            Result<AgentId>.Ok(Typed.Agent(agentId)),
            Require(OwnerId.Create(owner)),
            verified,
            OwnerVerificationMethod.Manual,
            "attested by the test fixture",
            Require(ActorId.Create("operator:fixture")),
            signer,
            ct));
    }

    /// <summary>
    /// An identity as every enrollment before errata G16 left it: its key row, written as the
    /// provisioning role, and an <c>agent.enrolled</c> naming its <c>kid</c> and no key, appended
    /// through the host's own event store -- and no <c>agent.key-bound</c>. <c>EnrollAgent</c> no longer
    /// writes this shape, so it is built here, with exactly the members <c>EnrollAgent</c> wrote until
    /// G16: <c>agent_id</c>, <c>kid</c> and <c>reason</c>. Such an identity is bound by its <c>kid</c>
    /// alone (R4.35).
    /// </summary>
    internal async Task EnrollBeforeKeyBindingAsync(string agentId, string kid, byte[] publicKey, CancellationToken ct)
    {
        static T Require<T>(Result<T> result) =>
            result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} ({e.Detail})"));

        await using (var admin = new NpgsqlConnection(ConnectionString))
        {
            await admin.OpenAsync(ct);
            await using var insert = new NpgsqlCommand(
                "INSERT INTO agent_keys (kid, agent_id, alg, public_key, valid_from, valid_until) " +
                "VALUES (@kid, @agent, 'ES256', @key, @from, NULL);",
                admin);
            insert.Parameters.AddWithValue("kid", kid);
            insert.Parameters.AddWithValue("agent", agentId);
            insert.Parameters.AddWithValue("key", publicKey);
            insert.Parameters.AddWithValue("from", Now);
            if (await insert.ExecuteNonQueryAsync(ct) != 1)
                throw new InvalidOperationException($"the key row for {agentId} was not written");
        }

        using var scope = Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore>();
        Require(await store.AppendAsync(
            Require(AggregateId.Create(agentId)),
            AggregateVersion.New,
            [new DomainEvent(
                Require(EventId.Create(Require(new UlidGenerator(Clock).Next()).ToString())),
                Require(EventType.Create(AgentStandingProjector.EnrolledType)),
                Require(ActorId.Create(agentId)),
                new JsonValue.Object(
                [
                    new(AgentStandingProjector.AgentIdField, new JsonValue.String(agentId)),
                    new(AgentStandingProjector.KeyIdField, new JsonValue.String(kid)),
                    new(AgentStandingProjector.ReasonField, new JsonValue.String("Enrollment accepted: agent key registered with the Registrar")),
                ]))],
            ct));
    }

    /// <summary>
    /// An identity as the enrollment route enrolled it before R4.36: the enrollment use case run
    /// directly, so none of the route's text checks is asked. It writes what the route writes: the key
    /// row, and the <c>agent.enrolled</c> and <c>agent.key-bound</c> of one append (R4.34). The route
    /// refuses an identifier outside NFC now; a Forum that enrolled one earlier still holds it.
    /// </summary>
    internal async Task EnrollPastTheRouteAsync(string agentId, string kid, byte[] publicKey, CancellationToken ct)
    {
        var enrolled = await Services.GetRequiredService<EnrollIdentity>()
            .EnrollAsync(Typed.Agent(agentId), new PublicKeyMaterial("ES256", kid, publicKey), ct);
        if (!enrolled.TryGetValue(out _, out var error))
            throw new InvalidOperationException($"{error!.Type}: {error.Title} ({error.Detail})");
    }

    /// <summary>
    /// Appends an <c>agent.key-bound</c> for <paramref name="agentId"/>'s <c>ES256</c> key at the end of
    /// its stream, now, through the host's own event store, with exactly the members
    /// <c>EnrollAgent</c> writes (R4.34). For an identity that has already posted, this is the binding
    /// that lands after its history: the one an identity enrolled before <c>agent.enrolled</c> existed
    /// receives when anyone re-announces its public key, and the one an operator's binding would append
    /// (the key-binding stage's spec, §2.1). No route appends it for an identity already enrolled.
    /// </summary>
    internal async Task BindKeyAfterItsPostsAsync(string agentId, string kid, byte[] publicKey, CancellationToken ct)
    {
        static T Require<T>(Result<T> result) =>
            result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} ({e.Detail})"));

        using var scope = Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore>();
        var aggregate = Require(AggregateId.Create(agentId));
        var stream = Require(await store.ReadByAggregateAsync(aggregate, ct));
        Require(await store.AppendAsync(
            aggregate,
            Require(AggregateVersion.From(stream.Count)),
            [new DomainEvent(
                Require(EventId.Create(Require(new UlidGenerator(Clock).Next()).ToString())),
                Require(EventType.Create(AgentStandingProjector.KeyBoundType)),
                Require(ActorId.Create(agentId)),
                new JsonValue.Object(
                [
                    new(AgentStandingProjector.AgentIdField, new JsonValue.String(agentId)),
                    new(AgentStandingProjector.KeyIdField, new JsonValue.String(kid)),
                    new(AgentStandingProjector.JwkField, Require(PublicJwk.Of(new PublicKeyMaterial("ES256", kid, publicKey)))),
                ]))],
            ct));
    }

    private static string AdminConnectionString =>
        Environment.GetEnvironmentVariable(EnvVarName)
        ?? new NpgsqlConnectionStringBuilder
        {
            Host = "localhost",
            Port = 5432,
            Username = Environment.UserName,
            Database = "postgres",
            Timeout = 5,
        }.ConnectionString;

    /// <summary>
    /// Provisions a throwaway database and applies <b>every</b> migration in <c>db/</c> through the
    /// production renderer -- so the schema under test is the one a deployment gets, including
    /// R11.6's <c>REVOKE UPDATE, DELETE</c> on <c>events</c> and 0002's deliberately different
    /// grants on the operational tables. A fixture that hand-wrote equivalent DDL would be testing
    /// its own transcription; one that named a single migration file would go on passing while a
    /// later migration was missing from the database it provisions.
    /// </summary>
    public async ValueTask InitializeAsync()
    {
        await using var admin = new NpgsqlDataSourceBuilder(AdminConnectionString).Build();

        try
        {
            await using var probe = await admin.OpenConnectionAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Curia.Api.Tests could not reach a Postgres server (env var '{EnvVarName}' " +
                $"{(Environment.GetEnvironmentVariable(EnvVarName) is null ? "unset; used the local default" : "set")}). " +
                "The Forum has no in-memory production event store on purpose -- R11.6's append-only " +
                "guarantee is a database grant -- so these tests fail rather than skip. " +
                $"Original error: {ex.Message}",
                ex);
        }

        await using (var create = admin.CreateCommand($"CREATE DATABASE \"{_database}\""))
            await create.ExecuteNonQueryAsync();

        var builder = new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = _database };
        _connectionString = builder.ConnectionString;

        var sql = SchemaMigrations.RenderAll(_role, _password);

        await using var target = new NpgsqlDataSourceBuilder(_connectionString).Build();
        await using var apply = target.CreateCommand(sql);
        await apply.ExecuteNonQueryAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        _operatorActsKey?.Dispose();
        _operatorActsGate.Dispose();

        await using var admin = new NpgsqlDataSourceBuilder(AdminConnectionString).Build();
        await using (var drop = admin.CreateCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)"))
            await drop.ExecuteNonQueryAsync();
        await using (var dropRole = admin.CreateCommand($"DROP ROLE IF EXISTS \"{_role}\""))
            await dropRole.ExecuteNonQueryAsync();

        GC.SuppressFinalize(this);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseSetting("ConnectionStrings:Events", HostConnectionString);

        // D41 (errata G82): the Forum's one name. The unmodified Forum ignores this key (it builds every URL from the request); the build that closes D41 reads it and nothing else.
        ConfigureOrigin(builder);

        // The issuer signing key is configuration, not something the host generates (see
        // Curia.Api.Issuer.IssuerSigningKey). One PEM per fixture, held for the fixture's whole
        // life, is what lets a test restart the host and find the tokens the previous one minted
        // still verifying -- the property a per-process key could not have. Generated rather than
        // checked in, because a signing key committed to a repository is the thing R4.20 names
        // first among the places a key must never be.
        builder.UseSetting("Curia:IssuerSigningKeyPem", IssuerSigningKeyPem);

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            if (ComposesPermissiveLifecycleGate)
            {
                services.RemoveAll<ILifecycleAppendGate>();
                services.AddSingleton(PermissiveGateFor(_connectionString));
            }

            if (ReplacesEmbedderWithTheHashedOne)
            {
                services.RemoveAll<ITextEmbedder>();
                services.AddSingleton<ITextEmbedder, HashedNGramEmbedder>();
            }

            ConfigureFuzz(services);
            ConfigureStore(services);
        });
    }

    /// <summary>
    /// Errata G65 (R11.91): whether this fixture's host runs with the hashed test adapter in place of the production embedder. The one explicit door: production
    /// composition registers no hashed adapter and no configuration selects one, so a host that must run without a model asks for it here. A fixture that overrides
    /// this to false boots the production path and needs a model directory (<c>EmbedderBootTests</c>).
    /// </summary>
    protected virtual bool ReplacesEmbedderWithTheHashedOne => true;

    /// <summary>
    /// The request fuzzer's hook (R14.10): its fixture installs what records the host's reads of
    /// headers and query parameters. Nothing else overrides it, and the base adds nothing.
    /// </summary>
    protected virtual void ConfigureFuzz(IServiceCollection services)
    {
    }

    /// <summary>
    /// The event store's hook (R6.56's route facts): a fixture that must see or disturb the host's
    /// appends decorates the registered <see cref="IEventStore"/> here. The base changes nothing.
    /// </summary>
    protected virtual void ConfigureStore(IServiceCollection services)
    {
    }
}
