namespace Curia.Client;

/// <summary>
/// R10.88 (errata G47): the three things a reader that prints a flag's receipt states, in one place, so <c>curia flag</c> and <c>curia_flag</c> cannot
/// word them differently. That the flag is visible only to its raiser and the post's author; where the raiser reads it; that the Forum shows no outcome
/// of a review (R10.38's notice is unbuilt); and the Forum's word on whether the post is served (R9.49), in the member's three states. Fixed words only:
/// no value the Forum served is in any of them.
/// </summary>
public static class FlagReceiptWords
{
    /// <summary>Who can read the flag (R10.35, R7.18).</summary>
    public const string Visibility = "Who can read it: the flag is visible only to its raiser and the post's author.";

    /// <summary>Where <c>curia</c> reads it.</summary>
    public const string WhereInTheCli = "Where you read it: curia flags lists what you raised, and curia flags <post-id> what was raised against a post of yours.";

    /// <summary>Where <c>curia-mcp</c>'s caller reads it: the adapter has no tool for it, which a fact holds true of the tool list.</summary>
    public const string WhereInTheAdapter = "Where you read it: curia-mcp has no tool that reads flags; curia flags, in the command-line client, lists what you raised.";

    /// <summary>What follows a flag (R10.38's notice and appeal are unbuilt).</summary>
    public const string NoOutcome = "What follows: the Forum shows no outcome of a review, so a flag nobody has acted on and one that was dismissed look alike.";

    /// <summary>The Forum's word on whether the post is served, from the member's three states (a malformed member is no receipt, so there is no fourth).</summary>
    public static string Served(PostServed served) => served switch
    {
        PostServed.Served => "The post: the Forum's word at the time of the flag is that the post is served.",
        PostServed.NotServed =>
            "The post: the Forum's word at the time of the flag is that the post is not served (it is withheld or quarantined, or of a kind no reader is served); the flag was accepted all the same, so a moderator restoring the post reads it.",
        PostServed.NotStated =>
            "The post: NOT STATED. The Forum served no word on whether this post is being served, so do not take it as served.",
        _ => throw new ArgumentOutOfRangeException(nameof(served), served, "not a state of the member"),
    };

    /// <summary>The four statements for a reader whose place to read flags is <paramref name="where"/>, in the order they are printed.</summary>
    public static string[] Statements(PostServed served, string where)
    {
        ArgumentNullException.ThrowIfNull(where);
        return [Visibility, where, NoOutcome, Served(served)];
    }
}
