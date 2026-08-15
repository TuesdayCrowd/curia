using Curia.AuthN.Ports;
using Curia.Canon.Jws;

namespace Curia.AuthN;

/// <summary>
/// The local, pre-configured trust material <see cref="AccessTokenValidator"/> needs beyond the
/// request itself -- everything R5.10 means by "configured issuer JWKS" and R5.14/R5.15's shared
/// replay cache, plus the crypto verifiers R11.2 puts behind a port and the clock CS-9 requires.
/// One instance is built once per resource server at startup (Gateway and Api each construct
/// their own from the same configuration, which is what makes them "the same module" per R5.13
/// rather than merely the same source file).
/// </summary>
public sealed record AccessTokenValidationContext(
    string ConfiguredIssuer,
    string ResourceServer,
    IJwsKeyResolver IssuerKeyResolver,
    IReplayCache ReplayCache,
    IReadOnlyDictionary<string, IContentVerifier> VerifiersByAlg,
    TimeProvider Clock,
    IDpopNonceStore? DpopNonceStore = null);

/// <summary>
/// The corresponding trust material for <see cref="ClientAssertionValidator"/>, kept as a
/// distinct type rather than reusing <see cref="AccessTokenValidationContext"/>: the key
/// resolver here is scoped to one asserting agent's own registered keys, not a single
/// issuer-wide namespace, and -- unlike <see cref="Ports.IJwsKeyResolver"/> -- must be asked "as
/// of when" (errata A12/R6.31), so it is a different port, <see cref="Ports.IAgentKeyResolver"/>;
/// see that interface's remarks. There is also no resource-server audience, DPoP context, or
/// DPoP nonce store at this artifact type at all.
///
/// <see cref="AgentKeyResolver"/> is scoped by the <em>caller</em>, before
/// <see cref="ClientAssertionValidator.ValidateAsync"/> ever runs: the caller reads the
/// assertion's (unverified) <c>sub</c> claim to decide which agent's Forum-served key set to
/// consult -- errata A16/R4.16 rev.'s "Forum serves JWKS," never a URL taken from the token --
/// then builds a resolver scoped to that one agent's keys. <see cref="ExpectedSubject"/> records
/// which agent that was, so Phase 3 can confirm the verified <c>sub</c> claim actually matches
/// (AuthNErrors.SubjectMismatch): without that check, a resolver bug that silently returned a
/// different agent's key material for a wrong or stale scoping decision would verify a signature
/// against the wrong agent's key and nothing downstream would catch it. See the Stage C report.
/// </summary>
public sealed record ClientAssertionValidationContext(
    string TokenEndpoint,
    string ExpectedSubject,
    IAgentKeyResolver AgentKeyResolver,
    IReplayCache ReplayCache,
    IReadOnlyDictionary<string, IContentVerifier> VerifiersByAlg,
    TimeProvider Clock);
