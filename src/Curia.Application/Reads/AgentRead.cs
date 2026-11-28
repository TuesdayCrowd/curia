using System.Collections.Immutable;
using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Content;
using Curia.Domain.Authorization;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;

namespace Curia.Application.Reads;

/// <summary>What one <see cref="AgentRead"/> reads beyond the agent's own stream and the published keys.</summary>
/// <param name="Posture">The agent's posts, the streams of those posts (their moderation) and the threads its answers belong to (their acceptances): what Table 11's facts are folded from.</param>
/// <param name="Findings">The signals on the agent's findings and the streams of their casters (R7.19's verified findings, errata G87's one payload-keyed clause); read only when the agent has a finding. Needs <paramref name="Posture"/>.</param>
/// <param name="Acceptances">The acceptances the agent recorded (R7.26's budget).</param>
/// <param name="Extra">Clauses the caller adds to the first stage (a flag's entries, a digest), for a route that decides on rows beyond the agent's own.</param>
public sealed record AgentReadOptions(bool Posture, bool Findings = false, bool Acceptances = false, IReadOnlyList<ReadClause>? Extra = null)
{
    /// <summary>The agent's stream and the published keys: what a key's binding and an enrolment are decided from.</summary>
    public static AgentReadOptions Stream { get; } = new(Posture: false);

    /// <summary>Table 11's facts without the verified-findings arm: what a token's stamped tier is computed from (the claim is for observability only, R7.7).</summary>
    public static AgentReadOptions PostureOnly { get; } = new(Posture: true);

    /// <summary>Table 11's facts in full: the posture every request that authorizes is decided on.</summary>
    public static AgentReadOptions PostureAndFindings { get; } = new(Posture: true, Findings: true);
}

/// <summary>
/// Everything one request decides about one agent, from one read set taken in one snapshot (errata G87, R5.28, R11.113): the agent's stream and the published keys, and when asked its posts, the
/// moderation of those posts, the acceptances in the threads of its answers and the signals on its findings. Nothing here is carried between requests; the object is the request's and is dropped with it.
/// It decides as the whole-log folds decide, from the rows that bear on the agent, and refuses (<see cref="OutOfScopeException"/>) a question about a row it was not read for.
/// </summary>
public sealed class AgentRead
{
    private readonly AgentReadOptions _options;
    private readonly Lazy<ImmutableArray<PostView>> _posts;
    private readonly Lazy<Result<PostureFacts>> _posture;

    private AgentRead(AgentId agent, AgentReadOptions options, LogSlice slice, VerifiedLog verified)
    {
        Agent = agent;
        _options = options;
        Slice = slice;
        Verified = verified;
        _posts = new Lazy<ImmutableArray<PostView>>(() => PostProjector.Fold([.. slice.Rows.Select(r => r.Event).Where(e => IsPostBy(e, agent))]));
        _posture = new Lazy<Result<PostureFacts>>(ComputePosture);
    }

    /// <summary>The agent this read was made for.</summary>
    public AgentId Agent { get; }

    /// <summary>The rows, with their log indices and what they were read for.</summary>
    public LogSlice Slice { get; }

    /// <summary>The log over the rows: decides what the read was made for.</summary>
    public VerifiedLog Verified { get; }

    /// <summary>What the verifier decides of the agent's stream (R6.64).</summary>
    public VerifiedLifecycle Lifecycle => Verified.For(Agent);

    /// <summary>
    /// The version of the agent's stream the decision was made on: its events' count, from the same snapshot (R6.56). An append conditioned on it lands only while no entry has joined the stream since.
    /// </summary>
    public Result<AggregateVersion> StreamVersion =>
        AggregateVersion.From(Slice.Rows.Count(r => string.Equals(r.Event.AggregateId.Value, Agent.Value, StringComparison.Ordinal)));

    /// <summary>The greatest <c>seq</c> of the log in the snapshot: an append guard's <c>After</c> for a decision made on this read.</summary>
    public EventSequence HighWater => Slice.HighWater;

    /// <summary>The agent's posts, in <c>seq</c> order (the rows <c>PostsBy</c> read).</summary>
    public ImmutableArray<PostView> Posts => _options.Posture ? _posts.Value : throw new OutOfScopeException("the agent's posts");

