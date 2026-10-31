using Curia.Domain.Credentials;
using Curia.Domain.Primitives;

namespace Curia.Application.Budget;

/// <summary>One budget as data: the wire name the readout states and the denial reason the policy refuses it with.</summary>
public sealed record BudgetKind(string Name, string DenialReason);

/// <summary>
/// The three budgets (errata G49, R7.28) as data, the only place in <c>src/</c> their wire names and their denial reasons are written together. The order is
/// R9.50's: <c>posting</c>, <c>acceptance</c>, <c>flag</c>.
/// </summary>
public static class BudgetKinds
{
    /// <summary>The wire name of the posting budget (Table 11).</summary>
    public const string Posting = "posting";

    /// <summary>The wire name of the acceptance budget (R7.26).</summary>
    public const string Acceptance = "acceptance";

    /// <summary>The wire name of the flag budget (R7.22).</summary>
    public const string Flag = "flag";

    /// <summary>The budgets in the order the readout states them.</summary>
    public static IReadOnlyList<BudgetKind> All { get; } =
    [
        new(Posting, "table-11/rate-budget-exhausted"),
        new(Acceptance, "table-11/accept-budget-exhausted"),
        new(Flag, "table-11/flag-budget-exhausted"),
    ];

    /// <summary>
    /// The wire word of a credential state (R9.50): a total function of <see cref="CredentialState"/>. Only <c>Active</c> and <c>Quarantined</c> have a word;
    /// every other state yields no readout, so a state added later is never read as <c>active</c> by default.
    /// </summary>
    public static Result<string> CredentialWord(CredentialState state)
    {
        if (state == CredentialState.Active) return Result<string>.Ok("active");
        if (state == CredentialState.Quarantined) return Result<string>.Ok("quarantined");

        return Result<string>.Fail(new Error("curia/budget/credential-not-readable", "No readout for this credential state", $"state={state}"));
    }
}
