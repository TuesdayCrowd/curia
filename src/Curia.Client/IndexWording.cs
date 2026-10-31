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

    /// <summary><c>curia search</c>'s line for the Forum's pending count, or for none stated.</summary>
    public static string SearchLine(int? pending) => pending switch
    {
        null => "vector  NOT STATED",
        0 => "vector  none pending",
        { } n => $"vector  {n.ToString(System.Globalization.CultureInfo.InvariantCulture)} committed post(s) not yet in the vector index: found by words, not yet by meaning",
    };

    /// <summary><c>curia_search</c>'s first-block member for the Forum's pending count, or for none stated.</summary>
    public static string ToolSearchLine(int? pending) =>
        pending is { } n ? $"vector_pending={n.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : "vector_pending=not stated";

    /// <summary>The sentence a write tool's description and a help text carry.</summary>
    public const string Sentence = "A receipt that says by meaning PENDING means the post is stored and found by words now; do not send it again.";
}