    /// <summary>The stored instants of the posts the agent authored (R7.15): the posting budget's entries.</summary>
    public IReadOnlyList<DateTimeOffset> PostInstants => [.. Posts.Select(p => p.ServerTimestamp.Value)];

    /// <summary>The stored instants of the acceptances the agent recorded (R7.26): the acceptance budget's entries.</summary>
    public IReadOnlyList<DateTimeOffset> AcceptInstants =>
        _options.Acceptances || _options.Posture && Slice.Scope.CoversAcceptancesBy(Agent.Value)
            ? [.. AcceptanceProjector.InstantsBy([.. Slice.Rows.Select(r => r.Event)], Agent)]
            : throw new OutOfScopeException("the agent's acceptances");

    /// <summary>Table 11's facts for the agent (R7.7, R7.19), decided from this read.</summary>
    public Result<PostureFacts> Posture => _options.Posture ? _posture.Value : throw new OutOfScopeException("the agent's posture");

    /// <summary>The read for <paramref name="agent"/>: the plan, one snapshot, one verifier over what came back.</summary>
    public static async Task<AgentRead> ReadAsync(
        IPositionedEventReader reader, LifecycleVerifier verifier, AgentId agent, AgentReadOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(options);

        // An identifier no aggregate can be named by has no stream: the read is the keys and nothing else, as the whole-log reader's answer for it was an empty log.
        if (!AggregateId.Create(agent.Value).TryGetValue(out var aggregate, out _))
        {
            var none = await reader.ReadAsync(ReadPlan.Of(ReadClause.Keys()), cancellationToken).ConfigureAwait(false);
            var scope = new ScopeBuilder();
            scope.Note(ReadClause.Keys());
            scope.Note(ReadClause.Aggregates([agent.Value]));
            var slice = new LogSlice(none.Rows, none.HighWater, scope.Build());
            return new AgentRead(agent, options, slice, verifier.OfSlice(slice));
        }

        var read = await reader.ReadAsync(PlanFor(agent, aggregate!.Value, options), cancellationToken).ConfigureAwait(false);
        return new AgentRead(agent, options, read, verifier.OfSlice(read));
    }

    /// <summary>The read plan for <paramref name="agent"/>: up to three stages, each a function of the rows before it.</summary>
    public static ReadPlan PlanFor(AgentId agent, string aggregate, AgentReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(options);

        var first = new List<ReadClause> { ReadClause.Aggregates([aggregate]), ReadClause.Keys() };
        if (options.Posture) first.Add(ReadClause.PostsBy(agent.Value));
        if (options.Acceptances) first.Add(ReadClause.AcceptancesBy(agent.Value));
        if (options.Extra is { } extra) first.AddRange(extra);

        if (!options.Posture) return ReadPlan.Of([.. first]);

        // Stage 2: the streams of the agent's posts (their moderation), the streams of the threads its answers belong to (their acceptances), and, for an agent holding a finding, the signals on it.
        IReadOnlyList<ReadClause> Second(IReadOnlyList<AppendedEvent> rows)
        {
            var mine = PostsOf(rows, agent);
            var clauses = new List<ReadClause>
            {
                ReadClause.Aggregates(mine.Select(p => p.PostId)),
                ReadClause.Aggregates(mine.Where(p => p.Kind == "answer" && p.Parent is not null).Select(p => p.Parent!)),
            };
            if (options.Findings && mine.Where(p => p.Kind == "finding").Select(p => p.Digest).ToArray() is { Length: > 0 } digests)
                clauses.Add(ReadClause.SignalsOn(digests));
            return clauses;
        }

        if (!options.Findings) return ReadPlan.Staged(_ => [.. first], Second);

        // Stage 3: the streams of the signals' posts (their moderation) and of their casters (their lifecycle and owner).
        IReadOnlyList<ReadClause> Third(IReadOnlyList<AppendedEvent> rows)
        {
            var signals = SignalsOn(rows, agent);
            return signals.Count == 0
                ? [ReadClause.Aggregates([])]
                : [ReadClause.Aggregates(signals.Select(s => s.PostId).Concat(signals.Select(s => s.Author)))];
        }

        return ReadPlan.Staged(_ => [.. first], Second, Third);
    }

