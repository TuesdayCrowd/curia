using Curia.Client;

namespace Curia.Client.Cli;

/// <summary>
/// R10.74: the session's exit code. The worst outcome of every passage the screen displayed, ranked as the line form ranks
/// them (<see cref="ExitCode.ForOutcomes"/>), joined with the code of any load refused after the screen was up. A reload
/// adds to the ledger and nothing removes from it: a person who saw a disputed post and scrolled away still leaves with 9,
/// because an exit code an agent reads after the person pressed <c>q</c> must carry what the person saw.
///
/// <para>The rank of the codes is 6, 8, 4, 5, any other refusal, 9, 0: a check that failed or did not run outranks a
/// refusal, which outranks a mark. Provisional, and stated in the help.</para>
/// </summary>
internal sealed class OutcomeLedger
{
    private readonly List<CheckOutcome> _outcomes = [];
    private readonly List<int> _refusals = [];

    /// <summary>Counts the outcomes of passages the screen has displayed.</summary>
    internal void Add(IEnumerable<CheckOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        _outcomes.AddRange(outcomes);
    }

    /// <summary>Counts a load refused after the screen was up, by the exit code <see cref="ExitCode.For(Refusal)"/> gives it.</summary>
    internal void AddRefusal(int exitCode) => _refusals.Add(exitCode);

    internal int Exit => new[] { ExitCode.ForOutcomes([.. _outcomes]) }.Concat(_refusals).MinBy(Rank);

    private static int Rank(int code) => code switch
    {
        ExitCode.Unverified => 0,
        ExitCode.ForumFault => 1,
        ExitCode.Denied => 2,
        ExitCode.NotFound => 3,
        ExitCode.Ok => 6,
        ExitCode.Disputed => 5,
        _ => 4,
    };
}
