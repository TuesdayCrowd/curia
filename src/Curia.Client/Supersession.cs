using System.Collections.Immutable;
using Curia.Canon.Json;
using Curia.Domain.Serving;

namespace Curia.Client;

/// <summary>
/// R10.80 (errata G32): the Forum's word that a post has been superseded by a revision of its author's, and every word a reader
/// uses to say it, in one place. The passage's frame, the titled index, the screen's header H6 and (Task 5) the verification
/// each compose their line from here, so no form can edit its words alone (<see cref="Passage.AcceptedStatement"/>'s lesson,
/// R10.74).
///
/// <para>Three states, and they are not the same: a list the Forum did not serve at all (<see cref="ImmutableArray{T}.IsDefault"/>)
/// is <b>not stated</b>, which a reader prints so that silence is never read as "current"; an empty list is silence; a populated
/// one is the statement. Every value in it (a digest, a post id) is a display literal (R10.63); the fixed words are this client's
/// own. The statement is the Forum's word with no signature behind it, and is no badge (R10.50). It moves no verdict and no exit
/// code: the original is still served and its signature is checked as it always was (R9.28).</para>
/// </summary>
public static class Supersession
{
    /// <summary>The basis every form states beside a supersession: whose word it is, what covers it, and what to do.</summary>
    public const string Basis = "(the Forum's word: no signature covers this relation. The original is still served and its signature is checked above; read the revision before citing this post)";

    /// <summary>The short form of the not-stated state, for the forms that are one row (the index, the screen's header).</summary>
    public const string NotStatedShort = "revision NOT STATED";

    /// <summary>The passage's not-stated line: it says why the absence matters.</summary>
    public const string NotStatedLong = "revision  NOT STATED (the Forum served no word on whether a revision supersedes this post, so do not take it as the last word)";

    /// <summary>
    /// The mark a form writes after a digest the Forum lists and does not serve, in one place (errata G50, R10.91; PR 3's behaviour-preserving seam: the words are
    /// as they were). <see cref="NotServedWords"/> is the phrase without its parentheses, for the clauses that compose it into a longer sentence.
    /// </summary>
    public const string NotServedMark = "(not served)";

    /// <summary>The phrase <see cref="NotServedMark"/> puts in parentheses, for a clause that says it in a longer sentence (<c>is not served; recheck ...</c>).</summary>
    public const string NotServedWords = "not served";

    /// <summary>
    /// R10.91's definition, declared by PR 3's Task 0 and used by nothing yet: what a digest-only entry means and what to do about it. The probes
    /// (RLW9 to RLW15, RLW43) read G50's text and not this constant, and are red until every form says it.
    /// </summary>
    public const string DigestOnlyWords = "not served; recheck of its digest says whether it is withheld";

    /// <summary>The fork's sentence: with more than one revision chaining to a post, the Forum does not pick one, and neither does a reader.</summary>
    internal const string ForkedSentence = " FORKED: more than one revision chains here and the Forum does not pick one";

    /// <summary>True when the Forum served a non-empty list.</summary>
    public static bool IsSuperseded(ImmutableArray<Successor> list) => !list.IsDefaultOrEmpty;

    /// <summary>The revisions, each digest and the post that holds it (or that it is not served), as display literals.</summary>
    public static OwnText Revisions(ImmutableArray<Successor> list) =>
        new(string.Join(", ", list.Select(s => s.PostId is { } id
            ? $"{DisplayLiteral.Of(s.Digest)} (post {DisplayLiteral.Of(id)})"
            : $"{DisplayLiteral.Of(s.Digest)} {NotServedMark}")));

    /// <summary>
    /// The statement of a populated list: how many revisions, which, the basis, and the fork sentence when there is more than one.
    /// </summary>
    public static OwnText Statement(ImmutableArray<Successor> list) =>
        new($"SUPERSEDED by {list.Length} revision(s) of this post's author: {Revisions(list).Text} -- {Basis}{(list.Length > 1 ? ForkedSentence : string.Empty)}");

    /// <summary>
    /// The one-row form (the titled index's row, the screen's H6), without a leading label: <c>superseded by ... basis</c>, or
    /// <see cref="NotStatedShort"/>, or nothing for an empty list.
    /// </summary>
    public static OwnText? Row(ImmutableArray<Successor> list)
    {
        if (list.IsDefault) return new OwnText(NotStatedShort);
        if (list.IsEmpty) return null;
        return new OwnText($"superseded by {Revisions(list).Text} {Basis}{(list.Length > 1 ? ForkedSentence : string.Empty)}");
    }

