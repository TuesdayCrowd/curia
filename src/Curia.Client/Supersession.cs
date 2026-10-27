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

    /// <summary>The fork's sentence: with more than one revision chaining to a post, the Forum does not pick one, and neither does a reader.</summary>
    internal const string ForkedSentence = " FORKED: more than one revision chains here and the Forum does not pick one";

    /// <summary>True when the Forum served a non-empty list.</summary>
    public static bool IsSuperseded(ImmutableArray<Successor> list) => !list.IsDefaultOrEmpty;

    /// <summary>The revisions, each digest and the post that holds it (or that it is not served), as display literals.</summary>
    public static OwnText Revisions(ImmutableArray<Successor> list) =>
        new(string.Join(", ", list.Select(s => s.PostId is { } id
            ? $"{DisplayLiteral.Of(s.Digest)} (post {DisplayLiteral.Of(id)})"
            : $"{DisplayLiteral.Of(s.Digest)} (not served)")));

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
    /// SEAM (PR 2, Task 0): R10.83's words for a <c>current</c> line, which Task 2 composes from here for <c>curia</c> and <c>curia-mcp</c> alike.
    /// Unused until then; it says nothing, so no reader's output moves.
    /// </summary>
    public static OwnText CurrentStanding() => new(string.Empty);

    /// <summary>
    /// SEAM (PR 2, Task 0): R10.83's fork-branch words for the other revisions <paramref name="fork"/> lists (N is its length plus one).
    /// Unused until Task 2; it says nothing.
    /// </summary>
    public static OwnText ForkBranch(ImmutableArray<Successor> fork) => new(string.Empty);

    /// <summary>
    /// SEAM (PR 2, Task 0): R11.55's <c>question</c> line of an accepted answer's receipt, from the question's <c>superseded_by</c> at the read.
    /// Unused until Task 3; it says nothing.
    /// </summary>
    public static OwnText QuestionStanding(ImmutableArray<Successor> list) => new(string.Empty);

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
            : $"{DisplayLiteral.Of(s.Digest)} (not served: there is no post id to revise)"));
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
