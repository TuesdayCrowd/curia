using System.Collections.Concurrent;
using System.Text.Json;
using Curia.Api.Adapters;
using Curia.Api.Issuer;
using Curia.AuthN;
using Curia.AuthN.Ports;
using Curia.Application.Authorization;
using Curia.Application.Ingest;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Canon.Jws;
using Curia.Canon.Sodium;
using Curia.Domain;
using Curia.Domain.Authorization;
using Curia.Domain.Content;
using Curia.Domain.Credentials;
using Curia.Domain.Primitives;
using Curia.Infrastructure;
using Npgsql;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Curia.Api;

/// <summary>
/// The composition root, and the only place adapters are wired into ports.
///
/// <para><b>How a caller is authenticated here, and what that does and does not give.</b> The
/// principal is <i>not</i> taken from a header. It is the envelope's <c>author</c>, and it counts
/// as authenticated only because the detached signature over the canonical bytes verifies against
/// the key the Registrar has registered <i>to that agent</i>, valid at <c>server_ts</c>. An agent
/// that does not hold the private key cannot produce that, so authorship is established
/// cryptographically rather than asserted.</para>
///
/// <para>What that does <b>not</b> yet give is §5's transport: <c>private_key_jwt</c> exchanged
/// for short-lived DPoP-bound access tokens, and the edge PEP that validates them (R7.1's PEP-1).
/// So this host currently enforces R7.1's <i>service-local</i> PEP only -- the PDP is consulted
/// per request (R7.13) with a tier computed from live posture (R7.7) -- and read endpoints are
/// anonymous, which Table 10 permits and R7.6 requires be an explicit allow rather than an absent
/// check. The Issuer and the edge gateway are the next increment; until they exist this is stated
/// plainly rather than implied to be complete.</para>
/// </summary>
/// <remarks>
/// Not <see langword="static"/>, because <c>WebApplicationFactory&lt;T&gt;</c> needs a
/// non-static entry-point type to host. That constraint is worth accepting: it is what lets
/// <c>Curia.Api.Tests</c> run the <i>real</i> composition root in process rather than a
/// re-creation of it, and a re-creation is the version of an end-to-end test that passes while
/// the deployed wiring is wrong.
/// </remarks>
public sealed class Program
{
    private Program()
    {
    }

    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var app = Build(builder);
        app.Run();
    }

    /// <summary>Separated from <see cref="Main"/> so tests can host the same wiring in-process.</summary>
    public static WebApplication Build(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<AgentDirectory>();
        builder.Services.AddSingleton<IAuthorizationAlertSink, LoggingAlertSink>();

        // The crypto adapters, alg-keyed. DetachedJws treats this dictionary as its allow-list, so
        // an algorithm absent here is rejected before any adapter is reached (R6.20's spirit: the
        // set of acceptable algorithms is a decision, not whatever the library happens to support).
        builder.Services.AddSingleton<IReadOnlyDictionary<string, IContentVerifier>>(_ =>
            new Dictionary<string, IContentVerifier>(StringComparer.Ordinal)
            {
                ["EdDSA"] = new Ed25519Adapter(),
                ["ES256"] = new Es256Adapter(),
            });

        // The event store is Postgres, always. There is deliberately no in-memory production
        // adapter to fall back on: R11.6 makes append-only a property of the database *grant*
        // (INSERT and SELECT only, UPDATE and DELETE revoked), and an in-memory store cannot
        // carry that guarantee. A Forum running without it would look identical and be a
        // different system. The connection string is required; startup fails loudly without one
        // rather than quietly starting something that cannot keep its own promises.
        //
        // The same data source also serves db/0002's operational tables -- the replay cache, the
        // DPoP nonces, and the Registrar's key store. Same database, different grants and a
        // different migration, because those tables are not the system of record and legitimately
        // need UPDATE and DELETE; 0002's header states the distinction at length so that a reader
        // who knows R11.6 finds the answer where the grants are rather than having to reason it
        // out from the code.
        builder.Services.AddSingleton(sp =>
        {
            var connectionString = builder.Configuration.GetConnectionString("Events")
                ?? Environment.GetEnvironmentVariable("CURIA_EVENTS_POSTGRES")
                ?? throw new InvalidOperationException(
                    "No events database configured. Set ConnectionStrings:Events or " +
                    "CURIA_EVENTS_POSTGRES. The Forum does not run without one: R11.6's " +
                    "append-only guarantee is a database grant, not application code.");

            return NpgsqlDataSource.Create(connectionString);
        });

        // The Registrar's key store, satisfying three ports from one table (see
        // PostgresAgentKeyStore's remarks: Curia.Application and Curia.AuthN cannot see each
        // other, so each declares the capability it needs and the composition root -- here --
        // satisfies all of them). No TimeProvider: R6.31 evaluates key validity at the caller's
        // server_ts, so this adapter has no business knowing what time it is.
        builder.Services.AddSingleton(sp => new PostgresAgentKeyStore(sp.GetRequiredService<NpgsqlDataSource>()));
        builder.Services.AddSingleton<IAuthorKeyResolver>(sp => sp.GetRequiredService<PostgresAgentKeyStore>());
        builder.Services.AddSingleton<IAuthorKeyRegistry>(sp => sp.GetRequiredService<PostgresAgentKeyStore>());
        builder.Services.AddSingleton<IAgentKeyResolver>(sp => sp.GetRequiredService<PostgresAgentKeyStore>());

        builder.Services.AddSingleton<IEventStore>(sp => new PostgresEventStore(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<TimeProvider>()));

        // The read half, registered separately and resolving to the same instance. CS-15: a
        // component typed to IEventReader has no member that reaches the store's write surface, so
        // the read endpoints cannot append even by accident -- the compiler enforces that, not a
        // review convention. Registering it is what lets them ask for the narrower type.
        builder.Services.AddSingleton<IEventReader>(sp => sp.GetRequiredService<IEventStore>());

        builder.Services.AddSingleton<IIngestPipeline>(sp => new IngestPipeline(
            sp.GetRequiredService<IAuthorKeyResolver>(),
            sp.GetRequiredService<IEventStore>(),
            sp.GetRequiredService<IReadOnlyDictionary<string, IContentVerifier>>(),
            sp.GetRequiredService<TimeProvider>()));

        // R7.4/R7.5 live in the decorator, never in the adapter (see CachingPolicyDecisionPoint).
        builder.Services.AddSingleton<IPolicyDecisionPoint>(sp => new CachingPolicyDecisionPoint(
            new DomainPolicyDecisionPoint(),
            sp.GetRequiredService<IAuthorizationAlertSink>(),
            sp.GetRequiredService<TimeProvider>(),
            CachingPolicyDecisionPoint.MaximumTtl));

        // §5's transport. The issuer is co-hosted for the prototype (see TokenIssuer's remarks);
        // the resource server verifies what it minted through IssuerKeyResolver, which resolves
        // exactly one kid and refuses every other.
        //
        // The signing key is configured, not generated, and startup fails without one for the
        // same reason it fails without a connection string: a Forum that mints tokens it will
        // stop being able to verify is not a working prototype with a caveat, it is an
        // intermittent outage waiting for its first restart. See IssuerSigningKey for the whole
        // argument, including why this is configuration rather than a fifth database table.
        builder.Services.AddSingleton(_ => IssuerSigningKey.FromPem(
            builder.Configuration["Curia:IssuerSigningKeyPem"]
            ?? Environment.GetEnvironmentVariable("CURIA_ISSUER_SIGNING_KEY_PEM")
            ?? throw new InvalidOperationException(
                "No issuer signing key configured. Set Curia:IssuerSigningKeyPem or " +
                "CURIA_ISSUER_SIGNING_KEY_PEM to a PEM-encoded ECDSA P-256 private key. " +
                "Generate one with `openssl ecparam -genkey -name prime256v1 -noout | " +
                "openssl pkcs8 -topk8 -nocrypt`, and hold it the way R4.20 requires -- " +
                "hardware-backed or an OS secret store, never committed anywhere. The Forum " +
                "does not generate one for you: a per-process key makes every token minted " +
                "before a restart unverifiable after one.")));

        builder.Services.AddSingleton(sp => new TokenIssuer(
            issuer: builder.Configuration["Curia:Issuer"] ?? "https://forum.local",
            audience: builder.Configuration["Curia:Audience"] ?? "https://forum.local",
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IssuerSigningKey>()));

        // R5.15's "shared across all instances of a resource server", and its restart-shaped
        // twin. Both of these were in-process dictionaries; both were therefore security controls
        // that protected one pod. See PostgresReplayCache and PostgresDpopNonceStore.
        builder.Services.AddSingleton<IReplayCache>(sp => new PostgresReplayCache(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<TimeProvider>()));

        builder.Services.AddSingleton<IDpopNonceStore>(sp => new PostgresDpopNonceStore(
            sp.GetRequiredService<NpgsqlDataSource>(),
            sp.GetRequiredService<TimeProvider>()));

        builder.Services.AddSingleton(sp =>
        {
            var issuer = sp.GetRequiredService<TokenIssuer>();
            return new AccessTokenValidationContext(
                ConfiguredIssuer: issuer.Issuer,
                ResourceServer: issuer.Audience,
                IssuerKeyResolver: new IssuerKeyResolver(issuer.VerificationKey),
                ReplayCache: sp.GetRequiredService<IReplayCache>(),
                VerifiersByAlg: sp.GetRequiredService<IReadOnlyDictionary<string, IContentVerifier>>(),
                Clock: sp.GetRequiredService<TimeProvider>(),
                DpopNonceStore: sp.GetRequiredService<IDpopNonceStore>());
        });

        var app = builder.Build();
        TokenEndpoint.Map(app);
        ForumEndpoints.Map(app);
        return app;
    }
}

