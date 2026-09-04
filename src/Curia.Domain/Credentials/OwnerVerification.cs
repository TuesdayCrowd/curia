using System.Collections.Frozen;
using Curia.Domain.Primitives;

namespace Curia.Domain.Credentials;

/// <summary>
/// R4.1's owner identity, as the log names it. CS-8: a strongly typed wrapper rather than a bare
/// string, validated at construction.
///
/// <para>Opaque and non-empty, and nothing more -- the same posture <see cref="ActorId"/> takes.
/// No scheme is enforced because no identifier scheme is enforced anywhere yet (implementation
/// plan D4), and a rule for owners that agents did not have to keep would be a second half-rule.
/// What the type guarantees is that an owner is named, so an attestation cannot be recorded
/// against nobody.</para>
/// </summary>
public readonly record struct OwnerId
{
    public string Value { get; }

    private OwnerId(string value) => Value = value;

    public static Result<OwnerId> Create(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? Result<OwnerId>.Fail(DomainErrors.EmptyIdentifier(nameof(OwnerId)))
            : Result<OwnerId>.Ok(new OwnerId(value));
}

/// <summary>
/// R4.24's four proofs, one member each: "domain control proof (DNS TXT or <c>.well-known</c>
/// file), verified organizational email plus MFA, a signed GitHub/GitLab organization attestation,
/// or manual review."
///
/// <para>Four and not three. Appendix F.1's example policy tests
/// <c>owner.verification in ["domain", "org", "manual"]</c>, which collapses two proofs of
/// materially different strength under one name; errata G5 settles the vocabulary at one value per
/// R4.24 arm, because an append-only log cannot later un-collapse a distinction it never
/// recorded, and §3.7's "only as strong as its weakest proof" is unactionable otherwise.</para>
/// </summary>
public enum OwnerVerificationMethod
{
    /// <summary>Domain control: a DNS TXT record or a <c>.well-known</c> file.</summary>
    Domain,

    /// <summary>Verified organizational email plus MFA.</summary>
    Email,

    /// <summary>A signed GitHub/GitLab organization attestation.</summary>
    Attestation,

    /// <summary>Manual review by an operator -- the one arm the Forum can serve today (G5).</summary>
    Manual,
}

/// <summary>The spellings the log carries for <see cref="OwnerVerificationMethod"/>, and their parser.</summary>
public static class OwnerVerificationMethods
{
    public static string Wire(OwnerVerificationMethod method) => method switch
    {
        OwnerVerificationMethod.Domain => "domain",
        OwnerVerificationMethod.Email => "email",
        OwnerVerificationMethod.Attestation => "attestation",
        OwnerVerificationMethod.Manual => "manual",
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Not one of R4.24's four proofs"),
    };

    private static readonly FrozenDictionary<string, OwnerVerificationMethod> ByWire =
        new Dictionary<string, OwnerVerificationMethod>(StringComparer.Ordinal)
        {
            ["domain"] = OwnerVerificationMethod.Domain,
            ["email"] = OwnerVerificationMethod.Email,
            ["attestation"] = OwnerVerificationMethod.Attestation,
            ["manual"] = OwnerVerificationMethod.Manual,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// The proof a spelling names, or a failure. Never a default: a parser that fell back to
    /// <see cref="OwnerVerificationMethod.Manual"/> would record a proof nobody performed.
    /// </summary>
    public static Result<OwnerVerificationMethod> Parse(string wire) =>
        wire is not null && ByWire.TryGetValue(wire, out var method)
            ? Result<OwnerVerificationMethod>.Ok(method)
            : Result<OwnerVerificationMethod>.Fail(OwnerVerificationErrors.UnknownMethod(wire));
}

/// <summary>RFC 9457 problem-type slugs for owner verification.</summary>
public static class OwnerVerificationErrors
{
    public static Error UnknownMethod(string? wire) => new(
        "curia/attest/unknown-method",
        "Not one of R4.24's four proofs: domain, email, attestation, manual",
        wire);
}
