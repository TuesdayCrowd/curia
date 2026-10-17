using System.Collections.Immutable;
using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.Canon.Envelope;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;
using Curia.Domain.Search;

namespace Curia.Application.Projections;

/// <summary>
/// Which posts are disputed (R6.57), and what the log says of the key each was signed under.
///
/// <para>A post is <b>disputed</b> when a declaration names the key it was signed under and the post lies
/// in the declaration's window: its log index is at least the declaration's <c>from_index</c> (and at
/// least the key's own binding), and below the declaration's own index. A post below <c>from_index</c>
/// is not disputed even under a compromised key, and a post is never disputed by a declaration made
/// before it: ingest refuses a post under a compromised key (R6.57), so none exists.</para>
///
/// <para><b>Disputed is a mark, not a withholding (R6.28).</b> Nothing here feeds
/// <c>Servable</c>: a disputed post is still served, marked, and a reader decides. The two places a
/// dispute takes effect are the ones the specification names: search omits the post and counts it, and
/// a disputed vote or verification carries no weight.</para>
///
/// <para>The Forum folds its own log, so a declaration is read as the log records it. The readers, who
/// need not trust the Forum, hold a declaration to R6.57's cascade themselves (Task 2.3). A lifecycle
/// the fold refuses (a stream that fails <see cref="KeyLifecycle.Of"/>) yields no mark for that author,
/// as it yields none anywhere else the Forum serves: ingest would not have admitted the entry.</para>
/// </summary>
public sealed class DisputedPosts
{
    /// <summary>No post is disputed, for a caller whose log holds no declaration.</summary>
    public static DisputedPosts None { get; } = new(
        ImmutableDictionary<string, long>.Empty.WithComparers(StringComparer.Ordinal),
        ImmutableDictionary<string, KeyStatus>.Empty.WithComparers(StringComparer.Ordinal));

    private readonly ImmutableDictionary<string, long> _declarationIndex;
    private readonly ImmutableDictionary<string, KeyStatus> _status;

    private DisputedPosts(ImmutableDictionary<string, long> declarationIndex, ImmutableDictionary<string, KeyStatus> status)
    {
        _declarationIndex = declarationIndex;
        _status = status;
    }

    /// <summary>How many posts are disputed.</summary>
    public int Count => _declarationIndex.Count;

    /// <summary>Whether <paramref name="post"/> is disputed.</summary>
    public bool IsDisputed(PostView post)
    {
        ArgumentNullException.ThrowIfNull(post);
        return _declarationIndex.ContainsKey(post.PostId);
    }

    /// <summary>Whether the search candidate <paramref name="post"/> is disputed.</summary>
    public bool IsDisputed(SearchablePost post)
    {
        ArgumentNullException.ThrowIfNull(post);
        return _declarationIndex.ContainsKey(post.PostId);
    }

    /// <summary>The log index of the declaration that disputes <paramref name="post"/>, or null when none does.</summary>
    public long? DisputedByLogIndex(PostView post)
    {
        ArgumentNullException.ThrowIfNull(post);
        return _declarationIndex.TryGetValue(post.PostId, out var index) ? index : null;
    }

    /// <summary>
    /// The wire word for the status of the key <paramref name="post"/> was signed under, as of the log
    /// this was folded from: <c>active</c>, <c>retired</c> or <c>compromised</c>. A key the log does not
    /// bind (an identity from before the log bound keys, or a signature whose header cannot be read) is
    /// <c>active</c>: the post was admitted under it, and nothing in the log ended it.
    /// </summary>
    public string KeyStatusOf(PostView post)
    {
        ArgumentNullException.ThrowIfNull(post);
        return _status.TryGetValue(post.PostId, out var status) && status is KeyStatus.Retired or KeyStatus.Compromised
            ? Wire(status)
            : Wire(KeyStatus.Active);
    }

