using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Curia.Client.Cli;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// Errata G78 (R4.45, R11.106, <c>R11.105 (rev.)</c>), the client's half: <c>curia rotate --signer</c> compares the public key its signer describes with every key of the identity's key set, in whatever standing
/// the Forum states, and refuses a key that is listed before it signs or sends anything (KBO15); the refusal line and the two help sentences are the errata's spans, and the Forum's words are written as display
/// literals (KBO16). Written against the tree before the check exists: each refusal below is red by its own assertion (the verb signs and sends), and the controls (a new key, a kid this profile has used) are green.
/// </summary>
[Collection("console")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class KeyBytesBoundOnceTests
{
    private const string EnrolledKid = "a-0000abcd";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The key set's own members for a signer's key: the public coordinates the Forum would serve for it.</summary>
    private static (string X, string Y) Coordinates(TestSigner signer)
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(signer.PublicKey, out _);
        var q = key.ExportParameters(includePrivateParameters: false).Q;
        return (KeyActDouble.Base64Url(q.X!), KeyActDouble.Base64Url(q.Y!));
    }

    /// <summary>A rotation the rig's profile has made: it holds the enrolled key as a retained key and signs with a generated one, bound at the double's <c>AcceptedAt</c>.</summary>
    private static async Task<AgentProfile> RotatedAsync(KeyActRig rig)
    {
        var (exit, _, stderr) = await KeyActRig.CaptureAsync(() => Program.RotateKeyAsync(rig.Client, rig.Session, rig.Agent, rig.Store, null, Ct));
        Assert.True(exit == 0, stderr);
        rig.Reload();
        rig.Double.Requests.Clear();
        rig.Double.LifecycleBodies.Clear();
        return rig.Profile();
    }

    /// <summary>What a rotation with <paramref name="signer"/> did: its exit, its first stderr line, how many lifecycle requests the Forum saw and how many signatures the signer made.</summary>
    private sealed record Ran(int Exit, string FirstLine, string Stderr, int LifecyclePosts, int Signatures, string ProfileBefore, string ProfileAfter);

    private static async Task<Ran> RotateWithAsync(KeyActRig rig, TestSigner signer)
    {
        var before = rig.Fingerprint();
        var (exit, _, stderr) = await KeyActRig.CaptureAsync(() => Program.RotateKeyAsync(rig.Client, rig.Session, rig.Agent, rig.Store, signer.Command, Ct));
        return new Ran(exit, stderr.Split('\n')[0], stderr, rig.Double.LifecyclePosts, signer.Signatures, before, rig.Fingerprint());
    }

    /// <summary>The scene of KBO15 (a) and (b): the profile rotated away from the enrolled key, and the key set lists that key, with the signer's public key, in <paramref name="status"/>.</summary>
    private static async Task<(KeyActRig Rig, TestSigner Signer)> ListedAfterARotationAsync(string status)
    {
        var rig = KeyActRig.Create();
        var signer = TestSigner.Create("held-1");
        var profile = await RotatedAsync(rig);
        var (x, y) = Coordinates(signer);
        rig.Double.KeySet = KeyActDouble.KeySetJson(
            [new KeyActDouble.Key(EnrolledKid, 1, status, null, x, y), new KeyActDouble.Key(profile.Kid, rig.Double.AcceptedAt)], [0, 1, rig.Double.AcceptedAt]);
        return (rig, signer);
    }

    /// <summary>
    /// KBO15 (a) and (b): the signer's key is one the key set lists as retired, as compromised, or as a standing the client does not know, under a <c>kid</c> the profile never used and which the profile retains. The
    /// refusal is by the listed key, not by a status it recognises. Nothing is signed, nothing is sent, the profile is left alone. Red on the tree: <c>Expected: 1  Actual: 0</c> (the exit): the verb proceeds.
    /// </summary>
    [Theory]
    [InlineData("retired")]
    [InlineData("compromised")]
    [InlineData("quarantined")]
    public async Task KBO15_ASignerWhoseKeyTheKeySetListsIsRefusedBeforeAnythingIsSigned(string status)
    {
        var (rig, signer) = await ListedAfterARotationAsync(status);
        using (rig)
        using (signer)
        {
            var ran = await RotateWithAsync(rig, signer);

            Assert.Equal(
                $"exit {ExitCode.Usage}; lifecycle requests 0; signatures 0; profile unchanged: True",
                $"exit {ran.Exit}; lifecycle requests {ran.LifecyclePosts}; signatures {ran.Signatures}; profile unchanged: {ran.ProfileBefore == ran.ProfileAfter}");
        }
    }

    /// <summary>
    /// KBO15 (c): the listed key is in the key set and not in the profile's retained keys (retired from another host). The key set, not the profile, is what the check reads. Red on the tree: the verb proceeds.
    /// </summary>
    [Fact]
    public async Task KBO15_AKeyTheKeySetListsAndTheProfileNeverRetainedIsRefusedToo()
    {
        using var rig = KeyActRig.Create();
        using var signer = TestSigner.Create("held-1");
        var (x, y) = Coordinates(signer);
        rig.Double.KeySet = KeyActDouble.KeySetJson([new KeyActDouble.Key(EnrolledKid, 1), new KeyActDouble.Key("retired-elsewhere", null, "retired", null, x, y)], [0, 1]);
        Assert.DoesNotContain(rig.Profile().Retained, k => k.Kid == "retired-elsewhere");

        var ran = await RotateWithAsync(rig, signer);

        Assert.Equal(
            $"exit {ExitCode.Usage}; lifecycle requests 0; signatures 0; profile unchanged: True",
            $"exit {ran.Exit}; lifecycle requests {ran.LifecyclePosts}; signatures {ran.Signatures}; profile unchanged: {ran.ProfileBefore == ran.ProfileAfter}");
    }

    /// <summary>KBO15 (d), the control: a signer whose key the key set does not list proceeds as it did, the Forum sees one rotation. Green on the tree and after.</summary>
    [Fact]
    public async Task KBO15_ASignerWithANewKeyProceeds()
    {
        using var rig = KeyActRig.Create();
        using var listed = TestSigner.Create("held-listed");
        using var signer = TestSigner.Create("held-1");
        var (x, y) = Coordinates(listed);
        rig.Double.KeySet = KeyActDouble.KeySetJson([new KeyActDouble.Key(EnrolledKid, 1), new KeyActDouble.Key("retired-elsewhere", null, "retired", null, x, y)], [0, 1]);

        var ran = await RotateWithAsync(rig, signer);

        Assert.Equal("exit 0; lifecycle requests 1", $"exit {ran.Exit}; lifecycle requests {ran.LifecyclePosts}");
    }

    /// <summary>KBO15 (e), the control: a signer whose kid this profile has used keeps its old words and exit. Green on the tree and after.</summary>
    [Fact]
    public async Task KBO15_ASignerWhoseKidTheProfileUsedKeepsItsOldWords()
    {
        using var rig = KeyActRig.Create();
        using var signer = TestSigner.Create(EnrolledKid);

        var ran = await RotateWithAsync(rig, signer);

        Assert.Equal(
            $"exit {ExitCode.Usage}; old words: True; lifecycle requests 0",
            $"exit {ran.Exit}; old words: {ran.FirstLine.Contains("a key this profile has already used", StringComparison.Ordinal)}; lifecycle requests {ran.LifecyclePosts}");
    }

    /// <summary>
    /// KBO16: the refusal's first line is the errata's span with the Forum's <c>kid</c> and standing as display literals, and a <c>kid</c> of control characters is escaped and never printed. Red on the tree: the line
    /// the errata publishes is not the first line the verb prints.
    /// </summary>
    [Theory]
    [InlineData("retired-elsewhere", "retired")]
    [InlineData("evil\u001b[31m\nFOREIGN: forged", "retired\u001b[0m")]
    public async Task KBO16_TheRefusalIsTheErratasLineWithTheForumsWordsAsDisplayLiterals(string listedKid, string status)
    {
        using var rig = KeyActRig.Create();
        using var signer = TestSigner.Create("held-1");
        var (x, y) = Coordinates(signer);
        rig.Double.KeySet = KeyActDouble.KeySetJson([new KeyActDouble.Key(EnrolledKid, 1), new KeyActDouble.Key(listedKid, null, status, null, x, y)], [0, 1]);

        var ran = await RotateWithAsync(rig, signer);

        Assert.Equal(KeyPagesWords.KeyAlreadyBoundRefusal(listedKid, status), ran.FirstLine);
        Assert.False(ran.Stderr.Contains('\u001b', StringComparison.Ordinal), "the refusal printed a raw escape character");
    }

    /// <summary>
    /// KBO16: <c>curia rotate --help</c> carries the span <c>R11.105 (rev.)</c> gives it, and says it once beside the sentences R11.105 already gives it. (<c>curia retire --help</c> is WRD11's.) Red on the tree: said / not said.
    /// </summary>
    [Fact]
    public void KBO16_TheRotateHelpSaysARotationNeedsAKeyNeverBound()
    {
        var sentence = KeyPagesWords.RotateNewKey();
        var help = Help.For("rotate") ?? "<no help>";

        Assert.Equal(
            $"curia rotate --help carries \"{sentence}\": said",
            $"curia rotate --help carries \"{sentence}\": {(help.Contains(sentence, StringComparison.Ordinal) ? "said" : "not said")}");
    }
}
