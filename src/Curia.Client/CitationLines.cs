using System.Collections.Immutable;
using System.Globalization;
using Curia.Canon.Json;
using Curia.Domain.Primitives;
using Curia.Domain.Serving;

namespace Curia.Client;

/// <summary>
/// One line of a recheck (errata G35, R9.34): the word it begins with, the line as this client words it, and what the line
/// means for a caller's exit. <see cref="Text"/> is this client's own words around display literals (R10.63).
/// </summary>
/// <param name="State">The word the line begins with: <c>current</c>, <c>superseded</c>, <c>withheld</c>, <c>unknown</c>, <c>malformed</c> or <c>disputed</c>, or the Forum's own word for a state this build does not know.</param>
/// <param name="Text">The line, without its newline.</param>
/// <param name="Gone">The citation is withheld, unknown or in a state this build does not know: change what is cited.</param>
/// <param name="Malformed">The element was not a digest.</param>
/// <param name="Disputed">The Forum carries the disputed mark (R6.57) on the post it serves for this element.</param>
/// <param name="Failed">The digest computed from the served canonical form is not the digest asked about (R9.34).</param>
public sealed record CitationLine(string State, OwnText Text, bool Gone, bool Malformed, bool Disputed, bool Failed);

/// <summary>
/// What <see cref="CitationLines.For"/> made of a batch response: one line per element sent, or an error and no line when the
/// response answers other than what was sent (R9.34).
/// </summary>
/// <param name="Lines">One line per element, in the order sent; empty when <see cref="Error"/> is set.</param>
/// <param name="Error">Why the response was refused, or <see langword="null"/>.</param>
public sealed record CitationReport(ImmutableArray<CitationLine> Lines, Error? Error)
{
    /// <summary>True when any line is FAILED.</summary>
    public bool Failed => Lines.Any(line => line.Failed);
}

/// <summary>
/// The surface a recheck line is composed for (errata G59, R10.95; plan ruling 42): what the reader of the line can do is a fact of the surface and is not read from whether an agent name is null, which stood for
/// two states.
/// </summary>
public enum CitationSurface
{
    /// <summary><c>curia recheck</c>: the reader runs commands, acts as no profile, and may run a <c>curia-mcp</c> session of its own.</summary>
    Cli,

    /// <summary>A <c>curia-mcp</c> session with an agent configured: it registers <c>curia_resend</c>, <c>curia_acts</c> and <c>curia_discard</c>.</summary>
    McpAgent,

    /// <summary>A <c>curia-mcp</c> session with no agent configured: it registers none of the three, so it cannot send a kept act again; whoever runs the host can.</summary>
    McpReadOnly,
}

/// <summary>
/// R9.34 (errata G35): every word a recheck line uses, in one place, so <c>curia recheck</c> and <c>curia_recheck</c> compose
/// from the same definition and no form edits its words alone (<see cref="Supersession"/>'s lesson).
///
/// <para><b>The response is checked against the request, which is the oracle the reader holds.</b> The batch answers one item per
/// digest, in the order sent (R9.18); a response with another number of items, or one that names at a position a digest other than
/// the one sent there, is refused with an error and no standing, since a line printed from it would put a state against a digest
/// the Forum did not answer for (the shape D63 closed for the single read). For an item whose post the Forum names, the digest
/// is recomputed from the served canonical form, a different artifact from the Forum's own statement of it, and a mismatch is a
/// <c>FAILED</c> line that offers no post id.</para>
///
/// <para>Nothing an author wrote reaches a line: it is built from the state, the ids, the successors and the disputed mark, never
/// from a title, a tag, a body or the rendered span (P22). Every value the Forum supplied is a display literal (R10.63), and a
/// malformed element is named by its zero-based position and never echoed (R9.18).</para>
/// </summary>
public static class CitationLines
{
    /// <summary>
    /// The lines for <paramref name="items"/>, the Forum's answer to a batch of <paramref name="sent"/>. <paramref name="answeredAt"/> is the
    /// reader's own instant, taken by the caller right after the batch returned (errata G53, R10.93); the <c>unknown</c> line states it, UTC to the second.
    /// <paramref name="surface"/> is the surface the lines are composed for (errata G59, R10.95). <paramref name="agent"/> is the profile the caller was given or has configured (R11.82 (b)): <c>recheck</c> acts as no one,
    /// so it never chooses one, and the <c>unknown</c> line prints the resend command with that profile, or with <c>--agent &lt;name&gt;</c> and the sentence that says what <c>&lt;name&gt;</c> is where it was given none.
    /// </summary>
    public static CitationReport For(IReadOnlyList<string> sent, ImmutableArray<CitationDocument> items, DateTimeOffset answeredAt, CitationSurface surface, string? agent = null)
    {
        ArgumentNullException.ThrowIfNull(sent);

        if (items.Length != sent.Count)
            return Refused(ClientErrors.BatchCount(sent.Count, items.Length));

        for (var i = 0; i < items.Length; i++)
        {
            if (items[i].Digest is { } named && !string.Equals(named, sent[i], StringComparison.Ordinal))
                return Refused(ClientErrors.BatchPosition(i));
        }

        var lines = ImmutableArray.CreateBuilder<CitationLine>(items.Length);
        for (var i = 0; i < items.Length; i++)
            lines.Add(LineOf(i, sent[i], items[i], answeredAt, surface, agent));

        return new CitationReport(lines.MoveToImmutable(), null);
    }

