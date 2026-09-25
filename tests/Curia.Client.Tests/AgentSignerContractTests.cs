using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Security.Cryptography;
using Curia.Canon.Canonical;
using Curia.Canon.Envelope;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Canon.Sodium;
using Curia.Client;
using Curia.Domain.Content;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R11.20's port, one contract, both adapters: whatever holds the registered key, what comes out of
/// <see cref="IAgentSigner"/> is a signature the Forum's own verifier accepts under the public key
/// the signer reports, over the header <see cref="DetachedJws"/> composes.
///
/// <para><b>Why one suite and not two.</b> R11.4 requires an in-memory adapter for every port so the
/// port can be tested without its infrastructure; the point of the pair is that they are
/// interchangeable, and interchangeability is a claim only a shared suite can make. Two suites
/// written separately would each describe their own adapter and agree only by coincidence.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public abstract class AgentSignerContract
{
    protected const string Kid = "contract-1";

    protected abstract IAgentSigner Signer { get; }

    [Fact]
    public void R11_20_TheSignerNamesItsOwnAlgorithmAndKid()
    {
        Assert.Equal("ES256", Signer.Alg);
        Assert.Equal(Kid, Signer.Kid);
    }

    [Fact]
    public void R11_20_ASignatureVerifiesUnderThePublicKeyTheSignerReports()
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Signer.PublicKey.Span, out _);

        var signature = Signer.Sign("contract input"u8);

        Assert.Equal(64, signature.Length);
        Assert.True(key.VerifyData(
            "contract input"u8, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        // Non-vacuity: the same key refuses a signature over different bytes, so "verifies" above is
        // a statement about these bytes rather than about a verifier that accepts anything.
        Assert.False(key.VerifyData(
            "other input"u8, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    /// <summary>
    /// The end of the chain that matters: a post envelope signed through the port verifies under the
    /// verifier the Forum runs at ingest, with the key the signer described.
    /// </summary>
    [Fact]
    public void R11_20_AnEnvelopeSignedThroughThePortVerifiesUnderTheForumsVerifier()
    {
        using var dpop = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var profile = new AgentProfile("contract", "https://agents.example/contract", Kid, "ES256", new Uri("http://forum.test"));
        var agent = EnrolledAgent.WithSigner(profile, Signer, dpop);

        var built = SubmissionBuilder.Build(
            agent,
            new PostDraft { Kind = PostKind.Question, Board = "b", Title = "t", Body = "Signed through the port." },
            DateTimeOffset.UnixEpoch);
        Assert.True(built.TryGetValue(out var submission, out var error), error?.Detail);

        var verifier = new DetachedJws(
            new Dictionary<string, IContentSigner>(StringComparer.Ordinal),
            new Dictionary<string, IContentVerifier>(StringComparer.Ordinal) { ["ES256"] = new Es256Adapter() });

        var verified = verifier.Verify(
            Canonical(submission.Canonical),
            new JwsSignature(submission.Signature),
            new PublicKeyMaterial("ES256", Kid, Signer.PublicKey));

        Assert.True(verified.TryGetValue(out _, out var verifyError), verifyError?.Type);
    }

    /// <summary>ES256 is randomized: two signatures over one input differ, and both verify.</summary>
    [Fact]
    public void R11_20_TwoSignaturesOverOneInputAreDistinctAndBothVerify()
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Signer.PublicKey.Span, out _);

        var first = Signer.Sign("same input"u8);
        var second = Signer.Sign("same input"u8);

        Assert.NotEqual(first, second);
        Assert.True(key.VerifyData("same input"u8, first, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        Assert.True(key.VerifyData("same input"u8, second, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    private static CanonicalBytes Canonical(ReadOnlyMemory<byte> bytes) =>
        JsonReader.Parse(bytes.Span, AdmitLimits.Default).TryGetValue(out var value, out _)
        && CanonicalJson.CanonicalizeWithNfc(value!).TryGetValue(out var canonical, out _)
            ? canonical
            : throw new InvalidOperationException("the submission's canonical bytes do not re-canonicalize");
}

/// <summary>R11.4's in-memory adapter: the key in this process.</summary>
public sealed class InProcessSignerContractTests : AgentSignerContract, IDisposable
{
    private readonly InProcessSigner _signer = new(ECDsa.Create(ECCurve.NamedCurves.nistP256), Kid);

    protected override IAgentSigner Signer => _signer;

    public void Dispose()
    {
        _signer.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// The external adapter, against a real signer in another process — and the rows only it can fail:
/// that the other process actually produced the signature, that a signer which declines is reported
/// as declining, and that nothing falls back to a key in this process when it does.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ExternalSignerContractTests : AgentSignerContract, IDisposable
{
    private readonly TestSigner _process = TestSigner.Create(Kid);
    private readonly ExternalSigner _signer;
    private readonly string _root = Directory.CreateTempSubdirectory("curia-external-profile-").FullName;

    public ExternalSignerContractTests()
    {
        Assert.True(ExternalSigner.Describe(_process.Command).TryGetValue(out var signer, out var error), error?.Detail);
        _signer = signer!;
    }

    protected override IAgentSigner Signer => _signer;

    public void Dispose()
    {
        _process.Dispose();
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The signature came from the other process. Without this, an <see cref="ExternalSigner"/> that
    /// quietly signed in-process would pass every row of the contract above.
    /// </summary>
    [Fact]
    public void R11_20_TheSignatureIsProducedByTheSignerProcess()
    {
        var before = _process.Signatures;
        _ = _signer.Sign("counted"u8);

        Assert.Equal(before + 1, _process.Signatures);
    }

    /// <summary>A signer that is up and declines is a refusal, never an invalid signature and never an exception.</summary>
    [Fact]
    public void R11_20_ASignerThatDeclinesIsReportedAsARefusal()
    {
        _process.Refuse();

        var jws = new DetachedJws(
            new Dictionary<string, IContentSigner>(StringComparer.Ordinal) { ["ES256"] = new Es256Adapter() },
            new Dictionary<string, IContentVerifier>(StringComparer.Ordinal));

        var canonical = CanonicalJson.CanonicalizeWithNfc(new JsonValue.Object([new("a", new JsonValue.String("b"))]))
            .TryGetValue(out var bytes, out _) ? bytes : throw new InvalidOperationException();

        var signed = jws.Sign(canonical, _signer);

        Assert.False(signed.TryGetValue(out _, out var error));
        Assert.Equal("curia/jws/signer-refused", error!.Type);
        Assert.Contains("refuse", error.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The adapter holds nothing that could sign. Asserted over its fields, so a fallback — an
    /// <c>ECDsa</c> kept "just in case", a profile store to reload one from — cannot be added
    /// without this failing: the plan's falsification "let the external-signer adapter fall back to
    /// an in-process key on error" needs somewhere to keep that key, and this is where it would go.
    /// </summary>
    [Fact]
    public void R11_20_TheExternalAdapterHoldsNothingThatCouldSign()
    {
        var fields = typeof(ExternalSigner).GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        // Non-vacuity: the scan sees the fields that are there.
        Assert.Contains(fields, f => f.FieldType == typeof(string));

        Assert.DoesNotContain(fields, f =>
            typeof(AsymmetricAlgorithm).IsAssignableFrom(f.FieldType)
            || f.FieldType == typeof(SigningKey)
            || f.FieldType == typeof(ProfileStore)
            || f.FieldType == typeof(EnrolledAgent)
            || typeof(IContentSigner).IsAssignableFrom(f.FieldType));
    }

    /// <summary>
    /// A profile enrolled through a signer writes no signing key at all, and records the signer, so
    /// every later process that acts as the agent signs through it.
    /// </summary>
    [Fact]
    public void R11_20_AProfileEnrolledThroughASignerHoldsNoSigningKey()
    {
        var store = new ProfileStore(_root);
        Assert.True(store.Create("delegated", "https://agents.example/delegated", new Uri("http://forum.test"), _signer)
            .TryGetValue(out var agent, out var error), error?.Detail);

        using (agent)
        {
            var directory = store.DirectoryFor("delegated");
            Assert.False(File.Exists(Path.Combine(directory, "signing-key.pem")));
            Assert.True(File.Exists(Path.Combine(directory, "dpop-key.pem")));
            Assert.Contains(_process.Command, File.ReadAllText(Path.Combine(directory, "identity.json")), StringComparison.Ordinal);
            Assert.Equal(Kid, agent.Profile.Kid);
            Assert.Equal(Convert.ToBase64String(_process.PublicKey), agent.PublicKeyBase64);
        }
    }

    /// <summary>
    /// Loading a delegated profile never opens <c>signing-key.pem</c>. A file sits there that the
    /// in-process path would refuse outright — readable by others, and not a key — and the load
    /// succeeds regardless, and then signs through the other process.
    /// </summary>
    [Fact]
    public void R11_20_LoadingADelegatedProfileNeverOpensTheSigningKeyFile()
    {
        var store = new ProfileStore(_root);
        Assert.True(store.Create("delegated", "https://agents.example/delegated", new Uri("http://forum.test"), _signer)
            .TryGetValue(out var created, out _));
        created!.Dispose();

        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("file modes are POSIX");

        var planted = Path.Combine(store.DirectoryFor("delegated"), "signing-key.pem");
        File.WriteAllText(planted, "not a key, and world-readable");
        File.SetUnixFileMode(planted, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        Assert.True(store.Load("delegated").TryGetValue(out var loaded, out var error), error?.Detail);

        using (loaded)
        {
            Assert.IsType<ExternalSigner>(loaded!.Signer);

            var before = _process.Signatures;
            _ = loaded.Signer.Sign("through the process"u8);
            Assert.Equal(before + 1, _process.Signatures);
        }
    }

    /// <summary>
    /// A signer that is down or declining does not send the key back into this process. The profile
    /// holds a valid in-process key as well — a deployment migrating from one custody to the other —
    /// and the submission is still refused rather than signed with it.
    /// </summary>
    [Fact]
    public void R11_20_ADecliningSignerIsNotReplacedByAKeyOnDisk()
    {
        var store = new ProfileStore(_root);
        Assert.True(store.Create("delegated", "https://agents.example/delegated", new Uri("http://forum.test"), _signer)
            .TryGetValue(out var created, out _));
        created!.Dispose();

        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("file modes are POSIX");

        using (var stray = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            var path = Path.Combine(store.DirectoryFor("delegated"), "signing-key.pem");
            File.WriteAllText(path, stray.ExportPkcs8PrivateKeyPem());
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        _process.Refuse();
        Assert.True(store.Load("delegated").TryGetValue(out var loaded, out var error), error?.Detail);

        using (loaded)
        {
            var built = SubmissionBuilder.Build(
                loaded!,
                new PostDraft { Kind = PostKind.Question, Board = "b", Title = "t", Body = "Must not be signed here." },
                DateTimeOffset.UnixEpoch);

            Assert.False(built.TryGetValue(out _, out var refusal));
            Assert.Contains("curia/jws/signer-refused", refusal!.Detail, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A signer that now describes a different key from the one enrolled is refused at load, once,
    /// rather than producing posts the Forum refuses one at a time as unverifiable.
    /// </summary>
    [Fact]
    public void R11_20_ASignerDescribingAnotherKidIsRefusedAtLoad()
    {
        var store = new ProfileStore(_root);
        Assert.True(store.Create("delegated", "https://agents.example/delegated", new Uri("http://forum.test"), _signer)
            .TryGetValue(out var created, out _));
        created!.Dispose();

        var describe = Path.Combine(_process.Directory, "describe.json");
        File.WriteAllText(describe, File.ReadAllText(describe).Replace(Kid, "someone-else-1", StringComparison.Ordinal));

        Assert.False(store.Load("delegated").TryGetValue(out _, out var error));
        Assert.Equal("curia/client/signer-unusable", error!.Type);
        Assert.Contains("someone-else-1", error.Detail, StringComparison.Ordinal);
    }
}
