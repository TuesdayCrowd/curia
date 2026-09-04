using System.Globalization;
using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Canon.Envelope;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Canon.Sodium;
using Curia.Domain;
using Curia.Domain.Acta;
using Curia.Domain.Credentials;
using Curia.Domain.Primitives;
using Curia.Infrastructure;

namespace Curia.OperatorTool;

/// <summary>Exit codes, stable so a script can branch on them.</summary>
public static class ExitCode
{
    public const int Ok = 0;

    /// <summary>Bad or missing arguments. Nothing was written.</summary>
    public const int Usage = 1;

    /// <summary>The use case refused; the slug is on stderr. Nothing was written.</summary>
    public const int Refused = 2;

    /// <summary>No events database configured.</summary>
    public const int Environment = 3;
}

/// <summary>
/// <c>curia-operator</c>: the Forum operator's out-of-band verbs. One today -- R4.30's owner
/// attestation (errata G5) -- because that is the one fact §4.6 places outside the agent's reach
/// and the one the Forum could not otherwise learn.
///
/// <para>Speaks to the event log directly, over the same <see cref="PostgresAdapters"/> the Forum
/// runs on and the same append-only grant (R11.6). There is no HTTP surface for what it does, on
/// purpose: an operator endpoint would need a Table 10 pair that does not exist, and inventing that
/// cell to reach a route is the move <c>ResourceActionModel.RowFor</c> exists to prevent.</para>
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var connectionString = System.Environment.GetEnvironmentVariable("CURIA_EVENTS_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            await Console.Error.WriteLineAsync(
                "error: CURIA_EVENTS_POSTGRES is not set. This tool appends to the Forum's event log " +
                "and needs the same connection the Forum uses; it does not run without one.")
                .ConfigureAwait(false);
            return ExitCode.Environment;
        }

        return await OperatorCommands
            .RunAsync(
                args,
                connectionString,
                TimeProvider.System,
                Console.Out,
                Console.Error,
                CancellationToken.None,
                logSigningKeyPem: System.Environment.GetEnvironmentVariable("CURIA_LOG_SIGNING_KEY_PEM"))
            .ConfigureAwait(false);
    }
}

/// <summary>
/// The verbs, separated from <see cref="Program.Main"/> so the end-to-end suite can drive them
/// against the database it provisions -- the whole path an operator runs, minus reading one
/// environment variable.
/// </summary>
public static class OperatorCommands
{
    private const string Usage =
        """
        curia-operator -- the Forum operator's out-of-band verbs

          curia-operator sign-head --by <operator-name>

              Signs the Acta's current root with the log key (CURIA_LOG_SIGNING_KEY_PEM) and
              appends it to the log as a log.head entry, publishing the key (log.key) first
              if the log has not seen it. R6.24: run this on a schedule of at most 60 minutes.

          curia-operator attest-owner --agent <agent-id> --owner <owner-id> --by <operator-name>
                                      [--method domain|email|attestation|manual] [--reason <text>]
                                      [--unverified]

              Records that <operator-name> attests <agent-id>'s owner <owner-id> as verified
              (R4.30), naming which of R4.24's proofs was satisfied (default: manual). --unverified
              records a lapse instead. The agent must already be enrolled; an agent's owner is
              bound once (R4.1), so a later attestation naming a different owner is refused.

        ENVIRONMENT
          CURIA_EVENTS_POSTGRES      the Forum's events database (required)
          CURIA_LOG_SIGNING_KEY_PEM  the Acta's ES256 signing key (sign-head only)

        EXIT CODES
          0  recorded
          1  usage error -- nothing was written
          2  refused -- the reason's slug is on stderr; nothing was written
          3  CURIA_EVENTS_POSTGRES is not set
        """;

    /// <param name="logSigningKeyPem">
    /// <c>CURIA_LOG_SIGNING_KEY_PEM</c>: the Acta's signing key, needed by <c>sign-head</c> alone.
    /// A parameter rather than a read of the environment here so the end-to-end suite can hand
    /// in a throwaway key; <see cref="Program.Main"/> is the one place that reads the variable.
    /// </param>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        string connectionString,
        TimeProvider clock,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken,
        string? logSigningKeyPem = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        if (args.Count == 0 || args[0] is "--help" or "-h" or "help")
        {
            await stdout.WriteLineAsync(Usage).ConfigureAwait(false);
            return args.Count == 0 ? ExitCode.Usage : ExitCode.Ok;
        }

        if (args[0] == "sign-head")
            return await SignHeadAsync(args.Skip(1).ToArray(), connectionString, clock, stdout, stderr, logSigningKeyPem, cancellationToken)
                .ConfigureAwait(false);

