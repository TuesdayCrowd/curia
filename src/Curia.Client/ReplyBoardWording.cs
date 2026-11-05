namespace Curia.Client;

/// <summary>R11.85 (errata G61): the sentence a reply verb prints after the read's own refusal when it could not learn the board of the post it follows.</summary>
public static class ReplyBoardWording
{
    /// <summary>The second line when the post a reply follows could not be read and no <c>--board</c> was given.</summary>
    public const string UnreadSentence = "The board is taken from that post, or from --board if you give it. Nothing was sent.";
}
