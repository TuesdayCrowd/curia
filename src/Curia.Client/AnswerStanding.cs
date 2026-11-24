using System.Collections.Immutable;
using System.Globalization;
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

    /// <summary>
    /// <see cref="NoneAccepted"/> where the count of answers is served (R10.81 (rev.)): the sentence that says nothing is known of whether answers exist is retired, because the count
    /// beside it says; what remains is whose word it is.
    /// </summary>
    private const string NoneAcceptedCounted = "none accepted (the Forum's word as of this read)";

    /// <summary>
    /// R10.100: what the count counts, said once beside the line of every question whose count the Forum served: posts the Forum serves, and no judgement of any answer. Not said where the count is
    /// NOT STATED, because there is no count to gloss.
    /// </summary>
    internal const string CountGloss = "; served counts posts the Forum serves and is not a judgement of the answers";

    /// <summary>
    /// Both the count and the acceptance unstated (an older Forum): one statement and not two, so a row that carried only the acceptance's NOT STATED stays as short as it was (the screen's header at 80 columns
    /// holds it in the rows it held).
    /// </summary>
    private const string BothNotStated = "NOT STATED (the Forum served no count and no word on which answer, if any, this thread has accepted, so do not take it as none)";

    /// <summary>The count's words where the Forum served none (R10.100): NOT STATED, never 0.</summary>
    private const string CountNotStated = "NOT STATED (no count; not 0)";

    /// <summary>
    /// The passage's frame line, <c>answers  ...</c>, for a post of kind <paramref name="kind"/> carrying <paramref name="member"/> and <paramref name="answers"/>; nothing for any other kind.
    /// A null <paramref name="answers"/> is a count the Forum did not serve (<c>NOT STATED</c>), never 0.
    /// </summary>
    public static OwnText? PassageLine(string kind, AcceptedAnswerMember? member, AnswersMember? answers = null) =>
        Statement(kind, member, answers) is { } statement ? new OwnText($"answers  {statement.Text}") : null;

    /// <summary>The one-row form (the titled index's row, the screen's H6): <c>answers ...</c>, or nothing for a post that is not a question.</summary>
    public static OwnText? Row(string kind, AcceptedAnswerMember? member, AnswersMember? answers = null) =>
        Statement(kind, member, answers) is { } statement ? new OwnText($"answers {statement.Text}") : null;

    /// <summary>
    /// The count alone, as a listing row carries it (the inbox's, R10.101): <c>answers N served</c>, or <c>answers NOT STATED</c> where the Forum served none; nothing for a post that is not a question.
    /// </summary>
    public static OwnText? CountRow(string kind, AnswersMember? answers) =>
        IsQuestion(kind)
            ? new OwnText(answers is null ? "answers NOT STATED" : $"answers {answers.Served.ToString(CultureInfo.InvariantCulture)} served")
            : null;

    /// <summary>
    /// The count as the default search hit line carries it: <c>answers N served</c> only where the post is a question and the Forum served the member, so a hit line of an older Forum, which prints no kind
    /// and no count today, is unchanged.
    /// </summary>
    public static OwnText? HitCount(string kind, AnswersMember? answers) =>
        IsQuestion(kind) && answers is not null ? new OwnText($"answers {answers.Served.ToString(CultureInfo.InvariantCulture)} served") : null;

    private static bool IsQuestion(string kind) => string.Equals(kind, PostKinds.Wire(PostKind.Question), StringComparison.Ordinal);

    /// <summary>
    /// R10.100, ruling 27: where the acceptance's instant is served, <c> (accepted at "instant")</c> as the Forum's word and a display literal; where the member is an object that does not carry it,
    /// <c> (accepted at NOT STATED)</c>. Every form that says an acceptance composes from here and from <see cref="RevisionOrder"/>.
    /// </summary>
    public static string AcceptedAt(string? instant) =>
        instant is null ? " (accepted at NOT STATED)" : $" (accepted at {DisplayLiteral.Of(instant)})";

    /// <summary>
    /// R10.100, ruling 27: for an acceptance whose <c>superseded_by</c> is not empty, whether the first revision of the post it names came before or after the acceptance, decided by the Forum from the log's
    /// order (R9.71). "Since revised" is said of <c>after_acceptance</c> and of no other state; an absent relation is NOT STATED and never read as either word.
    /// </summary>
    public static string RevisionOrder(string? firstRevision) => firstRevision switch
    {
        "after_acceptance" => "which its author has since revised",
        "before_acceptance" => "which its author had already revised when the asker accepted it",
        _ => "which its author has revised, and whether that was before or after the asker accepted it is NOT STATED",
    };

    private static OwnText? Statement(string kind, AcceptedAnswerMember? member, AnswersMember? answers)
    {
        if (!IsQuestion(kind)) return null;

        var counted = answers is not null;
        if (!counted && member is null) return new OwnText(BothNotStated);
        var count = answers is null ? CountNotStated : $"{answers.Served.ToString(CultureInfo.InvariantCulture)} served";
        return new OwnText($"{count}, {Acceptance(member, counted)}{(counted ? CountGloss : string.Empty)}");
    }

    private static string Acceptance(AcceptedAnswerMember? member, bool counted)
    {
        if (member is null) return NotStatedWords;
        if (member.IsNone) return counted ? NoneAcceptedCounted : NoneAccepted;

        // R10.82, R10.85: the post the acceptance names, as this line calls it. A literal for every value the Forum supplied (R10.63). The kind
        // is the Forum's metadata beside the digest and survives a post it does not serve (9b): a revision is told by its kind, an answer is told as
        // the G36 line told it (byte for byte), and a member with no kind is NOT STATED, never read as either word.
        var isRevision = string.Equals(member.Kind, "revision", StringComparison.Ordinal);
        var unknownKind = member.Kind is null;
        var named = member.PostId is { } id
            ? $"post {DisplayLiteral.Of(id)}"
            : $"the {(isRevision ? "revision" : unknownKind ? "post" : "answer")} with digest {DisplayLiteral.Of(member.Digest!)} {Supersession.DigestOnlyMark}";
        var kindTag = member.PostId is null
            ? string.Empty
            : isRevision ? " (a revision)" : unknownKind ? " (kind NOT STATED: the Forum served the accepted post without saying whether it is an answer or a revision)" : string.Empty;
        if (member.PostId is null && unknownKind) kindTag = " (kind NOT STATED: the Forum did not say whether it is an answer or a revision)";
        var at = AcceptedAt(member.AcceptedAt);

        // An empty list changes nothing but the instant; an absent one is said, since silence reads as "not revised".
        if (member.SupersededBy.IsDefault)
            return $"accepted: {named}{kindTag}{at}, chosen by the thread's asker {Basis}; {Supersession.NotStatedShort} (the Forum served the member without a word on whether the answer has been revised, so do not take it as unrevised)";

        if (member.SupersededBy.IsEmpty)
            return $"accepted: {named}{kindTag}{at}, chosen by the thread's asker {Basis}";

        return $"accepted: {named}{kindTag}{at}, {RevisionOrder(member.FirstRevision)}: {RevisionStatement(named, member.SupersededBy).Text} {Basis}";
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