        if (args[0] != "attest-owner")
        {
            await stderr.WriteLineAsync($"error: unknown verb '{args[0]}'. Try --help.").ConfigureAwait(false);
            return ExitCode.Usage;
        }

        var parsed = Parse(args.Skip(1).ToArray());
        if (parsed.Error is { } usageError)
        {
            await stderr.WriteLineAsync("error: " + usageError).ConfigureAwait(false);
            return ExitCode.Usage;
        }

        Result<OwnerAttestation> result;
        var adapters = new PostgresAdapters(connectionString, clock);
        await using (adapters.ConfigureAwait(false))
        {
            result = await new AttestOwner(adapters.EventStore, clock).RecordAsync(
                parsed.Agent!,
                parsed.Owner,
                parsed.Verified,
                parsed.Method,
                parsed.Reason!,
                parsed.By,
                cancellationToken).ConfigureAwait(false);
        }

        if (!result.TryGetValue(out var attestation, out var error))
        {
            await stderr.WriteLineAsync(
                $"error: {error!.Title} ({error.Type}){(error.Detail is { Length: > 0 } d ? ": " + d : string.Empty)}")
                .ConfigureAwait(false);
            return ExitCode.Refused;
        }

        await stdout.WriteLineAsync($"attested  {parsed.Agent}").ConfigureAwait(false);
        await stdout.WriteLineAsync(
            $"owner     {attestation!.Owner.Value}  ({(attestation.Verified ? "verified" : "NOT verified")} via {OwnerVerificationMethods.Wire(attestation.Method)})")
            .ConfigureAwait(false);
        await stdout.WriteLineAsync($"by        {parsed.By.Value}").ConfigureAwait(false);
        await stdout.WriteLineAsync(
            $"at        {attestation.AttestedAt.ToString("O", CultureInfo.InvariantCulture)}").ConfigureAwait(false);

