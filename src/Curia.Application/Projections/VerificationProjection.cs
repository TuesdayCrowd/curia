using System.Collections.Immutable;
using Curia.Canon.Json;
using Curia.Domain.Content;
using Curia.Domain.Verification;

namespace Curia.Application.Projections;

/// <summary>Table 13's answer for one target digest, and the reports behind it (R8.57, R8.59).</summary>
/// <param name="Target">The envelope digest the signals name.</param>
/// <param name="Level">The computed level.</param>
/// <param name="DistinctEndorsingOwners">How many distinct owners' countable votes endorse. Kept for the fold's own tests; never served (R8.59).</param>
/// <param name="Reproductions">Digests of the countable reports whose result is <c>reproduced</c>, in log order.</param>
/// <param name="Contradictions">Digests of the countable reports whose result is <c>contradicted</c>, in log order. R8.15: surfaced on the post.</param>
public sealed record VerificationState(
    string Target,
    VerificationLevel Level,
    int DistinctEndorsingOwners,
    ImmutableArray<string> Reproductions,
    ImmutableArray<string> Contradictions);

/// <summary>
/// The verification read model: every target that has countable signals, and the anomalies the
/// fold refused to count.
/// </summary>
/// <param name="ByTarget">States keyed by target digest. A digest with no entry is V0.</param>
/// <param name="Anomalies">
/// Post ids of votes and reports whose author has no attested owner. Not countable (R4.24 puts the
/// unit of cost on the owner) and not reachable through the authorised write path, since T1
/// requires owner verification and owner verification names an owner -- so one of these is an
/// authorisation-invariant violation, surfaced rather than folded into "doesn't count". Never
/// thrown: R11.9's replay stays total.
/// </param>
public sealed record VerificationFold(
    ImmutableDictionary<string, VerificationState> ByTarget,
    ImmutableArray<string> Anomalies)
{
    public static VerificationFold Empty { get; } = new(
        ImmutableDictionary<string, VerificationState>.Empty.WithComparers(StringComparer.Ordinal), []);

    public VerificationLevel LevelOf(string digest) =>
        ByTarget.TryGetValue(digest, out var state) ? state.Level : VerificationLevel.V0;

    public VerificationState StateOf(string digest) =>
        ByTarget.TryGetValue(digest, out var state) ? state : new VerificationState(digest, VerificationLevel.V0, 0, [], []);
}

/// <summary>
/// Folds votes and verification reports into Table 13 levels, per target digest (errata G8,
/// R8.57). No clock, no store; a function of the post read model, the agent standings and the
/// serving filter, so R11.9's replay drill covers it by covering those.
///
/// <para><b>What counts.</b> A vote or report counts when its own post may be served (R6.25,
/// R10.36 -- a withheld contradiction stops counting and the level rises again), when its author's
/// owner is known, when it is that author's current signal of its class for the target (a later
/// report supersedes the same agent's earlier one and nobody else's), and when the pair is one
/// <see cref="VerificationPolicy.Refusal"/> would permit -- the ingest path refuses self and
/// same-owner signals before they are written, and the fold applies the same rule to whatever the
/// log turns out to hold, so a stray event cannot mint a level on replay.</para>
///
/// <para><b>Levels attach to digests, never post ids.</b> R8.5 makes an edit a new signed revision
/// with a new digest, so a revision begins at V0; a level attached to a post id would be a badge
/// earned by one text and worn by another.</para>
/// </summary>
public static class VerificationProjector
{
    private sealed record Signal(string Owner, bool IsVote, bool Endorse, VerificationResult? Result, string Digest);

