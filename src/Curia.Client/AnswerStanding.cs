using System.Collections.Immutable;
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

    /// <summary>The words of the <c>NOT STATED</c> state (the Forum served no <c>accepted_answer</c> on a question), for the readers that state it in another form.</summary>
    public const string NotStatedWords = "NOT STATED (the Forum served no word on which answer, if any, this thread has accepted, so do not take it as none)";

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
        if (member is null) return new OwnText(NotStatedWords);
        if (member.IsNone) return new OwnText(NoneAccepted);

        // R10.82, R10.85: the post the acceptance names, as this line calls it. A literal for every value the Forum supplied (R10.63). The kind
        // is the Forum's metadata beside the digest and survives a post it does not serve (9b): a revision is told by its kind, an answer is told as
        // the G36 line told it (byte for byte), and a member with no kind is NOT STATED, never read as either word.
        var isRevision = string.Equals(member.Kind, "revision", StringComparison.Ordinal);
        var unknownKind = member.Kind is null;
        var named = member.PostId is { } id
            ? $"post {DisplayLiteral.Of(id)}"
            : $"the {(isRevision ? "revision" : unknownKind ? "post" : "answer")} with digest {DisplayLiteral.Of(member.Digest!)} (not served)";
        var kindTag = member.PostId is null
            ? string.Empty
            : isRevision ? " (a revision)" : unknownKind ? " (kind NOT STATED: the Forum served the accepted post without saying whether it is an answer or a revision)" : string.Empty;
        if (member.PostId is null && unknownKind) kindTag = " (kind NOT STATED: the Forum did not say whether it is an answer or a revision)";

        // An empty list changes nothing, byte for byte (the G36 literal); an absent one is said, since silence reads as "not revised".
        if (member.SupersededBy.IsDefault)
            return new OwnText($"accepted: {named}{kindTag}, chosen by the thread's asker {Basis}; {Supersession.NotStatedShort} (the Forum served the member without a word on whether the answer has been revised, so do not take it as unrevised)");

        if (member.SupersededBy.IsEmpty)
            return new OwnText($"accepted: {named}{kindTag}, chosen by the thread's asker {Basis}");

        return new OwnText($"accepted: {named}{kindTag}, which its author has since revised: {RevisionStatement(named, member.SupersededBy).Text} {Basis}");
    }

    /// <summary>
    /// R10.82's statement of a populated list: that the accepted answer has been superseded, by which revisions (FORKED where more than one),
    /// and that the acceptance names <paramref name="named"/> and not its revision; R10.85: and how the thread's asker accepts the revision
    /// (<paramref name="acceptWith"/> is the way this reader says it: <c>curia resolve</c> by default, the tool's name in <c>curia_read</c>).
    /// The caller supplies the answer as it names it (already a display literal); the revisions are written as <see cref="Supersession.Revisions"/>
    /// writes them. The Forum's word, with no verdict in it.
    /// </summary>
    public static OwnText RevisionStatement(string named, ImmutableArray<Successor> list, string acceptWith = "curia resolve") =>
        new($"SUPERSEDED by {Supersession.Revisions(list).Text}{(list.Length > 1 ? Supersession.ForkedSentence : string.Empty)} -- the acceptance names {named}, not its revision; read the revision before relying on it; the thread's asker can accept the revision with {acceptWith} <revision post id>");
}