        return ExitCode.Ok;
    }

    /// <summary>
    /// R6.24 and R6.49: sign the Acta's current root and append it to the log as a
    /// <c>log.head</c> entry, publishing the key first (<c>log.key</c>, R6.50) if the log has not
    /// seen it. The Forum holds no log key (R11.7), so this verb is the only way a head comes to
    /// exist; R6.24's "fixed intervals (≤ 60 minutes)" is an operator's scheduling obligation, run
    /// from cron or a timer, and the served head's age is visible on <c>GET /v1/log/head</c>.
    ///
    /// <para>The root is computed here, from the log as this tool reads it, not fetched from the
    /// Forum: a signer that signs a root the Forum handed it is a rubber stamp. The tree is
    /// folded by the same <see cref="ActaLog"/> the Forum uses, which is a shared implementation
    /// rather than an independent one; <c>curia-testis</c> is the independent one.</para>
    /// </summary>
    private static async Task<int> SignHeadAsync(
        string[] argv,
        string connectionString,
        TimeProvider clock,
        TextWriter stdout,
        TextWriter stderr,
        string? logSigningKeyPem,
        CancellationToken cancellationToken)
    {
        string? byValue = null;
        for (var i = 0; i < argv.Length; i++)
        {
            if (argv[i] == "--by" && i + 1 < argv.Length) { byValue = argv[++i]; continue; }
            await stderr.WriteLineAsync($"error: unexpected argument '{argv[i]}'. sign-head takes --by <operator-name>.").ConfigureAwait(false);
            return ExitCode.Usage;
        }

        if (string.IsNullOrWhiteSpace(byValue))
        {
            await stderr.WriteLineAsync("error: --by <operator-name> is required.").ConfigureAwait(false);
            return ExitCode.Usage;
        }

        var actorValue = byValue.StartsWith("operator:", StringComparison.Ordinal) ? byValue : "operator:" + byValue;
        if (!ActorId.Create(actorValue).TryGetValue(out var actor, out var actorError))
        {
            await stderr.WriteLineAsync("error: " + actorError!.Title).ConfigureAwait(false);
            return ExitCode.Usage;
        }

        if (string.IsNullOrWhiteSpace(logSigningKeyPem))
        {
            await stderr.WriteLineAsync(
                "error: CURIA_LOG_SIGNING_KEY_PEM is not set. Heads are signed with the log key, which the " +
                "Forum does not hold (R11.7); this tool needs it and does not sign without it.")
                .ConfigureAwait(false);
            return ExitCode.Environment;
        }

        using var key = LoadKey(logSigningKeyPem, out var keyProblem);
        if (key is null)
        {
            await stderr.WriteLineAsync("error: CURIA_LOG_SIGNING_KEY_PEM is not a usable P-256 key: " + keyProblem).ConfigureAwait(false);
            return ExitCode.Environment;
        }

        {
            var adapters = new PostgresAdapters(connectionString, clock);
            await using (adapters.ConfigureAwait(false))
            {
                var store = adapters.EventStore;
                var ids = new UlidGenerator(clock);

                var folded = await FoldAsync(store, cancellationToken).ConfigureAwait(false);
                if (!folded.TryGetValue(out var acta, out var foldError))
                    return await RefuseAsync(stderr, foldError!).ConfigureAwait(false);

                // First use of this key: publish it before anything is signed with it, so the key
                // history (R12.16) precedes every head it explains.
                if (!acta!.Keys.Any(k => string.Equals(k.Kid, key.Kid, StringComparison.Ordinal)))
                {
                    var published = await AppendAsync(
                        store, ids, LogEntries.KeysAggregate, acta.Keys.Length, LogEntries.KeyType, actor,
                        new JsonValue.Object(
                        [
                            new(LogEntries.KidMember, new JsonValue.String(key.Kid)),
                            new(LogEntries.JwkMember, key.PublicJwk()),
                            new(LogEntries.ValidFromMember, new JsonValue.String(LogLeaf.RenderServerTimestamp(ServerTimestamp.At(clock.GetUtcNow())))),
                        ]),
                        cancellationToken).ConfigureAwait(false);
                    if (!published.TryGetValue(out _, out var publishError))
                        return await RefuseAsync(stderr, publishError!).ConfigureAwait(false);

                    await stdout.WriteLineAsync($"published key  {key.Kid}").ConfigureAwait(false);

                    folded = await FoldAsync(store, cancellationToken).ConfigureAwait(false);
                    if (!folded.TryGetValue(out acta, out foldError))
                        return await RefuseAsync(stderr, foldError!).ConfigureAwait(false);
                }

                var timestamp = LogLeaf.RenderServerTimestamp(ServerTimestamp.At(clock.GetUtcNow()));
                var head = LogEntries.HeadDocument(acta!.Root, timestamp, acta.TreeSize);
                if (!LogEntries.HeadCanonical(head).TryGetValue(out var canonical, out var canonicalError))
                    return await RefuseAsync(stderr, canonicalError!).ConfigureAwait(false);

                var es256 = new Es256Adapter();
                var jws = new DetachedJws(
                    new Dictionary<string, IContentSigner>(StringComparer.Ordinal) { ["ES256"] = es256 },
                    new Dictionary<string, IContentVerifier>(StringComparer.Ordinal) { ["ES256"] = es256 },
                    DetachedJws.HeadTyp);
                if (!jws.Sign(canonical, key.AsSigningKey()).TryGetValue(out var signature, out var signError))
                    return await RefuseAsync(stderr, signError!).ConfigureAwait(false);

                var appended = await AppendAsync(
                    store, ids, LogEntries.HeadsAggregate, acta.Heads.Length, LogEntries.HeadType, actor,
                    new JsonValue.Object(
                    [
                        new(LogEntries.HeadMember, head),
                        new(LogEntries.KidMember, new JsonValue.String(key.Kid)),
                        new(LogEntries.SignatureMember, new JsonValue.String(signature!.Compact)),
                    ]),
                    cancellationToken).ConfigureAwait(false);
                if (!appended.TryGetValue(out _, out var appendError))
                    return await RefuseAsync(stderr, appendError!).ConfigureAwait(false);

                await stdout.WriteLineAsync($"signed head    tree_size={acta.TreeSize.ToString(CultureInfo.InvariantCulture)}").ConfigureAwait(false);
                await stdout.WriteLineAsync($"root           {LogEntries.Prefixed(acta.Root)}").ConfigureAwait(false);
                await stdout.WriteLineAsync($"kid            {key.Kid}").ConfigureAwait(false);
                await stdout.WriteLineAsync($"log_index      {acta.TreeSize.ToString(CultureInfo.InvariantCulture)}").ConfigureAwait(false);
                await stdout.WriteLineAsync($"by             {actor.Value}").ConfigureAwait(false);
                await stdout.WriteLineAsync($"at             {timestamp}").ConfigureAwait(false);
                return ExitCode.Ok;
            }
        }
    }

    private static LogSigningKey? LoadKey(string pem, out string? problem)
    {
        try
        {
            problem = null;
            return LogSigningKey.FromPem(pem);
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or ArgumentException or InvalidOperationException)
        {
            problem = e.Message;
            return null;
        }
    }

    private static async Task<Result<ActaLog>> FoldAsync(IEventStore store, CancellationToken cancellationToken)
    {
        var read = await store.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        return read.Bind(ActaLog.Fold);
    }

    private static async Task<Result<IReadOnlyList<AppendedEvent>>> AppendAsync(
        IEventStore store,
        UlidGenerator ids,
        string aggregateValue,
        long expectedCount,
        string type,
        ActorId actor,
        JsonValue.Object payload,
        CancellationToken cancellationToken)
    {
        if (!AggregateId.Create(aggregateValue).TryGetValue(out var aggregate, out var aggregateError))
            return Result<IReadOnlyList<AppendedEvent>>.Fail(aggregateError!);
        if (!AggregateVersion.From(expectedCount).TryGetValue(out var version, out var versionError))
            return Result<IReadOnlyList<AppendedEvent>>.Fail(versionError!);
        if (!ids.Next().TryGetValue(out var ulid, out var idError))
            return Result<IReadOnlyList<AppendedEvent>>.Fail(idError!);
        if (!EventId.Create(ulid.ToString()).TryGetValue(out var eventId, out var eventIdError))
            return Result<IReadOnlyList<AppendedEvent>>.Fail(eventIdError!);
        if (!EventType.Create(type).TryGetValue(out var eventType, out var typeError))
            return Result<IReadOnlyList<AppendedEvent>>.Fail(typeError!);

        return await store
            .AppendAsync(aggregate, version, [new DomainEvent(eventId, eventType, actor, payload)], cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<int> RefuseAsync(TextWriter stderr, Error error)
    {
        await stderr.WriteLineAsync(
            $"error: {error.Title} ({error.Type}){(error.Detail is { Length: > 0 } d ? ": " + d : string.Empty)}")
            .ConfigureAwait(false);
        return ExitCode.Refused;
    }

    private sealed record Parsed(
        string? Agent,
        OwnerId Owner,
        ActorId By,
        bool Verified,
        OwnerVerificationMethod Method,
        string? Reason,
        string? Error)
    {
        internal static Parsed Fail(string error) => new(null, default, default, false, default, null, error);
    }

    /// <summary>
    /// A hand-rolled flag reader rather than a parsing library, for the reason the CLI gives: the
    /// tool has one verb and six flags, and a dependency would be larger than the code it replaced.
    /// </summary>
    private static Parsed Parse(string[] argv)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var unverified = false;

        for (var i = 0; i < argv.Length; i++)
        {
            var arg = argv[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
                return Parsed.Fail($"unexpected argument '{arg}'.");

            var name = arg[2..];
            if (name == "unverified")
            {
                unverified = true;
                continue;
            }

            if (name is not ("agent" or "owner" or "by" or "method" or "reason"))
                return Parsed.Fail($"unknown flag --{name}.");

            if (i + 1 >= argv.Length)
                return Parsed.Fail($"--{name} needs a value.");

            values[name] = argv[++i];
        }

        if (!values.TryGetValue("agent", out var agent) || agent.Length == 0)
            return Parsed.Fail("--agent <agent-id> is required.");

        if (!values.TryGetValue("owner", out var ownerValue)
            || !OwnerId.Create(ownerValue).TryGetValue(out var owner, out _))
            return Parsed.Fail("--owner <owner-id> is required.");

        if (!values.TryGetValue("by", out var byValue) || byValue.Length == 0)
            return Parsed.Fail("--by <operator-name> is required.");

        // The operator namespace is a convention, not a rule the domain can hold (plan D4). Applied
        // here, once, so an operator who types a bare name is still recorded as one.
        var actorValue = byValue.StartsWith("operator:", StringComparison.Ordinal) ? byValue : "operator:" + byValue;
        if (!ActorId.Create(actorValue).TryGetValue(out var by, out _))
            return Parsed.Fail("--by <operator-name> is required.");

        var method = OwnerVerificationMethod.Manual;
        if (values.TryGetValue("method", out var methodValue))
        {
            if (!OwnerVerificationMethods.Parse(methodValue).TryGetValue(out method, out var methodError))
                return Parsed.Fail($"{methodError!.Title}: '{methodValue}'.");
        }

        var reason = values.TryGetValue("reason", out var reasonValue) && reasonValue.Length > 0
            ? reasonValue
            : $"Owner attested by {actorValue} via {OwnerVerificationMethods.Wire(method)}";

        return new Parsed(agent, owner, by, !unverified, method, reason, null);
    }
}
