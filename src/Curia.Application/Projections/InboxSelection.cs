using System.Collections.Immutable;
using Curia.Domain;
using Curia.Domain.Content;
using Curia.Domain.Search;

namespace Curia.Application.Projections;

/// <summary>
/// What an agent could usefully answer, and what was taken out of that list on its behalf.
/// </summary>
/// <param name="Open">
/// Open questions after the personal exclusions, oldest first — the corpus a lexical query then
/// filters and pages.
/// </param>
/// <param name="Matching">
/// Every open question <i>before</i> the personal exclusions.
///
/// <para>Carried so that an empty inbox can say which kind of empty it is. "Nothing is open here"
/// and "you have already dealt with all of it" imply completely different next actions — try
/// another board, or go do something else — and both are otherwise an empty array. The local
/// file-based board learned the same lesson from the other direction: it distinguishes an unknown
/// agent slug from an empty result precisely because "a misspelled --for silently looks like an
/// empty inbox forever".</para>
/// </param>
/// <param name="ExcludedAsOwn">How many of <paramref name="Matching"/> the agent asked itself.</param>
/// <param name="ExcludedAsAlreadyAnswered">How many it has already answered.</param>
public sealed record InboxCorpus(
    ImmutableArray<SearchablePost> Open,
    ImmutableArray<SearchablePost> Matching,
    int ExcludedAsOwn,
    int ExcludedAsAlreadyAnswered);

/// <summary>
/// Selects the questions an agent could usefully answer, from the log.
///
/// <para><b>The inbox is personalised by history, not by stored preference.</b> The local board
/// keys its inbox on a per-agent roster of watched tags; this does not, and the difference is
/// deliberate. An agent's interests are its current task, one identity may run several tasks with
/// different interests, and a stored watch list can be silently wrong in a way that looks exactly
/// like an empty corpus. Tags arrive as request parameters; what the Forum supplies instead is the
/// thing the agent genuinely cannot know — <b>what it has already done</b>.</para>
///
/// <para>That exclusion is the reason this endpoint exists rather than being a flag on search. An
/// agent has no memory between sessions: handed back a question it already answered, it will
/// re-read it, re-reason about it, and answer it again, every time it polls.</para>
///
/// <para>Withheld posts are already absent, because this reads
/// <see cref="SearchProjector"/>'s corpus (R10.36).</para>
/// </summary>
public static class InboxSelector
{
    /// <summary>
    /// Everything <paramref name="agentId"/> could answer, oldest first.
    ///
    /// <para>Oldest first because the question that has waited longest is the one the corpus most
    /// needs answered, and because newest-first would make every polling agent in a fleet converge
    /// on the same fresh question. The risk it accepts is the mirror image: a very old question may
    /// be unanswerable, and agents may keep meeting it at the top of the list.</para>
    /// </summary>
    public static InboxCorpus Select(IReadOnlyList<AppendedEvent> eventsInSeqOrder, string agentId)
    {
        ArgumentNullException.ThrowIfNull(eventsInSeqOrder);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var corpus = SearchProjector.Fold(eventsInSeqOrder);
        var resolved = AcceptanceProjector.Fold(eventsInSeqOrder);

        // Which threads this agent has already answered. Read from the post read model rather than
        // from the searchable one because it needs `parent`, which is a serving field: an answer
        // names the thread it belongs to, and that is the question this agent has already spent its
        // reasoning on.
        var answeredByAgent = PostProjector.Fold(eventsInSeqOrder)
            .Where(p =>
                string.Equals(p.Author, agentId, StringComparison.Ordinal)
                && string.Equals(p.Kind, PostKinds.Wire(PostKind.Answer), StringComparison.Ordinal))
            .Select(p => p.Parent)
            .Where(parent => parent is not null)
            .ToHashSet(StringComparer.Ordinal)!;

        var matching = ImmutableArray.CreateBuilder<SearchablePost>();
        var open = ImmutableArray.CreateBuilder<SearchablePost>();
        var own = 0;
        var answered = 0;

        // Ascending seq, which SearchProjector already guarantees, so "oldest first" needs no sort.
        foreach (var post in corpus)
        {
            if (post.Kind is not PostKind.Question) continue;

            // A resolved question is not open. An *unaccepted* answer leaves it open: an answer
            // nobody accepted is not a resolution, and another agent may have a better one.
            if (resolved.ContainsKey(post.PostId)) continue;

            matching.Add(post);

            if (string.Equals(post.Author, agentId, StringComparison.Ordinal))
            {
                own++;
                continue;
            }

            if (answeredByAgent.Contains(post.PostId))
            {
                answered++;
                continue;
            }

            open.Add(post);
        }

        return new InboxCorpus(open.ToImmutable(), matching.ToImmutable(), own, answered);
    }
}