    private static string Wire(KeyStatus status) => status switch
    {
        KeyStatus.Retired => "retired",
        KeyStatus.Compromised => "compromised",
        KeyStatus.Active or KeyStatus.Unbound => "active",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Not a key status"),
    };

    /// <summary>
    /// Folds <paramref name="posts"/> against the lifecycle <paramref name="log"/> records. A log index is a
    /// position in <paramref name="log"/> (R6.47: leaf <i>i</i> is the <i>i</i>th event), so <paramref name="log"/>
    /// is the whole log from its start, or a prefix of it.
    /// </summary>
    public static DisputedPosts Fold(IReadOnlyList<AppendedEvent> log, ImmutableArray<PostView> posts)
    {
        ArgumentNullException.ThrowIfNull(log);

        var position = new Dictionary<string, long>(StringComparer.Ordinal);
        var streams = new Dictionary<string, List<AppendedEvent>>(StringComparer.Ordinal);
        for (var i = 0; i < log.Count; i++)
        {
            var appended = log[i];
            position[appended.Event.Id.Value] = i;
            if (!KeyLifecycle.IsLifecycleKind(appended.Event.Type.Value)
                && !string.Equals(appended.Event.Type.Value, AgentStandingProjector.EnrolledType, StringComparison.Ordinal))
                continue;

            var key = appended.AggregateId.Value;
            if (!streams.TryGetValue(key, out var stream)) streams[key] = stream = [];
            stream.Add(appended);
        }

        var lifecycles = new Dictionary<string, KeyLifecycle?>(StringComparer.Ordinal);
        var disputed = ImmutableDictionary.CreateBuilder<string, long>(StringComparer.Ordinal);
        var statuses = ImmutableDictionary.CreateBuilder<string, KeyStatus>(StringComparer.Ordinal);

        foreach (var post in posts)
        {
            if (!lifecycles.TryGetValue(post.Author, out var lifecycle))
            {
                lifecycle = null;
                if (AgentId.Parse(post.Author).TryGetValue(out var agent, out _)
                    && KeyLifecycle.Of(streams.TryGetValue(post.Author, out var stream) ? stream : [], agent!).TryGetValue(out var folded, out _))
                    lifecycle = folded;

                lifecycles[post.Author] = lifecycle;
            }

            if (lifecycle is null || SigningKid(post) is not { } kid) continue;

            var status = lifecycle.StatusNow(kid);
            statuses[post.PostId] = status;
            if (status is not KeyStatus.Compromised || !position.TryGetValue(post.PostId, out var postIndex)) continue;

            var bound = lifecycle.Entries
                .Where(e => e.Kid is not null && string.Equals(e.Kid, kid, StringComparison.Ordinal) && e.Type != LifecycleAppendGuard.KeyRetiredType)
                .Select(e => position.TryGetValue(e.EventId, out var p) ? p : long.MaxValue)
                .DefaultIfEmpty(long.MaxValue)
                .Min();

            // The first declaration (lowest log index) whose window holds the post.
            long? by = null;
            foreach (var entry in lifecycle.Entries)
            {
                if (entry.Kids is not { } kids || entry.FromIndex is not { } from) continue;
                if (!kids.Contains(kid, StringComparer.Ordinal)) continue;
                if (!position.TryGetValue(entry.EventId, out var declared)) continue;
                if (postIndex < from || postIndex < bound || postIndex >= declared) continue;
                by = by is { } existing ? Math.Min(existing, declared) : declared;
            }

            if (by is { } index) disputed[post.PostId] = index;
        }

        return new DisputedPosts(disputed.ToImmutable(), statuses.ToImmutable());
    }

    /// <summary>The <c>kid</c> in the post's protected header, or null when the header cannot be read.</summary>
    private static string? SigningKid(PostView post) =>
        DetachedJws.ReadProtectedHeader(new JwsSignature(post.Signature)).TryGetValue(out var header, out _)
            ? header!.Kid
            : null;
}