    private static CitationReport Refused(Error error) => new([], error);

    private static CitationLine LineOf(int position, string asked, CitationDocument item, DateTimeOffset answeredAt, CitationSurface surface, string? agent)
    {
        switch (item.State)
        {
            case "current" or "superseded" when item.Post is { } post && Mismatch(post, asked) is { } computed:
                return new CitationLine("FAILED", Of($"FAILED      {asked}  the Forum answered {new OwnText(item.State)} with a post whose served canonical form hashes to {computed}, which is not this digest; no post id is offered, so do not cite on this word"), false, false, false, true);

            case "current" when item.Post?.Provenance.DisputedByLogIndex is not null:
                return new CitationLine("disputed", Of($"disputed    {asked}  the signature may hold, but the key was declared compromised from before this post (the Forum's word, R6.57): do not cite it as the author's{PostOf(item.Post)}"), false, false, true, false);

            // A successor may be signed under another key, so the line still names what to read next.
            case "superseded" when item.Post?.Provenance.DisputedByLogIndex is not null:
                return new CitationLine("disputed", Of($"disputed    {asked}  (superseded -> {Revisions(item)}{Forked(item)}) the signature may hold, but the key was declared compromised from before this post (the Forum's word, R6.57): do not cite it as the author's; re-read the successor before citing it{PostOf(item.Post)}"), false, false, true, false);

            case "current" when item.Post is null:
                return new CitationLine("current", Of($"current     {asked}  the Forum named no post for this digest, so nothing was recomputed here; do not cite it on this word alone"), false, false, false, false);

            case "current":
                return new CitationLine("current", Of($"current     {asked}  {Supersession.CurrentStanding()}{ForkWords(item.Fork, stated: true)}{PostOf(item.Post)}"), false, false, false, false);

            case "superseded":
                return new CitationLine("superseded", Of($"superseded  {asked}  -> {Revisions(item)}{Forked(item)}  the original is still served; read the revision(s) before citing this post{UnservedBranches(item)}{ForkWords(item.Fork, stated: false)}{PostOf(item.Post)}"), false, false, false, false);

            case "withheld":
                return new CitationLine("withheld", Of($"withheld    {asked}  drop this citation; it is no longer served (withholding can be reversed -- re-check later)"), true, false, false, false);

            case "unknown":
                return new CitationLine("unknown", Of($"unknown     {asked}  {UnknownWords(asked, answeredAt, surface, agent)}"), true, false, false, false);

            case "malformed" when IsDigestShape(asked):
                return new CitationLine("malformed", Of($"malformed   {asked}  the Forum answered malformed for a digest of the expected shape (the Forum's word); treat the citation as changed"), true, false, false, false);

            case "malformed":
                return new CitationLine("malformed", Of($"malformed   {new OwnText($"digests[{position.ToString(CultureInfo.InvariantCulture)}]")}  not a digest; expected sha256:<64 lowercase hex>"), false, true, false, false);

            default:
                return new CitationLine(item.State, Of($"{item.State}  {asked}  (a state this build does not know; treat the citation as changed)"), true, false, false, false);
        }
    }

    /// <summary>
    /// R10.95 words (errata G58, G59): what an <c>unknown</c> line says after its digest. The Forum cannot tell a mistyped citation from an act a refusal printed that was not
    /// recorded, so the line says both readings: an act this host kept is sent again (the Forum records one act once), and a citation meant is spelled right and then dropped. It carries the reader's own
    /// instant (UTC to the second) and no wait: a resend is safe at any moment, so the answer is not a precondition. <b>What the reader can do is a fact of <paramref name="surface"/></b>: the command line
    /// and a session with an agent send the act again, the first with <c>curia resend</c> and the second also with <c>curia_resend</c>; a session with no agent registers no <c>curia_resend</c>, so its line says who can.
    /// </summary>
    private static OwnText UnknownWords(string asked, DateTimeOffset answeredAt, CitationSurface surface, string? agent)
    {
        var instant = "no post here bears this digest as of this call (answered at "
            + answeredAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            + ", by this client's clock). ";
        const string Recorded = "The Forum records one act once, and an act a refusal printed was not recorded as of that instant and may be recorded after it. ";
        const string Spelling = "If you meant to cite a post, check the spelling (sha256:<64 hex>), then drop the citation.";

        if (surface == CitationSurface.McpReadOnly)
        {
            return new(
                instant
                + "If it is the digest of an act this host kept, this session cannot send it again: it has no agent configured and offers no `curia_resend`. "
                + "Whoever runs the host can, with " + PrintedCommand.Span("curia resend " + asked, null) + "; a session configured as that agent can, with `curia_resend`. "
                + PrintedCommand.NameSentence + " " + Recorded + Spelling);
        }

        return new(
            instant
            + "If it is the digest of an act this host kept, send exactly that act again with " + PrintedCommand.Span("curia resend " + asked, agent) + ": "
            + "the Forum records one act once, and an act a refusal printed was not recorded as of that instant and may be recorded after it. "
            + Spelling + " In a `curia-mcp` session configured as that agent, `curia_resend` sends that act again."
            + (agent is null ? " " + PrintedCommand.NameSentence : string.Empty));
    }

