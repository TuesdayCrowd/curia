using System.Collections.Immutable;
using System.Globalization;
using Curia.Application.Credentials;
using Curia.Application.Moderation;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Canon.Envelope;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Canon.Sodium;
using Curia.Domain;
using Curia.Domain.Acta;
using Curia.Domain.Credentials;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;
using Curia.Domain.Serving;
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

          curia-operator moderate --post <post-id> --category <kind> --effect <effect>
                                  --reason <text> --by <operator-name>

              Records R10.36's human moderator acting on <post-id> (R10.59): <effect> is one of
              withhold, quarantine, restore, dismiss; <kind> is one of R10.35's seven. The record
              names every flag of that kind raised against the post (R10.60) and is refused if it
              would change nothing. The reason lands in a public leaf and is screened like a
              flag's: credential material is refused.

          curia-operator flags [--post <post-id>] [--open]

              Lists flags with who raised them and why -- the review queue, out of band. Each
              rationale is delimited and datamarked (R10.44), and control characters are escaped.
              --open lists only flags no record has adjudicated.

        ENVIRONMENT
          CURIA_EVENTS_POSTGRES      the Forum's events database (required)
          CURIA_LOG_SIGNING_KEY_PEM  the Acta's ES256 signing key (sign-head only)

        EXIT CODES
          0  recorded
          1  usage error -- nothing was written
          2  refused -- the reason's slug is on stderr; nothing was written
          3  CURIA_EVENTS_POSTGRES is not set
        """;

    /// <summary>
    /// What an operator does about <c>curia/domain/concurrency-conflict</c> from <c>moderate</c>.
    /// <see cref="ApplyModeration"/> decides on one read of the log, and the store refuses the append
    /// once another write has reached the post since that read, so the record was never written and
    /// only a fresh read can say whether it is still wanted. Names no flag, raiser or rationale
    /// (R10.27, R10.28).
    /// </summary>
    private const string OvertakenGuidance =
        "hint: another write reached this post after this record was decided, so nothing was written. " +
        "Re-read the post (curia-operator flags --post <post-id>) and re-run moderate if the record is still wanted.";

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

        if (args[0] == "moderate")
            return await ModerateAsync(args.Skip(1).ToArray(), connectionString, clock, stdout, stderr, cancellationToken)
                .ConfigureAwait(false);

        if (args[0] == "flags")
            return await FlagsAsync(args.Skip(1).ToArray(), connectionString, clock, stdout, stderr, cancellationToken)
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

    /// <summary>
    /// R10.59: an operator's moderation record, appended out of band through
    /// <see cref="ApplyModeration"/> — the only producer of <c>moderation.applied</c>.
    /// </summary>
    private static async Task<int> ModerateAsync(
        string[] argv,
        string connectionString,
        TimeProvider clock,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < argv.Length; i++)
        {
            var arg = argv[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
                return await UsageErrorAsync(stderr, $"unexpected argument '{arg}'.").ConfigureAwait(false);

            var name = arg[2..];
            if (name is not ("post" or "category" or "effect" or "reason" or "by"))
                return await UsageErrorAsync(stderr, $"unknown flag --{name}.").ConfigureAwait(false);

            if (i + 1 >= argv.Length)
                return await UsageErrorAsync(stderr, $"--{name} needs a value.").ConfigureAwait(false);

            values[name] = argv[++i];
        }

        foreach (var required in (string[])["post", "category", "effect", "reason", "by"])
            if (!values.TryGetValue(required, out var given) || string.IsNullOrWhiteSpace(given))
                return await UsageErrorAsync(stderr, $"--{required} is required.").ConfigureAwait(false);

        if (!FlagKinds.Parse(values["category"]).TryGetValue(out var category, out var categoryError))
            return await UsageErrorAsync(stderr, categoryError!.Detail ?? categoryError.Title).ConfigureAwait(false);

        if (!ModerationEffects.Parse(values["effect"]).TryGetValue(out var effect, out var effectError))
            return await UsageErrorAsync(stderr, effectError!.Detail ?? effectError.Title).ConfigureAwait(false);

        // The operator namespace, applied once here as attest-owner applies it (plan D4).
        var by = values["by"];
        var actorValue = by.StartsWith(ApplyModeration.OperatorPrefix, StringComparison.Ordinal) ? by : ApplyModeration.OperatorPrefix + by;
        if (!ActorId.Create(actorValue).TryGetValue(out var actor, out _))
            return await UsageErrorAsync(stderr, "--by <operator-name> is required.").ConfigureAwait(false);

        Result<ModerationRecorded> result;
        var adapters = new PostgresAdapters(connectionString, clock);
        await using (adapters.ConfigureAwait(false))
        {
            result = await new ApplyModeration(adapters.EventStore, adapters.FlagDetails, clock)
                .RecordAsync(values["post"], effect, category, values["reason"], actor, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!result.TryGetValue(out var recorded, out var error))
        {
            var refused = await RefuseAsync(stderr, error!).ConfigureAwait(false);
            if (string.Equals(error!.Type, DomainErrors.ConcurrencyConflictType, StringComparison.Ordinal))
                await stderr.WriteLineAsync(OvertakenGuidance).ConfigureAwait(false);

            return refused;
        }

        var count = recorded!.Adjudicates.Length.ToString(CultureInfo.InvariantCulture);
        var named = recorded.Adjudicates.IsEmpty ? string.Empty : ": " + string.Join(", ", recorded.Adjudicates);

        await stdout.WriteLineAsync($"moderated    {TerminalText.Line(recorded.PostId)}").ConfigureAwait(false);
        await stdout.WriteLineAsync($"effect       {ModerationEffects.Wire(recorded.Effect)}").ConfigureAwait(false);
        await stdout.WriteLineAsync($"category     {FlagKinds.Wire(recorded.Category)}").ConfigureAwait(false);
        await stdout.WriteLineAsync($"digest       {recorded.Digest}").ConfigureAwait(false);
        await stdout.WriteLineAsync($"adjudicates  {count}{named}").ConfigureAwait(false);
        await stdout.WriteLineAsync($"by           {TerminalText.Line(recorded.Moderator.Value)}").ConfigureAwait(false);
        await stdout.WriteLineAsync($"at           {recorded.At.ToString("O", CultureInfo.InvariantCulture)}").ConfigureAwait(false);
        return ExitCode.Ok;
    }

    /// <summary>
    /// The review queue, out of band: every flag with its raiser and rationale, which only an
    /// operator ever sees (R10.44's <c>moderation</c>|<c>list</c> view for the human arm). Rationales
    /// are delimited and datamarked, because the reader may be a model, and every agent-written
    /// string is made terminal-safe first.
    /// </summary>
    private static async Task<int> FlagsAsync(
        string[] argv,
        string connectionString,
        TimeProvider clock,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        string? post = null;
        var openOnly = false;
        for (var i = 0; i < argv.Length; i++)
        {
            if (argv[i] == "--open") { openOnly = true; continue; }
            if (argv[i] == "--post" && i + 1 < argv.Length) { post = argv[++i]; continue; }
            return await UsageErrorAsync(stderr, $"unexpected argument '{argv[i]}'. flags takes [--post <post-id>] [--open].").ConfigureAwait(false);
        }

        IReadOnlyList<AppendedEvent> log;
        IReadOnlyList<FlagDetail> details;
        var adapters = new PostgresAdapters(connectionString, clock);
        await using (adapters.ConfigureAwait(false))
        {
            var read = await adapters.EventStore.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            if (!read.TryGetValue(out var events, out var readError))
                return await RefuseAsync(stderr, readError!).ConfigureAwait(false);

            var rows = await adapters.FlagDetails.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            if (!rows.TryGetValue(out var detailRows, out var detailError))
                return await RefuseAsync(stderr, detailError!).ConfigureAwait(false);

            log = events!;
            details = detailRows!;
        }

        var directory = FlagDirectory.Join(log, details);
        var moderation = FlagProjector.Fold(log);
        var rationales = RationalesByFlag(log, details);

        var listed = 0;
        foreach (var flag in directory.Flags.Where(f => post is null || string.Equals(f.PostId, post, StringComparison.Ordinal)))
        {
            ImmutableArray<ModerationAction> history = moderation.TryGetValue(flag.PostId, out var state) ? state.History : [];
            var status = ModerationPolicy.UpheldFlags(history).Contains(flag.FlagId) ? "upheld"
                : ModerationPolicy.AdjudicatedFlags(history).Contains(flag.FlagId) ? "adjudicated, not upheld"
                : "open";

            if (openOnly && status != "open") continue;
            listed++;

            await stdout.WriteLineAsync($"flag       {flag.FlagId}").ConfigureAwait(false);
            await stdout.WriteLineAsync($"post       {TerminalText.Line(flag.PostId)}").ConfigureAwait(false);
            await stdout.WriteLineAsync($"kind       {FlagKinds.Wire(flag.Kind)}").ConfigureAwait(false);
            await stdout.WriteLineAsync($"raised_by  {TerminalText.Line(flag.RaisedBy)}").ConfigureAwait(false);
            await stdout.WriteLineAsync($"raised_at  {flag.At.Value.ToString("O", CultureInfo.InvariantCulture)}").ConfigureAwait(false);
            await stdout.WriteLineAsync($"state      {status}").ConfigureAwait(false);
            await stdout.WriteLineAsync("rationale").ConfigureAwait(false);
            await stdout.WriteLineAsync(Datamarking.Render(
                TerminalText.Block(rationales.GetValueOrDefault(flag.FlagId, string.Empty)), MarkingMode.Datamark)).ConfigureAwait(false);
            await stdout.WriteLineAsync().ConfigureAwait(false);
        }

        // A skipped flag's post is unknown (no row) or not believed (a row that no longer opens the
        // commitment), so no skip can be filtered by --post; the counts are the whole directory's.
        if (post is not null && !directory.Skipped.IsEmpty)
            await stdout.WriteLineAsync("note       the skipped counts below cover every post, not only --post: a skipped flag cannot be attributed to one").ConfigureAwait(false);

        foreach (var (reason, skipped) in directory.Skipped)
            await stdout.WriteLineAsync($"skipped    {reason}: {skipped.ToString(CultureInfo.InvariantCulture)}").ConfigureAwait(false);

        await stdout.WriteLineAsync($"{listed.ToString(CultureInfo.InvariantCulture)} flag(s)").ConfigureAwait(false);
        return ExitCode.Ok;
    }

    /// <summary>A flag's rationale: the private row for a committed flag, the event itself for a legacy one.</summary>
    private static Dictionary<string, string> RationalesByFlag(IReadOnlyList<AppendedEvent> log, IReadOnlyList<FlagDetail> details)
    {
        var rationales = details.ToDictionary(d => d.EventId, d => d.Rationale, StringComparer.Ordinal);

        foreach (var appended in log.Where(e => e.Event.Type.Value == FlagProjector.FlagRaisedType))
        {
            if (appended.Event.Payload is JsonValue.Object payload
                && payload.Members.FirstOrDefault(m => m.Key == FlagProjector.RationaleField).Value is JsonValue.String legacy)
                rationales.TryAdd(appended.Event.Id.Value, legacy.Value);
        }

        return rationales;
    }

    private static async Task<int> UsageErrorAsync(TextWriter stderr, string message)
    {
        await stderr.WriteLineAsync("error: " + message).ConfigureAwait(false);
        return ExitCode.Usage;
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
