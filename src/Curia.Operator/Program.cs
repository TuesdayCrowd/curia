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
using Curia.Domain.Keys;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;
using Curia.Domain.Serving;
using Curia.Infrastructure;
using Curia.Infrastructure.Lifecycle;

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

    /// <summary>
    /// R4.38 (add.): the lifecycle guard is on for this kind, so the verb stopped before it read a key or
    /// signed anything. Nothing was written.
    /// </summary>
    public const int LifecycleNotEnabled = 4;

    /// <summary>
    /// The Forum's store could not complete the verb (a fault, not a refusal): what the verb appended, if anything, is not known, and a verb that only
    /// reads and signs is safe to run again. Errata G67 (R12.18, Amendment 1 ruling 23).
    /// </summary>
    public const int StoreFault = 5;

    /// <summary>
    /// <c>sign-head --if-grown</c> withheld the head because a lifecycle row follows the latest head and this tool does not check its signature. Nothing was
    /// written. Errata G67 (Amendment 2 ruling 28).
    /// </summary>
    public const int HeadWithheld = 6;
}

/// <summary>
/// Loads the declaration key a verb signs a compromise declaration with from its PEM (errata G67, R6.62: it is never the log key). The delegate lives here and
/// not in <c>Curia.Application</c> because it is the operator tool's seam: a test hands one that records or throws.
/// </summary>
public delegate Result<ILifecycleStatementSigner> DeclarationSignerFactory(string pem);