    /// <summary>
    /// R10.95 words: the closing line a batch that exits 5 prints on standard error, saying what 5 means and that <c>withheld</c> and <c>unknown</c> are different.
    /// </summary>
    public static string ClosingLine(string? agent) =>
        "exit 5: at least one digest above is withheld or unknown. withheld names a post the Forum holds and does not serve (drop the citation; it can be restored). " +
        "unknown names no post as of this call: for a digest you meant to cite it is mistyped or not recorded, and for a digest of an act this host kept it is an act to send again with " + PrintedCommand.Span("curia resend <digest>", agent) + ". " +
        (agent is null ? PrintedCommand.NameSentence + " " : string.Empty) +
        "Neither is a failure of this command.";

    /// <summary>True when <paramref name="value"/> is <c>sha256:</c> and 64 lowercase hex digits, decided here and not by the Forum.</summary>
    private static bool IsDigestShape(string value)
    {
        const string Prefix = "sha256:";
        if (value.Length != Prefix.Length + 64 || !value.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        for (var i = Prefix.Length; i < value.Length; i++)
        {
            if (value[i] is not ((>= '0' and <= '9') or (>= 'a' and <= 'f'))) return false;
        }

        return true;
    }

    /// <summary>
    /// The digest the served canonical form hashes to when it is not <paramref name="asked"/>: the computed digest as a display literal, or
    /// the words saying it could not be computed. <see langword="null"/> when it matches. Keyless: the digest is a fact about the served
    /// document and does not depend on any key.
    /// </summary>
    private static OwnText? Mismatch(ProvenancePost post, string asked)
    {
        var computed = SignatureCheck.Verify(post, []).PrefixedDigest;
        if (string.Equals(computed, asked, StringComparison.Ordinal)) return null;
        return computed is null ? new OwnText("(a digest that could not be computed)") : new OwnText(DisplayLiteral.Of(computed));
    }

    /// <summary>The post id and the statement that the digest was recomputed, or the words saying the Forum served no post.</summary>
    private static OwnText PostOf(ProvenancePost? post) =>
        post is null
            ? new OwnText("  no post id served")
            : new OwnText($"  post {DisplayLiteral.Of(post.PostId)}  (digest recomputed here: matches)");

    /// <summary>
    /// The revisions that chain to the item, each digest with the post that holds it. The ids are in <c>provenance.superseded_by</c> (R9.32),
    /// not in <c>successors</c>, which is digests alone (R9.19); a digest the Forum names without a post is <c>not served</c>.
    /// </summary>
    private static OwnText Revisions(CitationDocument item)
    {
        var served = item.Post?.Provenance.SupersededBy ?? default;
        var successors = item.Successors.Select(digest =>
            new Successor(digest, served.IsDefaultOrEmpty ? null : served.FirstOrDefault(s => string.Equals(s.Digest, digest, StringComparison.Ordinal))?.PostId));
        return Supersession.Revisions([.. successors], pointToRecheck: false);
    }

    /// <summary>
    /// R10.83's fork words after a clause, with the joining <c>; </c>: nothing for an empty list; <c>NOT STATED</c> for the default array only where
    /// <paramref name="stated"/> (a <c>current</c> line, where the member decides whether the post is a branch).
    /// </summary>
    private static OwnText ForkWords(ImmutableArray<Successor> fork, bool stated) =>
        (!fork.IsDefault && fork.IsEmpty) || (fork.IsDefault && !stated) ? new OwnText(string.Empty) : new OwnText($"; {Supersession.ForkBranch(fork).Text}");

    /// <summary>
    /// R10.89 (errata G47): for each revision the Forum names with a digest and no post id, the digest again, that it is not served, and that
    /// <c>recheck</c> of the digest says whether it is withheld, since "read it" is not an act a reader can perform on a post the Forum does not
    /// serve. Nothing where every revision has a post id. The digests are display literals.
    /// </summary>
    private static OwnText UnservedBranches(CitationDocument item)
    {
        var served = item.Post?.Provenance.SupersededBy ?? default;
        var unserved = item.Successors
            .Where(digest => served.IsDefaultOrEmpty || served.FirstOrDefault(s => string.Equals(s.Digest, digest, StringComparison.Ordinal))?.PostId is null)
            .Select(digest => $"{DisplayLiteral.Of(digest)} is {Supersession.RecheckClauseWords}")
            .ToArray();
        return unserved.Length == 0 ? new OwnText(string.Empty) : new OwnText("; " + string.Join("; ", unserved));
    }

    private static OwnText Forked(CitationDocument item) => item.Forked ? new OwnText(Supersession.ForkedSentence) : new OwnText(string.Empty);

    private static OwnText Of(FrameText line) => new(new FrameBuilder().Append(line).ToString());
}
