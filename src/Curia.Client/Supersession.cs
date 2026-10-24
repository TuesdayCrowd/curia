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
    private const string Forked = " FORKED: more than one revision chains here and the Forum does not pick one";

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
        new($"SUPERSEDED by {list.Length} revision(s) of this post's author: {Revisions(list).Text} -- {Basis}{(list.Length > 1 ? Forked : string.Empty)}");

    /// <summary>
    /// The one-row form (the titled index's row, the screen's H6), without a leading label: <c>superseded by ... basis</c>, or
    /// <see cref="NotStatedShort"/>, or nothing for an empty list.
    /// </summary>
    public static OwnText? Row(ImmutableArray<Successor> list)
    {
        if (list.IsDefault) return new OwnText(NotStatedShort);
        if (list.IsEmpty) return null;
        return new OwnText($"superseded by {Revisions(list).Text} {Basis}{(list.Length > 1 ? Forked : string.Empty)}");
    }

    /// <summary>The passage's frame line, <c>revision  ...</c>, or nothing for an empty list.</summary>
    public static OwnText? PassageLine(ImmutableArray<Successor> list)
    {
        if (list.IsDefault) return new OwnText(NotStatedLong);
        if (list.IsEmpty) return null;
        return new OwnText($"revision  {Statement(list).Text}");
    }
}
