using Curia.Canon.Json;

namespace Curia.Client;

/// <summary>
/// R11.60 (errata G44): the words a description, a help text and a refusal share about what <c>curia resolve</c> and <c>curia_resolve</c> take, in one
/// place so that none of them can edit the words alone.
/// </summary>
public static class AcceptWording
{
    /// <summary>What may be accepted: the id of an answer, or of a revision of one.</summary>
    public const string Target = "the post id of an answer, or of a revision of one";

    /// <summary>What an acceptance names: the text of the id given, and not the answer's later or earlier texts.</summary>
    public const string NamesTheText = "the acceptance names the text of the id you give";

    /// <summary>
    /// R10.92 (errata G50): the sentence a description and a help carry about the receipt's kind line, so neither words it alone.
    /// </summary>
    public const string ReceiptSentence =
        "The receipt says whether the post accepted is an answer or a revision and, for a revision, the answer it is the text of "
        + "(kind NOT STATED where the Forum's receipt says neither, which is never read as an answer).";

    /// <summary>
    /// R10.92's receipt line for the post accepted (errata G50): the one author of the line both <c>curia resolve</c> and <c>curia_resolve</c> print, on a
    /// first acceptance, a replay and a replacement. An answer is <c>kind answer</c>; a revision is <c>kind revision</c>, R10.87's statement (composed from
    /// <see cref="Passage.AcceptedRevisionStatement"/>, never copied) and the answer it is the text of; a receipt with no kind is <c>kind NOT STATED</c>,
    /// never <c>answer</c>. Every identifier is a display literal (R10.63).
    /// </summary>
    /// <param name="kind">The receipt's <c>post_kind</c>, or null where the Forum served none.</param>
    /// <param name="postId">The receipt's <c>post_id</c>.</param>
    /// <param name="textOf">The receipt's <c>text_of</c>, or null where the Forum served none.</param>
    public static OwnText ReceiptKind(string? kind, string postId, string? textOf)
    {
        ArgumentNullException.ThrowIfNull(postId);
        return kind switch
        {
            "answer" => new OwnText("kind answer   (the post accepted is an answer)"),
            "revision" => new OwnText(
                $"kind revision   {Passage.AcceptedRevisionStatement}; the answer it is the text of: "
                + (textOf is null ? "NOT STATED" : $"post {DisplayLiteral.Of(textOf)}")),
            _ => new OwnText("kind NOT STATED   (the Forum's receipt did not say whether the post accepted is an answer or a revision, so do not take it as an answer)"),
        };
    }
}
