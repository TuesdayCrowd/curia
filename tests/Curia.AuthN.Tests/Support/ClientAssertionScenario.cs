using Curia.AuthN.Tests.InMemory;

namespace Curia.AuthN.Tests.Support;

/// <summary>The client-assertion analog of <see cref="AccessTokenScenario"/>: Appendix C.1's
/// shape (header <c>typ:"JWT"</c>, payload <c>iss</c>/<c>sub</c> both the agent id, <c>aud</c> the
/// token endpoint), with a fully-valid baseline plus one-field mutation helpers.</summary>
internal sealed class ClientAssertionScenario
{
    public const string TokenEndpoint = "https://auth.curia.example/oauth2/token";
    public const string AgentId = "agent://curia.example/tuesdaycrowd/scriptor";

    public ManualTimeProvider Clock { get; }
    public TestKeyPair AgentKey { get; }
    public InMemoryReplayCache ReplayCache { get; }
    public ClientAssertionValidationContext Context { get; }
    public DateTimeOffset Iat { get; }
    public DateTimeOffset Exp { get; }

    public ClientAssertionScenario()
    {
        Clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero));
        AgentKey = TestKeys.Ed25519("agent-key-2026-08");
        ReplayCache = new InMemoryReplayCache();
        Iat = Clock.GetUtcNow();
        Exp = Iat + TimeSpan.FromSeconds(60);

        Context = new ClientAssertionValidationContext(
            TokenEndpoint: TokenEndpoint,
            ExpectedSubject: AgentId,
            AgentKeyResolver: new InMemoryJwsKeyResolver(AgentKey.Kid, AgentKey.PublicKey),
            ReplayCache: ReplayCache,
            VerifiersByAlg: TestKeys.Verifiers(),
            Clock: Clock);
    }

    public Dictionary<string, object> ValidHeader() => new()
    {
        ["alg"] = AgentKey.Alg,
        ["kid"] = AgentKey.Kid,
        ["typ"] = "JWT",
    };

    public Dictionary<string, object?> ValidPayload(string? jti = null) => new()
    {
        ["iss"] = AgentId,
        ["sub"] = AgentId,
        ["aud"] = TokenEndpoint,
        ["jti"] = jti ?? Guid.NewGuid().ToString("N"),
        ["iat"] = TestJwt.ToUnixSeconds(Iat),
        ["exp"] = TestJwt.ToUnixSeconds(Exp),
    };

    public string SignValid(
        Dictionary<string, object>? header = null,
        Dictionary<string, object?>? payload = null,
        TestKeyPair? key = null) =>
        TestJwt.Sign(header ?? ValidHeader(), payload ?? ValidPayload(), key ?? AgentKey);
}
