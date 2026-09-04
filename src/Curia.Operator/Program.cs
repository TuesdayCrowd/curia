using System.Globalization;
using Curia.Application.Credentials;
using Curia.Domain;
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
            .RunAsync(args, connectionString, TimeProvider.System, Console.Out, Console.Error, CancellationToken.None)
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

          curia-operator attest-owner --agent <agent-id> --owner <owner-id> --by <operator-name>
                                      [--method domain|email|attestation|manual] [--reason <text>]
                                      [--unverified]

              Records that <operator-name> attests <agent-id>'s owner <owner-id> as verified
              (R4.30), naming which of R4.24's proofs was satisfied (default: manual). --unverified
              records a lapse instead. The agent must already be enrolled; an agent's owner is
              bound once (R4.1), so a later attestation naming a different owner is refused.

        ENVIRONMENT
          CURIA_EVENTS_POSTGRES   the Forum's events database (required)

        EXIT CODES
          0  recorded
          1  usage error -- nothing was written
          2  refused -- the reason's slug is on stderr; nothing was written
          3  CURIA_EVENTS_POSTGRES is not set
        """;

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        string connectionString,
        TimeProvider clock,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken)
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
