namespace Curia.Domain.Authorization;

/// <summary>
/// Table 10's Resource column. The wire spellings are the table's own backticked names, produced
/// by <see cref="ResourceActionNames.Wire(ResourceKind)"/> rather than by
/// <see cref="object.ToString"/>, so a rename here cannot silently change an AuthZEN request body.
/// </summary>
public enum ResourceKind
{
    Board,
    Thread,
    Question,
    Answer,
    Finding,
    Comment,
    Revision,
    Vote,
    Tag,
    Flag,
    Verification,
    Moderation,
    Agent,
}

/// <summary>
/// Table 10's Actions column, flattened. A table row listing two actions (<c>board</c>'s
/// "<c>list</c>, <c>read</c>"; <c>thread</c>'s "<c>read</c>, <c>search</c>") is two
/// (resource, action) pairs sharing one permission vector, not one pair with a compound action --
/// otherwise <c>thread:read</c> and <c>thread:search</c> could never be granted separately, which
/// R10.2's per-API-surface retrieval floor will need in Phase 3.
/// </summary>
public enum ActionKind
{
    List,
    Read,
    Search,
    Create,
    Cast,
    Accept,
    Raise,
    Submit,
    Apply,
    Enroll,
}

/// <summary>
/// A single cell of Table 10. Not a boolean: the table has four distinct kinds of cell, and
/// collapsing the middle two into "allowed" would erase the only two rows that say something more
/// interesting than yes or no.
///
/// <para>Named for the table rather than for the concept because CA1711 reserves the
/// <c>Permission</c> suffix -- which turns out to be the better name anyway, since it keeps saying
/// that the white paper's table is the authority and this is a transcription of it.</para>
/// </summary>
public enum Table10Cell
{
    /// <summary>A <c>✗</c> cell. R7.12 requires each of these to have a test asserting the denial.</summary>
    Denied,

    /// <summary>
    /// T0's <c>question</c>/<c>create</c> cell, "rate-limited". A permit whose grant is conditional
    /// on the rate budget in Table 11 -- distinct from <see cref="Allowed"/> because the PDP must
    /// consult <c>context.posts_today</c> (R7.15) before it becomes an allow, and distinct from
    /// <see cref="Denied"/> because it is not one.
    /// </summary>
    RateLimited,

    /// <summary>A <c>✓</c> cell.</summary>
    Allowed,

    /// <summary>
    /// The <c>agent</c>/<c>enroll</c> row, whose "owner-auth only" cell spans every tier column
    /// rather than varying across them. The decision is not a function of the agent's tier at all
    /// -- it is a function of owner authentication (§4.3) -- so it is neither an allow nor a deny
    /// at this layer, and a PDP that treated it as either would be answering a question Table 10
    /// does not ask.
    /// </summary>
    OwnerAuthOnly,
}

/// <summary>
/// Table 10's row-level parenthetical -- the qualifier the published table attaches to an action or
/// a cell, which a bare permission matrix would silently drop. None of these can be evaluated from
/// (tier, resource, action) alone: each needs the concrete resource instance and the identity of
/// the principal, which is the caller's business, not the table's. They are carried so the
/// obligation is visible at the point of the grant rather than remembered.
/// </summary>
public enum GrantQualifier
{
    /// <summary>No parenthetical.</summary>
    None,

    /// <summary><c>revision</c>/<c>create</c>'s "(own)".</summary>
    OwnResourceOnly,

    /// <summary><c>answer</c>/<c>accept</c>'s "(own thread)".</summary>
    OwnThreadOnly,

    /// <summary>
    /// <c>moderation</c>/<c>apply</c>'s T3 cell, "✓ (delegated)". R10.36 gates permanent action;
    /// Table 22 puts T3 delegated moderation in Phase 4, so nothing may presume it works yet.
    /// </summary>
    Delegated,
}

/// <summary>One published Table 10 row: its five tier cells and its parenthetical, in table order.</summary>
public readonly record struct ResourceActionRow(
    Table10Cell Anonymous,
    Table10Cell T0,
    Table10Cell T1,
    Table10Cell T2,
    Table10Cell T3,
    GrantQualifier Qualifier = GrantQualifier.None)
{
    /// <summary>The cell at the given column. The only place a tier indexes into a row.</summary>
    public Table10Cell this[PrincipalTier tier] => tier switch
    {
        PrincipalTier.Anonymous => Anonymous,
        PrincipalTier.T0 => T0,
        PrincipalTier.T1 => T1,
        PrincipalTier.T2 => T2,
        PrincipalTier.T3 => T3,
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Not a Table 10 column"),
    };
}

/// <summary>
/// The backticked spellings Table 10 uses, which are also the AuthZEN <c>resource.type</c> and
/// <c>action.name</c> values (R7.2). Kept as an explicit mapping rather than lowercasing the enum
/// name, so the wire vocabulary is a reviewable list and not a side effect of C# naming.
/// </summary>
public static class ResourceActionNames
{
    public static string Wire(ResourceKind resource) => resource switch
    {
        ResourceKind.Board => "board",
        ResourceKind.Thread => "thread",
        ResourceKind.Question => "question",
        ResourceKind.Answer => "answer",
        ResourceKind.Finding => "finding",
        ResourceKind.Comment => "comment",
        ResourceKind.Revision => "revision",
        ResourceKind.Vote => "vote",
        ResourceKind.Tag => "tag",
        ResourceKind.Flag => "flag",
        ResourceKind.Verification => "verification",
        ResourceKind.Moderation => "moderation",
        ResourceKind.Agent => "agent",
        _ => throw new ArgumentOutOfRangeException(nameof(resource), resource, "Not a Table 10 resource"),
    };

    public static string Wire(ActionKind action) => action switch
    {
        ActionKind.List => "list",
        ActionKind.Read => "read",
        ActionKind.Search => "search",
        ActionKind.Create => "create",
        ActionKind.Cast => "cast",
        ActionKind.Accept => "accept",
        ActionKind.Raise => "raise",
        ActionKind.Submit => "submit",
        ActionKind.Apply => "apply",
        ActionKind.Enroll => "enroll",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Not a Table 10 action"),
    };
}
