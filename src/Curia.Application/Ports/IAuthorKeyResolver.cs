using Curia.Canon.Jws;
using Curia.Domain.Primitives;

namespace Curia.Application.Ports;

/// <summary>
/// Resolves the key a post's signature must be checked against, <b>as of a named instant</b>.
///
/// <para><b>R6.31 (errata A12) is the whole reason the instant is a parameter.</b> "Key validity
/// SHALL be evaluated at <c>server_ts</c>" -- not at submission time and not at
/// <c>created_at</c>. A resolver without the instant can only answer "is this key valid now",
/// which silently becomes the wrong question the moment a post is re-verified during a replay, an
/// export, or an audit: a key retired last week would fail a signature it correctly made last
/// month.</para>
///
/// <para><b>Why this port exists when <c>Curia.AuthN.Ports.IAgentKeyResolver</c> has the same
/// shape.</b> Not an oversight and not duplication for its own sake: the architecture test
/// <c>CS7_ApplicationDoesNotDependOnInfrastructureOrHostProjects</c> confines
/// <c>Curia.Application</c> to Domain, Canon, Domain.Primitives and the BCL, and AuthN is none of
/// those. Two consumers that cannot see each other declare the capability they each need, and the
/// composition root satisfies both from one adapter over one key store. That is the ordinary
/// hexagonal answer, and it keeps the ingest path from acquiring a dependency on the
/// authentication module merely to look up a public key.</para>
///
/// <para><b>R4.16 rev. (errata A16) constrains every implementation:</b> the Registrar's key store
/// is authoritative and the Forum serves JWKS. An adapter behind this port SHALL NOT fetch an
/// agent-hosted JWKS at request time -- that was removed as an SSRF and availability surface.</para>
/// </summary>
public interface IAuthorKeyResolver
{
    /// <summary>
    /// The key registered <b>to <paramref name="agentId"/></b> under <paramref name="kid"/> and
    /// valid at <paramref name="at"/>, or a failure naming why not -- unknown, not that agent's,
    /// revoked, or not yet valid at that instant.
    ///
    /// <para><b>Why the agent is a parameter and not just the <c>kid</c>.</b> Resolving by
    /// <c>kid</c> alone is not exploitable on its own -- an attacker naming someone else's
    /// <c>kid</c> cannot produce a signature that verifies under that someone's public key -- so
    /// the cryptography already carries the weight. But it makes the *question* wrong: it asks
    /// "what key is this?" when §4 registers keys per agent and the thing that matters is "is this
    /// the author's key?". Scoping it here means a key that is not the author's is a resolution
    /// failure with its own reason, rather than a signature failure that looks identical to a
    /// corrupted body. Those are different incidents and an operator should be able to tell them
    /// apart.</para>
    /// </summary>
    Task<Result<PublicKeyMaterial>> ResolveAsync(
        string agentId,
        string kid,
        ServerTimestamp at,
        CancellationToken cancellationToken = default);
}
