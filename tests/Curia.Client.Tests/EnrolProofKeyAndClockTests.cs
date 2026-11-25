using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using Curia.Canon.Jws;
using Curia.Canon.Sodium;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// Two facts of R11.107's client that the Task 6 falsification found no probe held (rows FEPP20e, FEPP20e2 and FEPP20d). Each expected value is from outside the client: the thumbprint
/// of the RFC 8037 Appendix A key as that RFC prints it, and a clock this test sets far from the system's.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class EnrolProofKeyAndClockTests
{
    // RFC 8037 Appendix A.1 (d), A.2 (x) and the thumbprint of A.3, as the RFC prints them.
    private const string Rfc8037D = "nWGxne_9WmC6hEr0kuwsxERJxWl7MmkZcDusAxyuf2A";
    private const string Rfc8037X = "11qYAYKxCrfVS_7TyWQHOg7hcvPapiMlrwIaaPcHURo";
    private const string Rfc8037Thumbprint = "kPrK_qmxVWaYVA9wwBF6Iuo3vVzz7TxHCTwXBygrS4k";

    private sealed class Ed25519AgentSigner(string kid) : IAgentSigner
    {
        private readonly byte[] _seed = Base64Url.DecodeFromChars(Rfc8037D);

        public string Alg => "EdDSA";

        public string Kid => kid;

        public ReadOnlyMemory<byte> PublicKey => Base64Url.DecodeFromChars(Rfc8037X);

        public byte[] Sign(ReadOnlySpan<byte> signingInput) => new Ed25519Adapter().Sign(signingInput, new SigningKey("EdDSA", kid, _seed));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Serves a nonce and records the enrolment request it is sent.</summary>
    private sealed class RecordingForum : HttpMessageHandler
    {
        internal JsonElement? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/v1/enroll/nonce")
                return ScriptedForum.Json(200, """{"nonce":"epp-nonce-1","expires_at":"2099-01-01T00:00:00.0000000+00:00"}""");

            Body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
            return ScriptedForum.Json(201,
                $$"""{"agent_id":"{{Body.Value.GetProperty("agent_id").GetString()}}","kid":"{{Body.Value.GetProperty("kid").GetString()}}","enrolled_at":"2026-01-01T00:00:00.0000000+00:00","owner_verified":false}""");
        }
    }

    private static JsonElement Claims(string proof) => JsonDocument.Parse(Base64Url.DecodeFromChars(proof.Split('.')[1])).RootElement;

    private static EnrolledAgent Ed25519Agent(string slug)
    {
        var profile = new AgentProfile(slug, "https://agents.example/" + slug, slug + "-ed25519", "EdDSA", new Uri("http://forum.test/"));
#pragma warning disable CA2000 // the agent owns the DPoP key and disposes it
        var dpop = ECDsa.Create(ECCurve.NamedCurves.nistP256);
#pragma warning restore CA2000
        return EnrolledAgent.WithSigner(profile, new Ed25519AgentSigner(profile.Kid), dpop);
    }

    /// <summary>
    /// A client that enrols an Ed25519 profile carries, as <c>jkt</c>, the RFC 7638 thumbprint of the key as R4.34 renders it. The expected value is the thumbprint RFC 8037 Appendix A prints for its
    /// key, so a thumbprint built with its members in another order, or over other bytes, is not the published one.
    /// </summary>
    [Fact]
    public async Task R11_107_AnEd25519ProfilesProofCarriesTheThumbprintRfc8037Prints()
    {
        using var agent = Ed25519Agent("epp-ed");
        using var forum = new RecordingForum();
        using var http = new HttpClient(forum, disposeHandler: false) { BaseAddress = new Uri("http://forum.test/") };
        var client = new ForumClient(http, new Uri("http://forum.test/"));

        var result = await client.EnrolAsync(agent, TestContext.Current.CancellationToken);

        Assert.True(result.TryGetValue(out _, out var refusal), $"the enrolment was refused: {refusal?.Error.Type}");
        var proof = forum.Body!.Value.GetProperty("proof").GetString()!;
        Assert.Equal(Rfc8037Thumbprint, Claims(proof).GetProperty("jkt").GetString());
    }

    /// <summary>The same enrolment, held to a Forum that verifies the proof (the test project's verifier, from the errata's sentence): an Ed25519 proof signed by the in-process signer is accepted.</summary>
    [Fact]
    public async Task R11_107_AnEd25519ProfilesProofIsOneAVerifyingForumAccepts()
    {
        using var agent = Ed25519Agent("epp-ed-v");
        using var forum = new RecordingForum();
        using var http = new HttpClient(forum, disposeHandler: false) { BaseAddress = new Uri("http://forum.test/") };
        var client = new ForumClient(http, new Uri("http://forum.test/"));

        Assert.True((await client.EnrolAsync(agent, TestContext.Current.CancellationToken)).TryGetValue(out _, out _));

        var verifier = new EnrollProofVerifier("http://forum.test/v1/agents", () => DateTimeOffset.UtcNow, n => n == "epp-nonce-1");
        var slug = verifier.Verify(agent.Profile.AgentId, "EdDSA", agent.Profile.Kid, agent.Signer.PublicKey.ToArray(), forum.Body!.Value.GetProperty("proof").GetString());
        Assert.Null(slug);
    }

    /// <summary>
    /// EnrolAsync dates the proof from the clock it is given, not the system's: the clock is set to 2001-09-09, far from now, and the proof's <c>iat</c> is that instant. A client that read the system clock
    /// would pass every test that advances a fixture clock to the real time; this one does not.
    /// </summary>
    [Fact]
    public async Task R11_107_TheProofsIatIsTheClockTheClientWasGiven()
    {
        const long Fixed = 1_000_000_000;
        using var agent = Ed25519Agent("epp-clock");
        using var forum = new RecordingForum();
        using var http = new HttpClient(forum, disposeHandler: false) { BaseAddress = new Uri("http://forum.test/") };
        var client = new ForumClient(http, new Uri("http://forum.test/"));

        Assert.True((await client.EnrolAsync(agent, TestContext.Current.CancellationToken, new FixedClock(DateTimeOffset.FromUnixTimeSeconds(Fixed)))).TryGetValue(out _, out _));

        var iat = Claims(forum.Body!.Value.GetProperty("proof").GetString()!).GetProperty("iat").GetInt64();
        Assert.Equal(Fixed, iat);
    }
}
