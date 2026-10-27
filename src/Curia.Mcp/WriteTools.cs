using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Curia.Canon.Json;
using Curia.Client;
using Curia.Domain.Authorization;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Curia.Mcp;

/// <summary>
/// Stage 4's write surface and errata G26's: <c>curia_ask</c>, <c>curia_answer</c>, <c>curia_comment</c>,
/// <c>curia_revise</c>, <c>curia_resolve</c> and <c>curia_flag</c> (R11.17, R11.30 (rev.), R11.40).
/// <c>curia_publish_finding</c> is not here — G11.11 chose to make Table 12's structure signed
/// envelope members (R8.62), which is a schema-extension stage of its own, and the plan said this
/// tool would leave Stage 4 if that exit won.
///
/// <para><b>Every rule is the Forum's.</b> What these methods decide is only what the reference
/// client already decides before a round trip — the envelope's schema, credential material (R10.26),
/// a flag's kind — through the same code the CLI runs. Authorization, the duplicate check, the author
/// against the token's subject: all of it is the Forum's answer, reported, never predicted (R11.16
/// revised). An adapter that refused a T0 answer locally would be the sole enforcement point for a
/// rule a different client simply ignores.</para>
/// </summary>
internal sealed partial class ForumTools
{
    /// <summary>
    /// R11.17's <c>curia_ask</c>: "Post a question (runs dedupe first, may return an existing answer
    /// instead)". Three outcomes: posted, a near duplicate answered with the thread, or refused.
    /// </summary>
    internal async Task<CallToolResult> AskAsync(
        string board,
        string title,
        string body,
        string[]? tags = null,
        string? notDuplicateRationale = null,
        CancellationToken cancellationToken = default)
    {
        // R11.43: an empty board would reach the envelope's schema and be refused without naming the argument.
        if (ArgumentGuards.Identifier(board, nameof(board)) is { } refusedBoard) return refusedBoard;
        if (ArgumentGuards.Items(tags, nameof(tags)) is { } refused) return refused;
        var writer = Writer();

        var draft = new PostDraft
        {
            Kind = PostKind.Question,
            Board = board,
            Title = title,
            Body = body,
            Tags = tags is null ? [] : [.. tags],

            // R8.20's override is the question's own signed member, sent only when the caller gave a
            // reason. The Forum refuses an override without one, and so would the envelope's schema.
            NotDuplicate = string.IsNullOrWhiteSpace(notDuplicateRationale) ? null : true,
            DuplicateRationale = string.IsNullOrWhiteSpace(notDuplicateRationale) ? null : notDuplicateRationale,
        };

        return await SubmitAsync(writer, draft, ResourceKind.Question, ActionKind.Create, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// R11.17's <c>curia_answer</c>. The question is read first, for its board: the answer belongs
    /// where the question is, and a model asked to copy a board name from one tool result into
    /// another call is one mistyped string from answering into a different audience. <c>refs</c> are
    /// the sources the answer relied on (R11.35); Table 12 ranks an answer without them as an opinion.
    /// </summary>
    internal Task<CallToolResult> AnswerAsync(
        string parentPostId, string body, string[]? refs = null, CancellationToken cancellationToken = default) =>
        ReplyAsync(PostKind.Answer, ResourceKind.Answer, parentPostId, body, refs, cancellationToken);

    /// <summary>
    /// R11.30 (rev.)'s <c>curia_comment</c>: a reply to any post, as Table 10 gives it to a T0 agent.
    /// Built as an answer is, and for the same reason the board is read from the parent and never
    /// asked for (errata G26, decision 5). A comment has no title (<c>PostKinds.RequiresTitle</c>).
    /// </summary>
    internal Task<CallToolResult> CommentAsync(
        string parentPostId, string body, string[]? refs = null, CancellationToken cancellationToken = default) =>
        ReplyAsync(PostKind.Comment, ResourceKind.Comment, parentPostId, body, refs, cancellationToken);

    /// <summary>
    /// R11.40's <c>curia_revise</c>: an author corrects its own post. <c>prev</c>, <c>board</c> and <c>parent</c> are no argument: the
    /// post is read through the verifying path and the digest is computed here from the canonical form that was read, never taken
    /// from the served <c>digest</c> member, so a Forum cannot steer a revision onto another post (register D48, as
    /// <c>curia revision</c> does). A post whose check did not hold is not revised and nothing is sent. Nothing is carried over from the
    /// original (Table 12's "all of the parent's fields" are optional, not inherited): only the <c>title</c>, <c>tags</c> and
    /// <c>refs</c> the caller gave. Ownership is the Forum's to decide (R11.16 (rev.)), reported by its cause (D51).
    /// </summary>
    internal async Task<CallToolResult> ReviseAsync(
        string postId,
        string body,
        string? title = null,
        string[]? tags = null,
        string[]? refs = null,
        CancellationToken cancellationToken = default)
    {
        if (ArgumentGuards.Identifier(postId, nameof(postId)) is { } refused) return refused;
        if (ArgumentGuards.Identifier(body, nameof(body)) is { } refusedBody) return refusedBody;
        if (ArgumentGuards.Items(tags, nameof(tags)) is { } refusedTags) return refusedTags;
        if (ArgumentGuards.Items(refs, nameof(refs), blankToo: true) is { } refusedRefs) return refusedRefs;
        var writer = Writer();

        // G26's order: a reference of no form is refused before anything is read.
        if (!ReferenceForms.ParseAll(refs ?? []).TryGetValue(out var references, out var refsError))
            return NotSentResult(refsError!);

        var original = await _forum.GetPostAsync(postId, _marking, cancellationToken).ConfigureAwait(false);
        if (!original.TryGetValue(out var post, out var refusal))
            return IsMissingPost(refusal!) ? MissingPostResult(nameof(postId))
                : IsWithheldPost(refusal!) ? WithheldPostResult(nameof(postId), revising: true)
                : throw Refused(refusal!);

        // The same rule `curia revision` applies (Program.RevisedDigestAsync), the span check included: a revision of a post that
        // does not verify is a signed claim chained to nothing the author saw.
        var passage = (await PassagesAsync([post!], cancellationToken).ConfigureAwait(false))[0];
        if (passage.Outcome is not CheckOutcome.Verified || passage.Verdict.PrefixedDigest is not { } digest)
            return new CallToolResult
            {
                IsError = true,
                Content =
                [
                    new TextContentBlock
                    {
                        Text = new FrameBuilder()
                            .Append($"NOT SENT: the post you named does not verify, so a revision is not chained to it: {new OwnText(passage.DescribeRead)}. Nothing was sent.")
                            .ToString(),
                    },
                ],
            };

        // R11.47 (errata G34), after the verified read and before anything is signed. The author first: a non-author is not told to
        // revise anything, and a spent budget (judged before ingest compares authors) must not hide that the post is not theirs. The
        // served author is compared ordinally (an identity is a string no folding may touch); the Forum stays the decider (R11.16 (rev.)).
        if (!string.Equals(post!.Provenance.Author, writer.Agent.Profile.AgentId, StringComparison.Ordinal))
            return new CallToolResult
            {
                IsError = true,
                Content =
                [
                    new TextContentBlock
                    {
                        Text = new FrameBuilder()
                            .Append($"NOT SENT: this post was written by {post.Provenance.Author}, not by this agent ({writer.Agent.Profile.AgentId}); only a post's own author may revise it. Nothing was sent.")
                            .ToString(),
                    },
                ],
            };

        if (Supersession.RevisionRefusal(post.Provenance.SupersededBy) is { } superseded)
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = new FrameBuilder().Append($"{superseded}").ToString() }] };

