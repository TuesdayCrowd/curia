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

    /// <summary>
    /// <c>lifecycle-audit</c> found at least one lifecycle or operator-act row the Forum does not honour (R6.64, R6.67); or <c>attest-owner</c>, <c>moderate</c> or <c>compromise</c> appended a row
    /// the Forum then skips (R11.97). Nothing was written by the audit; it only reads. Errata G68 (ruling 13), G70.
    /// </summary>
    public const int LifecycleRowsSkipped = 7;

    /// <summary>
    /// R6.69: a stored leaf hash is not the leaf of its row. <c>sign-head</c> signed nothing and appended nothing; <c>lifecycle-audit</c> names the rows. The same number as
    /// <see cref="LifecycleRowsSkipped"/>: both are a finding in the log that a person reads before anything signs over it.
    /// </summary>
    public const int StoredLeafMismatch = 7;
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
                readDeclarationSigningKeyPem: () => System.Environment.GetEnvironmentVariable("CURIA_DECLARATION_SIGNING_KEY_PEM"),
                readOperatorActsSigningKeyPem: () => System.Environment.GetEnvironmentVariable("CURIA_OPERATOR_ACTS_SIGNING_KEY_PEM"))
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
              Refused, exit 3, in an environment that also holds the declaration key or the
              operator-acts key (R12.18, R11.97), and, exit 2, a log key that is a published
              operator key (curia/keys/log-key-is-operator-key).
              --if-grown (R6.63) signs only when an entry other than log.head and log.key
              follows the latest head this tool signed (the latest head that verifies under the
              key it holds; a head it cannot verify as its own moves nothing); otherwise it
              publishes no key, signs no head, appends nothing, prints "unchanged" and exits 0, so
              a timer does not grow the log by its own output. It also withholds the head, exit 6
              and nothing written, when a lifecycle row or an operator-act row the Forum does not
              honour (R6.64, R6.67: one that verifies under no published key) follows that head,
              because a head over such a row cannot be taken back; a row the Forum honours does
              not stop it. Each withheld row is
              named on stderr with its log index, its type and the predicate that failed. A first
              run after a log-key rotation has no such head and measures from the start of the log.
              A person reads the rows, and signs by hand without --if-grown (which prints "covers"
              for each row). Run a log-key rotation without --if-grown: it publishes a key on a log
              that may be quiet.

          curia-operator lifecycle-audit

              Reads the whole log and verifies every identity's lifecycle rows by R6.64 and every
              operator-act row (owner attestations and moderation records) by R6.67, the procedures
              the Forum decides by. It reads no key and writes nothing. Prints
              "honoured <h>  skipped <n>", over both kinds of row; with n above 0 it prints one line per skipped row,
              "skipped  log_index=<i>  type=<t>  aggregate=<id>  predicate=<slug>", the first 100
              and then "and <n> more", never a payload member, and exits 7. Run it before a
              plain sign-head covers a row, and after a restore. It cannot see a row planted at a
              position the application role's grant refuses (R11.6 (add.)): that is the start-up
              check's.

          curia-operator backfill-leaves

              Fills events.leaf_hash (db/0007, R6.69) for rows appended before that column existed,
              each from its own row by the leaf computation of R6.46. Run it with the DATABASE
              OWNER's connection string in CURIA_EVENTS_POSTGRES: the Forum's role holds no UPDATE
              (R11.6) and is refused, exit 2, before a row is read. It fills a row only where the
              column is empty and never replaces a leaf that is there (a wrong one is the audit's
              finding, not this verb's to repair). Idempotent; safe beside a running Forum. Prints
              "filled <n>  already_held <m>". sign-head refuses, exit 7, a log in which a stored
              leaf is not the leaf of its row; lifecycle-audit reports it (and the rows with none).

          curia-operator compromise --agent <agent-id> --kid <kid> --from-index <n>
                                    --reason owner-reported|operator-observed --operator <role>

              Declares <kid> of <agent-id> compromised from log index <n> (R6.26, R6.57), signed with
              the declaration key (CURIA_DECLARATION_SIGNING_KEY_PEM, which publish-declaration-key
              must have published first; refused, exit 3, in an environment that also holds the log
              key or the operator-acts key, R12.18). The declaration ends <kid> and every key it bound by a rotation at or after <n>, and marks
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
              does not govern it. Refused, exit 3, in an environment that also holds the log key
              or the operator-acts key.

          curia-operator publish-operator-key --by <role>

              Publishes the operator-acts key (CURIA_OPERATOR_ACTS_SIGNING_KEY_PEM) to the log as a
              log.operator-key entry (R6.66). Run it once, before the hosted instance is announced:
              the first operator key published is the one, a second is refused, and a key that is
              also a log key or a declaration key is refused. Refused, exit 3, in an environment
              that also holds the log key or the declaration key (R12.18).

          curia-operator attest-owner --agent <agent-id> --owner <owner-id> --by <operator-name>
                                      [--method domain|email|attestation|manual] [--reason <text>]
                                      [--unverified]

              Records that <operator-name> attests <agent-id>'s owner <owner-id> as verified
              (R4.30), naming which of R4.24's proofs was satisfied (default: manual). --unverified
              records a lapse instead. The agent must already be enrolled; an agent's owner is
              bound once (R4.1), so a later attestation naming a different owner is refused.
              The statement is signed with the operator-acts key (CURIA_OPERATOR_ACTS_SIGNING_KEY_PEM,
              which publish-operator-key must have published first: refused, exit 2,
              curia/keys/operator-key-not-published, and nothing written) and the log is re-read:
              a row the Forum does not honour is reported with its log index and the predicate,
              no success line is written, and the exit is 7 (curia/acts/not-honoured). Refused,
              exit 3, in an environment that also holds the log key or the declaration key.

          curia-operator moderate --post <post-id> --category <kind> --effect <effect>
                                  --reason <text> --by <operator-name>

              Records R10.36's human moderator acting on <post-id> (R10.59): <effect> is one of
              withhold, quarantine, restore, dismiss; <kind> is one of R10.35's seven. The record
              names every flag of that kind raised against the post (R10.60) and is refused if it
              would change nothing. It acts only on the category it cites (R10.61): a restore in a
              category that holds nothing is refused, as is a dismissal in one that holds the post.
              The reason lands in a public leaf and is screened like a flag's: credential
              material is refused. A reason repeating a flag's raiser or rationale is refused;
              neither is ever published (R10.62). Signed, re-read and refused as attest-owner is
              (the operator-acts key, exit 2 for an unpublished key, exit 7 for a row the Forum
              does not honour, exit 3 in an environment that also holds the log key or the
              declaration key).

          curia-operator flags [--post <post-id>] [--open] [--raisers]

              Lists flags and why they were raised -- the review queue, out of band. Each
              rationale is delimited and datamarked (R10.44), and control characters are escaped.
              --open lists only flags no record has adjudicated. --raisers adds who raised each;
              judging content needs no identity, so the listing leaves it out by default.

        ENVIRONMENT
          CURIA_EVENTS_POSTGRES      the Forum's events database (required)
          CURIA_LOG_SIGNING_KEY_PEM  the Acta's ES256 signing key (sign-head only; never in the same
                                     environment as the declaration key or the operator-acts key)
          CURIA_DECLARATION_SIGNING_KEY_PEM
                                     the declaration key (publish-declaration-key and compromise
                                     only; never in the same environment as the log key or the
                                     operator-acts key)
          CURIA_OPERATOR_ACTS_SIGNING_KEY_PEM
                                     the operator-acts key (publish-operator-key, attest-owner and
                                     moderate only; never in the same environment as the log key
                                     or the declaration key, R12.18)
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
             run with (R12.18): the declaration key and the operator-acts key for sign-head, the
             log key and the operator-acts key for publish-declaration-key and compromise, the log
             key and the declaration key for publish-operator-key, attest-owner and moderate
          4  the lifecycle guard refuses this verb -- nothing was read, signed or written
          5  the Forum's store could not complete the verb (curia/store/unavailable) -- what it
             appended, if anything, is not known: read the log before running it again; sign-head
             re-reads and re-signs, so it is safe to run again
          6  sign-head --if-grown withheld the head: a lifecycle or operator-act row the Forum does not honour follows the
             latest head this tool signed -- nothing was written; stderr names the log indices, types
             and predicates
          7  sign-head found a stored leaf hash that is not the leaf of its row (R6.69) -- nothing was
             written; stderr names the log indices. Or lifecycle-audit found a lifecycle or operator-act
             row the Forum does not honour, or such a stored leaf --
             nothing was written; stdout names the rows; compromise appended a declaration, and
             attest-owner or moderate appended an act, that the Forum then skips -- the row is in
             the log, stderr names its index and the predicate, nothing is marked or changed
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
    /// <param name="readOperatorActsSigningKeyPem">
    /// <c>CURIA_OPERATOR_ACTS_SIGNING_KEY_PEM</c> (errata G70, R4.43, R6.66): the key that signs the operator's acts, needed by <c>publish-operator-key</c>, <c>attest-owner</c> and <c>moderate</c>; the
    /// verbs that read the log key or the declaration key read it only to refuse an environment that holds it with either (R11.97, R12.18). Read by the same rule as the others, and last and optional
    /// so that no existing caller changes.
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
        Func<string?>? readDeclarationSigningKeyPem = null,
        Func<string?>? readOperatorActsSigningKeyPem = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stderr);

        // Errata G67 (Amendment 1 ruling 23): the one place every verb passes through answers a fault of the Forum's store as a worded refusal and never as an
        // unhandled exception. The caller's own cancellation is not a fault.
        try
        {
            return await RunVerbAsync(args, connectionString, clock, lifecycleGate, declarationSigner, stdout, stderr, readLogSigningKeyPem, readDeclarationSigningKeyPem, readOperatorActsSigningKeyPem, cancellationToken)
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
        Func<string?>? readOperatorActsSigningKeyPem,
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
            return await SignHeadAsync(args.Skip(1).ToArray(), connectionString, clock, lifecycleGate, stdout, stderr, readLogSigningKeyPem, readDeclarationSigningKeyPem, readOperatorActsSigningKeyPem, cancellationToken)
                .ConfigureAwait(false);

        if (args[0] == "publish-declaration-key")
            return await PublishDeclarationKeyAsync(args.Skip(1).ToArray(), connectionString, clock, lifecycleGate, stdout, stderr, readLogSigningKeyPem, readDeclarationSigningKeyPem, readOperatorActsSigningKeyPem, cancellationToken)
                .ConfigureAwait(false);

        if (args[0] == "compromise")
            return await CompromiseAsync(args.Skip(1).ToArray(), connectionString, clock, lifecycleGate, declarationSigner, stdout, stderr, readLogSigningKeyPem, readDeclarationSigningKeyPem, readOperatorActsSigningKeyPem, cancellationToken)
                .ConfigureAwait(false);

        if (args[0] == "publish-operator-key")
            return await PublishOperatorKeyAsync(args.Skip(1).ToArray(), connectionString, clock, lifecycleGate, stdout, stderr, readLogSigningKeyPem, readDeclarationSigningKeyPem, readOperatorActsSigningKeyPem, cancellationToken)
                .ConfigureAwait(false);

        if (args[0] == "moderate")
            return await ModerateAsync(args.Skip(1).ToArray(), connectionString, clock, lifecycleGate, stdout, stderr, readLogSigningKeyPem, readDeclarationSigningKeyPem, readOperatorActsSigningKeyPem, cancellationToken)
                .ConfigureAwait(false);

        if (args[0] == "lifecycle-audit")
            return await LifecycleAuditAsync(args.Skip(1).ToArray(), connectionString, clock, lifecycleGate, stdout, stderr, cancellationToken)
                .ConfigureAwait(false);

        if (args[0] == "backfill-leaves")
            return await BackfillLeavesAsync(args.Skip(1).ToArray(), connectionString, stdout, stderr, cancellationToken)
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

        // Errata G70, R11.97, R12.18: before the operator-acts key is parsed or used. It is never held with the log key or the declaration key.
        if (await HeldWithAnotherKeyAsync(stderr, "attest-owner", readLogSigningKeyPem, readDeclarationSigningKeyPem).ConfigureAwait(false) is { } sharedExit)
            return sharedExit;

        using var actsKey = await LoadOperatorActsKeyAsync(stderr, readOperatorActsSigningKeyPem).ConfigureAwait(false);
        if (actsKey is null)
            return ExitCode.Environment;

        Result<OwnerAttestation> result;
        var adapters = new PostgresAdapters(connectionString, clock, lifecycleGate);
        await using (adapters.ConfigureAwait(false))
        {
            result = await new AttestOwner(adapters.EventStore, Verifier, clock).RecordAsync(
                // R11.34: the parse outcome, refused by the use case where it refused an unknown agent (spec §4.7).
                AgentId.Parse(parsed.Agent, "agent"),
                parsed.Owner,
                parsed.Verified,
                parsed.Method,
                parsed.Reason!,
                parsed.By,
                actsKey,
                cancellationToken).ConfigureAwait(false);
        }

        // R11.97: a row the Forum then skips is exit 7 with the row's index and predicate, and no success line.
        if (!result.TryGetValue(out var attestation, out var error))
            return await RefuseAsync(stderr, error!).ConfigureAwait(false);

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
        Func<string?>? readOperatorActsSigningKeyPem,
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
        if (!string.IsNullOrWhiteSpace(readOperatorActsSigningKeyPem?.Invoke()))
            return await SharedEnvironmentAsync(stderr, "sign-head", "CURIA_OPERATOR_ACTS_SIGNING_KEY_PEM", "the operator-acts key", OperatorActsKeyHeldWithLogKeyWhy).ConfigureAwait(false);

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

                // R6.69: before anything is published or signed. A stored leaf that is not its row's means the Forum is serving proofs a client cannot verify; a head signed now would be over the
                // rows' own leaves and would be right, and that is the reason to stop and read, not to sign past it.
                if (await StoredLeavesMismatchAsync(acta!, stderr).ConfigureAwait(false))
                    return ExitCode.StoredLeafMismatch;

                // R11.97: a log key is never an operator key, by kid or by public key. Before the log.key append and the head: a key that signs heads and acts is one key to whatever holds it.
                if (string.Equals(PublishedKeys.OfLog(acta!.Events).FirstPublishedClass(key.Kid, key.PublicJwk()), LogEntries.OperatorKeyType, StringComparison.Ordinal))
                    return await RefuseAsync(stderr, KeyActErrors.LogKeyIsOperatorKey()).ConfigureAwait(false);

                // The anchor is the latest head this tool can verify as its own, under the key it holds and no key the log lists; the growth test, the stop and the covers lines read it and
                // nothing else (Amendment 3 ruling 34). `log.key` is unsigned, so a head by shape is not one.
                var signer = SignerOf(key);
                var measured = Measure(acta!, Verifier, signer);

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
                        store, ids, LogEntries.KeysAggregate, acta.Events.Count(e => string.Equals(e.AggregateId.Value, LogEntries.KeysAggregate, StringComparison.Ordinal)), LogEntries.KeyType, actor,
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
                    if (await StoredLeavesMismatchAsync(acta!, stderr).ConfigureAwait(false))
                        return ExitCode.StoredLeafMismatch;

                    // The race (Amendment 3 ruling 40): a row may have been appended between the two folds, and the head is built from this one, so the stop is applied to what this one
                    // holds. No head was signed; the one log.key just published stays (it is not a head and not growth, and the next run finds it published).
                    measured = Measure(acta!, Verifier, signer);
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
                    store, ids, LogEntries.HeadsAggregate, acta.Events.Count(e => string.Equals(e.AggregateId.Value, LogEntries.HeadsAggregate, StringComparison.Ordinal)), LogEntries.HeadType, actor,
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
                    await stdout.WriteLineAsync($"covers  log_index={covered.LogIndex.ToString(CultureInfo.InvariantCulture)}  type={TerminalText.Line(covered.Type)}").ConfigureAwait(false);

                return ExitCode.Ok;
            }
        }
    }

    /// <summary>The verifier every lifecycle decision of this tool goes through (errata G68): the same two algorithms the Forum accepts.</summary>
    private static readonly LifecycleVerifier Verifier = new(new Dictionary<string, IContentVerifier>(StringComparer.Ordinal)
    {
        ["EdDSA"] = new Ed25519Adapter(),
        ["ES256"] = new Es256Adapter(),
    });

    /// <summary>What one fold says about where the signer stands: the anchor, whether the log grew past it, and the lifecycle rows it does not cover (Amendment 3 ruling 40).</summary>
    private readonly record struct HeadMeasure(SignedHead? Anchor, bool Grown, ImmutableArray<SkippedRow> Tail);

    /// <summary>The three answers from one fold, so that the stop and the head built from that fold cannot disagree about which rows the head covers.</summary>
    private static HeadMeasure Measure(ActaLog acta, LifecycleVerifier verifier, Func<SignedHead, bool> signedByTheSigner)
    {
        var anchor = acta.LatestHeadWhere(signedByTheSigner);
        return new HeadMeasure(anchor, acta.GrownPast(anchor), verifier.OfLog(acta.Events).AllSkippedPast(anchor?.TreeSize ?? 0));
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
    private static async Task<int> WithheldAsync(TextWriter stderr, ImmutableArray<SkippedRow> rows)
    {
        await stderr.WriteLineAsync(
            $"error: curia/head/withheld: {rows.Length.ToString(CultureInfo.InvariantCulture)} lifecycle or operator-act row(s) the Forum does not honour (R6.64, R6.67) follow the latest head, so no head was signed. " +
            "A head over a row that verifies under no published key cannot be taken back: the log is append-only and clients retain the heads they have seen, so a restore to before the row forks the log for any client that retained a later head. " +
            "Read the rows (lifecycle-audit lists every one), and restore to before them if they are not to stay in the log. " +
            "The Forum acts on none of them; if they may stay, sign by hand with 'sign-head --by <name>' (without --if-grown), and that head covers them.")
            .ConfigureAwait(false);
        foreach (var row in rows.Take(WithheldListed))
            await stderr.WriteLineAsync($"withheld  log_index={row.LogIndex.ToString(CultureInfo.InvariantCulture)}  type={TerminalText.Line(row.Type)}  predicate={TerminalText.Line(row.Predicate)}").ConfigureAwait(false);

        if (rows.Length > WithheldListed)
            await stderr.WriteLineAsync($"and {(rows.Length - WithheldListed).ToString(CultureInfo.InvariantCulture)} more").ConfigureAwait(false);

        return ExitCode.HeadWithheld;
    }

    /// <summary>How many skipped rows <c>lifecycle-audit</c> lists before it says how many more there are.</summary>
    private const int AuditListed = 100;

    /// <summary>
    /// Errata G68, ruling 13: <c>lifecycle-audit</c>. Reads the whole log once, verifies it by R6.64 and reports how many lifecycle rows the Forum honours and which it does not. Read-only, and
    /// reads no key: the verifier holds the public algorithms only. Each skipped row is its log index, its type, its aggregate as a display literal (an identifier is a caller's string) and the
    /// predicate that failed, never a payload member. Exit 7 when any row is skipped, 5 (through <see cref="RunAsync"/>) when the log cannot be read.
    /// </summary>
    private static async Task<int> LifecycleAuditAsync(
        string[] argv,
        string connectionString,
        TimeProvider clock,
        ILifecycleAppendGate lifecycleGate,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
    {
        if (argv.Length != 0)
        {
            await stderr.WriteLineAsync($"error: unexpected argument '{argv[0]}'. lifecycle-audit takes no argument.").ConfigureAwait(false);
            return ExitCode.Usage;
        }

        var adapters = new PostgresAdapters(connectionString, clock, lifecycleGate);
        await using (adapters.ConfigureAwait(false))
        {
            var read = await adapters.EventStore.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            if (!read.TryGetValue(out var log, out var readError))
                return await RefuseAsync(stderr, readError!).ConfigureAwait(false);

            var verified = Verifier.OfLog(log!);
            var skipped = verified.AllSkipped();
            await stdout.WriteLineAsync($"honoured {verified.AllHonouredCount().ToString(CultureInfo.InvariantCulture)}  skipped {skipped.Length.ToString(CultureInfo.InvariantCulture)}").ConfigureAwait(false);

            foreach (var row in skipped.Take(AuditListed))
            {
                await stdout.WriteLineAsync(
                    $"skipped  log_index={row.LogIndex.ToString(CultureInfo.InvariantCulture)}  type={TerminalText.Line(row.Type)}  aggregate={DisplayLiteral.Of(row.Aggregate)}  predicate={TerminalText.Line(row.Predicate)}")
                    .ConfigureAwait(false);
            }

            if (skipped.Length > AuditListed)
                await stdout.WriteLineAsync($"and {(skipped.Length - AuditListed).ToString(CultureInfo.InvariantCulture)} more").ConfigureAwait(false);

            // R6.69: the stored leaves, held to the rows. Said only when there is something to say, so a log that has them all and right reads as it always did.
            if (!StoredLeafAudit.Check(log!).TryGetValue(out var leaves, out var leafError))
                return await RefuseAsync(stderr, leafError!).ConfigureAwait(false);

            if (leaves!.Mismatched.Count > 0 || leaves.Missing.Count > 0)
            {
                await stdout.WriteLineAsync(
                    $"stored-leaves  mismatched {leaves.Mismatched.Count.ToString(CultureInfo.InvariantCulture)}  missing {leaves.Missing.Count.ToString(CultureInfo.InvariantCulture)}").ConfigureAwait(false);
                foreach (var index in leaves.Mismatched.Take(AuditListed))
                    await stdout.WriteLineAsync($"mismatched  log_index={index.ToString(CultureInfo.InvariantCulture)}  stored leaf is not the leaf of its row").ConfigureAwait(false);
                if (leaves.Mismatched.Count > AuditListed)
                    await stdout.WriteLineAsync($"and {(leaves.Mismatched.Count - AuditListed).ToString(CultureInfo.InvariantCulture)} more mismatched").ConfigureAwait(false);
                if (leaves.Missing.Count > 0)
                    await stdout.WriteLineAsync("missing  rows with no stored leaf are served by recomputation; run curia-operator backfill-leaves with the database owner's connection").ConfigureAwait(false);
            }

            return skipped.IsEmpty && leaves.Mismatched.Count == 0 ? ExitCode.Ok : ExitCode.LifecycleRowsSkipped;
        }
    }

    /// <summary>
    /// R6.69: whether any row's stored leaf is not its row's leaf. Says so on stderr (the first <see cref="AuditListed"/> indices, never a payload member) and writes nothing. A row with no
    /// stored leaf is not a mismatch: the fold hashed it, so the root is right.
    /// </summary>
    private static async Task<bool> StoredLeavesMismatchAsync(ActaLog acta, TextWriter stderr)
    {
        if (!StoredLeafAudit.Check(acta.Events).TryGetValue(out var findings, out _) || findings!.Mismatched.Count == 0)
            return false;

        await stderr.WriteLineAsync(
            $"error: {findings.Mismatched.Count.ToString(CultureInfo.InvariantCulture)} stored leaf hash(es) are not the leaf of their row (R6.69); nothing was signed or written. " +
            "The Forum serves proofs from the stored leaves, so a client cannot verify them. Read the rows (lifecycle-audit lists them); the stored leaf is never repaired by this tool.")
            .ConfigureAwait(false);
        foreach (var index in findings.Mismatched.Take(AuditListed))
            await stderr.WriteLineAsync($"mismatched  log_index={index.ToString(CultureInfo.InvariantCulture)}").ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// R6.69, db/0007: <c>backfill-leaves</c> fills <c>events.leaf_hash</c> for rows appended before the column. It must be run with the database owner's connection string in
    /// <c>CURIA_EVENTS_POSTGRES</c>, because the application role holds no <c>UPDATE</c> (R11.6); as that role it is refused, exit 2, before a row is read.
    /// </summary>
    private static async Task<int> BackfillLeavesAsync(string[] argv, string connectionString, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        if (argv.Length != 0)
            return await UsageErrorAsync(stderr, $"unexpected argument '{argv[0]}'. backfill-leaves takes no argument.").ConfigureAwait(false);

        var backfill = PostgresLeafBackfill.ForConnection(connectionString);
        await using (backfill.ConfigureAwait(false))
        {
            var report = await backfill.RunAsync(cancellationToken).ConfigureAwait(false);
            if (!report.TryGetValue(out var done, out var error))
                return await RefuseAsync(stderr, error!).ConfigureAwait(false);

            await stdout.WriteLineAsync($"filled {done!.Filled.ToString(CultureInfo.InvariantCulture)}  already_held {done.AlreadyHeld.ToString(CultureInfo.InvariantCulture)}").ConfigureAwait(false);
            return ExitCode.Ok;
        }
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
        Func<string?>? readOperatorActsSigningKeyPem,
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
        if (!string.IsNullOrWhiteSpace(readOperatorActsSigningKeyPem?.Invoke()))
            return await SharedEnvironmentAsync(stderr, "publish-declaration-key", "CURIA_OPERATOR_ACTS_SIGNING_KEY_PEM", "the operator-acts key", OperatorActsKeyHeldWithDeclarationKeyWhy).ConfigureAwait(false);

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

    /// <summary>
    /// Errata G70, R6.66, R11.97: the operator publishes the key that signs its acts (<c>log.operator-key</c>), out of band, once. Not a lifecycle act, so the verb does not ask the gate. It refuses a key
    /// that is a log key or a declaration key and a second key (the first published wins), and an environment that also holds the log key or the declaration key (R12.18).
    /// </summary>
    private static async Task<int> PublishOperatorKeyAsync(
        string[] argv,
        string connectionString,
        TimeProvider clock,
        ILifecycleAppendGate lifecycleGate,
        TextWriter stdout,
        TextWriter stderr,
        Func<string?>? readLogSigningKeyPem,
        Func<string?>? readDeclarationSigningKeyPem,
        Func<string?>? readOperatorActsSigningKeyPem,
        CancellationToken cancellationToken)
    {
        string? byValue = null;
        for (var i = 0; i < argv.Length; i++)
        {
            if (argv[i] == "--by" && i + 1 < argv.Length) { byValue = argv[++i]; continue; }
            return await UsageErrorAsync(stderr, $"unexpected argument '{argv[i]}'. publish-operator-key takes --by <role>.").ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(byValue))
            return await UsageErrorAsync(stderr, "--by <role> is required.").ConfigureAwait(false);

        var actorValue = byValue.StartsWith(ApplyModeration.OperatorPrefix, StringComparison.Ordinal) ? byValue : ApplyModeration.OperatorPrefix + byValue;
        if (!ActorId.Create(actorValue).TryGetValue(out var actor, out _))
            return await UsageErrorAsync(stderr, "--by <role> is required.").ConfigureAwait(false);

        if (await HeldWithAnotherKeyAsync(stderr, "publish-operator-key", readLogSigningKeyPem, readDeclarationSigningKeyPem).ConfigureAwait(false) is { } sharedExit)
            return sharedExit;

        using var key = await LoadOperatorActsKeyAsync(stderr, readOperatorActsSigningKeyPem).ConfigureAwait(false);
        if (key is null)
            return ExitCode.Environment;

        Result<long> result;
        var adapters = new PostgresAdapters(connectionString, clock, lifecycleGate);
        await using (adapters.ConfigureAwait(false))
        {
            result = await new PublishOperatorKey(adapters.EventStore, clock).PublishAsync(key, actor!, cancellationToken).ConfigureAwait(false);
        }

        if (!result.TryGetValue(out var logIndex, out var error))
            return await RefuseAsync(stderr, error!).ConfigureAwait(false);

        await stdout.WriteLineAsync($"published operator key  {key.Kid}").ConfigureAwait(false);
        await stdout.WriteLineAsync($"log_index               {logIndex.ToString(CultureInfo.InvariantCulture)}").ConfigureAwait(false);
        return ExitCode.Ok;
    }

    /// <summary>
    /// Errata G70, R11.97, R12.18: the refusal of an environment that holds the operator-acts key's neighbours. Run before the operator-acts key is parsed or used by every verb that signs or publishes
    /// with it: it holds neither the log key nor the declaration key. Null when it holds neither.
    /// </summary>
    private static async Task<int?> HeldWithAnotherKeyAsync(
        TextWriter stderr, string verb, Func<string?>? readLogSigningKeyPem, Func<string?>? readDeclarationSigningKeyPem)
    {
        if (!string.IsNullOrWhiteSpace(readLogSigningKeyPem?.Invoke()))
            return await SharedEnvironmentAsync(stderr, verb, "CURIA_LOG_SIGNING_KEY_PEM", "the log key", LogKeyHeldWithOperatorActsKeyWhy).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(readDeclarationSigningKeyPem?.Invoke()))
            return await SharedEnvironmentAsync(stderr, verb, "CURIA_DECLARATION_SIGNING_KEY_PEM", "the declaration key", DeclarationKeyHeldWithOperatorActsKeyWhy).ConfigureAwait(false);

        return null;
    }

    /// <summary>The operator-acts key from <c>CURIA_OPERATOR_ACTS_SIGNING_KEY_PEM</c>, or null after saying (exit 3's reason) that it is not set or not usable. Names the variable, never key material.</summary>
    private static async Task<OperatorActsSigningKey?> LoadOperatorActsKeyAsync(TextWriter stderr, Func<string?>? readOperatorActsSigningKeyPem)
    {
        var pem = readOperatorActsSigningKeyPem?.Invoke();
        if (string.IsNullOrWhiteSpace(pem))
        {
            await stderr.WriteLineAsync(
                "error: CURIA_OPERATOR_ACTS_SIGNING_KEY_PEM is not set. The operator's acts are signed with the operator-acts key, which the Forum does not hold (R4.43); this tool needs it and does not sign without it.")
                .ConfigureAwait(false);
            return null;
        }

        try
        {
            return OperatorActsSigningKey.FromPem(pem);
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or ArgumentException or InvalidOperationException)
        {
            await stderr.WriteLineAsync("error: CURIA_OPERATOR_ACTS_SIGNING_KEY_PEM is not a usable P-256 key: " + TerminalText.Line(e.Message)).ConfigureAwait(false);
            return null;
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
        Func<string?>? readOperatorActsSigningKeyPem,
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
        if (!string.IsNullOrWhiteSpace(readOperatorActsSigningKeyPem?.Invoke()))
            return await SharedEnvironmentAsync(stderr, "compromise", "CURIA_OPERATOR_ACTS_SIGNING_KEY_PEM", "the operator-acts key", OperatorActsKeyHeldWithDeclarationKeyWhy).ConfigureAwait(false);

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
            result = await new DeclareCompromise(adapters.EventStore, Verifier, clock)
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
        Func<string?>? readLogSigningKeyPem,
        Func<string?>? readDeclarationSigningKeyPem,
        Func<string?>? readOperatorActsSigningKeyPem,
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
        // Errata G70, R11.97, R12.18: before the operator-acts key is parsed or used.
        if (await HeldWithAnotherKeyAsync(stderr, "moderate", readLogSigningKeyPem, readDeclarationSigningKeyPem).ConfigureAwait(false) is { } sharedExit)
            return sharedExit;

        using var actsKey = await LoadOperatorActsKeyAsync(stderr, readOperatorActsSigningKeyPem).ConfigureAwait(false);
        if (actsKey is null)
            return ExitCode.Environment;

        Result<ModerationRecorded> result;
        var adapters = new PostgresAdapters(connectionString, clock, lifecycleGate);
        await using (adapters.ConfigureAwait(false))
        {
            result = await new ApplyModeration(adapters.EventStore, adapters.FlagDetails, Verifier, clock)
                .RecordAsync(PostId.Parse(values["post"], "post_id"), effect, category, Rationale.Parse(values["reason"]), actor, actsKey, cancellationToken)
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
        // The history that decides a flag's state is that of the moderation records the Forum honours (errata G70, R4.43, R10.61): a record it skips upholds and adjudicates nothing.
        var moderation = Verifier.OfLog(log).Moderation();
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

    private const string OperatorActsKeyHeldWithLogKeyWhy = "the key that signs heads is held by an unattended process, and the key that signs the operator's acts is held by a person where none can read it.";

    private const string OperatorActsKeyHeldWithDeclarationKeyWhy = "the declaration key and the key that signs the operator's acts are two powers, and a process that reads both holds both.";

    private const string LogKeyHeldWithOperatorActsKeyWhy = "the log key is the one an unattended process holds, and the operator-acts key is held only where none can read it.";

    private const string DeclarationKeyHeldWithOperatorActsKeyWhy = "the declaration key and the operator-acts key are two powers, and a process that reads both holds both.";

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
        // R6.69: the operator's fold hashes every leaf from its row. The root a head signs is never built from the column it is meant to check.
        return read.Bind(ActaLog.FoldRecomputed);
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

        // A row the Forum recorded and skips is the audit's finding, not a refusal of the request (errata G68, ruling 81): the same exit code as lifecycle-audit.
        return error.Type is KeyActErrors.NotHonouredType or ActErrors.NotHonouredType ? ExitCode.LifecycleRowsSkipped : ExitCode.Refused;
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
