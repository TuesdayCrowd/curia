namespace Curia.Client;

/// <summary>Whether the Forum stored a post's vector, as the receipt states it (R8.81), or that it said nothing.</summary>
public enum VectorIndexState
{
    NotStated = 0,
    Indexed,
    Pending,
    NotApplicable,
}

/// <summary>
/// The words both clients print for <c>vector_index</c> and <c>vector_pending</c> (errata G51, R11.70), written once: <c>curia</c> and <c>curia-mcp</c>
/// call this class and neither words a line of its own, so the two cannot drift. A receipt that carries no member is <c>NOT STATED</c> and never
/// <c>indexed</c>; a pending post is stored, so the line says so and says not to send it again.
/// </summary>
public static class IndexWording
{
    /// <summary>The receipt's line for <paramref name="state"/>.</summary>
    public static string ReceiptLine(VectorIndexState state) => state switch
    {
        VectorIndexState.Indexed => "search  by words and by meaning",
        VectorIndexState.Pending => "search  by words now; by meaning PENDING (the Forum stored the post and will keep trying; do not send it again)",
        VectorIndexState.NotApplicable => "search  not placed in the vector index (a kind that is not searched, or a text with nothing to embed)",
        VectorIndexState.NotStated => "search  NOT STATED (the Forum's receipt did not say whether the post is in the vector index)",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "A vector index state this client has no words for."),
    };

    /// <summary>
    /// <c>curia search</c>'s vector line (errata G54, R11.75): <c>vector  &lt;count clause&gt;; &lt;owed piece&gt;; &lt;checked piece&gt;</c>, or <c>vector  NOT STATED</c> where
    /// the Forum served no count. <c>none pending</c> is said only where both instants are stated and no check is owed; a member the Forum did not serve reads
    /// <c>NOT STATED</c> and is never none.
    /// </summary>
    public static string SearchLine(int? pending, StatedInstant checkedAt, StatedInstant dueAt)
    {
        ArgumentNullException.ThrowIfNull(checkedAt);
        ArgumentNullException.ThrowIfNull(dueAt);
        if (pending is not { } n) return "vector  NOT STATED";

        var bothStated = checkedAt.Kind == StatedKind.Instant && dueAt.Kind != StatedKind.NotStated;
        var clause = n > 0
            ? $"{n.ToString(System.Globalization.CultureInfo.InvariantCulture)} committed post(s) not yet in the vector index: found by words, not yet by meaning"
            : bothStated && dueAt.Kind == StatedKind.Null ? "none pending" : "none counted as pending";
        var owed = dueAt.Kind switch
        {
            StatedKind.Null => "no check is owed",
            StatedKind.Instant => $"a check is owed (a post the Forum answered with a fault may be committed and not yet in the vector index: found by words, not yet by meaning) and next runs at {dueAt.Instant}",
            StatedKind.NotStated => "whether a check is owed is NOT STATED",
            _ => throw new ArgumentOutOfRangeException(nameof(dueAt), dueAt.Kind, "A stated kind this client has no words for."),
        };
        var compared = checkedAt.Kind == StatedKind.Instant
            ? $"the Forum last compared its log with the vector index at {checkedAt.Instant}"
            : "when the Forum last compared its log with the vector index is NOT STATED";
        return $"vector  {clause}; {owed}; {compared}";
    }

    /// <summary>
    /// <c>curia_search</c>'s first-block members (errata G54, R11.75): <c>vector_pending</c>, <c>vector_check_due_at</c> (an instant, <c>none</c> or <c>not stated</c>) and
    /// <c>vector_checked_at</c> (an instant or <c>not stated</c>), and, where a check is owed, the sentence that says what that means.
    /// </summary>
    public static string ToolSearchLine(int? pending, StatedInstant checkedAt, StatedInstant dueAt)
    {
        ArgumentNullException.ThrowIfNull(checkedAt);
        ArgumentNullException.ThrowIfNull(dueAt);
        var count = pending is { } n ? $"vector_pending={n.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : "vector_pending=not stated";
        var due = dueAt.Kind switch
        {
            StatedKind.Instant => $"vector_check_due_at={dueAt.Instant}",
            StatedKind.Null => "vector_check_due_at=none",
            StatedKind.NotStated => "vector_check_due_at=not stated",
            _ => throw new ArgumentOutOfRangeException(nameof(dueAt), dueAt.Kind, "A stated kind this client has no words for."),
        };
        var compared = checkedAt.Kind == StatedKind.Instant ? $"vector_checked_at={checkedAt.Instant}" : "vector_checked_at=not stated";
        return dueAt.Kind == StatedKind.Instant ? $"{count} {due} {compared} {OwedSentence}" : $"{count} {due} {compared}";
    }

    /// <summary>The sentence <c>curia_search</c>'s first block adds where a check is owed (errata G54, R11.75 words).</summary>
    public const string OwedSentence = "A check is owed: a post the Forum answered with a fault may be committed and not yet in the vector index, found by words and not yet by meaning.";

    /// <summary>The paragraph <c>curia_search</c>'s description and <c>curia search</c>'s help carry (errata G54, R11.75 words).</summary>
    public const string CheckParagraph =
        "The vector line states how many committed posts this Forum process holds as pending in the vector index, whether a check of the Forum's log against the index is owed " +
        "(and when it next runs), and when the Forum last made one: none pending is said only when no check is owed, and an owed check means a post the Forum answered with a " +
        "fault may be committed and found by words but not yet by meaning. A member the Forum did not state reads NOT STATED, never none.";

    /// <summary>The sentence a write tool's description and a help text carry.</summary>
    public const string Sentence = "A receipt that says by meaning PENDING means the post is stored and found by words now; do not send it again.";
}