    /// <summary>
    /// R10.83: what a <c>current</c> line says it established, which <c>curia</c> and <c>curia-mcp</c> compose from here. The Forum reports no
    /// later revision of the document and this is its standing in the log; it is no instruction to cite, and no signature and no acceptance was
    /// checked there. Fixed words only: a Forum value is never in it.
    /// </summary>
    public static OwnText CurrentStanding() =>
        new("the Forum reports no later revision of this document, which is its standing in the log; no signature and no acceptance was checked here (a standing is not soundness)");

    /// <summary>
    /// R10.83's words for a <c>fork</c> list: for a populated list (the other revisions; N is its length plus one) that the post is one of N
    /// revisions of the same post, FORKED, each other revision as <see cref="Revisions"/> gives it, and that the Forum picks none; for the
    /// default array (the member not served) <c>fork NOT STATED</c>; for an empty list nothing, since a post that is not a branch has nothing to say.
    /// </summary>
    public static OwnText ForkBranch(ImmutableArray<Successor> fork)
    {
        if (fork.IsDefault) return new OwnText("fork NOT STATED (the Forum served no word on whether this post is one branch of a fork)");
        if (fork.IsEmpty) return new OwnText(string.Empty);
        return new OwnText($"this post is one of {fork.Length + 1} revisions of the same post and the chain is FORKED; the other(s): {Revisions(fork).Text}; the Forum picks none");
    }

    /// <summary>
    /// The core of what is said of a revised question, shared by the answer's receipt (<see cref="QuestionStanding"/>) and the refusal under a revision
    /// (<see cref="RevisedQuestionPointer"/>) so the two cannot word the same list differently (errata G47, R11.62): how many revisions the question's
    /// <c>superseded_by</c> lists and which, each by post id or as a digest not served. For a populated list only; the tails are the callers' own, because
    /// "this answer went to the question as named" is about an answer that was sent and is false in a refusal where nothing was.
    /// </summary>
    internal static OwnText QuestionCore(ImmutableArray<Successor> list) =>
        new($"SUPERSEDED by {list.Length} revision(s) of the question: {Revisions(list).Text}");

    /// <summary>
    /// R11.55's <c>question</c> line of an accepted answer's receipt, from the question's <c>superseded_by</c> at the read this tool already
    /// made: superseded (each revision named, FORKED for two or more), <c>none was reported</c>, or <c>NOT STATED</c>. A note and never a
    /// refusal: answering the original can be right.
    /// </summary>
    public static OwnText QuestionStanding(ImmutableArray<Successor> list)
    {
        if (list.IsDefault) return new OwnText("NOT STATED (the Forum served no word on whether a revision supersedes the question, as of the read of it before this answer)");
        if (list.IsEmpty) return new OwnText("none was reported (the Forum's word on the question at the read before this answer; a revision that landed since is not seen)");
        return new OwnText(
            $"{QuestionCore(list).Text} (the Forum's word as of the read before this answer; this answer went to the question as named, which can be right){(list.Length > 1 ? ForkedSentence : string.Empty)}");
    }

    /// <summary>
    /// R11.62: the refusal's remedy under a revision, from the two reads the walk already made. The question the Forum's <c>superseded_by</c> confirms the
    /// named revision revises, by its post id; that question's standing (<see cref="QuestionCore"/>, FORKED for two or more); and the named revision's own
    /// (<paramref name="named"/>, the list the named post served): the latest the Forum reports where it is empty, the revisions that supersede it where it
    /// is not, <c>NOT STATED</c> where the Forum served none. Only called where the walk found the question's list populated and holding the revision's
    /// digest, so there is no arm for a question that is not superseded; where the walk ends without naming a question it states no standing at all.
    /// No hedge: the tool read the question and says what it read. The original is still the only legal parent, so the sentence says which text stands and
    /// leaves the remedy as it was.
    /// </summary>
    public static OwnText RevisedQuestionPointer(string questionPostId, ImmutableArray<Successor> question, ImmutableArray<Successor> named)
    {
        ArgumentNullException.ThrowIfNull(questionPostId);
        if (question.IsDefaultOrEmpty) throw new ArgumentException("The walk names a question only where its superseded_by is populated.", nameof(question));

        var question1 = $"{QuestionCore(question).Text}{(question.Length > 1 ? ForkedSentence : string.Empty)}";
        var own = named.IsDefault
            ? "the Forum served no word on whether a later revision supersedes it (NOT STATED), so do not take it as the last word"
            : named.IsEmpty
                ? "it is the latest the Forum reports (no revision supersedes it, as of this read)"
                : $"it is superseded by {named.Length} revision(s): {Revisions(named).Text}{(named.Length > 1 ? ForkedSentence : string.Empty)}";

        return new OwnText(
            $"The post you named is a revision. The Forum's superseded_by confirms it revises the question {DisplayLiteral.Of(questionPostId)} (the Forum's word as of this read). The question's standing: {question1}. As for the revision you named, {own}. Answer that question instead: the original is the only legal parent, so say in the answer which text it answers.");
    }

