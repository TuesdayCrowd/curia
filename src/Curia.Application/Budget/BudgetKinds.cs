using Curia.Domain.Credentials;
using Curia.Domain.Primitives;

namespace Curia.Application.Budget;

/// <summary>One budget as data: the wire name the readout states and the denial reason the policy refuses it with.</summary>
public sealed record BudgetKind(string Name, string DenialReason);

/// <summary>
/// The three budgets (errata G49, R7.28) as data. Inert in the seam: the set is empty until the standing is built, so nothing reads a name from here yet.
/// </summary>
public static class BudgetKinds
{
    /// <summary>The budgets in the order the readout states them. Empty in the seam.</summary>
    public static IReadOnlyList<BudgetKind> All { get; } = [];

    /// <summary>
    /// The wire word of a credential state (R9.50): a total function of <see cref="CredentialState"/>, where only <c>Active</c> and <c>Quarantined</c> have
    /// a word and every other state yields no readout. Inert in the seam: every state yields no readout.
    /// </summary>
    public static Result<string> CredentialWord(CredentialState state) =>
        Result<string>.Fail(new Error("curia/budget/not-built", "The credential word is not built", $"state={state}"));
}