    private static ImmutableArray<PostView> PostsOf(IReadOnlyList<AppendedEvent> rows, AgentId agent) =>
        PostProjector.Fold([.. rows.Where(e => IsPostBy(e, agent))]);

    /// <summary>The votes and verifications in <paramref name="rows"/> that target a finding of <paramref name="agent"/>, whoever cast them.</summary>
    private static List<PostView> SignalsOn(IReadOnlyList<AppendedEvent> rows, AgentId agent)
    {
        var posts = PostProjector.Fold([.. rows.Where(e => e.Event.Type.Value == PostProjector.PostAcceptedType)]);
        var findings = posts
            .Where(p => string.Equals(p.Author, agent.Value, StringComparison.Ordinal) && p.Kind == "finding")
            .Select(p => p.Digest)
            .ToHashSet(StringComparer.Ordinal);
        return [.. posts.Where(p => SignalTargetOf(p) is { } target && findings.Contains(target))];
    }

    /// <summary>
    /// The <c>target</c> of a vote or verification, read from its canonical envelope as <see cref="VerificationProjector.Fold"/> reads it (and as <c>events_post_signal_target</c> indexes it); null for any
    /// other kind or an unreadable canonical. <see cref="VerificationProjector.TargetOf"/> is the one-vote rule's and reads votes alone.
    /// </summary>
    internal static string? SignalTargetOf(PostView post)
    {
        if (!PostKinds.TryParse(post.Kind, out var kind) || !PostKinds.RequiresTarget(kind)) return null;

        var parsed = JsonReader.ParseUnrestricted(System.Text.Encoding.UTF8.GetBytes(post.Canonical));
        if (!parsed.TryGetValue(out var value, out _) || value is not JsonValue.Object root) return null;

        return PostEnvelope.Read(root).TryGetValue(out var envelope, out _) ? envelope!.Target : null;
    }

    private static bool IsPostBy(AppendedEvent e, AgentId agent) =>
        e.Event.Type.Value == PostProjector.PostAcceptedType
        && string.Equals(e.Event.Actor?.Value, agent.Value, StringComparison.Ordinal);

    private Result<PostureFacts> ComputePosture()
    {
        var own = Verified.PostureStandingOf(Agent);
        var standings = new Dictionary<string, AgentStanding>(StringComparer.Ordinal);
        if (own is not null) standings[Agent.Value] = own;

        if (!_options.Findings)
            return AgentStandingProjector.PostureOf(standings, Agent);

        // R7.19's one payload-keyed clause: the findings the agent holds, the signals on them and each caster's standing, owner and key status, which VerificationProjector reads.
        var signals = SignalsOn([.. Slice.Rows.Select(r => r.Event)], Agent);
        var mine = _posts.Value;
        var findings = mine.Where(p => p.Kind == "finding").ToArray();
        if (findings.Length == 0)
            return AgentStandingProjector.PostureOf(standings, Agent);

        foreach (var signal in signals)
            if (!standings.ContainsKey(signal.Author) && AgentId.Parse(signal.Author).TryGetValue(out var caster, out _) && Verified.StandingOf(caster!) is { } standing)
                standings[signal.Author] = standing;

        // The posts the verification reads, in log order, each once: the agent's own (its findings are the targets) and the signals on them. Another agent's post the read holds (a thread's root, say) is not
        // among them, so no question is asked about an agent whose stream was not read.
        var considered = ImmutableArray.CreateRange(
            mine.Concat(signals).DistinctBy(p => p.PostId, StringComparer.Ordinal).OrderBy(p => Verified.PositionOf(p.PostId) ?? long.MaxValue));

        bool Servable(string postId) => Verified.ModerationOf(postId) is not { } state || state.MayServe;
        var verification = VerificationProjector.Fold(considered, standings, Servable, DisputedPosts.Fold(Verified, considered));
        return AgentStandingProjector.PostureOf(standings, Agent, VerificationProjector.VerifiedFindingsBy(verification, considered, Agent));
    }
}
