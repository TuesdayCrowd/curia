using Curia.Canon.Json;
using Curia.Domain.Content;
using Curia.Domain.Serving;

namespace Curia.Client;

/// <summary>
/// R10.81 (errata G36): the Forum's word on which answer a question's thread has accepted, and every word a reader uses to say it, in
/// one place, beside <see cref="Supersession"/>. The passage's frame, the titled index and the screen's header H6 compose their line
/// from here, so no form can edit its words alone (R10.74).
///
/// <para>Three states, and they are not the same: a standing acceptance (the answer by post id, or by digest and that the Forum does not
/// serve it), <b>none accepted</b> (the member was <c>null</c>), and <b>NOT STATED</b> (the Forum served no member, which is not "none").
/// A post that is not a question has no line. Every value in it is a display literal (R10.63); the fixed words are this client's own.
/// The statement is the Forum's word (R10.79's basis: no signature covers an acceptance, and nothing is checked about the answer) and
/// is no rating and no badge (R10.50). It moves no verdict and no exit code.</para>
/// </summary>
public static class AnswerStanding
{
    /// <summary>
    /// The parenthesis of <see cref="Passage.AcceptedStatement"/> (the Forum's word, what no signature covers, what an acceptance checks),
    /// so this line, the resolution line of <c>curia_read</c> and the line that names the same post cannot drift apart.
    /// </summary>
    public static readonly string Basis = Passage.AcceptedStatement[Passage.AcceptedStatement.IndexOf('(', StringComparison.Ordinal)..];

    private const string NotStated = "NOT STATED (the Forum served no word on which answer, if any, this thread has accepted, so do not take it as none)";

    private const string NoneAccepted = "none accepted (the Forum's word as of this read; it says nothing of whether answers exist)";

    /// <summary>The passage's frame line, <c>answers  ...</c>, for a post of kind <paramref name="kind"/> carrying <paramref name="member"/>; nothing for any other kind.</summary>
    public static OwnText? PassageLine(string kind, AcceptedAnswerMember? member) =>
        Statement(kind, member) is { } statement ? new OwnText($"answers  {statement.Text}") : null;

    /// <summary>The one-row form (the titled index's row, the screen's H6): <c>answers ...</c>, or nothing for a post that is not a question.</summary>
    public static OwnText? Row(string kind, AcceptedAnswerMember? member) =>
        Statement(kind, member) is { } statement ? new OwnText($"answers {statement.Text}") : null;

    private static OwnText? Statement(string kind, AcceptedAnswerMember? member)
    {
        if (!string.Equals(kind, PostKinds.Wire(PostKind.Question), StringComparison.Ordinal)) return null;
        if (member is null) return new OwnText(NotStated);
        if (member.IsNone) return new OwnText(NoneAccepted);

        return member.PostId is { } id
            ? new OwnText($"accepted: post {DisplayLiteral.Of(id)}, chosen by the thread's asker {Basis}")
            : new OwnText($"accepted: the answer with digest {DisplayLiteral.Of(member.Digest!)} (not served), chosen by the thread's asker {Basis}");
    }
}
