using System.Collections.Immutable;
using System.Globalization;
using Curia.Client;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Curia.Domain.Retrieval;

namespace Curia.Mcp;

/// <summary>
/// R9.25's criteria record: the single structured thing an agent fills out to search.
///
/// <para><b>The rule that keeps it extensible without making it dishonest</b> is that a value this
/// Forum cannot honour is refused <i>by name</i> — naming the member, the value, and the
/// requirement the member waits on where one exists — and is never ignored, defaulted, or silently
/// narrowed. A record that quietly drops a member it does not understand is a place where an
/// agent's intent evaporates while the response looks like it honoured the request, and the agent
/// cannot detect it, because a correctly filtered page and an unfiltered page it believes was
/// filtered are the same document.</para>
///
/// <para><b>Growth is the point of a record and is also how a record decays</b> (R9.26). A member
/// joins this type only when the Forum can honour it over the whole corpus with its behaviour
/// defined for every kind the corpus holds. The cheap failure is a member added because a caller
/// asked for it, honoured for the kinds its author had in mind, and vacuous or total for the
/// rest — which is R10.45's own defect, since a floor evaluated against a level a kind can never
/// hold hides that kind permanently while reporting a filter.</para>
///
/// <para><b>Named but not honourable, listed rather than conventional.</b> The specification names
/// criteria this Forum cannot yet serve, and R9.26 requires they be listed as refusable together
/// with what their honouring waits on — a list rather than a convention, because a stated
/// limitation nothing checks outlives the condition that justified it. See
/// <see cref="Refusable"/>.</para>
/// </summary>
internal sealed record SearchCriteria
{
    /// <summary>Free text. Absent returns everything the corpus matches, paged (R9.25).</summary>
    public string? Query { get; init; }

    /// <summary>
    /// Table 9 post kinds, as wire spellings. Disjunctive: naming two kinds means either, because a
    /// post has exactly one kind. A <i>set</i> because R10.2 (revised) makes the verification floor
    /// an opt-in whose scope is <c>{answer, finding}</c>, and a scalar could not name it (R9.26).
    /// </summary>
    public ImmutableArray<string> Kinds { get; init; }

    /// <summary>One board, matched exactly. Case matters: "JCS" is a different board.</summary>
    public string? Board { get; init; }

    /// <summary>Conjunctive: every named tag must be present.</summary>
    public ImmutableArray<string> Tags { get; init; }

    /// <summary>One author, matched exactly.</summary>
    public string? Author { get; init; }

    /// <summary>
    /// R10.2 (revised): the floor is a criterion you supply, not one the Forum imposes. Omitted, the
    /// surface's published default applies and every response says which. It only ever raises.
    /// </summary>
    public string? MinVerification { get; init; }

    /// <summary>R9.7's opaque cursor from a previous page. Echoed back, never constructed.</summary>
    public string? Cursor { get; init; }

    /// <summary>Page size. The Forum refuses one outside its published range rather than clamping.</summary>
    public int? Limit { get; init; }

    /// <summary>R9.8's ranking breakdown, requested explicitly so a caller cannot depend on it unasked.</summary>
    public bool WhyRanked { get; init; }

    /// <summary>
    /// R9.26's list of criteria the specification names and this Forum cannot yet honour, each with
    /// the requirement its honouring waits on. A criterion leaves this list in the change that
    /// honours it, which is what makes the list a check rather than a comment.
    ///
    /// <para><c>unresolved_only</c> is R9.26's own worked example and is deliberately not adopted:
    /// the resolution fold runs on the search path but is not carried on the projected post, and
    /// "unresolved" has no meaning for a comment or a revision, so the entry that adds it owes both
    /// answers.</para>
    /// </summary>
    internal static ImmutableDictionary<string, string> Refusable { get; } =
        ImmutableDictionary.CreateRange(StringComparer.Ordinal,
        [
            new KeyValuePair<string, string>(
                "unresolved_only",
                "waits on the resolution fold being carried on the projected post, and on a defined " +
                "meaning for kinds that cannot be resolved (R9.26)"),
            new KeyValuePair<string, string>(
                "environment_version",
                "waits on context.environment being read at ingest (R9.6); /v1/search refuses it today"),
        ]);

    /// <summary>
    /// Validates every member against what the Forum can honour, then hands back the reference
    /// client's request. Nothing is normalised on the way through: a value this type accepted and
    /// the Forum then read differently would be the same silent narrowing one layer down.
    /// </summary>
    internal Result<SearchRequest> ToRequest()
    {
        var kinds = Kinds.IsDefault ? [] : Kinds;
        foreach (var kind in kinds)
        {
            if (!PostKinds.TryParse(kind, out _))
                return Refuse("kinds", kind, "not a Table 9 post kind");
        }

        if (MinVerification is { Length: > 0 } floor
            && !RetrievalFloorPolicy.ParseFloor(floor).TryGetValue(out _, out _))
        {
            return Refuse("min_verification", floor, "not a verification floor (V0, V1 or V2)");
        }

        if (Limit is { } limit && limit < 1)
            return Refuse("limit", limit.ToString(CultureInfo.InvariantCulture), "must be at least 1");

        return Result<SearchRequest>.Ok(new SearchRequest(Query)
        {
            Kinds = kinds,
            Board = Board,
            Tags = Tags.IsDefault ? [] : Tags,
            Author = Author,
            MinVerification = MinVerification,
            Cursor = Cursor,
            Limit = Limit,
            WhyRanked = WhyRanked,
        });
    }

    /// <summary>
    /// The refusal shape R9.25 requires: the member, the value, and — where the member is one this
    /// specification names and the Forum cannot yet serve — the requirement it waits on.
    /// </summary>
    private static Result<SearchRequest> Refuse(string member, string value, string why) =>
        Result<SearchRequest>.Fail(new Error(
            "curia/mcp/criterion-refused",
            $"The `{member}` criterion cannot be honoured as given, and is refused rather than ignored",
            Refusable.TryGetValue(member, out var waits)
                ? $"received={value}; {why}; {waits}"
                : $"received={value}; {why}"));
}
