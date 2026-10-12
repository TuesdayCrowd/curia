using System.Diagnostics.CodeAnalysis;
using Curia.AuthN.Dpop;
using Curia.AuthN.Ports;
using Curia.AuthN.Tests.InMemory;
using Curia.AuthN.Tests.Support;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;
using Xunit;

namespace Curia.AuthN.Tests;

/// <summary>
/// R5.14: a DPoP proof's <c>jti</c> is held for at least as long as the proof could be accepted. The
/// skew test accepts a proof while <c>|iat - now| &lt;= MaxSkew</c>, so until <c>iat + MaxSkew</c>; a
/// proof dated ahead of the instant it was consumed, held only until that instant plus
/// <c>MaxSkew</c>, would be accepted again once its <c>jti</c> lapsed (errata G21, row §5.5, §5.6).
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class DpopProofRetentionTests
{
    /// <summary>
    /// <see cref="DpopProofValidator.ConsumeAsync"/>, consumed at <c>now</c>, holds a proof dated 20 s
    /// ahead until beyond its <c>iat + MaxSkew</c>: at least that long, and strictly longer, because a
    /// replay cache may readmit a <c>jti</c> at the instant it expires.
    /// </summary>
    [Fact]
    public async Task R5_14_ConsumeHoldsAProofUntilItsIatPlusSkew()
    {
        var scenario = new AccessTokenScenario();
        var recording = new RecordingReplayCache();
        var now = scenario.Clock.GetUtcNow();
        var token = scenario.SignAccessToken();
        var iat = now + TimeSpan.FromSeconds(20);
        var p = scenario.ValidDpopPayload(token);
        p["iat"] = TestJwt.ToUnixSeconds(iat);
        var proof = scenario.SignDpopProof(token, payload: p);
        Assert.True(DpopProofValidator.Validate(
                proof,
                new DpopExpectation(AccessTokenScenario.HttpMethod, AccessTokenScenario.CanonicalUrl, token, null),
                scenario.Context.VerifiersByAlg,
                now)
            .TryGetValue(out var validated, out var error), error?.Detail);
        Assert.Equal(iat, validated!.Claims.Iat);

        var consumed = await DpopProofValidator.ConsumeAsync(validated, recording, now, TestContext.Current.CancellationToken);

        Assert.True(consumed.TryGetValue(out _, out var consumeError), consumeError?.Detail);
        var inserts = recording.Inserts.Where(i => i.Jti == validated.Claims.Jti.Value).ToList();
        Assert.Single(inserts);
        Assert.True(
            inserts[0].ExpiresAt >= iat + AuthNConstants.MaxSkew,
            $"the proof's jti is held until {inserts[0].ExpiresAt:O}, before iat + MaxSkew ({iat + AuthNConstants.MaxSkew:O})");
        Assert.True(
            inserts[0].ExpiresAt > iat + AuthNConstants.MaxSkew,
            $"the proof's jti is held until {inserts[0].ExpiresAt:O}, not beyond iat + MaxSkew ({iat + AuthNConstants.MaxSkew:O})");
    }

    /// <summary>
    /// The resource path: a proof dated 20 s ahead is accepted, and the expiry recorded for its
    /// <c>jti</c> is strictly beyond <c>iat + MaxSkew</c>. Asserted by the proof's <c>jti</c>, never by
    /// the only or the last insert.
    /// </summary>
    [Fact]
    public async Task R5_14_TheResourcePathHoldsAProofUntilItsIatPlusSkew()
    {
        var scenario = new AccessTokenScenario();
        var recording = new RecordingReplayCache();
        var token = scenario.SignAccessToken();
        var iat = scenario.Iat + TimeSpan.FromSeconds(20);
        var p = scenario.ValidDpopPayload(token);
        p["iat"] = TestJwt.ToUnixSeconds(iat);
        var jti = (string)p["jti"]!;
        var proof = scenario.SignDpopProof(token, payload: p);

        var result = await AccessTokenValidator.ValidateRequestAsync(
            scenario.ValidRequest(token, proof), scenario.Context with { ReplayCache = recording }, TestContext.Current.CancellationToken);

        Assert.True(result.TryGetValue(out _, out var error), error?.Detail);
        var inserts = recording.Inserts.Where(i => i.Jti == jti).ToList();
        Assert.Single(inserts);
        Assert.True(
            inserts[0].ExpiresAt > iat + AuthNConstants.MaxSkew,
            $"the proof's jti is held until {inserts[0].ExpiresAt:O}, not beyond iat + MaxSkew ({iat + AuthNConstants.MaxSkew:O})");
    }

    /// <summary>Records every insert's <c>jti</c> and expiry, and delegates it to a real
    /// <see cref="InMemoryReplayCache"/>, returning its answer: a request is accepted because the
    /// insert succeeded, never because this said so.</summary>
    private sealed class RecordingReplayCache : IReplayCache
    {
        private readonly InMemoryReplayCache _inner = new();

        public List<(string Jti, DateTimeOffset ExpiresAt)> Inserts { get; } = [];

        public Task<Result<bool>> TryInsertAsync(Jti jti, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
        {
            Inserts.Add((jti.Value, expiresAt));
            return _inner.TryInsertAsync(jti, expiresAt, cancellationToken);
        }
    }
}
