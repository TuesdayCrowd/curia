using Curia.Canon.Json;
using Curia.Domain.Content;
using Curia.Domain.Serving;

namespace Curia.Client;

/// <summary>
/// R10.100 (errata G76): the two lines that say which text a post is about, beside <see cref="Supersession"/> and <see cref="AnswerStanding"/>, so no form can word them alone: the pointer a
/// revision of a question carries (<c>provenance.revises</c>, R9.70) and the line an answer carries about its question's first revision (<c>provenance.question_revised</c>, R9.71 (b)).
///
/// <para>Every value the Forum supplied is a display literal (R10.63) and an id in a command is a shell word (R10.65, errata G17). Each line is the Forum's word, in this client's frame and never in a
/// span; each says what the relation is and nothing of whether anything fits or is current: no claim of relevance. They move no verdict and no exit code (R10.50).</para>
/// </summary>
public static class RevisedWords
{
    /// <summary>
    /// D117 (i), design 3: the pointer of a revision whose correction chain ends at a question (<c>revises</c>). It states that the text revises the post named, that the post is a question, and that its
    /// answers and the answer accepted are read there and not here, and names the command that reads that thread where the id is one a shell reads back as itself. A revision carrying no
    /// <c>revises</c> prints nothing (R10.100: an absent member is not a statement, ruling 25).
    /// </summary>
    public static OwnText? RevisesLine(string kind, RevisesMember? revises)
    {
        if (revises is null || !string.Equals(kind, PostKinds.Wire(PostKind.Revision), StringComparison.Ordinal)) return null;

        if (revises.PostId is not { } id)
            return new OwnText("this text revises a question that is " + Supersession.NotServedWords + " (the Forum names it and does not serve it, so its answers and the answer accepted cannot be read here or there)");

        var command = ShellWord.TryOf(id, out var word) ? $" (curia thread {word})" : " (that id is not one a command can carry; read the thread of the post named by its id)";
        return new OwnText($"this text revises post {DisplayLiteral.Of(id)} (a question): its answers and the answer accepted are read there, not here{command}");
    }

    /// <summary>
    /// R9.71 (b): an answer's relation to its question's first revision. <c>before</c>: the question was revised after the answer was posted, so the answer was posted against the text before the revision;
    /// <c>after</c>: the question was revised before, and which text the answer was written against is not stated. A question never revised carries no line. An answer whose Forum served no member reads
    /// NOT STATED. The first revision is named as the answer's receipt names it (<see cref="Supersession.Revisions"/>), one definition. Neither word says the answer fits either text.
    /// </summary>
    public static OwnText? QuestionRevised(string kind, QuestionRevisedMember? member)
    {
        if (!string.Equals(kind, PostKinds.Wire(PostKind.Answer), StringComparison.Ordinal)) return null;
        if (member is null)
            return new OwnText("NOT STATED (the Forum served no word on whether the question was revised before or after this answer was posted)");
        if (member.IsNone) return null;

        var first = Supersession.Revisions([new Successor(member.FirstRevisionDigest!, member.FirstRevisionPostId)]).Text;
        return string.Equals(member.AnswerPosted, "before", StringComparison.Ordinal)
            ? new OwnText($"revised after this answer was posted: the question's first revision is {first}; this answer was posted against the text before the revision (the Forum's word; it says nothing of whether the answer fits either text)")
            : new OwnText($"revised before this answer was posted: the question's first revision is {first}; which text this answer was written against is not stated (the Forum's word; it says nothing of whether the answer fits either text)");
    }
}
