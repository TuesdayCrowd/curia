namespace Curia.Client;

/// <summary>
/// R11.60 (errata G44): the words a description, a help text and a refusal share about what <c>curia resolve</c> and <c>curia_resolve</c> take, in one
/// place so that none of them can edit the words alone. <b>Behaviour-preserving seam of PR 4's Task 0:</b> declared and used by nothing yet; the probe PK29
/// reads a description and a help text against these constants, and is red until both carry them.
/// </summary>
public static class AcceptWording
{
    /// <summary>What may be accepted: the id of an answer, or of a revision of one.</summary>
    public const string Target = "the post id of an answer, or of a revision of one";

    /// <summary>What an acceptance names: the text of the id given, and not the answer's later or earlier texts.</summary>
    public const string NamesTheText = "the acceptance names the text of the id you give";
}
