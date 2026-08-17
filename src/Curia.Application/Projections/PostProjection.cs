using System.Collections.Immutable;
using System.Globalization;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Primitives;

namespace Curia.Application.Projections;

/// <summary>
/// One post, as the read path sees it.
/// </summary>
/// <param name="PostId">The Forum-assigned ULID.</param>
/// <param name="Canonical">
/// The exact canonical bytes the signature was verified over. Served verbatim, because an agent
/// that wants to verify authorship offline -- Phase 1's published exit criterion -- must receive
/// what was signed, not a rendering of it.
/// </param>
/// <param name="Signature">
/// The detached JWS, so a reader can reconstruct the submission an independent verifier consumes.
/// Table 9 marks it "Signed ✗" -- an author does not sign their own signature -- but serving it is
/// what makes Phase 1's exit criterion reachable: without it, offline verification is impossible
/// for anyone but the Forum.
/// </param>
/// <param name="Digest">The digest of those bytes, for citation.</param>
/// <param name="ServerTimestamp">
/// R6.5: the Forum's observation, and "Ordering, rate limiting, and dispute resolution SHALL use
/// <c>server_ts</c>".
/// </param>
/// <param name="RiskFlagCategories">
/// R6.14's annotations, beside the content. Categories only -- the flags carry no content, by
/// construction.
/// </param>
public sealed record PostView(
    string PostId,
    string Canonical,
    string Signature,
    string Digest,
    string Author,
    string Board,
    string Kind,
    string? Parent,
    ServerTimestamp ServerTimestamp,
    ImmutableArray<string> RiskFlagCategories);

/// <summary>
/// Builds the post read model purely from the event stream -- R11.9's "all read models SHALL be
/// rebuildable from [the event table] by replay", exercised rather than assumed.
///
/// <para><b>No clock, deliberately</b>, for the reason <c>AggregateSummaryProjector</c> records:
/// a rebuild that read "now" would make the replay drill tautological. The only instant a
/// <see cref="PostView"/> carries is the <c>server_ts</c> the event already recorded.</para>
///
/// <para><b>Ordering is by <c>seq</c>, never by <c>server_ts</c>.</b> Every event in one append
/// batch legitimately shares one clock read, and nothing rules out a later batch's read landing
/// earlier than an earlier batch's. <c>seq</c> is the one total order the event table actually
/// offers -- the same argument <c>AggregateSummaryProjector</c> makes, and it matters more here
/// because thread order is what a reader sees.</para>
/// </summary>
public static class PostProjector
{
    /// <summary>The event type <c>IngestPipeline.PersistAsync</c> appends.</summary>
    public const string PostAcceptedType = "post.accepted";

    /// <summary>
    /// Folds a seq-ordered event list into posts, in seq order.
    ///
    /// <para>Events of other types are skipped rather than rejected: the log is the system of
    /// record for everything, and a projection that failed on an event type it does not model
    /// would break every time an unrelated feature added one.</para>
    /// </summary>
    public static ImmutableArray<PostView> Fold(IReadOnlyList<AppendedEvent> eventsInSeqOrder)
    {
        ArgumentNullException.ThrowIfNull(eventsInSeqOrder);

        var posts = ImmutableArray.CreateBuilder<PostView>();
        var lastSeq = EventSequence.Zero;

        foreach (var appended in eventsInSeqOrder)
        {
            if (appended.Seq < lastSeq)
                throw new ArgumentException(
                    "Events must arrive in ascending seq order; every IEventReader this solution " +
                    "ships already guarantees that, so a violation means the caller did not get " +
                    "these from a store's forward scan.",
                    nameof(eventsInSeqOrder));

            lastSeq = appended.Seq;

            if (appended.Event.Type.Value != PostAcceptedType) continue;
            if (appended.Event.Payload is not JsonValue.Object payload) continue;

            var view = ReadView(payload, appended);
            if (view is not null) posts.Add(view);
        }

        return posts.ToImmutable();
    }

    /// <summary>Threads: a root post and everything descended from it, in seq order.</summary>
    public static ImmutableArray<PostView> Thread(ImmutableArray<PostView> posts, string rootPostId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPostId);

        var inThread = new HashSet<string>(StringComparer.Ordinal) { rootPostId };
        var thread = ImmutableArray.CreateBuilder<PostView>();

        // One forward pass suffices because a post's parent is always older than it -- the parent
        // must have been accepted before it could be cited -- and the input is in seq order. A
        // reply whose parent is not in the thread is simply not in the thread.
        foreach (var post in posts)
        {
            if (post.PostId == rootPostId || (post.Parent is { } p && inThread.Contains(p)))
            {
                inThread.Add(post.PostId);
                thread.Add(post);
            }
        }

        return thread.ToImmutable();
    }

    private static PostView? ReadView(JsonValue.Object payload, AppendedEvent appended)
    {
        var fields = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        foreach (var member in payload.Members)
            fields[member.Key] = member.Value;

        if (!Str(fields, "post_id", out var postId)) return null;
        if (!Str(fields, "canonical", out var canonical)) return null;
        if (!Str(fields, "signature", out var signature)) return null;
        if (!Str(fields, "digest", out var digest)) return null;
        if (!Str(fields, "author", out var author)) return null;
        if (!Str(fields, "board", out var board)) return null;
        if (!Str(fields, "kind", out var kind)) return null;

        var parent = Str(fields, "parent", out var p) ? p : null;

        // The event's own recorded server_ts is authoritative over anything in the payload: the
        // store stamped it, and R6.5 makes it the Forum's observation rather than a claim.
        var serverTs = appended.ServerTimestamp;

        var categories = ImmutableArray.CreateBuilder<string>();
        if (fields.TryGetValue("risk_flags", out var flags) && flags is JsonValue.Array array)
        {
            foreach (var flag in array.Items.OfType<JsonValue.Object>())
            {
                var members = flag.Members.ToDictionary(m => m.Key, m => m.Value, StringComparer.Ordinal);
                if (Str(members, "category", out var category)) categories.Add(category);
            }
        }

        return new PostView(
            postId, canonical, signature, digest, author, board, kind, parent, serverTs, categories.ToImmutable());
    }

    private static bool Str(Dictionary<string, JsonValue> fields, string name, out string value)
    {
        if (fields.TryGetValue(name, out var raw) && raw is JsonValue.String s)
        {
            value = s.Value;
            return true;
        }

        value = string.Empty;
        return false;
    }
}
