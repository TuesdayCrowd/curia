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
/// The words both clients print for <c>vector_index</c> and <c>vector_pending</c> (errata G51, R11.70), written once. <b>Task 0 seam: inert.</b>
/// Every member answers an empty line, so a probe that expects a published line is red by assertion until the clients' task writes them.
/// </summary>
public static class IndexWording
{
    /// <summary>The receipt's line for <paramref name="state"/>.</summary>
    public static string ReceiptLine(VectorIndexState state) => string.Empty;

    /// <summary><c>curia search</c>'s line for the Forum's pending count, or for none stated.</summary>
    public static string SearchLine(int? pending) => string.Empty;

    /// <summary><c>curia_search</c>'s first-block member for the Forum's pending count, or for none stated.</summary>
    public static string ToolSearchLine(int? pending) => string.Empty;

    /// <summary>The sentence a write tool's description and a help text carry.</summary>
    public static string Sentence => string.Empty;
}
