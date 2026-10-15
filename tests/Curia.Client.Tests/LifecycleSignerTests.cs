using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R4.39 and R11.20 for an external signer: a lifecycle statement is signed through an operation of its own. The
/// signer here is the real <c>test-signer.py</c> in another process, because the claim is about what crosses the
/// process boundary: a process that may call the signer to post cannot thereby obtain a signature that adds or
/// ends a key.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class LifecycleSignerTests
{
    private static readonly string[] CritB64 = ["b64"];

    /// <summary>A JWS signing input as <c>DetachedJws</c> builds one: the base64url header, a dot, and the raw payload.</summary>
    private static byte[] SigningInput(string typ) =>
        Encoding.UTF8.GetBytes(
            System.Buffers.Text.Base64Url.EncodeToString(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["alg"] = "ES256",
                    ["kid"] = "held-1",
                    ["typ"] = typ,
                    ["b64"] = false,
                    ["crit"] = CritB64,
                })))
            + ".{\"statement\":true}");

    private static ExternalSigner Described(TestSigner held)
    {
        Assert.True(ExternalSigner.Describe(held.Command).TryGetValue(out var signer, out var error), error?.Title);
        return signer!;
    }

    private static bool VerifiesUnder(TestSigner held, byte[] input, byte[] signature)
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(held.PublicKey, out _);
        return key.VerifyData(input, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    [Theory]
    [InlineData("curia-key-rotation+jws")]
    [InlineData("curia-key-retirement+jws")]
    [InlineData("curia-key-compromise+jws")]
    public void R11_20_TheReferenceSignerRefusesALifecycleTypOnSign(string typ)
    {
        using var held = TestSigner.Create("held-1");
        var signer = Described(held);

        var refused = Assert.Throws<CryptographicException>(() => signer.Sign(SigningInput(typ)));

        Assert.Contains("sign refuses a lifecycle statement", refused.Message, StringComparison.Ordinal);
        Assert.Equal(0, held.Signatures);
    }

    [Theory]
    [InlineData("curia-key-rotation+jws")]
    [InlineData("curia-key-retirement+jws")]
    public void R11_20_TheReferenceSignerSignsALifecycleStatementOnSignLifecycle(string typ)
    {
        using var held = TestSigner.Create("held-1");
        var signer = Described(held);
        var input = SigningInput(typ);

        var signature = signer.SignLifecycle(input);

        Assert.Equal(64, signature.Length);
        Assert.True(VerifiesUnder(held, input, signature));
        Assert.Equal(1, held.Signatures);
    }

    [Theory]
    [InlineData("curia-post+jws")]
    [InlineData("curia-head+jws")]
    [InlineData("dpop+jwt")]
    public void R11_20_TheReferenceSignerSignsNothingButALifecycleStatementOnSignLifecycle(string typ)
    {
        using var held = TestSigner.Create("held-1");
        var signer = Described(held);
        var input = SigningInput(typ);

        Assert.Throws<CryptographicException>(() => signer.SignLifecycle(input));
        Assert.Equal(0, held.Signatures);

        // And the verb a post is signed with still signs it.
        Assert.True(VerifiesUnder(held, input, signer.Sign(input)));
    }

    [Fact]
    public void R11_20_ARotationAsksTheCurrentSignerForItsLifecycleOperationAndNothingElse()
    {
        using var held = TestSigner.Create("held-1");
        var home = Directory.CreateTempSubdirectory("curia-keyact-sign-").FullName;
        try
        {
            var store = new ProfileStore(home);
            Assert.True(store.Create("x", KeyActRig.AgentId, KeyActDouble.Forum, Described(held)).TryGetValue(out var agent, out _));
            using (agent)
            using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            using (var next = new InProcessSigner(key, "x-next"))
            {
                Assert.True(KeyActs.BuildRotation(agent!, next, 1).TryGetValue(out var request, out var error), error?.Title);
                KeyActRig.AssertSigned(request!.Statement, request.Authorization, "curia-key-rotation+jws", "held-1", held.PublicKey);

                Assert.True(KeyActs.BuildRetirement(agent!.Signer, KeyActRig.AgentId, 2).TryGetValue(out var retirement, out _));
                KeyActRig.AssertSigned(retirement!.Statement, retirement.Authorization, "curia-key-retirement+jws", "held-1", held.PublicKey);
            }

            // Two signatures, both through sign-lifecycle: the signer's own log names the verb.
            Assert.Equal(["sign-lifecycle", "sign-lifecycle"], File.ReadAllLines(Path.Combine(held.Directory, "signed.log")));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void R11_20_ASignerThatDeclinesIsARefusalNotAForgery()
    {
        using var held = TestSigner.Create("held-1");
        var home = Directory.CreateTempSubdirectory("curia-keyact-decline-").FullName;
        try
        {
            var store = new ProfileStore(home);
            Assert.True(store.Create("x", KeyActRig.AgentId, KeyActDouble.Forum, Described(held)).TryGetValue(out var agent, out _));
            using (agent)
            {
                held.Refuse();

                Assert.False(KeyActs.BuildRetirement(agent!.Signer, KeyActRig.AgentId, 2).TryGetValue(out _, out var error));
                Assert.Equal("curia/client/key-statement-invalid", error!.Type);
                Assert.Contains("curia/jws/signer-refused", error.Detail, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }
}
