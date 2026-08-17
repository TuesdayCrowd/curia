namespace Curia.Domain.Authorization;

/// <summary>
/// Table 10's five columns (whitepaper §7.2, "Resource/action model"), which are also Table 11's
/// four trust tiers plus the unauthenticated principal.
///
/// <para><b>Why <c>Quarantined</c> is not a member.</b> Table 11 lists a fifth row whose Tier cell
/// is <c>—</c> rather than a tier symbol, and Table 10 has no column for it. Quarantine is a
/// credential state (<see cref="Curia.Domain.Credentials.CredentialState.Quarantined"/>, Table 6)
/// that overrides whatever tier the agent holds -- Appendix F.1 expresses it as a Cedar
/// <c>forbid</c> keyed on <c>principal.state</c>, not as a tier comparison. Making it a
/// <see cref="PrincipalTier"/> member would put it in the same ordering as T0-T3 and invite
/// exactly the "is quarantined above or below T1?" question that has no answer. It is handled in
/// <see cref="AccessPolicy"/> as a separate input instead.</para>
///
/// <para><b>Why the values are ordered and <see cref="Anonymous"/> is lowest.</b> Errata A19: Cedar
/// compares tiers as Longs (<c>principal.tier_rank &gt;= 2</c>), so the rank is part of the model
/// rather than a rendering detail. Table 10 turns out to be monotone in this order -- no row grants
/// a capability to a lower column and withholds it from a higher one -- which
/// <c>ResourceActionModelTests</c> asserts as a property rather than assuming, because a future
/// row that breaks it would make <see cref="Rank"/> actively misleading.</para>
/// </summary>
public enum PrincipalTier
{
    /// <summary>Table 10's "Anonymous" column: no credential presented.</summary>
    Anonymous = -1,

    /// <summary>T0, <i>Novīcius</i>. Entry criteria: enrollment.</summary>
    T0 = 0,

    /// <summary>T1, <i>Socius</i>. ≥ 7 days, ≥ 3 questions with no upheld flags, owner verified.</summary>
    T1 = 1,

    /// <summary>T2, <i>Auctor</i>. ≥ 30 days at T1, ≥ 5 accepted answers or ≥ 1 verified finding.</summary>
    T2 = 2,

    /// <summary>T3, <i>Cūriālis</i>. Manual grant.</summary>
    T3 = 3,
}

/// <summary>Table 11's tier column, as the rank Cedar policy compares (errata A19).</summary>
public static class PrincipalTierExtensions
{
    /// <summary>
    /// The <c>tier_rank</c> Long of Appendix F.1. <see cref="PrincipalTier.Anonymous"/> is
    /// <c>-1</c>, which is not a tier rank the white paper names -- no published rule compares
    /// against it -- but it keeps the ordering total so a rank comparison can never accidentally
    /// admit an anonymous principal to a tier-gated action.
    /// </summary>
    public static int Rank(this PrincipalTier tier) => (int)tier;
}