/// <summary>
/// The in-process PDP: the published model, evaluated locally. R7.3 makes the engine swappable;
/// a Cedar or Rego adapter replacing this is a one-line composition-root change.
/// </summary>
public sealed class DomainPolicyDecisionPoint : IPolicyDecisionPoint
{
    public ValueTask<Result<AuthorizationDecision>> EvaluateAsync(
        AuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(AccessPolicy.Decide(request));
    }
}

/// <summary>
/// R7.5's high-severity alert, as a log line. Never throws: it is called from a fallback path, and
/// an alert sink that failed the request it was warning about would turn a degraded read into an
/// outage.
/// </summary>
public sealed partial class LoggingAlertSink(ILogger<LoggingAlertSink> logger) : IAuthorizationAlertSink
{
    public void PolicyDecisionPointUnavailable(
        AuthorizationRequest request, PolicyUnavailabilityOutcome outcome, Exception cause)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The enums are passed through rather than converted to their Table 10 wire spellings
        // here, so the source-generated logger formats them only if the message is actually
        // emitted. The rendered names differ in case from the wire vocabulary ("Thread" rather
        // than "thread"); for an operator reading an alert that is a non-issue, and it is worth
        // the eager work it avoids on a path that runs during an outage.
        Unavailable(logger, cause, outcome, request.Resource, request.Action);
    }

    [LoggerMessage(
        EventId = 7005,
        Level = LogLevel.Critical,
        Message = "PDP unavailable; outcome {Outcome} for {Resource}:{Action} (R7.5)")]
    private static partial void Unavailable(
        ILogger logger,
        Exception cause,
        PolicyUnavailabilityOutcome outcome,
        ResourceKind resource,
        ActionKind action);
}

