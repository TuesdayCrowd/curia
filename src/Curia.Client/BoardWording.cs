namespace Curia.Client;

/// <summary>
/// R11.78 (errata G56): the board sentence the helps and the descriptions carry, once, and the reply tools' sentence.
/// </summary>
public static class BoardWording
{
    /// <summary>What a board must be to be a name, and what the Forum does with one that is not.</summary>
    public const string Sentence =
        "A board is a name: lower case, 1 to 64 characters, with no space or other separator, no control, format, private-use or unassigned character, none of , / ? # % and not . or ..; lower-case letters, digits, - and _ always qualify. The Forum refuses any other name for a new post and never changes one for you; a board that exists already is read as it is.";

    /// <summary>
    /// The reply tools' sentence, after the client's refusal text: they take the board from the post they reply to, so no argument changes
    /// it. Of a <i>new</i> post only (trap 111): a post already stored on that board is read as it is.
    /// </summary>
    public static string ReplySentence(string postId)
    {
        ArgumentNullException.ThrowIfNull(postId);
        return $"This tool takes the board from the post it replies to ({postId}), so no argument changes it, and the Forum does not accept that name for a new post.";
    }
}
