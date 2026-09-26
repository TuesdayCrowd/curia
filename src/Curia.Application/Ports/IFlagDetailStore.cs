using Curia.Domain.Primitives;

namespace Curia.Application.Ports;

/// <summary>
/// The private half of a flag (R10.62, R11.32): the post it concerns, who raised it, why, and the
/// salt its log entry commits with.
///
/// <para><b>Never an event.</b> R6.46 and R6.47 make every event of the store a leaf, and R6.51
/// serves every leaf's input verbatim to anyone, so a fact written as an event is published. This
/// row is bound to its public <c>flag.committed</c> entry by the commitment that entry carries,
/// which is what makes a later substitution of any member here detectable.</para>
/// </summary>
/// <param name="EventId">The <c>flag.committed</c> event this row opens.</param>
/// <param name="PostId">The post the flag concerns. Public only once a moderation record adjudicates the flag (R10.60).</param>
/// <param name="RaisedBy">The authenticated principal that raised it. Never published.</param>
/// <param name="Rationale">R10.35's rationale, screened before it was stored. Never published.</param>
/// <param name="Salt">The commitment's salt, 32 random bytes in base64url.</param>
public sealed record FlagDetail(string EventId, string PostId, string RaisedBy, string Rationale, string Salt);

/// <summary>
/// The store of <see cref="FlagDetail"/> rows: append-only under R11.6's grant, in Postgres in
/// production and in memory for the application tests (R11.4).
/// </summary>
public interface IFlagDetailStore
{
    /// <summary>
    /// Records <paramref name="detail"/>. Refuses a second row for the same event
    /// (<c>curia/flag/detail-exists</c>) and any member the store cannot hold
    /// (<c>curia/flag/detail-unstorable</c>, <see cref="FlagDetailRules.Admit"/>), leaving the store
    /// unchanged either way.
    /// </summary>
    Task<Result<FlagDetail>> AppendAsync(FlagDetail detail, CancellationToken cancellationToken = default);

    /// <summary>Every row, ordered by <see cref="FlagDetail.EventId"/> ordinally. An empty store yields an empty list.</summary>
    Task<Result<IReadOnlyList<FlagDetail>>> ReadAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The one admission rule both adapters apply, and the refusals the port names. One rule, called by
/// both, because two copies of a rule are how two adapters come to disagree (R11.21, errata E11).
/// </summary>
public static class FlagDetailRules
{
    /// <summary>
    /// Refuses a row with an empty member, or with U+0000 anywhere — which a Postgres <c>text</c>
    /// column cannot hold, so an in-memory store that took it would be more permissive than the real
    /// one.
    /// </summary>
    public static Result<FlagDetail> Admit(FlagDetail detail)
    {
        ArgumentNullException.ThrowIfNull(detail);

        string?[] members = [detail.EventId, detail.PostId, detail.RaisedBy, detail.Rationale, detail.Salt];

        return members.Any(m => string.IsNullOrEmpty(m) || m.Contains('\0', StringComparison.Ordinal))
            ? Result<FlagDetail>.Fail(Unstorable())
            : Result<FlagDetail>.Ok(detail);
    }

    /// <summary>A second row for one event. Append-only: the first stands.</summary>
    public static Error Exists(string eventId) => new(
        "curia/flag/detail-exists",
        "A flag detail is already recorded for that event",
        $"event={eventId}");

    /// <summary>An empty member, or U+0000. The detail names no member value: a rationale is screened text, but it is still the raiser's.</summary>
    public static Error Unstorable() => new(
        "curia/flag/detail-unstorable",
        "A flag detail member is empty or contains U+0000, which the store cannot hold");

    /// <summary>The store failed for a reason of its own; the detail carries its state code and nothing else.</summary>
    public static Error Unavailable(string detail) => new(
        "curia/flag/detail-store-unavailable",
        "The flag detail store could not complete the request",
        detail);
}
