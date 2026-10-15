using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using Curia.Canon.Envelope;
using Curia.Canon.Jws;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// What the client library builds, sends and keeps for a rotation or a retirement (errata G21, spec sections 4.5, 4.7
/// and 4.12): the statements and their signatures, the paths and the proof targets of the two routes, and the
/// profile that records the acts.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class KeyActLibraryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Text(string path) => File.ReadAllText(path);

    [Fact]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The rig and the signer are disposed by the using declarations.")]
    public void R4_18_ARotationIsSignedByEachRoleUnderTheRotationHeader()
    {
        using var rig = KeyActRig.Create();
        using var next = new InProcessSigner(ECDsa.Create(ECCurve.NamedCurves.nistP256), "a-next");

        Assert.True(KeyActs.BuildRotation(rig.Agent, next, 4).TryGetValue(out var request, out var error), error?.Title);

        var statement = JsonDocument.Parse(request!.Statement).RootElement;
        Assert.Equal("curia.key-rotation/1", statement.GetProperty("typ").GetString());
        Assert.Equal("a-0000abcd", statement.GetProperty("authorized_by").GetString());
        Assert.Equal("a-next", statement.GetProperty("kid").GetString());
        Assert.Equal(4, statement.GetProperty("prev").GetInt64());
        Assert.Equal("a-next", statement.GetProperty("jwk").GetProperty("kid").GetString());

        var oldPublic = KeyActRig.PublicOf(Text(Path.Combine(rig.Directory, "signing-key.pem")));
        KeyActRig.AssertSigned(request.Statement, request.Authorization, "curia-key-rotation+jws", "a-0000abcd", oldPublic);
        KeyActRig.AssertSigned(request.Statement, request.Possession, "curia-key-rotation+jws", "a-next", next.PublicKey);
    }

    [Fact]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The rig is disposed by the using declaration.")]
    public void R4_18_ARotationSignatureIsNoPostSignatureAndNoRetirementSignature()
    {
        using var rig = KeyActRig.Create();
        using var next = new InProcessSigner(ECDsa.Create(ECCurve.NamedCurves.nistP256), "a-next");
        Assert.True(KeyActs.BuildRotation(rig.Agent, next, 4).TryGetValue(out var request, out _));

        var oldPublic = new PublicKeyMaterial("ES256", "a-0000abcd", KeyActRig.PublicOf(Text(Path.Combine(rig.Directory, "signing-key.pem"))));
        Assert.True(JsonReader_Canonical(request!.Statement, out var canonical));

        foreach (var wrong in new[] { DetachedJws.ExpectedTyp, DetachedJws.HeadTyp, "curia-key-retirement+jws" })
        {
            var verifier = new DetachedJws(
                new Dictionary<string, IContentSigner>(),
                new Dictionary<string, IContentVerifier> { ["ES256"] = new Curia.Canon.Sodium.Es256Adapter() },
                wrong);
            Assert.False(verifier.Verify(canonical, new JwsSignature(request.Authorization), oldPublic).TryGetValue(out _, out _), wrong);
        }
    }

    private static bool JsonReader_Canonical(string statement, out Curia.Canon.Canonical.CanonicalBytes canonical)
    {
        canonical = default!;
        return Curia.Canon.Json.JsonReader.Parse(System.Text.Encoding.UTF8.GetBytes(statement), Curia.Canon.Json.AdmitLimits.Default).TryGetValue(out var parsed, out _)
            && Curia.Canon.Canonical.CanonicalJson.CanonicalizeWithNfc(parsed!).TryGetValue(out canonical!, out _);
    }

    [Fact]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The signer is disposed by the using declaration.")]
    public void R4_39_ARetirementIsSignedByTheKeyItEndsAlone()
    {
        using var retiring = new InProcessSigner(ECDsa.Create(ECCurve.NamedCurves.nistP256), "a-old");

        Assert.True(KeyActs.BuildRetirement(retiring, KeyActRig.AgentId, 9).TryGetValue(out var request, out var error), error?.Title);

        var statement = JsonDocument.Parse(request!.Statement).RootElement;
        Assert.Equal("curia.key-retirement/1", statement.GetProperty("typ").GetString());
        Assert.Equal("a-old", statement.GetProperty("kid").GetString());
        Assert.Equal(KeyActRig.AgentId, statement.GetProperty("agent_id").GetString());
        Assert.Equal(9, statement.GetProperty("prev").GetInt64());
        Assert.Equal(4, statement.EnumerateObject().Count());

        KeyActRig.AssertSigned(request.Statement, request.Authorization, "curia-key-retirement+jws", "a-old", retiring.PublicKey);
    }

    [Theory]
    [InlineData("a", "/v1/agents/a/keys")]
    [InlineData("urn:curia:agent:a", "/v1/agents/urn:curia:agent:a/keys")]
    [InlineData("https://agents.example/a", "/v1/agents/https:%2F%2Fagents.example%2Fa/keys")]
    [InlineData("https://agents.example/%2F", "/v1/agents/https:%2F%2Fagents.example%2F%252F/keys")]
    public void R4_18_AnIdentifierIsOneEscapedSegment(string agentId, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        Assert.Equal(path, KeyActUrls.RotatePath(agentId));
        Assert.StartsWith(path + "/", KeyActUrls.RetirePath(agentId, "k"), StringComparison.Ordinal);
        Assert.EndsWith("/k/retire", KeyActUrls.RetirePath(agentId, "k"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://localhost/v1/agents/a/keys", "http://localhost/v1/agents/a/keys")]
    [InlineData("http://localhost/v1/agents/https:%2F%2Fa.example%2Fx/keys", "http://localhost/v1/agents/https:%2F%2Fa.example%2Fx/keys")]
    [InlineData("http://localhost/v1/agents/https:%2F%2Fa.example%2F%252F/keys", "http://localhost/v1/agents/https:%2F%2Fa.example%2F%2F/keys")]
    [InlineData("http://localhost/v1/agents/a%20b/keys", "http://localhost/v1/agents/a%20b/keys")]
    [InlineData("http://localhost/v1/agents/%C3%A9/keys", "http://localhost/v1/agents/%C3%A9/keys")]
    [InlineData("http://localhost/v1/agents/a%25b/keys", "http://localhost/v1/agents/a%25b/keys")]
    [InlineData("http://localhost/v1/agents/a%7Cb/keys", "http://localhost/v1/agents/a%7Cb/keys")]
    [InlineData("http://localhost/v1/agents/a%3Ab/keys", "http://localhost/v1/agents/a:b/keys")]
    public void R4_18_TheProofNamesThePathTheHostReads(string sent, string proofTarget)
    {
        Assert.Equal(proofTarget, KeyActUrls.ProofTarget(new Uri(sent)));
    }

    [Fact]
    public void R4_18_TheEscapedAndTheDoubleEscapedSlashAreTwoSentUrlsAndOneProofTarget()
    {
        var slash = KeyActUrls.RotatePath("https://a.example/x");
        var literal = KeyActUrls.RotatePath("https://a.example/%2Fx");

        Assert.NotEqual(slash, literal);
        Assert.Contains("%252F", literal, StringComparison.Ordinal);
        Assert.DoesNotContain("%252F", slash, StringComparison.Ordinal);
    }

    // ---- the profile (spec section 4.12) ------------------------------------------------------------

    [Fact]
    public void R4_39_AProfileKeepsItsRetainedKeysAndItsActsAcrossAWriteAndARead()
    {
        using var rig = KeyActRig.Create();
        var written = rig.Profile() with
        {
            RetainedKeys =
            [
                new RetainedKey("a-old", "ES256", "keys/" + Convert.ToHexStringLower(SHA256.HashData("a-old"u8)) + ".pem"),
                new RetainedKey("a-held", "ES256", Signer: "/usr/local/bin/held", Retired: true),
            ],
            LifecycleActs = [new LifecycleAct("rotation", "a-new", 3), new LifecycleAct("retirement", "a-held", 5)],
        };

        rig.Store.RecordEnrollment(written, "2026-01-01T00:00:00Z");

        var read = rig.Profile();
        Assert.Equal(written.Retained, read.Retained);
        Assert.Equal(written.Acts, read.Acts);
        Assert.True(File.Exists(Path.Combine(rig.Directory, "identity.json")));
    }

    [Fact]
    public void R4_39_AProfileThatNamesAKeyFileOfItsOwnIsMalformed()
    {
        using var rig = KeyActRig.Create();
        var written = rig.Profile() with { RetainedKeys = [new RetainedKey("a-old", "ES256", "../../elsewhere.pem")] };
        rig.Store.RecordEnrollment(written, "2026-01-01T00:00:00Z");

        Assert.False(rig.Store.ReadProfile(rig.Slug).TryGetValue(out _, out var error));
        Assert.Equal("curia/client/malformed-profile", error!.Type);
    }

    [Fact]
    public void R4_39_AProfileIsReadWithoutOpeningAKey()
    {
        using var rig = KeyActRig.Create();
        File.Delete(Path.Combine(rig.Directory, "signing-key.pem"));

        Assert.True(rig.Store.ReadProfile(rig.Slug).TryGetValue(out var profile, out _));
        Assert.Equal("a-0000abcd", profile!.Kid);
        Assert.False(rig.Store.Load(rig.Slug).TryGetValue(out _, out _));
    }

    [Fact]
    public void R4_39_ARetainedKeyThatIsNotOnItsFileCannotSignARetirement()
    {
        using var rig = KeyActRig.Create();
        var profile = rig.Profile() with { RetainedKeys = [new RetainedKey("a-old", "ES256", "keys/" + Convert.ToHexStringLower(SHA256.HashData("a-old"u8)) + ".pem")] };

        Assert.False(rig.Store.LoadRetained(profile, "a-old").TryGetValue(out _, out var error));
        Assert.Equal("curia/client/key-unreadable", error!.Type);

        Assert.False(rig.Store.LoadRetained(profile, "stranger").TryGetValue(out _, out var absent));
        Assert.Equal("curia/client/no-retained-key", absent!.Type);
    }

    [Fact]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The signers and the rig are disposed by the using declarations.")]
    public void R11_20_ARotationOntoAnExternalSignerLeavesNoSigningKeyAndRecordsTheCommand()
    {
        using var rig = KeyActRig.Create();
        using var held = Curia.Tests.Shared.TestSigner.Create("held-1");
        Assert.True(ExternalSigner.Describe(held.Command).TryGetValue(out var signer, out _));

        rig.Store.CompleteRotation(rig.Profile(), signer!, null, new KeyActReceipt(KeyActRig.AgentId, "held-1", 3, "a-0000abcd"));

        var profile = rig.Profile();
        Assert.Equal("held-1", profile.Kid);
        Assert.Equal(held.Command, profile.Signer);
        Assert.False(File.Exists(Path.Combine(rig.Directory, "signing-key.pem")));
        Assert.True(File.Exists(rig.Store.RetainedKeyFile("a", "a-0000abcd")));

        // Loaded as an identity whose key another process holds, with the old key still a retained one.
        Assert.True(rig.Store.Load(rig.Slug).TryGetValue(out var loaded, out var error), error?.Title);
        using (loaded) Assert.IsType<ExternalSigner>(loaded!.Signer);
        Assert.Equal("a-0000abcd", Assert.Single(profile.Retained).Kid);
    }

    [Fact]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The signers and the rig are disposed by the using declarations.")]
    public void R11_20_ARotationAwayFromAnExternalSignerRetainsItByItsCommand()
    {
        using var held = Curia.Tests.Shared.TestSigner.Create("held-1");
        Assert.True(ExternalSigner.Describe(held.Command).TryGetValue(out var signer, out _));
        var home = Directory.CreateTempSubdirectory("curia-keyact-ext-").FullName;
        try
        {
            var store = new ProfileStore(home);
            Assert.True(store.Create("x", KeyActRig.AgentId, KeyActDouble.Forum, signer!).TryGetValue(out var agent, out _));
            using (agent)
            {
                using var next = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                using var nextSigner = new InProcessSigner(next, "x-next");
                store.CompleteRotation(agent!.Profile, nextSigner, next, new KeyActReceipt(KeyActRig.AgentId, "x-next", 3, "held-1"));
            }

            Assert.True(store.ReadProfile("x").TryGetValue(out var profile, out _));
            var retained = Assert.Single(profile!.Retained);
            Assert.Equal(("held-1", held.Command, null), (retained.Kid, retained.Signer, retained.File));
            Assert.Null(profile.Signer);
            Assert.True(File.Exists(Path.Combine(store.DirectoryFor("x"), "signing-key.pem")));
            Assert.False(File.Exists(store.RetainedKeyFile("x", "held-1")));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void R4_39_AnActIsRecordedBesideTheOnesAlreadyThere()
    {
        using var rig = KeyActRig.Create();
        var profile = rig.Profile();

        var recorded = ProfileStore.RecordAct(ProfileStore.RecordAct(profile, new LifecycleAct("rotation", "k1", 3)), new LifecycleAct("retirement", "k0", 5));

        Assert.Equal([new LifecycleAct("rotation", "k1", 3), new LifecycleAct("retirement", "k0", 5)], recorded.Acts);
        Assert.Empty(profile.Acts);
    }

    [Fact]
    public void R4_39_ComparingTheLifecycleNeedsNoKeyAndFindsEachDisagreement()
    {
        using var rig = KeyActRig.Create();
        var profile = rig.Profile() with { LifecycleActs = [new LifecycleAct("rotation", "k1", 3), new LifecycleAct("retirement", "k0", 9)] };
        var keySet = new KeySetDocument(
            [
                new ForumJwk("EC", "P-256", "ES256", "a-0000abcd", "x", "y", null, null, 1, "active"),
                new ForumJwk("EC", "P-256", "ES256", "k1", "x", "y", null, null, 3, "active"),
                new ForumJwk("EC", "P-256", "ES256", "k2", "x", "y", null, null, 6, "retired", 8),
            ],
            [0, 1, 3, 6, 8], [], ["rotation"]);

        var findings = LifecycleWatch.Compare(profile, keySet);

        Assert.Equal(
            [
                new LifecycleFinding(true, "rotation", 6),
                new LifecycleFinding(true, "retirement", 8),
                new LifecycleFinding(false, "retirement", 9),
            ],
            findings);
        Assert.Empty(LifecycleWatch.Compare(profile, keySet with { Lifecycle = null }));
    }
}
