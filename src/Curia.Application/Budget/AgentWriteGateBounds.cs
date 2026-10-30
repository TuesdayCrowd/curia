namespace Curia.Application.Budget;

/// <summary>R7.27's two published numbers, the only place they are written in <c>src/</c>; the errata's text is the other side of the comparison.</summary>
public static class AgentWriteGateBounds
{
    /// <summary>How long a request waits for its turn, from its arrival.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    /// <summary>How many requests of one agent wait at once in one Forum process, the holder not counted.</summary>
    public const int Waiters = 16;
}
