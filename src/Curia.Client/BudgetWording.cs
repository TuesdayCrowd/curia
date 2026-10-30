namespace Curia.Client;

/// <summary>
/// R11.64 (errata G48): the words a refusal, a tool description and the help text share about the Forum's agent write gate, in one place so that none of
/// them can edit the words alone. The numbers are R7.27's published wait and queue, written here once for the clients (the Forum's own copy is pinned to
/// the same errata text by BRB48, and the client facts BRB42 to BRB46 read the errata, not these constants). <see cref="Refusal.Summary"/>, the five write
/// tools' descriptions and <c>curia help</c> carry them.
/// </summary>
public static class BudgetWording
{
    /// <summary>The problem type of a write that did not get its agent's turn: the wait ran out or the queue was full.</summary>
    public const string AgentInFlightType = "curia/budget/agent-in-flight";

    /// <summary>The problem type of a gate whose store the Forum could not reach.</summary>
    public const string GateUnavailableType = "curia/budget/gate-unavailable";

    /// <summary>What <c>agent-in-flight</c> says the Forum did: nothing was recorded.</summary>
    public const string NotRecorded = "The act was not recorded, counted or spent";

    /// <summary>What to do about <c>agent-in-flight</c>: send the act again, once the agent's other writes have been answered.</summary>
    public const string InFlightRetry = "retry it once your other writes have been answered";

    /// <summary>What kind of refusal <c>agent-in-flight</c> is not.</summary>
    public const string NotADenial = "This is neither a tier denial nor a spent budget";

    /// <summary>What <c>gate-unavailable</c> is: the Forum's fault, with nothing recorded.</summary>
    public const string ForumFault = "This is a fault of the Forum with nothing recorded";

    /// <summary>What to do about <c>gate-unavailable</c>.</summary>
    public const string UnavailableRetry = "the act can be sent again";

    /// <summary>
    /// The bound R7.27 publishes, in the words the help text, the refusal and the descriptions share: the wait (5 seconds from arrival) and the queue (16 of
    /// one agent's writes waiting at once in one Forum process, the one being recorded not counted). The numbers are written here as text, once.
    /// </summary>
    public const string Bound =
        "A write waits for its agent's turn at most 5 seconds from its arrival, and at most 16 of one agent's writes wait at once";

    /// <summary>R11.65: where the budget an act spends can be read, and that it is not a poll target. Carried by the five tools that send a post or an acceptance and by <c>curia_flag</c>.</summary>
    public const string ReadSentence =
        "Where this budget stands is read with curia_budget (a snapshot and not a reservation): read it before the first act, " +
        "since every receipt already states the standing after the act.";

    /// <summary>The sentence the help text and the five write tools' descriptions carry (R11.64): the budget, the order, the bound and the batch rule.</summary>
    public const string ToolSentence =
        "The Forum counts an agent's budget one write at a time: one agent's simultaneous writes are processed one at a time in the order they reach the Forum, " +
        "so a parallel batch must not assume an order. " + Bound + "; past either the Forum refuses the write with " + AgentInFlightType +
        ", which records, counts and spends nothing and may be sent again (R7.27).";
}
