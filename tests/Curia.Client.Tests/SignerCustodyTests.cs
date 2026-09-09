using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Security.Cryptography;
using Curia.Canon.Jws;
using Curia.Client;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R11.20, asserted where it can actually fail: <c>EnrolledAgent</c> no longer offers a path to the
/// registered private key, and an external signer never receives one.
///
/// <para><b>Why the type and not the behaviour.</b> The plan asks for this "asserted structurally,
/// not by inspection", and the reason is concrete: <c>ExportECPrivateKey()</c> allocates a fresh
/// plaintext array per call and nothing in this tree zeroes it — <c>CryptographicOperations.ZeroMemory</c>
/// appears nowhere. A test that watched one signing call and found no key in it would pass while
/// copies persisted elsewhere. What can be checked is that no member returns private material, and
/// that is a property of the surface rather than of a run.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class SignerCustodyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("curia-custody-").FullName;

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The registered key is reachable only as a capability. <c>EnrolledAgent</c> exposed a public
    /// <c>ECDsa SigningKey</c> until this stage, which made R11.20 unstatable: every holder of an
    /// agent could export the private half in one call.
    /// </summary>
    [Fact]
    public void R11_20_EnrolledAgentExposesNoMemberYieldingTheRegisteredPrivateKey()
    {
        var offenders = typeof(EnrolledAgent)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(ReturnsAsymmetricKey)
            .Select(m => m.Name)
            .Where(n => !n.StartsWith("get_", StringComparison.Ordinal))
            .ToArray();

        // Exactly two, and each for a stated reason. ExportPublicKey carries the public half only.
        // DpopKey is a live private key and is deliberately NOT behind the port: R11.20 says "agent
        // private keys" without distinguishing, and this stage distinguishes them — theft of the
        // registered key buys authoring posts as this agent forever, theft of this one is bounded by
        // a 300-second token and the key is freely rotatable because nobody registered it.
        //
        // Pinned as a list so the split stays a decision. A third entry means some new member
        // returns a key, and the question "which key, and why is it reachable" gets asked here.
        Assert.Equal(["DpopKey", "ExportPublicKey"], offenders.Order(StringComparer.Ordinal));

        // And the registered key is not among them under any name -- the property the list is for.
        Assert.DoesNotContain("SigningKey", offenders);

        // Non-vacuity: the scan really did look at this type's members rather than an empty set.
        Assert.Contains(nameof(EnrolledAgent.Signer), typeof(EnrolledAgent).GetMembers().Select(m => m.Name));
    }

    /// <summary>And what it does hand over is public-only, demonstrated rather than asserted.</summary>
    [Fact]
    public void R11_20_TheExportedKeyCannotSign()
    {
        using var agent = Enrolled();
        using var exported = agent.ExportPublicKey();

        Assert.Throws<CryptographicException>(() => exported.SignData(
            "anything"u8, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        // Non-vacuity: the same bytes verify what the agent's own signer produced, so the key is
        // real and usable — it simply has no private half.
        var signature = agent.Signer.Sign("anything"u8);
        Assert.True(exported.VerifyData(
            "anything"u8, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    /// <summary>
    /// The signer port carries no private material, so an external adapter cannot be handed one by
    /// its own interface. This is the fifth of R14.9's MCP negative tests — "the MCP process able to
    /// obtain agent private key material while an external signer is configured" — expressed as the
    /// only thing that can be checked without a running signer.
    /// </summary>
    [Fact]
    public void R14_9_NoImplementationOfTheSignerPortCanReceiveOrReturnPrivateMaterial()
    {
        var sign = typeof(IAgentSigner).GetMethod(nameof(IAgentSigner.Sign))!;

        // What goes in is bytes to be signed; what comes out is a signature. Neither is a key.
        Assert.Equal(typeof(byte[]), sign.ReturnType);
        Assert.Equal([typeof(ReadOnlySpan<byte>)], sign.GetParameters().Select(p => p.ParameterType));

        Assert.DoesNotContain(
            typeof(IAgentSigner).GetMembers(),
            m => ReturnsAsymmetricKey(m) || m.Name.Contains("Private", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// An external signer that cannot be run is a configuration fault named as one, not a signature
    /// failure. "The signer is not there" and "the signer produced something wrong" are an
    /// operational fault and a cryptographic one — R6.52's distinction in a new place.
    /// </summary>
    [Fact]
    public void R11_20_AnUnrunnableExternalSignerIsAConfigurationFault()
    {
        var described = ExternalSigner.Describe(Path.Combine(_root, "no-such-signer"));

        Assert.False(described.TryGetValue(out _, out var error));
        Assert.Equal("curia/client/signer-unusable", error!.Type);
    }

    /// <summary>
    /// An identity built from an external signer holds no registered private key anywhere, and can
    /// still be enrolled — which is why the port carries the public half at all. Without it a
    /// delegated signer could not be registered, and the seam would be unusable for its own case.
    /// </summary>
    [Fact]
    public void R11_20_AnIdentityFromADelegatedSignerCanStillEnrol()
    {
        using var dpop = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var elsewhere = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var profile = new AgentProfile("alice", "https://agents.example/alice", "alice-1", "ES256", new Uri("http://forum.test"));
        var agent = EnrolledAgent.WithSigner(profile, new DelegatedSigner(elsewhere), dpop);

        Assert.Equal(Convert.ToBase64String(elsewhere.ExportSubjectPublicKeyInfo()), agent.PublicKeyBase64);

        // And the identity signs, without this type ever holding the key: the signer does.
        var signature = agent.Signer.Sign("payload"u8);
        Assert.True(elsewhere.VerifyData(
            "payload"u8, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    private static bool ReturnsAsymmetricKey(MemberInfo member) => member switch
    {
        PropertyInfo p => typeof(AsymmetricAlgorithm).IsAssignableFrom(p.PropertyType),
        MethodInfo m => typeof(AsymmetricAlgorithm).IsAssignableFrom(m.ReturnType),
        FieldInfo f => typeof(AsymmetricAlgorithm).IsAssignableFrom(f.FieldType),
        _ => false,
    };

    private EnrolledAgent Enrolled()
    {
        var store = new ProfileStore(_root);
        Assert.True(store
            .Create("alice", "https://agents.example/alice", "alice-1", new Uri("http://forum.test"))
            .TryGetValue(out var agent, out var error), error?.Type);

        return agent!;
    }

    /// <summary>A signer standing in for one in another process: this test holds the key, the agent does not.</summary>
    private sealed class DelegatedSigner(ECDsa key) : IAgentSigner
    {
        public string Alg => "ES256";

        public string Kid => "alice-1";

        public ReadOnlyMemory<byte> PublicKey => key.ExportSubjectPublicKeyInfo();

        public byte[] Sign(ReadOnlySpan<byte> signingInput) => key.SignData(
            signingInput, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
}
