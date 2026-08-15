using System.Collections.Immutable;
using Curia.Domain.Primitives;

namespace Curia.Domain;

/// <summary>
/// One agent's key history: every key it has ever published, each with its own
/// <see cref="KeyValidityWindow"/>. "History," not "current keys" -- R4.19 requires revoked
/// <c>kid</c>s retained indefinitely with their valid interval, because verifying a historical
/// signature requires knowing what was valid when it was made, so nothing here ever removes an
/// entry.
///
/// R4.17 ("at least two simultaneously valid keys, so rotation is overlap-then-retire") is a
/// capability this type makes natural rather than a count it polices: <see cref="Rotate"/> only
/// ever appends, so an old key's window is untouched by the act of adding a new one, and two
/// entries with overlapping open (or overlapping closed) windows are exactly as representable as
/// one. Nothing here requires at least two keys to be simultaneously valid at every instant --
/// only that having two never becomes an outage while an agent is retiring one.
///
/// Deliberately absent: any operation that fetches a key from anywhere. Errata A16/R4.16
/// (revised) makes the Registrar's own store -- of which this type is the domain model -- the
/// sole authority; every entry here arrives already verified, as the payload of an enrollment
/// (R4.11) or a rotation submission (R4.18) the caller has already checked was signed by a
/// currently valid key. A method here that reached out for key material, or a port shaped to let
/// an adapter do so, would reintroduce exactly the SSRF/availability surface A16 closes -- so
/// there is no such method, and no such port, not even one this type merely declines to call.
/// </summary>
public sealed record AgentKeySet
{
    public AggregateId AgentId { get; }
    public ImmutableArray<AgentKey> Keys { get; }

    private AgentKeySet(AggregateId agentId, ImmutableArray<AgentKey> keys)
    {
        AgentId = agentId;
        Keys = keys;
    }

    /// <summary>The seed key history at enrollment (R4.11), or a full history reconstructed from replay.</summary>
    public static Result<AgentKeySet> Create(AggregateId agentId, IReadOnlyList<AgentKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        if (keys.Count == 0)
            return Result<AgentKeySet>.Fail(KeyErrors.EmptyKeySet(agentId));

        var duplicate = FindDuplicateKid(keys);
        if (duplicate is { } dupKid)
            return Result<AgentKeySet>.Fail(KeyErrors.DuplicateKid(agentId, dupKid));

        return Result<AgentKeySet>.Ok(new AgentKeySet(agentId, [.. keys]));
    }

    /// <summary>
    /// R4.18: rotation is the agent submitting a new public key. This only appends -- it never
    /// touches any existing entry, which is precisely why a just-rotated-away-from key stays
    /// valid for whatever remains of its own window rather than being cut short by the rotation
    /// itself. (Closing a key's window early is a separate, explicit act: <see cref="Revoke"/>.)
    /// </summary>
    public Result<AgentKeySet> Rotate(AgentKey newKey)
    {
        ArgumentNullException.ThrowIfNull(newKey);

        return Keys.Any(k => k.Kid == newKey.Kid)
            ? Result<AgentKeySet>.Fail(KeyErrors.DuplicateKid(AgentId, newKey.Kid))
            : Result<AgentKeySet>.Ok(new AgentKeySet(AgentId, Keys.Add(newKey)));
    }

    /// <summary>
    /// Closes an open-ended key's window at <paramref name="at"/> -- a compromise revocation or a
    /// routine retirement (R4.19; which of the two it was is an actor/reason a caller records
    /// alongside the append-only event this produces, not a distinction this type makes). The
    /// entry is updated in place, never removed.
    /// </summary>
    public Result<AgentKeySet> Revoke(KeyId kid, ServerTimestamp at)
    {
        var index = -1;
        for (var i = 0; i < Keys.Length; i++)
        {
            if (Keys[i].Kid != kid) continue;
            index = i;
            break;
        }

        if (index < 0)
            return Result<AgentKeySet>.Fail(KeyErrors.KeyNotFound(AgentId, kid));

        var target = Keys[index];
        if (!target.Validity.IsOpenEnded)
            return Result<AgentKeySet>.Fail(KeyErrors.AlreadyClosed(AgentId, kid));

        var closedWindowResult = KeyValidityWindow.Create(target.Validity.ValidFrom, at);
        if (!closedWindowResult.TryGetValue(out var closedWindow, out var windowError))
            return Result<AgentKeySet>.Fail(windowError!);

        var updated = target with { Validity = closedWindow };
        return Result<AgentKeySet>.Ok(new AgentKeySet(AgentId, Keys.SetItem(index, updated)));
    }

    /// <summary>
    /// R6.31, in full: was <paramref name="kid"/> valid for this agent at <paramref name="at"/>?
    /// <paramref name="at"/> is a <see cref="ServerTimestamp"/>, never a bare
    /// <see cref="DateTimeOffset"/> -- see that type's remarks for why the distinction is
    /// enforced in the signature rather than left to caller discipline.
    /// </summary>
    public Result<AgentKey> ValidateAt(KeyId kid, ServerTimestamp at)
    {
        foreach (var key in Keys)
        {
            if (key.Kid != kid) continue;

            return key.IsValidAt(at)
                ? Result<AgentKey>.Ok(key)
                : Result<AgentKey>.Fail(KeyErrors.KeyNotValidAt(AgentId, key, at));
        }

        return Result<AgentKey>.Fail(KeyErrors.KeyNotFound(AgentId, kid));
    }

    /// <summary>Every key valid at <paramref name="at"/> -- R4.17's overlap made queryable directly.</summary>
    public IEnumerable<AgentKey> ValidKeysAt(ServerTimestamp at) => Keys.Where(k => k.IsValidAt(at));

    private static KeyId? FindDuplicateKid(IReadOnlyList<AgentKey> keys)
    {
        var seen = new HashSet<KeyId>();
        foreach (var key in keys)
        {
            if (!seen.Add(key.Kid))
                return key.Kid;
        }

        return null;
    }
}
