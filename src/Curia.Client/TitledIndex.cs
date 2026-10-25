using Curia.Canon.Json;
using Curia.Domain.Serving;

namespace Curia.Client;

/// <summary>
/// R10.71 (errata G19): an index whose titles come from the verified canonical form, as content.
///
/// <para><b>A title is content.</b> It is prose an author wrote, so it is written as a span of its own,
/// rendered by this client under the requested marking, never as a literal on a frame line: R10.63
/// keeps the reader's voice and an author's words apart, and a literal on a frame row would put an
/// author's sentence in the reader's position.</para>
///
/// <para><b>A title is one line.</b> A body span is canonical JSON, which writes a line feed as an
/// escape; a title is a decoded string and can hold one. <see cref="SpanText.Line"/> writes line feeds
/// and tabs as escapes too, before the title is rendered, so no title can close its span with a
/// near-delimiter and set a row of the reader's own beneath it. Escape, then render: escaping after
/// <see cref="Datamarking.Delimit"/> would turn the delimiters' own line feeds into escapes.</para>
///
/// <para><b>Only the covered bytes' title.</b> The title is read from the served canonical form, never
/// from the Forum's span, and only when the signature verified and the served form is the bytes it
/// covers; otherwise nothing of the post is shown as content.</para>
/// </summary>
public static class TitledIndex
{
    /// <summary>
    /// The title member of <see cref="Passage.Envelope"/>, or <see langword="null"/> when the post has
    /// none, the envelope does not parse, the passage's signature outcome (<see cref="Passage.SignatureOutcome"/>) is not Verified (so a disputed title is never shown, R6.57), or the served form is not
    /// ordinal-equal to <see cref="SpanCheck.CoveredForm"/>. <see cref="Passage.Outcome"/> is not
    /// consulted: the title is not taken from the span.
    /// </summary>
    public static string? Title(Passage passage)
    {
        ArgumentNullException.ThrowIfNull(passage);

        if (passage.SignatureOutcome != CheckOutcome.Verified) return null;
        if (!string.Equals(SpanCheck.CoveredForm(passage.Post), passage.Post.Canonical, StringComparison.Ordinal)) return null;

        return passage.Envelope?.Title;
    }

    /// <summary>
    /// The post's frame lines, then its title as a span of its own, or a line saying why there is none.
    /// A search hit's <paramref name="score"/> sits on a frame line of its own beneath the id line, with
    /// the level read from the passage's post, then its why rows (<see cref="SearchScore.WriteWhy"/>).
    /// A <c>content   FAILED:</c> line after the parent line says the Forum's span did not render the
    /// canonical form, whenever the span check fails the passage (delimited or not), whatever the
    /// signature verdict (EXIT CODES 6).
    /// </summary>
    public static void Write(FrameBuilder frame, int number, Passage passage, MarkingMode marking, SearchScore? score)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(passage);

        var post = passage.Post;

        frame.Line($"#{number} post {post.PostId}  {post.Kind}  {post.ServerTs}  signature {SignatureWord(passage)}");
        // R10.79: directly beneath the first line, the row states an acceptance the Forum serves, as the line form's
        // frame does; a screen's list rows are the exception (IndexRow writes the first line only) and its header
        // states it (R10.74).
        if (post.Accepted) frame.Line($"   {new OwnText(Passage.AcceptedStatement)}");
        // R10.80: and a supersession the Forum serves, or that it served none, from Supersession's words; an empty list is silence.
        if (Supersession.Row(post.Provenance.SupersededBy) is { } superseded) frame.Line($"   {superseded}");

        if (score is not null)
        {
            frame.Line($"   score {score.ScoreMicro} µ   {post.Provenance.VerificationLevel}");
            if (score.Why is { } why) SearchScore.WriteWhy(frame, why);
        }

        if (post.Parent is { Length: > 0 } parent) frame.Line($"   parent {parent}");
        else frame.Line("   parent none");

        var title = Title(passage);

        if (ContentFailed(passage, title is not null) is { } failed)
            frame.Line($"   {failed}");

        if (title is not null)
        {
            frame.Span(Datamarking.Render(SpanText.Line(title), marking));
            return;
        }

        frame.Line($"   {NoTitle(passage)}");
    }

    /// <summary>
    /// The word the index writes after <c>signature</c>: <see cref="Passage.SignatureOutcome"/>'s, so the Forum's mark and
    /// the key set's word are one (R6.57). The span is a separate row (<see cref="ContentFailed"/>).
    /// </summary>
    public static OwnText SignatureWord(Passage passage)
    {
        ArgumentNullException.ThrowIfNull(passage);

        return new OwnText(passage.SignatureOutcome switch
        {
            CheckOutcome.Verified => "verified",
            CheckOutcome.CouldNotCheck => "COULD NOT BE CHECKED",
            CheckOutcome.Disputed => "DISPUTED",
            CheckOutcome.Failed => "FAILED",
            _ => "FAILED",
        });
    }

    /// <summary>
    /// The sentence saying the Forum's span did not render the canonical form (R10.71), or <see langword="null"/> when
    /// it did. <paramref name="titleShown"/> chooses between the two endings the index has always had.
    /// </summary>
    public static OwnText? ContentFailed(Passage passage, bool titleShown)
    {
        ArgumentNullException.ThrowIfNull(passage);

        if (SpanCheck.RendersCanonical(passage.Post)) return null;

        return titleShown
            ? new OwnText("content   FAILED: the Forum's span is not the rendering of this post's canonical form; the title shown is from the canonical form (R10.71).")
            : new OwnText("content   FAILED: the Forum's span is not the rendering of this post's canonical form; no title is shown (R10.71).");
    }

    /// <summary>The parenthesised sentence the index writes where it shows no title, and why.</summary>
    public static OwnText NoTitle(Passage passage)
    {
        ArgumentNullException.ThrowIfNull(passage);

        if (passage.SignatureOutcome == CheckOutcome.CouldNotCheck)
            return new OwnText("(no title: the signature could not be checked, so nothing it covers is shown, R10.71)");
        if (passage.SignatureOutcome == CheckOutcome.Disputed)
            return new OwnText("(no title: the key that signed it was declared compromised from before this post, so it is disputed, R6.57; read it with curia read, verify it with curia verify)");
        if (passage.SignatureOutcome != CheckOutcome.Verified)
            return new OwnText("(no title: the signature did not verify, so nothing it covers is shown, R10.71)");
        if (!string.Equals(SpanCheck.CoveredForm(passage.Post), passage.Post.Canonical, StringComparison.Ordinal))
            return new OwnText("(no title: the served form is not the bytes the signature covers, R10.71)");

        return new OwnText("(no title: this post has none)");
    }
}