    public static VerificationFold Fold(
        ImmutableArray<PostView> postsInSeqOrder,
        IReadOnlyDictionary<string, AgentStanding> standings,
        Func<string, bool> servable)
    {
        ArgumentNullException.ThrowIfNull(standings);
        ArgumentNullException.ThrowIfNull(servable);

        var byDigest = new Dictionary<string, PostView>(StringComparer.Ordinal);
        foreach (var post in postsInSeqOrder)
            byDigest.TryAdd(post.Digest, post);

        string? OwnerOf(string agent) => standings.TryGetValue(agent, out var s) ? s.OwnerId : null;

        var latest = new Dictionary<(string Author, string Target, bool IsVote), Signal>();
        var order = new List<(string Author, string Target, bool IsVote)>();
        var anomalies = ImmutableArray.CreateBuilder<string>();

        foreach (var post in postsInSeqOrder)
        {
            if (!PostKinds.TryParse(post.Kind, out var kind) || !PostKinds.RequiresTarget(kind)) continue;

            // Re-derived from the persisted canonical bytes, as SearchProjector and PostView.Prev
            // are: R6.12's byte-identity makes them exactly what the author signed.
            var parsed = JsonReader.ParseUnrestricted(System.Text.Encoding.UTF8.GetBytes(post.Canonical));
            if (!parsed.TryGetValue(out var value, out _) || value is not JsonValue.Object root) continue;
            if (!PostEnvelope.Read(root).TryGetValue(out var envelope, out _)) continue;
            if (envelope!.Target is not { } target) continue;

            if (!servable(post.PostId)) continue;

            var owner = OwnerOf(post.Author);
            if (owner is null)
            {
                anomalies.Add(post.PostId);
                continue;
            }

            if (!byDigest.TryGetValue(target, out var targetPost)) continue;
            if (!PostKinds.TryParse(targetPost.Kind, out var targetKind)) continue;

            if (VerificationPolicy.Refusal(
                    post.Author, owner, targetPost.Author, OwnerOf(targetPost.Author), targetKind, post.Board, targetPost.Board) is not null)
                continue;

            var key = (post.Author, target, kind is PostKind.Vote);
            if (!latest.ContainsKey(key)) order.Add(key);
            latest[key] = new Signal(owner, kind is PostKind.Vote, envelope.Endorse ?? false, envelope.Result, post.Digest);
        }

        var states = ImmutableDictionary.CreateBuilder<string, VerificationState>(StringComparer.Ordinal);
        foreach (var group in order.GroupBy(k => k.Target, StringComparer.Ordinal))
        {
            var signals = group.Select(k => latest[k]).ToArray();

            var endorsingOwners = signals
                .Where(s => s.IsVote && s.Endorse)
                .Select(s => s.Owner)
                .Distinct(StringComparer.Ordinal)
                .Count();

            var reproductions = signals
                .Where(s => !s.IsVote && s.Result is VerificationResult.Reproduced)
                .Select(s => s.Digest)
                .ToImmutableArray();

            var contradictions = signals
                .Where(s => !s.IsVote && s.Result is VerificationResult.Contradicted)
                .Select(s => s.Digest)
                .ToImmutableArray();

            states[group.Key] = new VerificationState(
                group.Key,
                VerificationPolicy.Level(endorsingOwners, reproductions.Length, contradictions.Length),
                endorsingOwners,
                reproductions,
                contradictions);
        }

        return new VerificationFold(states.ToImmutable(), anomalies.ToImmutable());
    }

    /// <summary>
    /// The <c>target</c> a vote or verification names, re-read from its canonical bytes, or
    /// <see langword="null"/> for any other kind or an unreadable canonical. For the ingest path's
    /// one-vote rule (R8.55), which needs the same reading the fold uses.
    /// </summary>
    public static string? TargetOf(PostView post)
    {
        ArgumentNullException.ThrowIfNull(post);

        if (!PostKinds.TryParse(post.Kind, out var kind) || kind is not PostKind.Vote) return null;

        var parsed = JsonReader.ParseUnrestricted(System.Text.Encoding.UTF8.GetBytes(post.Canonical));
        if (!parsed.TryGetValue(out var value, out _) || value is not JsonValue.Object root) return null;

        return PostEnvelope.Read(root).TryGetValue(out var envelope, out _) ? envelope!.Target : null;
    }

    /// <summary>
    /// R7.19: Table 11's "≥ 1 verified finding" for one agent -- findings it authored that stand at
    /// V2 or above. Counted over the current fold, so a later contradiction takes the finding out
    /// of the count on the next evaluation, which is R7.8's demotion-without-intervention.
    /// </summary>
    public static int VerifiedFindingsBy(VerificationFold fold, ImmutableArray<PostView> posts, string author)
    {
        ArgumentNullException.ThrowIfNull(fold);

        return posts.Count(p =>
            string.Equals(p.Author, author, StringComparison.Ordinal)
            && string.Equals(p.Kind, PostKinds.Wire(PostKind.Finding), StringComparison.Ordinal)
            && VerificationPolicy.IsVerifiedFinding(fold.LevelOf(p.Digest)));
    }
}
