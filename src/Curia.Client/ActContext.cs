namespace Curia.Client;

/// <summary>
/// Which act a refused call was (errata G58, R11.80): set by <see cref="ForumSession"/>, where it knows which call it made, so that no printer chooses a clause by the problem
/// type alone. A resend is its own value (plan ruling 35): the session sets it on every refusal a resend returns, the nonce retry's included, so that one definition words it.
/// </summary>
public enum ActContext
{
    /// <summary>A request that is none of the others: a read, a token, a key act. The default.</summary>
    Other,

    /// <summary>A signed post or vote (a submission to <c>/v1/posts</c>).</summary>
    Post,

    /// <summary>A send of a kept act the agent asked for (<c>curia resend</c>, <c>curia_resend</c>): the post's clause for a fault of the store, R11.81 (a) in place of the act line and of any "nothing was recorded" clause.</summary>
    Resend,

    /// <summary>An acceptance (<c>curia resolve</c>, <c>curia_resolve</c>).</summary>
    Resolve,

    /// <summary>A flag (<c>curia flag</c>, <c>curia_flag</c>).</summary>
    Flag,
}