/// <summary>
/// <c>curia-operator</c>: the Forum operator's out-of-band verbs. Four today: <c>attest-owner</c>,
/// R4.30's owner attestation (errata G5), because that is the one fact §4.6 places outside the
/// agent's reach and the one the Forum could not otherwise learn; <c>sign-head</c>, R6.49's signed
/// head (errata G9), because the Forum holds no log key (R11.7); and <c>moderate</c> and
/// <c>flags</c>, R10.36's human moderator (R10.59, errata G13) -- the record, and the review queue
/// it is decided from.
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

        // R4.38 (add.), lock 2: the same setting the Forum reads, as ASP.NET maps it from the
        // environment (Curia:Lifecycle:Enabled), so one exported variable governs both processes. A
        // refusal stops the tool before any verb runs.
        var gate = EmbeddedLifecycleAppendGate.FromEmbedded(System.Environment.GetEnvironmentVariable("Curia__Lifecycle__Enabled"));
        if (!gate.TryGetValue(out var lifecycleGate, out var gateError))
        {
            await Console.Error.WriteLineAsync(
                $"error: Curia__Lifecycle__Enabled is refused ({gateError!.Type}: {gateError.Title}; {gateError.Detail}). " +
                "The lifecycle guard is on until the owner lifts it (R4.38 (add.)); the setting is none until then.")
                .ConfigureAwait(false);
            return ExitCode.Environment;
        }

        return await OperatorCommands
            .RunAsync(
                args,
                connectionString,
                TimeProvider.System,
                lifecycleGate!,
                OperatorCommands.LoadDeclarationSigner,
                Console.Out,
                Console.Error,
                CancellationToken.None,
                readLogSigningKeyPem: () => System.Environment.GetEnvironmentVariable("CURIA_LOG_SIGNING_KEY_PEM"),
                readDeclarationSigningKeyPem: () => System.Environment.GetEnvironmentVariable("CURIA_DECLARATION_SIGNING_KEY_PEM"))
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

          curia-operator sign-head --by <operator-name> [--if-grown]

              Signs the Acta's current root with the log key (CURIA_LOG_SIGNING_KEY_PEM) and
              appends it to the log as a log.head entry, publishing the key (log.key) first
              if the log has not seen it. R6.24: run this on a schedule of at most 60 minutes.
              Refused, exit 3, in an environment that also holds the declaration key (R12.18).
              --if-grown (R6.63) signs only when an entry other than log.head and log.key
              follows the latest head this tool signed (the latest head that verifies under the
              key it holds; a head it cannot verify as its own moves nothing); otherwise it
              publishes no key, signs no head, appends nothing, prints "unchanged" and exits 0, so
              a timer does not grow the log by its own output. It also withholds the head, exit 6
              and nothing written, when a lifecycle row (a retirement, a compromise declaration or
              a key binding beyond an enrolment) follows that head: this tool does not check such
              a row's signature, and a head over a forged row cannot be taken back. A first run
              after a log-key rotation has no such head and measures from the start of the log. A person reads the rows named on stderr, and
              signs by hand without --if-grown (which prints "covers" for each row). Run a log-key
              rotation without --if-grown: it publishes a key on a log that may be quiet.

          curia-operator compromise --agent <agent-id> --kid <kid> --from-index <n>
                                    --reason owner-reported|operator-observed --operator <role>

              Declares <kid> of <agent-id> compromised from log index <n> (R6.26, R6.57), signed with
              the declaration key (CURIA_DECLARATION_SIGNING_KEY_PEM, which publish-declaration-key
              must have published first; refused, exit 3, in an environment that also holds the log
              key, R12.18). The declaration ends <kid> and every key it bound by a rotation at or after <n>, and marks
              the agent's posts under them from <n> disputed. <role> is a role name, never a person's
              name. Pass --from-index no later than the first index the owner's client reports as
              FOREIGN (their lifecycle_acts are the evidence); a later --from-index leaves live any key
              the compromised key bound before it (R6.57 cascades only at or after from_index).
              Refused with exit 4, before the declaration key is read, while the lifecycle guard is
              on: compromise must be enabled by Curia__Lifecycle__Enabled (R4.38 (add.)).

          curia-operator publish-declaration-key --by <role>

              Publishes the declaration key (CURIA_DECLARATION_SIGNING_KEY_PEM) to the log as a
              log.declaration-key entry (R6.62). Run it once, before the hosted instance is
              announced: the first declaration key published is the one, a second is refused, and a
              key that is also a log key is refused. It ends no agent's key, so the lifecycle guard
              does not govern it. Refused, exit 3, in an environment that also holds the log key.

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
              would change nothing. It acts only on the category it cites (R10.61): a restore in a
              category that holds nothing is refused, as is a dismissal in one that holds the post.
              The reason lands in a public leaf and is screened like a flag's: credential
              material is refused. A reason repeating a flag's raiser or rationale is refused;
              neither is ever published (R10.62).

          curia-operator flags [--post <post-id>] [--open] [--raisers]

              Lists flags and why they were raised -- the review queue, out of band. Each
              rationale is delimited and datamarked (R10.44), and control characters are escaped.
              --open lists only flags no record has adjudicated. --raisers adds who raised each;
              judging content needs no identity, so the listing leaves it out by default.

        ENVIRONMENT
          CURIA_EVENTS_POSTGRES      the Forum's events database (required)
          CURIA_LOG_SIGNING_KEY_PEM  the Acta's ES256 signing key (sign-head only; never in the same
                                     environment as the declaration key)
          CURIA_DECLARATION_SIGNING_KEY_PEM
                                     the declaration key (publish-declaration-key and compromise
                                     only; never in the same environment as the log key)
          Curia__Lifecycle__Enabled  none, or a comma list over rotation, retirement and compromise
                                     that this build's approval record approves; it is read before
                                     any verb runs, so a kind the record does not approve refuses
                                     every verb (exit 3), sign-head and publish-declaration-key
                                     among them

        EXIT CODES
          0  recorded
          1  usage error -- nothing was written
          2  refused -- the reason's slug is on stderr; nothing was written
          3  CURIA_EVENTS_POSTGRES is not set, a key is unusable, Curia__Lifecycle__Enabled names a
             kind this build does not approve, or the environment holds the key the verb does not
             run with (R12.18): the declaration key for sign-head, the log key for
             publish-declaration-key and compromise
          4  the lifecycle guard refuses this verb -- nothing was read, signed or written
          5  the Forum's store could not complete the verb (curia/store/unavailable) -- what it
             appended, if anything, is not known: read the log before running it again; sign-head
             re-reads and re-signs, so it is safe to run again
          6  sign-head --if-grown withheld the head: a lifecycle row follows the latest head this tool signed --
             nothing was written; stderr names the log indices and types
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

    /// <param name="lifecycleGate">
    /// R4.38 (add.): the gate every append this tool makes passes. Required, with no default;
    /// <see cref="Program.Main"/> builds it from <c>Curia__Lifecycle__Enabled</c>.
    /// </param>
    /// <param name="declarationSigner">
    /// Loads the declaration key a verb signs a declaration with from its PEM (R6.26 rev., R6.62). Required, with no
    /// default; <see cref="Program.Main"/> passes <see cref="LoadDeclarationSigner"/>.
    /// </param>
    /// <param name="readLogSigningKeyPem">
    /// <c>CURIA_LOG_SIGNING_KEY_PEM</c>: the Acta's signing key, needed by <c>sign-head</c> alone, and read only at the point the verb needs it. A reader rather
    /// than a value so a verb the guard refuses never calls it, and so the end-to-end suite can hand in a throwaway key; <see cref="Program.Main"/> is the one
    /// place that reads the variable. <c>publish-declaration-key</c> and <c>compromise</c> read it only to refuse an environment that holds it (R12.18).
    /// </param>
    /// <param name="readDeclarationSigningKeyPem">
    /// <c>CURIA_DECLARATION_SIGNING_KEY_PEM</c> (errata G67, R6.62): the key that signs compromise declarations, needed by <c>publish-declaration-key</c> and
    /// <c>compromise</c>. <c>sign-head</c> reads it only to refuse an environment that holds it (R12.18). Read by the same rule as the log key's.
    /// </param>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        string connectionString,
        TimeProvider clock,
        ILifecycleAppendGate lifecycleGate,
        DeclarationSignerFactory declarationSigner,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken,
        Func<string?>? readLogSigningKeyPem = null,
        Func<string?>? readDeclarationSigningKeyPem = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stderr);

        // Errata G67 (Amendment 1 ruling 23): the one place every verb passes through answers a fault of the Forum's store as a worded refusal and never as an
        // unhandled exception. The caller's own cancellation is not a fault.
        try
        {
            return await RunVerbAsync(args, connectionString, clock, lifecycleGate, declarationSigner, stdout, stderr, readLogSigningKeyPem, readDeclarationSigningKeyPem, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException && !cancellationToken.IsCancellationRequested
                                  && (e is LogUnreadableException || PostgresStoreFaults.Is(e)))
        {
            // The cause is the type name and, for a Postgres fault, its SQLSTATE (R11.71): never the driver's message, which can hold what it read. What the
            // verb appended before the fault is not known, so the refusal is not "nothing was written" and has a code of its own.
            var cause = e is LogUnreadableException unreadable ? unreadable.Cause : PostgresStoreFaults.Cause(e);
            await stderr.WriteLineAsync(
                $"error: {StoreUnavailableType}: the Forum's store could not complete this verb (cause: {TerminalText.Line(cause)}); " +
                "what it appended, if anything, is not known: read the log before running it again")
                .ConfigureAwait(false);
            return ExitCode.StoreFault;
        }
    }

    /// <summary>The refusal a store fault is answered with (R11.71's type, spelled as the Forum spells it).</summary>
    private const string StoreUnavailableType = "curia/store/unavailable";

    private static async Task<int> RunVerbAsync(
        IReadOnlyList<string> args,
        string connectionString,
        TimeProvider clock,
        ILifecycleAppendGate lifecycleGate,
        DeclarationSignerFactory declarationSigner,
        TextWriter stdout,
        TextWriter stderr,
        Func<string?>? readLogSigningKeyPem,
        Func<string?>? readDeclarationSigningKeyPem,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(lifecycleGate);
        ArgumentNullException.ThrowIfNull(declarationSigner);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        if (args.Count == 0 || args[0] is "--help" or "-h" or "help")
        {
            await stdout.WriteLineAsync(Usage).ConfigureAwait(false);
            return args.Count == 0 ? ExitCode.Usage : ExitCode.Ok;
        }

        if (args[0] == "sign-head")
            return await SignHeadAsync(args.Skip(1).ToArray(), connectionString, clock, lifecycleGate, stdout, stderr, readLogSigningKeyPem, readDeclarationSigningKeyPem, cancellationToken)
                .ConfigureAwait(false);

        if (args[0] == "publish-declaration-key")
            return await PublishDeclarationKeyAsync(args.Skip(1).ToArray(), connectionString, clock, lifecycleGate, stdout, stderr, readLogSigningKeyPem, readDeclarationSigningKeyPem, cancellationToken)
                .ConfigureAwait(false);

        if (args[0] == "compromise")
            return await CompromiseAsync(args.Skip(1).ToArray(), connectionString, clock, lifecycleGate, declarationSigner, stdout, stderr, readLogSigningKeyPem, readDeclarationSigningKeyPem, cancellationToken)
                .ConfigureAwait(false);

        if (args[0] == "moderate")
            return await ModerateAsync(args.Skip(1).ToArray(), connectionString, clock, lifecycleGate, stdout, stderr, cancellationToken)
                .ConfigureAwait(false);

        if (args[0] == "flags")
            return await FlagsAsync(args.Skip(1).ToArray(), connectionString, clock, lifecycleGate, stdout, stderr, cancellationToken)
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
        var adapters = new PostgresAdapters(connectionString, clock, lifecycleGate);
        await using (adapters.ConfigureAwait(false))
        {
            result = await new AttestOwner(adapters.EventStore, clock).RecordAsync(
                // R11.34: the parse outcome, refused by the use case where it refused an unknown agent (spec §4.7).
                AgentId.Parse(parsed.Agent, "agent"),
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
        ILifecycleAppendGate lifecycleGate,
        TextWriter stdout,
        TextWriter stderr,
        Func<string?>? readLogSigningKeyPem,
        Func<string?>? readDeclarationSigningKeyPem,
        CancellationToken cancellationToken)
    {
        string? byValue = null;
        var ifGrown = false;
        for (var i = 0; i < argv.Length; i++)
        {
            if (argv[i] == "--by" && i + 1 < argv.Length) { byValue = argv[++i]; continue; }

            // Errata G67 (R6.63): sign only when the log has grown past the latest head. Given once; a repeat is a usage error, as an unknown flag is.
            if (argv[i] == "--if-grown" && !ifGrown) { ifGrown = true; continue; }

            await stderr.WriteLineAsync($"error: unexpected argument '{argv[i]}'. sign-head takes --by <operator-name> and, once, --if-grown.").ConfigureAwait(false);
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

        // Errata G67, R12.18: before either key is parsed or used. The host that signs heads holds the log key and no declaration key.
        if (!string.IsNullOrWhiteSpace(readDeclarationSigningKeyPem?.Invoke()))
            return await SharedEnvironmentAsync(stderr, "sign-head", "CURIA_DECLARATION_SIGNING_KEY_PEM", "the declaration key", DeclarationKeyHeldWhy).ConfigureAwait(false);

        var logSigningKeyPem = readLogSigningKeyPem?.Invoke();
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
            var adapters = new PostgresAdapters(connectionString, clock, lifecycleGate);
            await using (adapters.ConfigureAwait(false))
            {
                var store = adapters.EventStore;
                var ids = new UlidGenerator(clock);

                var folded = await FoldAsync(store, cancellationToken).ConfigureAwait(false);
                if (!folded.TryGetValue(out var acta, out var foldError))
                    return await RefuseAsync(stderr, foldError!).ConfigureAwait(false);

                // The anchor is the latest head this tool can verify as its own, under the key it holds and no key the log lists; the growth test, the stop and the covers lines read it and
                // nothing else (Amendment 3 ruling 34). `log.key` is unsigned, so a head by shape is not one.
                var signer = SignerOf(key);
                var measured = Measure(acta!, signer);

                if (ifGrown)
                {
                    // R6.63: the growth test runs before the key is published, so that a first run on a quiet log publishes no key it never uses.
                    if (!measured.Grown)
                    {
                        var latest = measured.Anchor is { } head0 ? $"latest_head_log_index={head0.LogIndex.ToString(CultureInfo.InvariantCulture)}" : "latest_head none";
                        await stdout.WriteLineAsync($"unchanged  tree_size={acta!.TreeSize.ToString(CultureInfo.InvariantCulture)}  {latest}").ConfigureAwait(false);
                        return ExitCode.Ok;
                    }

                    // The stop: this tool does not check a lifecycle row's signature, and a head signed over a forged row cannot be taken back. Nothing is written.
                    if (!measured.Tail.IsEmpty)
                        return await WithheldAsync(stderr, measured.Tail).ConfigureAwait(false);
                }

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

                    // The race (Amendment 3 ruling 40): a row may have been appended between the two folds, and the head is built from this one, so the stop is applied to what this one
                    // holds. No head was signed; the one log.key just published stays (it is not a head and not growth, and the next run finds it published).
                    measured = Measure(acta!, signer);
                    if (ifGrown && !measured.Tail.IsEmpty)
                        return await WithheldAsync(stderr, measured.Tail).ConfigureAwait(false);
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

                // The resume path (Amendment 2 ruling 28 (c)): a head signed by hand over lifecycle rows says which, so that the run leaves a record.
                foreach (var covered in measured.Tail)
                    await stdout.WriteLineAsync($"covers  log_index={covered.LogIndex.ToString(CultureInfo.InvariantCulture)}  type={TerminalText.Line(covered.Type.Value)}").ConfigureAwait(false);

                return ExitCode.Ok;
            }
        }
    }

    /// <summary>What one fold says about where the signer stands: the anchor, whether the log grew past it, and the lifecycle rows it does not cover (Amendment 3 ruling 40).</summary>
    private readonly record struct HeadMeasure(SignedHead? Anchor, bool Grown, ImmutableArray<LifecycleRow> Tail);

    /// <summary>The three answers from one fold, so that the stop and the head built from that fold cannot disagree about which rows the head covers.</summary>
    private static HeadMeasure Measure(ActaLog acta, Func<SignedHead, bool> signedByTheSigner)
    {
        var anchor = acta.LatestHeadWhere(signedByTheSigner);
        return new HeadMeasure(anchor, acta.GrownPast(anchor), acta.LifecycleRowsPast(anchor));
    }

    /// <summary>
    /// Whether a head is this tool's own: it names the <c>kid</c> of the key the tool holds and verifies, over its signed document's canonical form and under a head's header
    /// <c>typ</c>, under that key's public half derived from the PEM the tool loaded (Amendment 3 ruling 34: no key the log lists is asked).
    /// </summary>
    private static Func<SignedHead, bool> SignerOf(LogSigningKey key)
    {
        if (!key.PublicMaterial().TryGetValue(out var material, out _))
            return _ => false;

        var es256 = new Es256Adapter();
        var jws = new DetachedJws(
            new Dictionary<string, IContentSigner>(StringComparer.Ordinal),
            new Dictionary<string, IContentVerifier>(StringComparer.Ordinal) { ["ES256"] = es256 },
            DetachedJws.HeadTyp);
        return head => string.Equals(head.Kid, key.Kid, StringComparison.Ordinal)
            && LogEntries.HeadCanonical(head.Document).TryGetValue(out var canonical, out _)
            && jws.Verify(canonical, new JwsSignature(head.Signature), material!).Match(_ => true, _ => false);
    }

    /// <summary>How many withheld rows the refusal lists before it says how many more there are.</summary>
    private const int WithheldListed = 20;

    /// <summary>
    /// Errata G67, Amendment 2 ruling 28: <c>sign-head --if-grown</c> found a lifecycle row the latest head does not cover and signed nothing. Stdout is empty;
    /// stderr says why and lists the log indices and types (never a payload member), up to <see cref="WithheldListed"/>, then how many more. Exit 6.
    /// </summary>
    private static async Task<int> WithheldAsync(TextWriter stderr, ImmutableArray<LifecycleRow> rows)
    {
        await stderr.WriteLineAsync(
            $"error: curia/head/withheld: {rows.Length.ToString(CultureInfo.InvariantCulture)} lifecycle row(s) follow the latest head and this tool does not check their signatures, so no head was signed. " +
            "A head over a forged row cannot be taken back: the log is append-only and clients retain the heads they have seen, so a restore to before the row forks the log for any client that retained a later head. " +
            "Read the rows, and restore to before them if they are not genuine. " +
            "If they are genuine, sign by hand with 'sign-head --by <name>' (without --if-grown); that head covers them.")
            .ConfigureAwait(false);
        foreach (var row in rows.Take(WithheldListed))
            await stderr.WriteLineAsync($"withheld  log_index={row.LogIndex.ToString(CultureInfo.InvariantCulture)}  type={TerminalText.Line(row.Type.Value)}").ConfigureAwait(false);

        if (rows.Length > WithheldListed)
            await stderr.WriteLineAsync($"and {(rows.Length - WithheldListed).ToString(CultureInfo.InvariantCulture)} more").ConfigureAwait(false);

        return ExitCode.HeadWithheld;
    }

    /// <summary>
    /// Errata G67, R6.62: the operator publishes the declaration key (<c>log.declaration-key</c>), out of band. Not a lifecycle act, so the verb does not ask
    /// the gate (Amendment 2 ruling 29); it refuses a key that is a log key and a second key (the first published wins), and an environment that holds the
    /// log key (R12.18).
    /// </summary>
    private static async Task<int> PublishDeclarationKeyAsync(
        string[] argv,
        string connectionString,
        TimeProvider clock,
        ILifecycleAppendGate lifecycleGate,
        TextWriter stdout,
        TextWriter stderr,
        Func<string?>? readLogSigningKeyPem,
        Func<string?>? readDeclarationSigningKeyPem,
        CancellationToken cancellationToken)
    {
        string? byValue = null;
        for (var i = 0; i < argv.Length; i++)
        {
            if (argv[i] == "--by" && i + 1 < argv.Length) { byValue = argv[++i]; continue; }
            return await UsageErrorAsync(stderr, $"unexpected argument '{argv[i]}'. publish-declaration-key takes --by <role>.").ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(byValue))
            return await UsageErrorAsync(stderr, "--by <role> is required.").ConfigureAwait(false);

        var actorValue = byValue.StartsWith(ApplyModeration.OperatorPrefix, StringComparison.Ordinal) ? byValue : ApplyModeration.OperatorPrefix + byValue;
        if (!ActorId.Create(actorValue).TryGetValue(out var actor, out _))
            return await UsageErrorAsync(stderr, "--by <role> is required.").ConfigureAwait(false);

        // Errata G67, R12.18: before either key is parsed or used. A declaration key is never published or used in an environment that holds the log key.
        if (!string.IsNullOrWhiteSpace(readLogSigningKeyPem?.Invoke()))
            return await SharedEnvironmentAsync(stderr, "publish-declaration-key", "CURIA_LOG_SIGNING_KEY_PEM", "the log key", LogKeyHeldWhy).ConfigureAwait(false);

        var pem = readDeclarationSigningKeyPem?.Invoke();
        if (string.IsNullOrWhiteSpace(pem))
        {
            await stderr.WriteLineAsync("error: CURIA_DECLARATION_SIGNING_KEY_PEM is not set.").ConfigureAwait(false);
            return ExitCode.Environment;
        }

        using var key = LoadDeclarationKey(pem, out var keyProblem);
        if (key is null)
        {
            await stderr.WriteLineAsync("error: CURIA_DECLARATION_SIGNING_KEY_PEM is not a usable P-256 key: " + keyProblem).ConfigureAwait(false);
            return ExitCode.Environment;
        }

        {
            Result<long> result;
            var adapters = new PostgresAdapters(connectionString, clock, lifecycleGate);
            await using (adapters.ConfigureAwait(false))
            {
                result = await new PublishDeclarationKey(adapters.EventStore, clock).PublishAsync(key, actor!, cancellationToken).ConfigureAwait(false);
            }

            if (!result.TryGetValue(out var logIndex, out var error))
                return await RefuseAsync(stderr, error!).ConfigureAwait(false);

            await stdout.WriteLineAsync($"published declaration key  {key.Kid}").ConfigureAwait(false);
            await stdout.WriteLineAsync($"log_index                  {logIndex.ToString(CultureInfo.InvariantCulture)}").ConfigureAwait(false);
            return ExitCode.Ok;
        }
    }

    /// <summary>The production <see cref="DeclarationSignerFactory"/>: the PEM loaded as <see cref="DeclarationSigningKey"/>.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The key is handed to the caller, which disposes it (CompromiseAsync's using).")]
    public static Result<ILifecycleStatementSigner> LoadDeclarationSigner(string pem)
    {
        var key = LoadDeclarationKey(pem, out var problem);
        return key is null
            ? Result<ILifecycleStatementSigner>.Fail(new Error("curia/keys/declaration-key-unusable", "The declaration signing key is not a usable P-256 key", problem))
            : Result<ILifecycleStatementSigner>.Ok(key);
    }

    /// <summary>
    /// R6.26 (rev.), R6.57: the operator's compromise declaration. <b>The gate is the first act after the
    /// arguments parse</b> (R4.38 add., GF2): a guarded build exits <see cref="ExitCode.LifecycleNotEnabled"/>
    /// before the declaration key is read, the signer is built, or anything is signed or appended. The declaration is signed by the declaration key
    /// (errata G67, R6.62), in an environment that holds no log key (R12.18).
    /// </summary>
    private static async Task<int> CompromiseAsync(
        string[] argv,
        string connectionString,
        TimeProvider clock,
        ILifecycleAppendGate lifecycleGate,
        DeclarationSignerFactory declarationSigner,
        TextWriter stdout,
        TextWriter stderr,
        Func<string?>? readLogSigningKeyPem,
        Func<string?>? readDeclarationSigningKeyPem,
        CancellationToken cancellationToken)
    {
        if (argv.Any(a => a is "--help" or "-h"))
        {
            await stdout.WriteLineAsync(Usage).ConfigureAwait(false);
            return ExitCode.Ok;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < argv.Length; i++)
        {
            var arg = argv[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
                return await UsageErrorAsync(stderr, $"unexpected argument '{arg}'.").ConfigureAwait(false);

            var name = arg[2..];
            if (name is not ("agent" or "kid" or "from-index" or "reason" or "operator"))
                return await UsageErrorAsync(stderr, $"unknown flag --{name}.").ConfigureAwait(false);

            if (i + 1 >= argv.Length)
                return await UsageErrorAsync(stderr, $"--{name} needs a value.").ConfigureAwait(false);

            values[name] = argv[++i];
        }

        foreach (var required in (string[])["agent", "kid", "from-index", "reason", "operator"])
            if (!values.TryGetValue(required, out var given) || string.IsNullOrWhiteSpace(given))
                return await UsageErrorAsync(stderr, $"--{required} is required.").ConfigureAwait(false);

        if (!long.TryParse(values["from-index"], NumberStyles.None, CultureInfo.InvariantCulture, out var fromIndex))
            return await UsageErrorAsync(stderr, "--from-index is a log index: a non-negative integer.").ConfigureAwait(false);
        if (!CompromiseReasons.Parse(values["reason"]).TryGetValue(out var reason, out _))
            return await UsageErrorAsync(stderr, "--reason is owner-reported or operator-observed.").ConfigureAwait(false);
        if (!AgentId.Parse(values["agent"], "agent").TryGetValue(out var agent, out var agentError))
            return await UsageErrorAsync(stderr, agentError!.Detail ?? agentError.Title).ConfigureAwait(false);
        if (!Kid.Parse(values["kid"], "kid").TryGetValue(out var kid, out var kidError))
            return await UsageErrorAsync(stderr, kidError!.Detail ?? kidError.Title).ConfigureAwait(false);

        // The operator namespace, applied once here as the other verbs apply it. A role name, never a person's.
        var by = values["operator"];
        var actorValue = by.StartsWith(ApplyModeration.OperatorPrefix, StringComparison.Ordinal) ? by : ApplyModeration.OperatorPrefix + by;
        if (!ActorId.Create(actorValue).TryGetValue(out var actor, out _))
            return await UsageErrorAsync(stderr, "--operator <role> is required.").ConfigureAwait(false);

        // R4.38 (add.): the gate, before the log key is read or anything is signed.
        if (!lifecycleGate.Enables(LifecycleKind.Compromise))
        {
            await stderr.WriteLineAsync(
                $"error: {KeyActErrors.LifecycleNotEnabledType}: compromise declarations are not enabled. " +
                "The lifecycle guard is on until the owner lifts it (R4.38 (add.)); to enable compromise, " +
                "set Curia__Lifecycle__Enabled to include it, for this tool and for the Forum.")
                .ConfigureAwait(false);
            return ExitCode.LifecycleNotEnabled;
        }

        // Errata G67, R12.18: after the gate, before either key is parsed or used.
        if (!string.IsNullOrWhiteSpace(readLogSigningKeyPem?.Invoke()))
            return await SharedEnvironmentAsync(stderr, "compromise", "CURIA_LOG_SIGNING_KEY_PEM", "the log key", LogKeyHeldWhy).ConfigureAwait(false);

        var pem = readDeclarationSigningKeyPem?.Invoke();
        if (string.IsNullOrWhiteSpace(pem))
        {
            await stderr.WriteLineAsync(
                "error: CURIA_DECLARATION_SIGNING_KEY_PEM is not set. Declarations are signed with the declaration key, which no unattended process " +
                "holds and the Forum does not hold (R6.62, R12.18); this tool needs it and does not sign without it.")
                .ConfigureAwait(false);
            return ExitCode.Environment;
        }

        if (!declarationSigner(pem).TryGetValue(out var signer, out var signerError))
        {
            await stderr.WriteLineAsync($"error: CURIA_DECLARATION_SIGNING_KEY_PEM is not a usable P-256 key: {signerError!.Detail}").ConfigureAwait(false);
            return ExitCode.Environment;
        }

        using var disposable = signer as IDisposable;
        Result<CompromiseRecorded> result;
        var adapters = new PostgresAdapters(connectionString, clock, lifecycleGate);
        await using (adapters.ConfigureAwait(false))
        {
            result = await new DeclareCompromise(adapters.EventStore, clock)
                .DeclareAsync(agent!, kid!, fromIndex, reason, signer!, actor, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!result.TryGetValue(out var recorded, out var error))
            return await RefuseAsync(stderr, error!).ConfigureAwait(false);

        await stdout.WriteLineAsync($"compromised  {TerminalText.Line(recorded!.AgentId)}").ConfigureAwait(false);
        foreach (var ended in recorded.Kids)
            await stdout.WriteLineAsync($"kid          {TerminalText.Line(ended)}").ConfigureAwait(false);
        await stdout.WriteLineAsync($"from_index   {fromIndex.ToString(CultureInfo.InvariantCulture)}").ConfigureAwait(false);
        await stdout.WriteLineAsync($"log_index    {recorded.LogIndex.ToString(CultureInfo.InvariantCulture)}").ConfigureAwait(false);
        await stdout.WriteLineAsync($"by           {TerminalText.Line(actor.Value)}").ConfigureAwait(false);
        return ExitCode.Ok;
    }

    /// <summary>
    /// R10.59: an operator's moderation record, appended out of band through
    /// <see cref="ApplyModeration"/> — the only producer of <c>moderation.applied</c>.
    /// </summary>
    private static async Task<int> ModerateAsync(
        string[] argv,
        string connectionString,
        TimeProvider clock,
        ILifecycleAppendGate lifecycleGate,
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

        // R10.68 and R11.34 (errata G18): the post and the reason reach ApplyModeration as the types it
        // takes, parsed at their first use. Each parse outcome is passed rather than refused here, so
        // the use case answers every refusal where it answered it before the types existed: after the
        // operator checks, which are this path's authorization.
        Result<ModerationRecorded> result;
        var adapters = new PostgresAdapters(connectionString, clock, lifecycleGate);
        await using (adapters.ConfigureAwait(false))
        {
            result = await new ApplyModeration(adapters.EventStore, adapters.FlagDetails, clock)
                .RecordAsync(PostId.Parse(values["post"], "post_id"), effect, category, Rationale.Parse(values["reason"]), actor, cancellationToken)
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
    /// The review queue, out of band: every flag with its rationale, which only an operator ever sees
    /// (R10.44's <c>moderation</c>|<c>list</c> view for the human arm). Rationales are delimited and
    /// datamarked, because the reader may be a model, and every agent-written string is made
    /// terminal-safe first. The raiser is listed only under <c>--raisers</c>: judging content needs
    /// no identity, and a reviewer shown none cannot repeat one into a public reason (R10.62).
    /// </summary>
    private static async Task<int> FlagsAsync(
        string[] argv,
        string connectionString,
        TimeProvider clock,
        ILifecycleAppendGate lifecycleGate,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        string? post = null;
        var openOnly = false;
        var raisers = false;
        for (var i = 0; i < argv.Length; i++)
        {
            if (argv[i] == "--open") { openOnly = true; continue; }
            if (argv[i] == "--raisers") { raisers = true; continue; }
            if (argv[i] == "--post" && i + 1 < argv.Length) { post = argv[++i]; continue; }
            return await UsageErrorAsync(stderr, $"unexpected argument '{argv[i]}'. flags takes [--post <post-id>] [--open] [--raisers].").ConfigureAwait(false);
        }

        IReadOnlyList<AppendedEvent> log;
        IReadOnlyList<FlagDetail> details;
        var adapters = new PostgresAdapters(connectionString, clock, lifecycleGate);
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
        var rationales = FlagDirectory.RationalesByFlag(log, details);

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
            if (raisers)
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

    /// <summary>
    /// Errata G67, R12.18: the refusal of an environment that holds the key the verb does not run with. It names the variable to unset and the verb, never a value, and is made before
    /// either key is parsed or used (two keys in one environment are one key to whatever reads that environment); the other side's variable is tested for emptiness and held by nothing after.
    /// Nothing was written.
    /// </summary>
    private static async Task<int> SharedEnvironmentAsync(TextWriter stderr, string verb, string variable, string held, string why)
    {
        await stderr.WriteLineAsync(
            $"error: {variable} is set in this environment, and {verb} does not run in an environment that holds {held} (R12.18): {why} " +
            $"Unset {variable} and run {verb} again. No key was parsed or used, and nothing was signed or written.")
            .ConfigureAwait(false);
        return ExitCode.Environment;
    }

    private const string DeclarationKeyHeldWhy = "the key that signs heads is held by an unattended process, and the declaration key is held where none can read it.";

    private const string LogKeyHeldWhy = "the log key is the one an unattended process holds, and a declaration key is published and used only where none can read it.";

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

    private static DeclarationSigningKey? LoadDeclarationKey(string pem, out string? problem)
    {
        try
        {
            problem = null;
            return DeclarationSigningKey.FromPem(pem);
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

        // A blank value is a missing one, as moderate treats it: " " would otherwise reach the use
        // case's argument guard (--agent), a public leaf naming no one (--by), or the use case's
        // refusal rather than the verb's (--reason).
        if (!values.TryGetValue("agent", out var agent) || string.IsNullOrWhiteSpace(agent))
            return Parsed.Fail("--agent <agent-id> is required.");

        if (!values.TryGetValue("owner", out var ownerValue)
            || !OwnerId.Create(ownerValue).TryGetValue(out var owner, out _))
            return Parsed.Fail("--owner <owner-id> is required.");

        if (!values.TryGetValue("by", out var byValue) || string.IsNullOrWhiteSpace(byValue))
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

        // --reason is optional, and omitting it records the default; given, it may not be blank.
        var hasReason = values.TryGetValue("reason", out var reasonValue);
        if (hasReason && string.IsNullOrWhiteSpace(reasonValue))
            return Parsed.Fail("--reason <text> may not be blank; omit it for the default.");

        var reason = hasReason
            ? reasonValue!
            : $"Owner attested by {actorValue} via {OwnerVerificationMethods.Wire(method)}";

        return new Parsed(agent, owner, by, !unverified, method, reason, null);
    }
}
