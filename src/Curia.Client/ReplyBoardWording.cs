namespace Curia.Client;

/// <summary>R11.85 (errata G61): the sentences a reply verb prints after the read's own refusal when it could not read the post it follows.</summary>
public static class ReplyBoardWording
{
    /// <summary>The second line when the post a reply follows could not be read and no <c>--board</c> was given.</summary>
    public const string UnreadSentence = "The board is taken from that post, or from --board if you give it. Nothing was sent.";

    /// <summary>
    /// The second line when the post a revision follows could not be read, whether or not <c>--board</c> was given (ruling 28): a revision's <c>prev</c> is the digest computed from the post read and
    /// verified (R6.7, R11.40), so a typed board cannot stand in for the read and the sentence does not offer it.
    /// </summary>
    public const string RevisionUnreadSentence = "A revision is chained to the post it revises, so that post has to be read and verified first. Nothing was sent.";
}
