using System.Collections.Immutable;
using Curia.Application.Moderation;

namespace Curia.Application.Credentials;

/// <summary>
/// R4.33 (errata G15): the prefixes under which the Forum's own writers mint aggregate identifiers,
/// which an agent's identifier may not begin with. An agent's identifier is also the aggregate its
/// credential events are appended under, and while R4.5's form is enforced nowhere (plan D4) it can
/// be any text. So an enrollment under <c>log:keys</c> appended an agent's first event where the
/// operator's tool appends the Acta's first key, and no head could ever be signed again.
///
/// <para><b>Prefixes, not names.</b> <c>log:</c> reserves <c>log:heads</c> and <c>log:keys</c>,
/// and any stream the Acta adds later, such as epoch sealing's, without anyone having to remember to
/// add it here. <c>flag:</c> is the prefix <see cref="RaiseFlag"/> mints each flag's aggregate under.
/// A post's aggregate is a ULID, which no prefix can reserve; <see cref="EnrollIdentity"/>'s second
/// clause refuses an identifier whose aggregate already holds another writer's events.</para>
///
/// <para><b>Ordinal and case-sensitive,</b> because aggregate identity is: nothing in the event
/// store folds case, so <c>LOG:keys</c> names a different aggregate and collides with nothing.</para>
/// </summary>
public static class ReservedIdentifiers
{
    /// <summary>The Acta's streams (<c>LogEntries.HeadsAggregate</c>, <c>LogEntries.KeysAggregate</c>), and any it adds.</summary>
    public const string LogPrefix = "log:";

    /// <summary>Every prefix R4.33 refuses an identifier for beginning with.</summary>
    public static readonly ImmutableArray<string> ReservedPrefixes = [LogPrefix, RaiseFlag.FlagAggregatePrefix];

    /// <summary>Whether <paramref name="identifier"/> begins with a prefix the Forum's own writers mint aggregates under.</summary>
    public static bool IsReserved(string identifier)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        return ReservedPrefixes.Any(prefix => identifier.StartsWith(prefix, StringComparison.Ordinal));
    }
}
