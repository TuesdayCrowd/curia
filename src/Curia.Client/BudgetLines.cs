namespace Curia.Client;

/// <summary>
/// The lines <c>curia budget</c> and <c>curia_budget</c> print (R11.65), and the line a receipt prints for the budget it states, one definition for both
/// clients. Every string the Forum supplied (a tier, a credential, an instant, a budget's name) is quoted as a display literal (R10.63) and no instant is
/// computed here: a next slot is the Forum's own, as given.
/// </summary>
public static class BudgetLines
{
    /// <summary>R11.65: the readout is a snapshot and not a reservation.</summary>
    public const string Snapshot =
        "This is a snapshot and not a reservation: an act that commits after it was read is not in it.";

    /// <summary>R11.65: every receipt already states the standing, so the readout is read before the first act and not after each one.</summary>
    public const string ReceiptsState =
        "Read it before the first act: every receipt already states the standing after the act.";

    /// <summary>The lines for a readout: the tier, the credential, the instant, one line per budget in the response's order, the ones it lacks as NOT STATED, and the snapshot sentence.</summary>
    public static string Of(BudgetStanding standing) => Frame(standing).ToString();

    /// <summary>The same lines as a frame, for a client that writes frames.</summary>
    public static FrameBuilder Frame(BudgetStanding standing)
    {
        ArgumentNullException.ThrowIfNull(standing);

        var text = new FrameBuilder()
            .Line($"tier        {standing.Tier}")
            .Line($"credential  {standing.Credential}");

        if (string.Equals(standing.Credential, "quarantined", StringComparison.Ordinal))
            text.Line($"            a quarantined credential may only read and retire its key: every other act is refused whatever remains below");

        text.Line($"as of       {standing.AsOf}   (counted over the trailing {standing.WindowSeconds} seconds)");

        foreach (var entry in standing.Budgets)
        {
            var next = NextWords(entry);

            text.Line($"budget  {entry.Name}  {Counts(entry.Limit, entry.Used, entry.Remaining)}  {next}");
        }

        foreach (var name in ForumDocuments.BudgetNames)
            if (!standing.Budgets.Any(e => string.Equals(e.Name, name, StringComparison.Ordinal)))
                text.Line($"budget  {new OwnText(name)}  {new OwnText("NOT STATED")}");

        return text.Line($"{new OwnText(Snapshot)}").Line($"{new OwnText(ReceiptsState)}");
    }

    /// <summary>The line a receipt prints for the budget its act spent: the standing after the act as the Forum stated it, or <c>budget  NOT STATED</c> where it sent none.</summary>
    public static string Receipt(BudgetReceipt? budget) => ReceiptFrame(budget).ToString();

    /// <summary>The same line as a frame.</summary>
    public static FrameBuilder ReceiptFrame(BudgetReceipt? budget) =>
        budget is null
            ? new FrameBuilder().Line($"budget  {new OwnText("NOT STATED")}   (the Forum's receipt did not say where this budget stands; `curia budget` reads it)")
            : new FrameBuilder().Line($"budget  {budget.Name}  {new OwnText("after this act:")} {Counts(budget.Limit, budget.Used, budget.Remaining)}");

    private static OwnText NextWords(BudgetEntryStanding entry)
    {
        if (entry.NextSlot.Kind == NextSlotKind.Instant) return new OwnText("next slot " + Curia.Canon.Json.DisplayLiteral.Of(entry.NextSlot.At));
        if (entry.NextSlot.Kind == NextSlotKind.None) return new OwnText(entry.Limit is null ? string.Empty : "next slot none (nothing is counted)");

        return new OwnText("next slot NOT STATED");
    }

    private static OwnText Counts(int? limit, int used, int? remaining) =>
        limit is { } l && remaining is { } r
            ? new OwnText($"limit {l}  used {used}  remaining {r}")
            : new OwnText($"no fixed limit  used {used}");
}
