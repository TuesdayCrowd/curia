namespace Curia.Client;

/// <summary>
/// R11.78 (errata G56): the board sentence the helps and the descriptions carry, once. Nothing prints it at this stage.
/// </summary>
public static class BoardWording
{
    /// <summary>What a board must be to be a name, and what the Forum does with one that is not.</summary>
    public const string Sentence =
        "A board is a name: lower case, 1 to 64 characters, with no space or other separator, no control, format, private-use or unassigned character, none of , / ? # % and not . or ..; lower-case letters, digits, - and _ always qualify. The Forum refuses any other name for a new post and never changes one for you; a board that exists already is read as it is.";
}
