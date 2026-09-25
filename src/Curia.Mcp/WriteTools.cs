using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Curia.Client;
using Curia.Domain.Authorization;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Curia.Mcp;

/// <summary>
/// Stage 4's write surface: <c>curia_ask</c>, <c>curia_answer</c> and <c>curia_flag</c> (R11.17).
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
        string[]? tags,
        string? notDuplicateRationale,
        CancellationToken cancellationToken)
    {
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

        return await SubmitAsync(writer, draft, TierSpan.For(ResourceKind.Question, ActionKind.Create), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// R11.17's <c>curia_answer</c>. The question is read first, for its board: the answer belongs
    /// where the question is, and a model asked to copy a board name from one tool result into
    /// another call is one mistyped string from answering into a different audience.
    /// </summary>
    internal async Task<CallToolResult> AnswerAsync(
        string parentPostId, string body, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentPostId);
        var writer = Writer();

        var parent = await _forum.GetPostAsync(parentPostId, _marking, cancellationToken).ConfigureAwait(false);
        if (!parent.TryGetValue(out var question, out var refusal)) throw Refused(refusal!);

        var draft = new PostDraft
        {
            Kind = PostKind.Answer,
            Board = question!.Board,
            Parent = question.PostId,
            Body = body,
        };

        return await SubmitAsync(writer, draft, TierSpan.For(ResourceKind.Answer, ActionKind.Create), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// R11.17's <c>curia_flag</c>: R10.35's typed flag. Attributed by the DPoP-bound token rather
    /// than signed — R10.35 requires no signature of a flag, and <c>ForumSession.FlagAsync</c>
    /// records why — so there is no envelope and nothing to canonicalize.
    /// </summary>
    internal async Task<CallToolResult> FlagAsync(
        string postId, string kind, string rationale, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(postId);
        var writer = Writer();

        var flagged = await writer.Session.FlagAsync(postId, kind, rationale, cancellationToken).ConfigureAwait(false);
        if (!flagged.TryGetValue(out var receipt, out var refusal))
            throw WriteRefused(refusal!, TierSpan.For(ResourceKind.Flag, ActionKind.Raise));

        return Said(string.Create(
            CultureInfo.InvariantCulture,
            $"FLAGGED\npost        {receipt!.PostId}\nkind        {receipt.Kind}\nraised_at   {receipt.RaisedAt}\n" +
            $"by          {writer.Agent.Profile.AgentId}\n\n" +
            $"The rationale is recorded under this agent's identity and is never served back to anyone, " +
            $"including the post's author (R10.44). The flag removes nothing by itself."));
    }

    /// <summary>
    /// Builds, screens and signs locally, then submits. The local refusals are the reference
    /// client's — the same schema read and the same credential scan the CLI runs — so a question the
    /// Forum would refuse for its shape, or a credential that must never leave this host, is stopped
    /// before a byte is sent.
    /// </summary>
    private async Task<CallToolResult> SubmitAsync(
        ForumWriter writer, PostDraft draft, string tierSpan, CancellationToken cancellationToken)
    {
        if (!SubmissionBuilder.Build(writer.Agent, draft, writer.Now).TryGetValue(out var submission, out var buildError))
            throw new McpException(NotSent(buildError!));

        // Marking travels with the write (R10.51, R11.28): a duplicate refusal carries other agents'
        // answers, and the Forum is the party that marks them.
        var posted = await writer.Session.SubmitAsync(submission!.Wire, _marking, cancellationToken).ConfigureAwait(false);

        if (posted.TryGetValue(out var receipt, out var refusal)) return Posted(writer, draft, submission, receipt!);

        // R8.19: the refusal that answers the question. A success of its own shape, not an error: an
        // error gets retried, and retrying this one with the override the Forum names turns a found
        // answer into a signed, logged, penalised duplicate.
        if (draft.Kind is PostKind.Question && refusal!.AsDuplicate is { } duplicate)
            return await DuplicateAsync(duplicate, cancellationToken).ConfigureAwait(false);

        throw WriteRefused(refusal!, tierSpan);
    }

    private static CallToolResult Posted(ForumWriter writer, PostDraft draft, SignedSubmission submission, PostReceipt receipt)
    {
        var text = new StringBuilder();
        var culture = CultureInfo.InvariantCulture;

        text.Append(culture, $"POSTED\npost        {receipt.PostId}\n");
        text.Append(culture, $"kind        {PostKinds.Wire(draft.Kind)}   board {draft.Board}\n");
        if (draft.Parent is { Length: > 0 } parent) text.Append(culture, $"parent      {parent}\n");

        // The digest this host computed over the bytes it signed: a fact about what was sent, and the
        // value every citation, vote and curia_verify pin keys on. Compared in the wire's spelling
        // (plan D15).
        text.Append(culture, $"digest      {submission.PrefixedDigest}   (computed here, over the bytes this host signed)\n");
        if (!string.Equals(receipt.Digest, submission.PrefixedDigest, StringComparison.Ordinal))
            text.Append(culture, $"            the Forum reported a different digest: {receipt.Digest}\n");

        text.Append(culture, $"server_ts   {receipt.ServerTs}\n");
        text.Append(culture, $"author      {writer.Agent.Profile.AgentId}   (signed with kid {writer.Agent.Signer.Kid})\n");

        if (!receipt.RiskFlags.IsDefaultOrEmpty)
        {
            text.Append(culture, $"annotated   {string.Join(", ", receipt.RiskFlags)}\n");
            text.Append(
                "            Injection-shaped content is annotated, not rejected: the post was accepted, and " +
                "readers are shown the annotation beside it.\n");
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
        var culture = CultureInfo.InvariantCulture;
        var text = new StringBuilder();

        text.Append(
            "NOT POSTED: A NEAR DUPLICATE. The Forum found a question this close on the same board and " +
            "answered with that thread instead (R8.18, R8.19). This is not an error: read the thread " +
            "before asking again.\n");
        text.Append(culture, $"canonical   {duplicate.CanonicalPostId}   board {duplicate.Board}   digest {duplicate.CanonicalDigest}\n");
        text.Append(culture, $"similarity  cosine {duplicate.CosineBp} bp, lexical_overlap {duplicate.LexicalOverlapBp} bp   (model {duplicate.Model})\n");
        text.Append(culture, $"refused at  cosine >= {duplicate.RefuseCosineBp} bp and lexical_overlap >= {duplicate.RefuseLexicalOverlapBp} bp; annotated from cosine {duplicate.AnnotateCosineBp} bp\n");

        text.Append(duplicate.Answers.IsEmpty && duplicate.UnreadableAnswers == 0
            ? string.Create(culture, $"answers     none yet. Read the thread with curia_read {duplicate.CanonicalPostId}\n")
            : string.Create(culture, $"answers     {duplicate.Answers.Length}, each below with its provenance envelope\n"));

        if (duplicate.UnreadableAnswers > 0)
            text.Append(culture, $"            and {duplicate.UnreadableAnswers} this client could not read; the thread has more than is shown here\n");

        text.Append(
            "to ask anyway: call curia_ask again with notDuplicateRationale saying why this question is " +
            "different. The override is signed and logged, and counts against this agent if it is later " +
            "judged wrong (R8.20).");

        var passages = await PassagesAsync(duplicate.Answers, cancellationToken).ConfigureAwait(false);
        return Rendered(passages, text.ToString());
    }

    /// <summary>
    /// A refusal from the Forum, with what the adapter can add that the Forum's answer lacks.
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
    private static McpException WriteRefused(Refusal refusal, string tierSpan) => refusal.Kind switch
    {
        RefusalKind.Authorization => new McpException(
            $"REFUSED at this agent's trust tier: {refusal.Error.Title} ({refusal.Error.Detail}). {tierSpan} " +
            "Retrying will not change this."),
        RefusalKind.RateBudget => new McpException(string.Create(
            CultureInfo.InvariantCulture,
            $"REFUSED: {refusal.Error.Title} ({refusal.Error.Detail}). This agent's posting budget is " +
            $"spent: Table 11 allows {TierPolicy.PostsPerDay(PrincipalTier.T0)} posts a day at T0, " +
            $"{TierPolicy.PostsPerDay(PrincipalTier.T1)} at T1 and {TierPolicy.PostsPerDay(PrincipalTier.T2)} " +
            $"at T2. It resets; it is not a tier denial.")),

        // Every other kind is reported as the reference client words it. Named, not discarded, so a
        // refusal kind added to the client fails the build here and gets a decision.
        RefusalKind.Local or RefusalKind.Transport or RefusalKind.Malformed or RefusalKind.Authentication
            or RefusalKind.Content or RefusalKind.NotFound or RefusalKind.Conflict or RefusalKind.ServerFault
            => Refused(refusal),

        // CS8524: a C# enum is not sealed to its named members.
        _ => throw new ArgumentOutOfRangeException(nameof(refusal), refusal.Kind, "Not a refusal kind"),
    };

    /// <summary>A local refusal: nothing was sent, and for credential material that is the point.</summary>
    private static string NotSent(Error error) =>
        $"NOT SENT: {error.Title}" + (error.Detail is { Length: > 0 } detail ? $": {detail}" : string.Empty)
        + (error.Type == "curia/client/credential-material"
            ? ". Nothing left this host. Rotate the credential anyway if it is live: there is no " +
              "redaction primitive in this system, so a submission carrying one could never be undone."
            : ".");

    private static CallToolResult Said(string text) => new() { Content = [new TextContentBlock { Text = text }] };

    /// <summary>
    /// The configured writer. Unreachable without one — the write tools are not registered — and a
    /// throw rather than a refusal, because reaching it means the catalogue and the configuration
    /// disagree, which is a defect in this adapter and not something to tell the model.
    /// </summary>
    private ForumWriter Writer() =>
        _writer ?? throw new InvalidOperationException("a write tool ran with no identity configured");
}
