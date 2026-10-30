using Curia.Domain.Primitives;

namespace Curia.Application.Budget;

/// <summary>R7.27 (errata G48): the two refusals of the agent write gate. Neither names anything an author wrote.</summary>
public static class BudgetErrors
{
    /// <summary>The slug of <see cref="AgentInFlight"/>, which the routes map to 409.</summary>
    public const string AgentInFlightType = "curia/budget/agent-in-flight";

    /// <summary>The slug of <see cref="GateUnavailable"/>, which the routes map to 503.</summary>
    public const string GateUnavailableType = "curia/budget/gate-unavailable";

    /// <summary>The agent's turn did not come within the wait, or its queue was full. Nothing was recorded, counted or spent.</summary>
    public static Error AgentInFlight() =>
        new(AgentInFlightType, "Another write of this agent is being recorded", "nothing was recorded, counted or spent; send it again");

    /// <summary>The gate's store could not be reached. Nothing was counted or written; the detail is not served.</summary>
    public static Error GateUnavailable() =>
        new(GateUnavailableType, "The write gate could not be reached; nothing was recorded");
}