    /// <summary>
    /// R11.67's words for the walk down a revised question's chain, declared by PR 3's Task 0 and called by nothing: the seam returns no words. Task 4
    /// builds them from <see cref="QuestionCore"/> so the receipt and the refusal cannot word one list differently.
    /// </summary>
    internal static OwnText QuestionChainStanding(QuestionChainResult walk)
    {
        ArgumentNullException.ThrowIfNull(walk);
        return new OwnText(string.Empty);
    }

    /// <summary>What a verification adds to the statement: whose read it is about, and that a later read may differ (R11.44, ruling 9).</summary>
    private const string AsOfTheRead = " (the Forum's word as of the read this verification is about; read the post again for now)";

    /// <summary>
    /// The verification's line, without its label (<c>revision    </c> is the caller's): the statement, or <c>none reported</c>, or
    /// <c>NOT STATED</c>, each as of the read the verification is about. It is not a check and is in no verdict (R11.44).
    /// </summary>
    public static OwnText VerificationLine(ImmutableArray<Successor> list)
    {
        if (list.IsDefault) return new OwnText("NOT STATED (the Forum served no word on whether a revision supersedes this post, as of the read this verification is about)");
        if (list.IsEmpty) return new OwnText("none reported (the Forum's word as of the read this verification is about)");
        return new OwnText(Statement(list).Text + AsOfTheRead);
    }

    /// <summary>The passage's frame line, <c>revision  ...</c>, or nothing for an empty list.</summary>
    public static OwnText? PassageLine(ImmutableArray<Successor> list)
    {
        if (list.IsDefault) return new OwnText(NotStatedLong);
        if (list.IsEmpty) return null;
        return new OwnText($"revision  {Statement(list).Text}");
    }

    /// <summary>
    /// R11.62 (errata G47): the first block's <c>revision</c> line for the root a thread was read from: the root's own supersession in the passage's words
    /// (<see cref="PassageLine"/>: <c>NOT STATED</c> for a list the Forum did not serve, nothing for an unrevised root) and, for a superseded root, the text
    /// that stands among the posts the thread holds (<see cref="TextThatStands"/>). Written from the root, never from the post that was read.
    /// </summary>
    public static OwnText? RootLine(ProvenancePost root, ImmutableArray<ProvenancePost> members)
    {
        ArgumentNullException.ThrowIfNull(root);

        var line = PassageLine(root.Provenance.SupersededBy);
        return line is not null && IsSuperseded(root.Provenance.SupersededBy)
            ? new OwnText($"{line.Value.Text}; {TextThatStands(root, members).Text}")
            : line;
    }

