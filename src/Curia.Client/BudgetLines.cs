namespace Curia.Client;

/// <summary>
/// The lines <c>curia budget</c> and <c>curia_budget</c> print (R11.65), one definition for both clients. Inert in the seam: it prints nothing yet.
/// </summary>
public static class BudgetLines
{
    /// <summary>The lines for a readout: one per budget in the response's order, the snapshot sentence, and the credential note where it applies.</summary>
    public static string Of(BudgetStanding standing)
    {
        ArgumentNullException.ThrowIfNull(standing);
        return string.Empty;
    }
}
