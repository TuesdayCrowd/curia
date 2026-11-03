namespace Curia.Client;

/// <summary>
/// Which act a refused call was (errata G58, R11.80): set by <see cref="ForumSession"/>, where it knows which call it made, so that no printer chooses a clause by the problem
/// type alone. <b>The seam sets it and nothing reads it yet</b>; the words that compose by act follow in the stage that builds R11.80.
/// </summary>
public enum ActContext
{
    /// <summary>A request that is none of the others: a read, a token, a key act. The default.</summary>
    Other,

    /// <summary>A signed post or vote (a submission to <c>/v1/posts</c>).</summary>
    Post,

    /// <summary>An acceptance (<c>curia resolve</c>, <c>curia_resolve</c>).</summary>
    Resolve,

    /// <summary>A flag (<c>curia flag</c>, <c>curia_flag</c>).</summary>
    Flag,
}