    /// <summary>
    /// The text that stands among <paramref name="members"/>, found by following each successor digest of <paramref name="root"/> to the member that bears
    /// it and recursing through that member's own <c>superseded_by</c>, to any depth (each member is followed once, so the walk is bounded by the member
    /// count). A member whose list is empty is a head; a member whose list the Forum did not serve is named as NOT STATED and is no head; a digest no
    /// member bears leads outside what the thread holds and is said to, and no head is claimed for it. A fork names every head and picks none. Computed
    /// from the thread's members alone: no read is made, and the claim is only as complete as the Forum's lists are.
    /// </summary>
    public static OwnText TextThatStands(ProvenancePost root, ImmutableArray<ProvenancePost> members)
    {
        ArgumentNullException.ThrowIfNull(root);

        var heads = new List<string>();
        var notStated = new List<string>();
        var outside = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { root.PostId };

        void Follow(ProvenancePost node)
        {
            foreach (var successor in node.Provenance.SupersededBy.IsDefault ? [] : node.Provenance.SupersededBy)
            {
                var member = members.IsDefault ? null : members.FirstOrDefault(m => string.Equals(m.Digest, successor.Digest, StringComparison.Ordinal));
                if (member is null)
                {
                    outside.Add(successor.Digest);
                    continue;
                }

                if (!visited.Add(member.PostId)) continue;
                if (member.Provenance.SupersededBy.IsDefault) notStated.Add(member.PostId);
                else if (member.Provenance.SupersededBy.IsEmpty) heads.Add(member.PostId);
                else Follow(member);
            }
        }

        Follow(root);

        var parts = new List<string>();
        parts.Add(heads.Count switch
        {
            0 => "the text that stands is not named: no post this thread holds is a head of the chain",
            1 when outside.Count > 0 => $"the text that stands is not settled here: this thread holds one head, post {DisplayLiteral.Of(heads[0])}, and another successor leads outside it",
            1 => $"the text that stands among the posts this thread holds is post {DisplayLiteral.Of(heads[0])}",
            _ => $"the text that stands is not one text, the chain is FORKED: the heads this thread holds are {string.Join(", ", heads.Select(h => "post " + DisplayLiteral.Of(h)))}, and the Forum picks none",
        });
        if (outside.Count > 0)
            parts.Add($"{string.Join(", ", outside.Select(DisplayLiteral.Of))} leads outside what this thread holds, so no head is claimed for it");
        if (notStated.Count > 0)
            parts.Add($"{string.Join(", ", notStated.Select(n => "post " + DisplayLiteral.Of(n)))}: revision NOT STATED, so no head is claimed past it");

        return new OwnText(string.Join("; ", parts));
    }

    /// <summary>
    /// R11.47 (errata G34): the refusal <c>curia_revise</c> gives for a post the Forum serves as superseded, naming the post to revise
    /// instead (its post id, or the digest and that it is not served), and saying the chain is already FORKED where there is more than one.
    /// Null for a list that is not a statement (unstated or empty), which is the caller's cue to proceed. Every value is a display literal
    /// (R10.63); the opening words are the caller's <c>NOT SENT</c> prefix and are written here so no form words them alone.
    /// </summary>
    public static OwnText? RevisionRefusal(ImmutableArray<Successor> list)
    {
        if (!IsSuperseded(list)) return null;
        var named = string.Join(", ", list.Select(s => s.PostId is { } id
            ? DisplayLiteral.Of(id)
            : $"{DisplayLiteral.Of(s.Digest)} ({NotServedWords}: there is no post id to revise)"));
        // One successor: the Forum says only that it supersedes this post, and it may itself have been superseded, so it is never called current.
        return list.Length == 1
            ? new OwnText(list[0].PostId is null
                ? $"NOT SENT: the Forum reports this post superseded by {named}; that revision cannot be addressed by post id and the Forum does not serve it. Nothing was sent."
                : $"NOT SENT: the Forum reports this post superseded by {named}; revise that post instead, and read it first, since it may itself have been revised. Nothing was sent.")
            : new OwnText($"NOT SENT: the Forum reports this post superseded by {list.Length} revisions: {named}; the chain is already FORKED, and the Forum does not pick one. Choose which post to revise, and revise that one. Nothing was sent.");
    }

    /// <summary>
    /// R11.47: the line a revision's receipt carries beneath the label <c>supersession</c>, so a receipt is never silent on the question.
    /// An unstated list says it was <b>NOT CHECKED</b>; an empty one says none was reported, as of the read (the read-to-write race is a
    /// stated non-property). The two differ by their first words, so "checked and clear" and "not checked" are never one line. A populated
    /// list is refused before a receipt exists, and reads here as the statement for completeness.
    /// </summary>
    public static OwnText ReceiptLine(ImmutableArray<Successor> list)
    {
        if (list.IsDefault) return new OwnText("NOT CHECKED (the Forum served no word on whether a revision supersedes this post, so this revision was not checked against one)");
        if (list.IsEmpty) return new OwnText("none was reported (the Forum's word at the read; a revision that landed since is not seen)");
        return Statement(list);
    }
}
