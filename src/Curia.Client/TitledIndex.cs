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
    /// none, the envelope does not parse, the verdict is not Verified, or the served form is not
    /// ordinal-equal to <see cref="SpanCheck.CoveredForm"/>. <see cref="Passage.Outcome"/> is not
    /// consulted: the title is not taken from the span.
    /// </summary>
    public static string? Title(Passage passage)
    {
        ArgumentNullException.ThrowIfNull(passage);

        if (passage.Verdict.Outcome != CheckOutcome.Verified) return null;
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
        var signature = passage.Verdict.Outcome switch
        {
            CheckOutcome.Verified => "verified",
            CheckOutcome.CouldNotCheck => "COULD NOT BE CHECKED",
            CheckOutcome.Failed => "FAILED",
            _ => "FAILED",
        };

        frame.Line($"#{number} post {post.PostId}  {post.Kind}  {post.ServerTs}  signature {new OwnText(signature)}");
        if (score is not null)
        {
            frame.Line($"   score {score.ScoreMicro} µ   {post.Provenance.VerificationLevel}");
            if (score.Why is { } why) SearchScore.WriteWhy(frame, why);
        }

        if (post.Parent is { Length: > 0 } parent) frame.Line($"   parent {parent}");
        else frame.Line("   parent none");

        var title = Title(passage);

        if (!SpanCheck.RendersCanonical(post))
        {
            if (title is not null)
                frame.Line("   content   FAILED: the Forum's span is not the rendering of this post's canonical form; the title shown is from the canonical form (R10.71).");
            else
                frame.Line("   content   FAILED: the Forum's span is not the rendering of this post's canonical form; no title is shown (R10.71).");
        }

        if (title is not null)
        {
            frame.Span(Datamarking.Render(SpanText.Line(title), marking));
            return;
        }

        if (passage.Verdict.Outcome == CheckOutcome.CouldNotCheck)
            frame.Line("   (no title: the signature could not be checked, so nothing it covers is shown, R10.71)");
        else if (passage.Verdict.Outcome != CheckOutcome.Verified)
            frame.Line("   (no title: the signature did not verify, so nothing it covers is shown, R10.71)");
        else if (!string.Equals(SpanCheck.CoveredForm(post), post.Canonical, StringComparison.Ordinal))
            frame.Line("   (no title: the served form is not the bytes the signature covers, R10.71)");
        else
            frame.Line("   (no title: this post has none)");
    }
}
