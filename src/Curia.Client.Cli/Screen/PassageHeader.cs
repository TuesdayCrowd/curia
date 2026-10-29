using System.Collections.Immutable;
using System.Globalization;
using Curia.Canon.Json;
using Curia.Client;
using Curia.Domain.Serving;

namespace Curia.Client.Cli;

/// <summary>Which content rows of a passage a pinned header sits over: rows <c>A</c> to <c>B</c> of <c>N</c>, counted from one.</summary>
internal readonly record struct ContinuedRange(int A, int B, int N);

/// <summary>
/// R10.74: the header every passage on a screen carries, as rows of this client's own (R10.72). It says what this client
/// established and what it did not, and states the key's status and the post's position as facts beside each other and
/// never as a conclusion: the conclusion is the one word <see cref="Passage.Outcome"/> holds, written by H4 and the tag.
///
/// <list type="bullet">
/// <item>H1: the post, its kind and board, its parent, then the outcome tag.</item>
/// <item>H2: the author and whether the owner is verified; the owner; <c>server_ts</c>.</item>
/// <item>H3: the digest computed here, the <c>kid</c>; a second row only when the Forum reported another digest.</item>
/// <item>H4: the verdict, <see cref="Passage.DescribeRead"/> itself, then what was not checked here.</item>
/// <item>H4b: only when the span check failed (R10.71).</item>
/// <item>H5: the key row, <see cref="KeyRow"/>.</item>
/// <item>H6: the Forum's own word: its level, an acceptance, a supersession (R10.80), contradictions and reproductions, risk flags, a mark of dispute.</item>
/// </list>
///
/// <para>A header takes two forms (spec 14.1). The compact form is the full one without H3's <c>digest ... kid</c> row and
/// without the owner and <c>server_ts</c> on H2; everything R10.74 lists stays, and so does the row that says the Forum
/// reported another digest. Which form a document uses is <see cref="Screen"/>'s choice, from the window's size alone.</para>
///
/// <para>A header is a set of frame rows: each begins with this client's own text, and every value it did not compose is a
/// display literal (R10.63, R10.64). A pinned header (a scroll that has left the passage's top above the window) is the
/// same rows with the content rows in view after H1.</para>
/// </summary>
internal static class PassageHeader
{
    /// <summary>The Forum's word for a key whose set says nothing of its status, said as that: absent is not active.</summary>
    internal const string NoStatusSentence = "the Forum's key set does not state a status";

    /// <summary>
    /// The row for a check that did not run with no key-set status to show (spec 14.8 item 4): true whether the key set was never
    /// obtained or was read and named no status, and it says neither that there is no key set nor that it stated none.
    /// </summary>
    internal const string NoKeySetSentence = "no key-set status is available to this client: the check did not run (see the signature row)";

    /// <summary>What was not checked here, as R10.74 lists it. The signature row already says what was.</summary>
    internal const string NotCheckedSentence = "Not checked here: inclusion, consistency, key binding, lifecycle (! for verify)";

    private const string AsTheForumSays = "(as the Forum's key set says)";

    /// <summary>
    /// The header's rows at <paramref name="width"/> columns. <paramref name="continued"/> is set for a pinned header, and
    /// its numbers are written zero-padded to the width of <c>N</c> so the header's height does not move as they change.
    /// <paramref name="compact"/> is the form that leaves out H3's digest and kid row and H2's owner and <c>server_ts</c>.
    /// </summary>
    internal static ImmutableArray<ScreenRow> Rows(Passage passage, int number, int width, ContinuedRange? continued = null, bool compact = false)
    {
        ArgumentNullException.ThrowIfNull(passage);

        var rows = ImmutableArray.CreateBuilder<ScreenRow>();
        foreach (var logical in Logical(passage, number, continued, compact))
        {
            foreach (var row in RowWrap.Frame(logical, width)) rows.Add(new ScreenRow.Frame(row));
        }

        return rows.ToImmutable();
    }

    /// <summary>The tag that ends H1: nothing for a verified passage, otherwise the word H4 states, in brackets.</summary>
    internal static string? Tag(Passage passage)
    {
        ArgumentNullException.ThrowIfNull(passage);

        // The word is Passage.Outcome's and no other (spec 14.5 item 3): a span that does not render the canonical form is
        // Outcome Failed with a signature that held, and is told apart by its own tag.
        if (passage.Outcome is CheckOutcome.Failed && !SpanCheck.RendersCanonical(passage.Post)) return "[CONTENT FAILED]";

        return passage.Outcome switch
        {
            CheckOutcome.Verified => null,
            CheckOutcome.Disputed => "[DISPUTED]",
            CheckOutcome.CouldNotCheck => "[COULD NOT BE CHECKED]",
            CheckOutcome.Failed => "[NOT VERIFIED]",
            _ => "[NOT VERIFIED]",
        };
    }

