using System.Collections.Immutable;

namespace Curia.Client;

/// <summary>What the Forum said of a budget's next slot (R11.65): an instant, an explicit null (nothing counted), or nothing at all.</summary>
public enum NextSlotKind
{
    /// <summary>The Forum stated an instant.</summary>
    Instant,

    /// <summary>The Forum stated null: nothing is counted, or the limit is negotiated.</summary>
    None,

    /// <summary>The Forum's response did not hold the member.</summary>
    NotStated,
}

/// <summary>A budget's next slot, as the Forum gave it. The client computes none (R11.65).</summary>
public sealed record NextSlot(NextSlotKind Kind, string? At);

/// <summary>One budget as the readout states it (R9.50).</summary>
public sealed record BudgetEntryStanding(string Name, int? Limit, int Used, int? Remaining, NextSlot NextSlot);

/// <summary>
/// The readout <c>GET /v1/budget</c> answers (R9.50), as the client reads it. Inert in the seam: nothing builds one yet.
/// </summary>
public sealed record BudgetStanding(string Tier, string Credential, string AsOf, int WindowSeconds, ImmutableArray<BudgetEntryStanding> Budgets);