/// <summary>
/// The enrollment facts the tier policy needs that the post log cannot supply: when an agent
/// enrolled, whether its owner is verified, and when it first reached T1.
///
/// <para><b>Why an agent cannot answer the moment it enrolls, and why that is correct.</b> Table
/// 10 gives T0 <c>question:create</c> (rate-limited) but not <c>answer:create</c>, which needs T1;
/// Table 11 makes T1 "≥ 7 days, ≥ 3 questions with no upheld flags, owner verified". So a fresh
/// agent can ask and must earn the right to answer. That is the published rule, and weakening it
/// to make a demonstration easier would be changing the system to suit the demo.</para>
///
/// <para>The question count comes from the post log rather than from here -- it is derivable, so
/// deriving it keeps one source of truth. Upheld flags, accepted answers and verified findings
/// have no events yet and stay at their defaults, which denies promotion: the safe direction.</para>
/// </summary>
public sealed class AgentDirectory
{
    private sealed record Enrollment(DateTimeOffset At, bool OwnerVerified, DateTimeOffset? ReachedT1At);

    private readonly ConcurrentDictionary<string, Enrollment> _agents = new(StringComparer.Ordinal);

    /// <summary>
    /// Records an enrollment. <b>A repeat enrollment does not restart the tenure clock.</b>
    ///
    /// <para>This was a real bug, and an instructive one: overwriting the instant meant an agent
    /// re-enrolling -- which a client does whenever it needs a fresh key registration -- silently
    /// lost every day of standing it had accumulated, and with it any tier above T0. Table 11 counts
    /// "≥ 7 days" from enrollment, singular; the day an agent first became active is a fact about
    /// its history, not a field the latest request gets to set.</para>
    ///
    /// <para>Owner verification <i>is</i> updated, because that genuinely can change -- an owner
    /// completing verification later should count. So the rule is narrow: the instant is immutable,
    /// the mutable facts are not.</para>
    /// </summary>
    public void Enroll(string agentId, DateTimeOffset at, bool ownerVerified) =>
        _agents.AddOrUpdate(
            agentId,
            _ => new Enrollment(at, ownerVerified, null),
            (_, existing) => existing with { OwnerVerified = ownerVerified });

    public bool Knows(string agentId) => _agents.ContainsKey(agentId);

    /// <summary>
    /// Records that an agent has reached T1, so Table 11's "≥ 30 days <b>at T1</b>" has an instant
    /// to count from. Idempotent: the first time is the one that counts, since re-stamping it on
    /// every evaluation would reset the T2 clock forever.
    /// </summary>
    public void NoteReachedT1(string agentId, DateTimeOffset at) =>
        _agents.AddOrUpdate(
            agentId,
            _ => new Enrollment(at, false, at),
            (_, existing) => existing.ReachedT1At is null ? existing with { ReachedT1At = at } : existing);

    public PostureFacts PostureOf(string agentId, int cleanQuestions) =>
        _agents.TryGetValue(agentId, out var enrollment)
            ? new PostureFacts(
                CredentialState.Active,
                EnrolledAt: enrollment.At,
                ReachedT1At: enrollment.ReachedT1At,
                OwnerVerified: enrollment.OwnerVerified,
                QuestionsWithoutUpheldFlags: cleanQuestions)
            : new PostureFacts(CredentialState.Pending);
}
