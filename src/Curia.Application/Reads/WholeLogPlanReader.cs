using System.Collections.Immutable;
using Curia.Application.Credentials;
using Curia.Application.Moderation;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Acta;

namespace Curia.Application.Reads;

/// <summary>
/// The reference semantics of a <see cref="ReadPlan"/> (errata G87, R11.113): the plan answered over the whole log, a row's index being its position in it (R6.47). It is the answer the Postgres
/// adapter's statements are held to by the tests, the in-memory adapter (R11.4), and the default of <see cref="IPositionedEventReader"/> for a store that has no better. It reads the whole log, so
/// no route may rely on it: the fence says so.
/// </summary>
public static class WholeLogPlanReader
{
    /// <summary>Answers <paramref name="plan"/> over <paramref name="events"/> read whole.</summary>
    public static async Task<LogSlice> RunAsync(IEventReader events, ReadPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);

        var read = await events.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        if (!read.TryGetValue(out var log, out var error))
            throw new LogUnreadableException(error!.Type) { Refusal = error };

        return Answer(log!, plan);
    }

    /// <summary>Answers <paramref name="plan"/> over <paramref name="log"/>, which is the whole log from its start.</summary>
    public static LogSlice Answer(IReadOnlyList<AppendedEvent> log, ReadPlan plan)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(plan);

        var chosen = new SortedDictionary<int, PositionedEvent>();
        var scope = new ScopeBuilder();
        foreach (var stage in plan.Stages)
        {
            var clauses = stage([.. chosen.Values.Select(r => r.Event)]);
            foreach (var clause in clauses)
            {
                scope.Note(clause);
                for (var i = 0; i < log.Count; i++)
                    if (Matches(clause, log[i]))
                        chosen[i] = new PositionedEvent(i, log[i]);
            }
        }

        return new LogSlice([.. chosen.Values], log.Count == 0 ? EventSequence.Zero : log[^1].Seq, scope.Build());
    }

    private static bool Matches(ReadClause clause, AppendedEvent e)
    {
        var type = e.Event.Type.Value;
        return clause.Match(
            ids => ids.Contains(e.AggregateId.Value, StringComparer.Ordinal),
            () => type is LogEntries.KeyType or LogEntries.DeclarationKeyType or LogEntries.OperatorKeyType,
            actor => type == PostProjector.PostAcceptedType && e.Event.Actor is { } a && a.Value == actor,
            actor => type == AcceptanceProjector.AnswerAcceptedType && e.Event.Actor is { } a && a.Value == actor,
            (member, values) => type == PostProjector.PostAcceptedType && PayloadValue(member, e) is { } v && values.Contains(v, StringComparer.Ordinal),
            ids => ids.Contains(e.Event.Id.Value, StringComparer.Ordinal),
            () => type == FlagProjector.FlagRaisedType,
            kid => type is AgentStandingProjector.EnrolledType or AgentStandingProjector.KeyBoundType or LifecycleAppendGuard.KeyRetiredType
                && e.Event.Payload is JsonValue.Object o
                && o.Members.Any(m => m.Key == AgentStandingProjector.KeyIdField && m.Value is JsonValue.String s && s.Value == kid));
    }

    /// <summary>What a payload clause compares: the post's <c>digest</c>, or the <c>target</c> of a vote's or verification's signed envelope (null for a post of another kind).</summary>
    private static string? PayloadValue(ReadClause.PayloadMember member, AppendedEvent e)
    {
        if (e.Event.Payload is not JsonValue.Object payload) return null;

        string? Text(JsonValue.Object o, string name) => o.Members.FirstOrDefault(m => m.Key == name).Value is JsonValue.String s ? s.Value : null;

        switch (member)
        {
            case ReadClause.PayloadMember.Digest:
                return Text(payload, "digest");
            case ReadClause.PayloadMember.SignalTarget:
                if (Text(payload, "kind") is not ("vote" or "verification")) return null;
                if (Text(payload, "canonical") is not { } canonical) return null;
                return JsonReader.ParseUnrestricted(System.Text.Encoding.UTF8.GetBytes(canonical)).TryGetValue(out var root, out _)
                    && root is JsonValue.Object o ? Text(o, "target") : null;
            default:
                throw new ArgumentOutOfRangeException(nameof(member), member, "Not a payload member");
        }
    }
}

/// <summary>Accumulates what the clauses of a plan name into the <see cref="ReadScope"/> its slice carries; both adapters use it, so the scope is one rule.</summary>
public sealed class ScopeBuilder
{
    private readonly HashSet<string> _aggregates = new(StringComparer.Ordinal);
    private readonly HashSet<string> _postActors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _acceptanceActors = new(StringComparer.Ordinal);
    private readonly HashSet<string> _digests = new(StringComparer.Ordinal);
    private readonly HashSet<string> _signalTargets = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flagEvents = new(StringComparer.Ordinal);
    private bool _keys;
    private bool _legacyFlags;

    /// <summary>Notes what <paramref name="clause"/> reads.</summary>
    public void Note(ReadClause clause)
    {
        ArgumentNullException.ThrowIfNull(clause);

        clause.Match(
            ids => { _aggregates.UnionWith(ids); return 0; },
            () => { _keys = true; return 0; },
            actor => { _postActors.Add(actor); return 0; },
            actor => { _acceptanceActors.Add(actor); return 0; },
            (member, values) =>
            {
                (member == ReadClause.PayloadMember.Digest ? _digests : _signalTargets).UnionWith(values);
                return 0;
            },
            ids => { _flagEvents.UnionWith(ids); return 0; },
            () => { _legacyFlags = true; return 0; },
            _ => 0);
    }

    /// <summary>The scope of what was noted.</summary>
    public ReadScope Build() => new(_aggregates, _keys, _postActors, _acceptanceActors, _digests, _signalTargets, _flagEvents, _legacyFlags);
}