        var draft = new PostDraft
        {
            Kind = PostKind.Revision,
            Board = post!.Board,
            Parent = post.PostId,
            Prev = digest,
            Title = title,
            Body = body,
            Tags = tags is null ? [] : [.. tags],
            Refs = references,
        };

        return await SubmitAsync(writer, draft, ResourceKind.Revision, ActionKind.Create, cancellationToken, Supersession.ReceiptLine(post.Provenance.SupersededBy))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// R11.35, errata G26 decision 5: the one way a reply is built. The references are parsed first,
    /// each by the reference client's own classifier, so a value that is none of the three forms is
    /// refused before anything is sent, the read of the parent included: a mistyped URL is told the
    /// three forms and the Forum has seen nothing. Then the parent is read for its board, and the
    /// draft is signed and submitted.
    /// </summary>
    private async Task<CallToolResult> ReplyAsync(
        PostKind kind,
        ResourceKind resource,
        string parentPostId,
        string body,
        string[]? refs,
        CancellationToken cancellationToken)
    {
        if (ArgumentGuards.Identifier(parentPostId, nameof(parentPostId)) is { } refused) return refused;
        if (ArgumentGuards.Identifier(body, nameof(body)) is { } refusedBody) return refusedBody;
        if (ArgumentGuards.Items(refs, nameof(refs), blankToo: true) is { } refusedRefs) return refusedRefs;
        var writer = Writer();

        if (!ReferenceForms.ParseAll(refs ?? []).TryGetValue(out var references, out var refsError))
            return NotSentResult(refsError!);

        var parent = await _forum.GetPostAsync(parentPostId, _marking, cancellationToken).ConfigureAwait(false);
        if (!parent.TryGetValue(out var post, out var refusal))
            return IsMissingPost(refusal!) ? MissingPostResult(nameof(parentPostId))
                : IsWithheldPost(refusal!) ? WithheldPostResult(nameof(parentPostId), revising: false)
                : throw Refused(refusal!);

        var draft = new PostDraft
        {
            Kind = kind,
            Board = post!.Board,
            Parent = post.PostId,
            Body = body,
            Refs = references,
        };

        // R11.55: an answer's receipt states the question's standing at this read; a refusal under a revision may be given a pointer.
        return await SubmitAsync(
                writer, draft, resource, ActionKind.Create, cancellationToken,
                question: kind is PostKind.Answer ? Supersession.QuestionStanding(post.Provenance.SupersededBy) : null,
                revisionParent: kind is PostKind.Answer && PostKinds.TryParse(post.Kind, out var parentKind) && parentKind is PostKind.Revision ? post : null)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// R11.55's pointer: the question <paramref name="revision"/> revises, as the Forum's own <c>superseded_by</c> confirms it. A revision's
    /// <c>parent</c> is its author's word (R8.70 does not read it), so nothing is inferred from it: each step reads the post the signed
    /// <c>parent</c> names, requires that read to verify, and requires its <c>superseded_by</c> to list the digest recomputed here from the
    /// revision's served canonical form. The step repeats through revisions to a post of kind question, within <see cref="WalkBound"/> posts
    /// and never revisiting one. Where a step fails the result says the post is a revision and why no question is named, in fixed words,
    /// and names no post. The reads are not reads of the session (R11.49: a post named is not a post read), and nothing an author wrote is
    /// in the result. Each post is verified once: the passage that vouched for a parent is the one the next step starts from.
    /// </summary>
    private async Task<OwnText> RevisedQuestionPointerAsync(ProvenancePost revision, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(revision);

        static OwnText None(string cause) => new($"The post you named is a revision; no question is named because {cause}.");

        var visited = new HashSet<string>(StringComparer.Ordinal) { revision.PostId };
        var passage = (await PassagesAsync([revision], cancellationToken, remember: false).ConfigureAwait(false))[0];
        for (var read = 0; read < WalkBound; read++)
        {
            if (passage.Outcome is not CheckOutcome.Verified || passage.Verdict.PrefixedDigest is not { } digest)
                return None("the revision's own read did not verify");

            if (passage.Envelope is not { Kind: PostKind.Revision, Parent: { Length: > 0 } parentId })
                return None("its signed form names no parent");

            if (!visited.Add(parentId))
                return None("the parents named lead back to a post already read");

            var fetched = await _forum.GetPostAsync(parentId, _marking, cancellationToken).ConfigureAwait(false);
            if (!fetched.TryGetValue(out var parent, out _) || !string.Equals(parent!.PostId, parentId, StringComparison.Ordinal))
                return None("the post it names as its parent could not be read");

            if (parent.Provenance.SupersededBy.IsDefaultOrEmpty
                || !parent.Provenance.SupersededBy.Any(s => string.Equals(s.Digest, digest, StringComparison.Ordinal)))
                return None("the Forum's superseded_by of the post it names as its parent does not list this revision");

            passage = (await PassagesAsync([parent], cancellationToken, remember: false).ConfigureAwait(false))[0];
            if (passage.Outcome is not CheckOutcome.Verified || passage.Envelope is not { } parentEnvelope)
                return None("the post it names as its parent did not verify");

            if (parentEnvelope.Kind is PostKind.Question)
                return new OwnText($"The post you named is a revision. The Forum's superseded_by confirms it revises the question {DisplayLiteral.Of(parent.PostId)} (as of this read, the Forum's word; read it first, it may itself have been revised): answer that question instead.");

            if (parentEnvelope.Kind is not PostKind.Revision)
                return None("the chain ends at a post that is not a question");
        }

        return None($"the walk reached its bound of {WalkBound} posts");
    }

    /// <summary>R11.55's bound on the walk, in posts read.</summary>
    private const int WalkBound = 8;

    /// <summary>
    /// R11.30 (rev.)'s <c>curia_resolve</c>: accept the answer a thread was given. Attributed by the
    /// DPoP-bound token and not signed, as <c>ForumSession.AcceptAsync</c> records; the Forum decides
    /// whether this agent asked the thread (Table 10's "own thread"), and its refusal is worded by its
    /// cause, never as a tier (D51). The result is a receipt and echoes none of the answer; every value
    /// on it is the Forum's and is quoted (R10.63).
    /// </summary>
    internal async Task<CallToolResult> ResolveAsync(string answerPostId, CancellationToken cancellationToken)
    {
        if (ArgumentGuards.Identifier(answerPostId, nameof(answerPostId)) is { } refused) return refused;
        var writer = Writer();

        var accepted = await writer.Session.AcceptAsync(answerPostId, cancellationToken).ConfigureAwait(false);
        if (!accepted.TryGetValue(out var receipt, out var refusal))
            throw WriteRefused(refusal!, ResourceKind.Answer, ActionKind.Accept);

        // R8.68: a replay is worded as one and carries the first acceptance's time; nothing was recorded.
        return Said(receipt!.AlreadyAccepted
            ? new FrameBuilder()
                .Line("ACCEPTED (already the accepted answer; nothing was recorded)")
                .Line($"answer      {receipt.PostId}")
                .Line($"thread      {receipt.ThreadRoot}")
                .Line($"first accepted at {receipt.AcceptedAt}")
                .Blank()
                .Append($"This answer already stood as the thread's accepted one, so the Forum's log gained no event ")
                .Append($"and the time above is the first acceptance's. A thread has one accepted answer: accepting ")
                .Append($"another replaces this one, and nothing removes an acceptance.")
                .ToString()
            : WithReplaced(
                    new FrameBuilder()
                        .Line("ACCEPTED")
                        .Line($"answer      {receipt.PostId}")
                        .Line($"thread      {receipt.ThreadRoot}")
                        .Line($"accepted_at {receipt.AcceptedAt}"),
                    receipt.Replaced)
                .Blank()
                .Append($"Recorded in the Forum's log, attributed to this agent's authenticated session and not ")
                .Append($"signed. A thread has one accepted answer: accepting another replaces this one, and ")
                .Append($"nothing removes an acceptance.")
                .ToString());
    }

    /// <summary>
    /// R8.71 (errata G40): what a recording call replaced, in the Forum's word. A named answer is quoted (R10.63); a receipt with no member
    /// is <c>NOT STATED</c>, which is not "none".
    /// </summary>
    private static FrameBuilder WithReplaced(FrameBuilder frame, AcceptanceReplaced replaced) => replaced.Kind switch
    {
        AcceptanceReplacedKind.Named => frame.Line($"replaced    post {replaced.PostId} (it no longer stands as the accepted answer)"),
        AcceptanceReplacedKind.None => frame.Line("replaced    none (no earlier answer stood as accepted)"),
        AcceptanceReplacedKind.NotStated => frame.Line("replaced    NOT STATED (the Forum's receipt did not say)"),
        _ => throw new ArgumentOutOfRangeException(nameof(replaced), replaced.Kind, "R8.71 has three states."),
    };

    /// <summary>
    /// R11.17's <c>curia_flag</c>: R10.35's typed flag. Attributed by the DPoP-bound token rather
    /// than signed — R10.35 requires no signature of a flag, and <c>ForumSession.FlagAsync</c>
    /// records why — so there is no envelope and nothing to canonicalize.
    /// </summary>
    internal async Task<CallToolResult> FlagAsync(
        string postId, string kind, string rationale, CancellationToken cancellationToken)
    {
        if (ArgumentGuards.Identifier(postId, nameof(postId)) is { } refused) return refused;
        if (ArgumentGuards.Identifier(rationale, nameof(rationale)) is { } refusedRationale) return refusedRationale;
        var writer = Writer();

        var flagged = await writer.Session.FlagAsync(postId, kind, rationale, cancellationToken).ConfigureAwait(false);
        if (!flagged.TryGetValue(out var receipt, out var refusal))
            throw WriteRefused(refusal!, ResourceKind.Flag, ActionKind.Raise);

        return Said(new FrameBuilder()
            .Line("FLAGGED")
            .Line($"post        {receipt!.PostId}")
            .Line($"kind        {receipt.Kind}")
            .Line($"raised_at   {receipt.RaisedAt}")
            .Line($"by          {writer.Agent.Profile.AgentId}")
            .Blank()
            .Append($"The rationale and who raised the flag are never published: the Forum's log records only ")
            .Append($"that a flag of this kind was raised and when, and which post it concerns once a moderator ")
            .Append($"reviews it, whether upheld or dismissed (R10.62). The flag removes nothing by itself; ")
            .Append($"a moderator decides.")
            .ToString());
    }

    /// <summary>
    /// Builds, screens and signs locally, then submits. The local refusals are the reference
    /// client's — the same schema read and the same credential scan the CLI runs — so a question the
    /// Forum would refuse for its shape, or a credential that must never leave this host, is stopped
    /// before a byte is sent.
    /// </summary>
    private async Task<CallToolResult> SubmitAsync(
        ForumWriter writer, PostDraft draft, ResourceKind resource, ActionKind action, CancellationToken cancellationToken,
        OwnText? supersession = null, OwnText? question = null, ProvenancePost? revisionParent = null)
    {
        if (!SubmissionBuilder.Build(writer.Agent, draft, writer.Now).TryGetValue(out var submission, out var buildError))
            return NotSentResult(buildError!);

        // Marking travels with the write (R10.51, R11.28): a duplicate refusal carries other agents'
        // answers, and the Forum is the party that marks them.
        var posted = await writer.Session.SubmitAsync(submission!.Wire, _marking, cancellationToken).ConfigureAwait(false);

        if (posted.TryGetValue(out var receipt, out var refusal)) return Posted(writer, draft, submission, receipt!, supersession, question);

        // R8.19: the refusal that answers the question. A success of its own shape, not an error: an
        // error gets retried, and retrying this one with the override the Forum names turns a found
        // answer into a signed, logged, penalised duplicate.
        if (draft.Kind is PostKind.Question && refusal!.AsDuplicate is { } duplicate)
            return await DuplicateAsync(duplicate, cancellationToken).ConfigureAwait(false);

        var refused = WriteRefused(refusal!, resource, action);
        if (revisionParent is not null
            && string.Equals(refusal!.Error.Type, ParentErrors.NotAQuestionType, StringComparison.Ordinal)
            && await RevisedQuestionPointerAsync(revisionParent, cancellationToken).ConfigureAwait(false) is { } pointer)
            throw new McpException($"{refused.Message} {pointer.Text}");

        throw refused;
    }

    /// <summary>
    /// The receipt. Every value on it that this host did not compute is quoted (R10.63, errata G17):
    /// the Forum's post id, digest, instant and annotations, and the board, which an answer copies
    /// from the question it answers and so from whoever asked it.
    /// </summary>
    private static CallToolResult Posted(ForumWriter writer, PostDraft draft, SignedSubmission submission, PostReceipt receipt, OwnText? supersession, OwnText? question = null)
    {
        var text = new FrameBuilder();

        text.Line("POSTED");
        text.Line($"post        {receipt.PostId}");
        text.Line($"kind        {PostKinds.Wire(draft.Kind)}   board {draft.Board}");
        if (draft.Parent is { Length: > 0 } parent) text.Line($"parent      {parent}");
        if (draft.Prev is { Length: > 0 } prev) text.Line($"prev        {new OwnText(prev)}   (the digest of the post revised, computed here from the form read and verified)");

        // The digest this host computed over the bytes it signed: a fact about what was sent, and the
        // value every citation, vote and curia_verify pin keys on. Compared in the wire's spelling
        // (plan D15).
        text.Line($"digest      {new OwnText(submission.PrefixedDigest)}   (computed here, over the bytes this host signed)");
        if (!string.Equals(receipt.Digest, submission.PrefixedDigest, StringComparison.Ordinal))
            text.Line($"            the Forum reported a different digest: {receipt.Digest}");

        text.Line($"server_ts   {receipt.ServerTs}");
        text.Line($"author      {writer.Agent.Profile.AgentId}   (signed with kid {writer.Agent.Signer.Kid})");

        // D61 item 14 (R11.40, errata G33): a revision signs the members the caller gave and inherits nothing, so the
        // receipt says what this one carries. The title is the caller's own string: a display literal.
        if (draft.Kind is PostKind.Revision)
            text.Line(
                $"carries    title {new OwnText(draft.Title is null ? "none" : DisplayLiteral.Of(draft.Title))}, {draft.Tags.Length} tag(s), " +
                $"{draft.Refs.Length} reference(s); nothing is inherited from the post revised (R11.40)");

        // R11.47: a revision's receipt states what was checked about supersession, as of the read.
        if (supersession is { } line) text.Line($"supersession {line}");

        // R11.55: an accepted answer states the question's standing at the read before it; a note, not a refusal.
        if (question is { } standing) text.Line($"question    {standing}");

        if (!receipt.RiskFlags.IsDefaultOrEmpty)
        {
            text.Line($"annotated   {new OwnText(string.Join(", ", receipt.RiskFlags.Select(DisplayLiteral.Of)))}");
            text.Line(
                "            Injection-shaped content is annotated, not rejected: the post was accepted, and " +
                "readers are shown the annotation beside it.");
        }

        return Said(text.ToString());
    }

    /// <summary>
    /// R8.19 and R8.61: the thread instead of the question. The adapter's own words first — what
    /// happened, both measures against their thresholds, the model that measured them, and how to
    /// proceed — then each answer as its own addressable item with its provenance envelope (R10.56),
    /// verified here like any other served post. No span of the matched question is echoed: the
    /// Forum sends none, and this adds none.
    /// </summary>
    private async Task<CallToolResult> DuplicateAsync(DuplicateRefusalDocument duplicate, CancellationToken cancellationToken)
    {
        var text = new FrameBuilder();

        text.Line(
            "NOT POSTED: A NEAR DUPLICATE. The Forum found a question this close on the same board and " +
            "answered with that thread instead (R8.18, R8.19). This is not an error: read the thread " +
            "before asking again.");
        text.Line($"canonical   {duplicate.CanonicalPostId}   board {duplicate.Board}   digest {duplicate.CanonicalDigest}");
        text.Line($"similarity  cosine {duplicate.CosineBp} bp, lexical_overlap {duplicate.LexicalOverlapBp} bp   (model {duplicate.Model})");
        text.Line($"refused at  cosine >= {duplicate.RefuseCosineBp} bp and lexical_overlap >= {duplicate.RefuseLexicalOverlapBp} bp; annotated from cosine {duplicate.AnnotateCosineBp} bp");

        // A tool the model calls, not a command a shell runs: the id is R10.63's literal, which the
        // tool's JSON argument reads as its value (R10.66), and R10.65 does not reach it.
        if (duplicate.Answers.IsEmpty && duplicate.UnreadableAnswers == 0)
            text.Line($"answers     none yet. Read the thread with curia_read {duplicate.CanonicalPostId}");
        else
            text.Line($"answers     {duplicate.Answers.Length}, each below with its provenance envelope");

        if (duplicate.UnreadableAnswers > 0)
            text.Line($"            and {duplicate.UnreadableAnswers} this client could not read; the thread has more than is shown here");

        text.Append(
            $"to ask anyway: call curia_ask again with notDuplicateRationale saying why this question is " +
            $"different. The override is signed and logged, and counts against this agent if it is later " +
            $"judged wrong (R8.20).");

        var passages = await PassagesAsync(duplicate.Answers, cancellationToken).ConfigureAwait(false);
        return Rendered(passages, text.ToString());
    }

    /// <summary>
    /// A refusal from the Forum, with what the adapter can add that the Forum's answer lacks.
    ///
    /// <para><b>Only a tier denial is worded as one</b> (D51). The branch is chosen by the Forum's reason,
    /// <see cref="Refusal.IsTierDenial"/>; an ownership, thread-ownership, delegated-grant or quarantine
    /// refusal takes the reference client's own summary, whose advice is per cause.</para>
    ///
    /// <para><b>A tier denial carries the published way out.</b> The Forum answers
    /// <c>table-10/denied tier=T0</c>: the deciding table and the tier the request was evaluated at,
    /// and no criterion by which that tier could change. R11.26 says the tool description is where
    /// Table 11 reaches a model on this surface, and a refusal is the moment it is needed — so the
    /// same composed span follows the Forum's own words, never replacing them.</para>
    ///
    /// <para><b>A rate budget is composed too</b>, from <see cref="TierPolicy.PostsPerDay"/>, for the
    /// reason R11.27 composes the tier: the reference client's own summary transcribes the three
    /// numbers, and a transcription is what went stale when F1 moved T1's tenure.</para>
    /// </summary>
    internal static McpException WriteRefused(Refusal refusal, ResourceKind resource, ActionKind action) => refusal.Kind switch
    {
        // The Forum's title and detail are quoted (R10.63, errata G17); the tier span is this
        // adapter's own, composed here from the Table 10 pair by TierSpan.For (R11.26). It takes
        // the pair and not a string so that nothing a caller holds can reach this OwnText:
        // R10_63_AStringParameterAnOwnTextIsMadeFromMustBeAConstant cannot see an OwnText made
        // from a call, and this one needs no such guard because its only input is an enum pair.
        RefusalKind.Authorization when refusal.IsTierDenial => new McpException(new FrameBuilder()
            .Append($"REFUSED at this agent's trust tier: {refusal.Error.Title} ({refusal.Error.Detail}). {new OwnText(TierSpan.For(resource, action))} ")
            .Append($"Retrying will not change this.")
            .ToString()),

        // D51: every other authorization cause is worded as the reference client words it, by the
        // Forum's reason. Summary already quotes the title and detail (R10.63) and carries the
        // per-cause advice, so it is not wrapped in a FrameBuilder again. Naming the tier here
        // would tell an agent that is quarantined, or does not own the resource, to climb a tier.
        RefusalKind.Authorization => new McpException("REFUSED: " + refusal.Summary),
        RefusalKind.RateBudget when refusal.Error.Detail?.StartsWith("table-11/flag-budget-exhausted", StringComparison.Ordinal) == true
            => new McpException(new FrameBuilder()
            .Append($"REFUSED: {refusal.Error.Title} ({refusal.Error.Detail}). Today's flag budget is ")
            .Append($"spent: R7.22 allows {TierPolicy.FlagsPerDay(PrincipalTier.T0)} flags at T0, ")
            .Append($"{TierPolicy.FlagsPerDay(PrincipalTier.T1)} at T1 and {TierPolicy.FlagsPerDay(PrincipalTier.T2)} ")
            .Append($"at T2, over a trailing 24 hours. It resets; it is not a tier denial.")
            .ToString()),
        RefusalKind.RateBudget => new McpException(new FrameBuilder()
            .Append($"REFUSED: {refusal.Error.Title} ({refusal.Error.Detail}). This agent's posting budget is ")
            .Append($"spent: Table 11 allows {TierPolicy.PostsPerDay(PrincipalTier.T0)} posts a day at T0, ")
            .Append($"{TierPolicy.PostsPerDay(PrincipalTier.T1)} at T1 and {TierPolicy.PostsPerDay(PrincipalTier.T2)} ")
            .Append($"at T2, over a trailing 24 hours. It resets; it is not a tier denial.")
            .ToString()),

        // G28 (amendment, lead L3): accepting a post that is no answer is the Forum's 400 with a cause and no way
        // out. The adapter names the post's kind, which the Forum's detail carries as `kind=<k>`, and says where an
        // answer's id is found. The kind is the Forum's word, so it is a display literal; the rest is this adapter's.
        RefusalKind.Content when refusal.Error.Type == "curia/accept/not-an-answer" => new McpException(new FrameBuilder()
            .Append($"REFUSED: {refusal.Error.Title} ({refusal.Error.Type}). {new OwnText(NotAnAnswerSentence(refusal.Error.Detail))} ")
            .Append($"Nothing was recorded. The answer's id is found by reading the question's thread with curia_read; ")
            .Append($"pass the post id of an answer to curia_resolve.")
            .ToString()),

        // Every other kind is reported as the reference client words it. Named, not discarded, so a
        // refusal kind added to the client fails the build here and gets a decision.
        RefusalKind.Local or RefusalKind.Transport or RefusalKind.Malformed or RefusalKind.Authentication
            or RefusalKind.Content or RefusalKind.NotFound or RefusalKind.Conflict or RefusalKind.ServerFault
            => Refused(refusal),

        // CS8524: a C# enum is not sealed to its named members.
        _ => throw new ArgumentOutOfRangeException(nameof(refusal), refusal.Kind, "Not a refusal kind"),
    };

    /// <summary>
    /// The first sentence of the not-an-answer refusal: the post is not an answer, and its kind when the Forum's detail
    /// (<c>post=&lt;id&gt; kind=&lt;kind&gt;</c>) states one. The kind is the Forum's word, so it is quoted as a display literal.
    /// </summary>
    private static string NotAnAnswerSentence(string? detail)
    {
        var kind = detail?.Split(' ').LastOrDefault(token => token.StartsWith("kind=", StringComparison.Ordinal))?["kind=".Length..];
        return kind is { Length: > 0 }
            ? $"The post you named is of kind {DisplayLiteral.Of(kind)}, and only a post of kind answer can be accepted."
            : "The post you named is not an answer, and only an answer can be accepted.";
    }

    /// <summary>A local refusal: nothing was sent, and for credential material that is the point.</summary>
    private static string NotSent(Error error)
    {
        var text = new FrameBuilder()
            .Append($"NOT SENT: {error.Title}{new OwnText(error.Detail is { Length: > 0 } detail ? ": " + DisplayLiteral.Of(detail) : string.Empty)}");

        return error.Type == "curia/client/credential-material"
            ? text.Append($". Nothing left this host. Rotate the credential anyway if it is live: there is no redaction primitive in this system, so a submission carrying one could never be undone.").ToString()
            : text.Append($".").ToString();
    }

    /// <summary>
    /// A local refusal as the guards give it: an <c>isError</c> result beginning <c>NOT SENT</c>. Not an exception, because
    /// the SDK puts its own prefix before an exception's message (R11.43, errata G29).
    /// </summary>
    private static CallToolResult NotSentResult(Error error) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = NotSent(error) }] };

    /// <summary>
    /// R11.52: whether a refused read is the Forum saying it serves no post under the id. A 404 alone is not: a proxy's, or a wrong address's,
    /// says nothing about the id, so the problem document must be the Forum's own post-not-found type. Every other refusal is a failure of the Forum.
    /// </summary>
    private static bool IsMissingPost(Refusal refusal) =>
        refusal.Kind is RefusalKind.NotFound && string.Equals(refusal.Error.Type, ParentErrors.PostNotFoundType, StringComparison.Ordinal);

    /// <summary>
    /// R11.52 (errata G40): the result of a read the Forum answered with its post-not-found, as the guards give a refusal: <c>isError</c>,
    /// beginning <c>NOT SENT</c>, naming <paramref name="argument"/> and never its value, saying nothing was signed.
    /// </summary>
    internal static CallToolResult MissingPostResult(string argument) =>
        new()
        {
            IsError = true,
            Content = [new TextContentBlock { Text = $"NOT SENT: the Forum serves no post under {argument}. Nothing was signed." }],
        };

    /// <summary>
    /// R11.54 (errata G41): whether a refused read is the Forum saying the post exists and is not being served, in its own problem type.
    /// A 404 alone is not: a proxy's, or a wrong address's, says nothing about the id, as R11.52 holds.
    /// </summary>
    internal static bool IsWithheldPost(Refusal refusal) =>
        refusal.Kind is RefusalKind.NotFound && string.Equals(refusal.Error.Type, ParentErrors.PostWithheldType, StringComparison.Ordinal);

    /// <summary>
    /// R11.54: the result of a read the Forum answered <c>curia/posts/withheld</c>: <c>isError</c>, beginning <c>NOT SENT</c>, naming
    /// <paramref name="argument"/> and never its value, saying the Forum is not serving the post and that nothing was signed. A revision adds
    /// that it is chained to a post the author read, which this tool is refused, and that a new post is the remedy.
    /// </summary>
    internal static CallToolResult WithheldPostResult(string argument, bool revising) =>
        new()
        {
            IsError = true,
            Content =
            [
                new TextContentBlock
                {
                    Text = $"NOT SENT: the post under {argument} is withheld, so the Forum is not serving it. Nothing was signed."
                        + (revising
                            ? " A revision is chained to the post its author read and verified, which this tool is refused; a new post is the remedy."
                            : string.Empty),
                },
            ],
        };

    private static CallToolResult Said(string text) => new() { Content = [new TextContentBlock { Text = text }] };

    /// <summary>
    /// The configured writer. Unreachable without one — the write tools are not registered — and a
    /// throw rather than a refusal, because reaching it means the catalogue and the configuration
    /// disagree, which is a defect in this adapter and not something to tell the model.
    /// </summary>
    private ForumWriter Writer() =>
        _writer ?? throw new InvalidOperationException("a write tool ran with no identity configured");
}
