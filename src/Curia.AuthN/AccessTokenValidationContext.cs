using Curia.AuthN.Ports;
using Curia.Canon.Jws;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;

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
    IAgentKeyLiveness KeyLiveness,
    IDpopNonceStore? DpopNonceStore = null);

/// <summary>
/// The corresponding trust material for <see cref="ClientAssertionValidator"/>, kept as a
/// distinct type rather than reusing <see cref="AccessTokenValidationContext"/>: the key
/// resolver here answers for agents' own registered keys, not a single issuer-wide namespace,
/// and -- unlike <see cref="Ports.IJwsKeyResolver"/> -- must be asked "whose" and "as of when"
/// (R5.20; errata A12/R6.31), so it is a different port, <see cref="Ports.IAgentKeyResolver"/>;
/// see that interface's remarks. There is also no resource-server audience, DPoP context, or
/// DPoP nonce store at this artifact type at all.
///
/// Nothing scopes <see cref="AgentKeyResolver"/> before
/// <see cref="ClientAssertionValidator.ValidateAsync"/> runs. <see cref="ExpectedSubject"/> is the
/// agent the request names as its client (the token request's <c>client_id</c>); it is the agent
/// the key is resolved <em>for</em>, by that agent and the assertion's <c>kid</c> together, and the
/// agent the verified <c>sub</c> claim must equal (R5.20, AuthNErrors.SubjectMismatch). Each half
/// has a test of its own. The key set consulted is the one the Forum holds, never one fetched from
/// a URL taken from the token (errata A16/R4.16 rev.). This remark once said the caller scoped the
/// resolver to one agent's keys; the token endpoint passed the Registrar's store unscoped, and a
/// key enrolled under its holder's own identifier authenticated every enrolled identity (errata G15).
///
/// <see cref="ExpectedSubject"/> is the client_id form value as the host parsed it, an AgentId or the
/// refusal. R11.34 (errata G18): the host parses it where it reads the request, and
/// ClientAssertionValidator answers a refusal at the resolve step, where the key store first used the
/// value, so a client_id that is no AgentId never changes which of the assertion's own checks answers
/// first (spec section 4.7). It is a parse outcome and not a string, so the port fence needs no row for
/// it.
/// </summary>
public sealed record ClientAssertionValidationContext(
    string TokenEndpoint,
    Result<AgentId> ExpectedSubject,
    IAgentKeyResolver AgentKeyResolver,
    IReplayCache ReplayCache,
    IReadOnlyDictionary<string, IContentVerifier> VerifiersByAlg,
    TimeProvider Clock);