    /// <summary>
    /// H5 (spec 4.2): the signing key's status as the key set's word, labelled as the Forum's, and the post's log index
    /// beside it. Facts: the same row sits over a post that stands, one that is disputed and one that fails, and only the
    /// index differs. A key set that states no status is shown as stating none, never as <c>active</c>.
    /// </summary>
    internal static string KeyRow(Passage passage)
    {
        ArgumentNullException.ThrowIfNull(passage);

        var verdict = passage.Verdict;
        var kid = verdict.Kid;
        var at = passage.Post.LogIndex is { } index
            ? index.ToString(CultureInfo.InvariantCulture)
            : "not stated";
        var thisPost = Row($"; this post: log index {new OwnText(at)}");

        if (verdict.KeyStatus is not { } status)
        {
            // A key set never obtained is not one that said nothing (spec 14.5 item 8).
            return verdict.CouldNotCheck
                ? Row($"key {kid}: {new OwnText(NoKeySetSentence)}")
                : Row($"key {kid}: {new OwnText(NoStatusSentence)}");
        }

        if (string.Equals(status, "active", StringComparison.Ordinal))
            return Row($"key {kid}: {new OwnText("active")} {new OwnText(AsTheForumSays)}") + thisPost;

        if (string.Equals(status, "retired", StringComparison.Ordinal) && verdict.KeyRetiredAt is { } retiredAt)
            return Row($"key {kid}: {new OwnText("retired at log index")} {retiredAt} {new OwnText(AsTheForumSays)}") + thisPost;

        if (string.Equals(status, "compromised", StringComparison.Ordinal)
            && verdict.KeyCompromisedAt is { } declaredAt && verdict.KeyDisputedFrom is { } disputedFrom)
            return Row($"key {kid}: {new OwnText("compromised, declaration at log index")} {declaredAt}{new OwnText(", content disputed from log index")} {disputedFrom} {new OwnText(AsTheForumSays)}") + thisPost;

        return Row($"key {kid}: {new OwnText("marked")} {status} {new OwnText(AsTheForumSays)}") + thisPost;
    }

    private static List<string> Logical(Passage passage, int number, ContinuedRange? continued, bool compact)
    {
        var post = passage.Post;
        var provenance = post.Provenance;
        var verdict = passage.Verdict;
        var rows = new List<string>();

        // H1
        var h1 = Row($"#{number} post {post.PostId}  {post.Kind}  board {post.Board}");
        if (post.Parent is { Length: > 0 } parent) h1 += Row($" parent {parent}");
        if (continued is { } range)
        {
            var digits = range.N.ToString(CultureInfo.InvariantCulture).Length;
            string Padded(int value) => value.ToString("D" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            h1 += Row($" {new OwnText("(continued: content rows")} {new OwnText(Padded(range.A))}-{new OwnText(Padded(range.B))} {new OwnText("of")} {new OwnText(Padded(range.N))}{new OwnText(")")}");
        }

        if (Tag(passage) is { } tag) h1 += Row($"  {new OwnText(tag)}");
        rows.Add(h1);

        // H2
        var h2 = Row($"by {provenance.Author}  {new OwnText(provenance.OwnerVerified ? "(owner verified)" : "(owner NOT verified)")}");
        if (!compact)
        {
            if (provenance.Owner is { Length: > 0 } owner) h2 += Row($" owner {owner}");
            h2 += Row($" server_ts {post.ServerTs}");
        }

        rows.Add(h2);

        // H3: the digest computed here; the Forum's, only when it differs (the same comparison Passage.Render makes). The
        // compact form leaves out the first row, and never the second: that one is a warning.
        if (!compact)
            rows.Add(Row($"digest {new OwnText(verdict.PrefixedDigest ?? "(not computed)")}  {new OwnText("(computed here)")}  kid {verdict.Kid}"));
        if (verdict.PrefixedDigest is { } computed && !string.Equals(post.Digest, computed, StringComparison.Ordinal))
            rows.Add(Row($"the Forum reported a different value for digest: {post.Digest}"));

        // H4
        rows.Add(Row($"signature {new OwnText(passage.DescribeRead)}"));
        rows.Add(NotCheckedSentence);
        if (!SpanCheck.RendersCanonical(post))
            rows.Add("content   FAILED: the Forum's own rendering of this post is not its signed bytes; this screen lays out the signed bytes, never the Forum's span");

        // H5
        rows.Add(KeyRow(passage));

        // H6
        var h6 = Row($"forum says {provenance.VerificationLevel}");
        if (post.Accepted) h6 += Row($"; {new OwnText(Passage.AcceptedStatementFor(post.Kind))}");
        if (Supersession.Row(provenance.SupersededBy) is { } superseded) h6 += Row($"; {superseded}");
        if (AnswerStanding.Row(post.Kind, provenance.AcceptedAnswer) is { } answers) h6 += Row($"; {answers}");
        if (!provenance.Contradictions.IsDefaultOrEmpty) h6 += Row($"; {new OwnText("CONTRADICTED by")} {Literals(provenance.Contradictions)}");
        if (!provenance.Reproductions.IsDefaultOrEmpty) h6 += Row($"; {new OwnText("reproduced by")} {Literals(provenance.Reproductions)}");
        if (!provenance.RiskFlags.IsDefaultOrEmpty)
        {
            foreach (var flag in provenance.RiskFlags.Distinct(StringComparer.Ordinal))
                h6 += Row($"; risk {flag} x{provenance.RiskFlags.Count(f => string.Equals(f, flag, StringComparison.Ordinal))}");
        }

        if (provenance.DisputedByLogIndex is { } disputedBy) h6 += Row($"; {new OwnText("marked disputed by log index")} {disputedBy}");
        rows.Add(h6);

        // Passage.Standing's rule for the warning: this client's own sentence is on the screen's chrome; a different one
        // served is quoted here, beneath a line saying so.
        if (!string.Equals(provenance.Warning, Provenance.StandardWarning, StringComparison.Ordinal))
            rows.Add(Row($"the Forum served a warning that is not the published text: {provenance.Warning}"));

        return rows;
    }

    private static OwnText Literals(ImmutableArray<string> values) =>
        new(string.Join(", ", values.Select(DisplayLiteral.Of)));

    private static string Row(FrameText text) => text.ToString();
}
