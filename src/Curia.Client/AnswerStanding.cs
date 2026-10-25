using Curia.Domain.Serving;

namespace Curia.Client;

/// <summary>
/// R10.81 (errata G36): the Forum's word on which answer a question's thread has accepted, and every word a reader uses to say it, in
/// one place, beside <see cref="Supersession"/>. The passage's frame, the titled index and the screen's header H6 compose their line
/// from here, so no form can edit its words alone (R10.74).
///
/// <para><b>Seam, not yet the rule.</b> Both forms return nothing, so a reader prints no <c>answers</c> line; the probes that expect
/// one fail by assertion. The three states (accepted, none accepted, NOT STATED) are written in the stage that builds item 15.</para>
/// </summary>
public static class AnswerStanding
{
    /// <summary>The passage's frame line, <c>answers  ...</c>, for a post of kind <paramref name="kind"/> carrying <paramref name="member"/>; nothing for any other kind.</summary>
    public static OwnText? PassageLine(string kind, AcceptedAnswerMember? member)
    {
        _ = kind;
        _ = member;
        return null;
    }

    /// <summary>The one-row form (the titled index's row, the screen's H6), without a leading label.</summary>
    public static OwnText? Row(string kind, AcceptedAnswerMember? member)
    {
        _ = kind;
        _ = member;
        return null;
    }
}
