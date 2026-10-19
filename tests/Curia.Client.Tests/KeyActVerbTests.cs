using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using Curia.Client.Cli;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// <c>curia keys</c>, <c>curia rotate</c> and <c>curia retire</c> (errata G21, spec sections 4.9 and 4.12; R4.18 rev.,
/// R4.39), driven in process through the internal methods <c>Main</c> calls, against a Forum double that serves the
/// key set and the two lifecycle routes. The routes themselves, and the wire shape the client library sends them,
/// are held to the real Forum in <c>Curia.Api.Tests</c>; these facts hold what the verbs read, in what order, what
/// they sign, what they send, and what they write.
/// </summary>
[Collection("console")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class KeyActVerbTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Text(string path) => File.ReadAllText(path);

    private static KeyActDouble.Key Key(string kid, long? at, string status = "active", long? retiredAt = null) => new(kid, at, status, retiredAt);

    /// <summary>A rotation the rig's profile has made, so that it holds a retained key to retire. Leaves the double serving its key set.</summary>
    private static async Task<string> RotatedAsync(KeyActRig rig)
    {
        var (exit, _, stderr) = await KeyActRig.CaptureAsync(() => Program.RotateKeyAsync(rig.Client, rig.Session, rig.Agent, rig.Store, null, Ct));
        Assert.True(exit == 0, stderr);

        var profile = rig.Profile();
        rig.Double.KeySet = KeyActDouble.KeySetJson(
            [Key("a-0000abcd", 1), Key(profile.Kid, rig.Double.AcceptedAt)], [0, 1, rig.Double.AcceptedAt]);
        rig.Double.Requests.Clear();
        rig.Double.LifecycleBodies.Clear();
        rig.Reload();
        return profile.Kid;
    }

    [Fact]
    public async Task R4_39_KeysReportsARecordedActTheLifecycleOmits()
    {
        using var rig = KeyActRig.Create();
        await RotatedAsync(rig);
        rig.Double.KeySet = KeyActDouble.KeySetJson([Key("a-0000abcd", 1)], [0, 1]);

        var (exit, stdout, _) = await KeyActRig.CaptureAsync(() => Program.ShowKeysAsync(rig.Client, rig.Profile(), Ct));

        Assert.Equal(ExitCode.Unverified, exit);
        Assert.Contains("MISSING: this profile's rotation at log index 3 is not in the lifecycle the Forum publishes", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R4_39_KeysIsCleanWhenTheLifecycleIsTheProfilesOwn()
    {
        using var rig = KeyActRig.Create();
        var rotated = await RotatedAsync(rig);

        var (exit, stdout, _) = await KeyActRig.CaptureAsync(() => Program.ShowKeysAsync(rig.Client, rig.Profile(), Ct));

        Assert.Equal(ExitCode.Ok, exit);
        Assert.DoesNotContain("MISSING", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("FOREIGN", stdout, StringComparison.Ordinal);
        Assert.Contains("key \"a-0000abcd\" active, bound at log index 1", stdout, StringComparison.Ordinal);
        Assert.Contains($"key \"{rotated}\" active, bound at log index 3", stdout, StringComparison.Ordinal);
        Assert.Contains("lifecycle published through log index 3; 1 acts of this profile recorded", stdout, StringComparison.Ordinal);
        Assert.Contains("lifecycle kinds this Forum enables: \"rotation\", \"retirement\"", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task D37_KeysSaysWhoseWordItPrintsAndWhatAnchorsIt()
    {
        using var rig = KeyActRig.Create();
        await RotatedAsync(rig);

        var (_, stdout, _) = await KeyActRig.CaptureAsync(() => Program.ShowKeysAsync(rig.Client, rig.Profile(), Ct));

        var lines = stdout.Split('\n');
        var word = Array.FindIndex(lines, l => l.Contains("the Forum's word", StringComparison.Ordinal));
        Assert.True(word >= 0, "no line says the key set is the Forum's word");
        Assert.Contains("curia verify", lines[word], StringComparison.Ordinal);
        Assert.True(word < Array.FindIndex(lines, l => l.StartsWith("key ", StringComparison.Ordinal)), "the line comes after the first key line");
    }

    [Fact]
    public async Task D37_NoneEnabledKindsNamesTheLifecycleGuard()
    {
        using var rig = KeyActRig.Create();
        rig.Double.KeySet = KeyActDouble.KeySetJson([Key("a-0000abcd", 1)], [0, 1], enabled: []);

        var (_, stdout, _) = await KeyActRig.CaptureAsync(() => Program.ShowKeysAsync(rig.Client, rig.Profile(), Ct));

        Assert.Contains(
            "lifecycle kinds this Forum enables: none (every lifecycle kind is guarded until the owner lifts the guard, R4.38)",
            stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R4_39_KeysReportsAForeignRotation()
    {
        using var rig = KeyActRig.Create();
        rig.Double.KeySet = KeyActDouble.KeySetJson([Key("a-0000abcd", 1), Key("thief-1", 7)], [0, 1, 7]);

        var (exit, stdout, _) = await KeyActRig.CaptureAsync(() => Program.ShowKeysAsync(rig.Client, rig.Profile(), Ct));

        Assert.Equal(ExitCode.Unverified, exit);
        Assert.Contains("FOREIGN: a rotation at log index 7 that this profile did not make", stdout, StringComparison.Ordinal);
        Assert.Contains("report it to the Forum's operator; a compromise declaration from no later than log index 7 ends the key it bound (R6.57)", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R4_39_KeysReportsAForeignRetirement()
    {
        using var rig = KeyActRig.Create();
        rig.Double.KeySet = KeyActDouble.KeySetJson([Key("a-0000abcd", 1, "retired", retiredAt: 6)], [0, 1, 6]);

        var (exit, stdout, _) = await KeyActRig.CaptureAsync(() => Program.ShowKeysAsync(rig.Client, rig.Profile(), Ct));

        Assert.Equal(ExitCode.Unverified, exit);
        Assert.Contains("FOREIGN: a retirement at log index 6 that this profile did not make", stdout, StringComparison.Ordinal);
        Assert.Contains("key \"a-0000abcd\" retired, bound at log index 1, retired at 6", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R10_63_EveryKeysLineQuotesTheForumsValues()
    {
        using var rig = KeyActRig.Create();
        rig.Double.KeySet = KeyActDouble.KeySetJson([Key("a\"b\nFOREIGN: forged", 1, status: "active\nok")], [0, 1], enabled: ["rot\"ation"]);

        var (_, stdout, _) = await KeyActRig.CaptureAsync(() => Program.ShowKeysAsync(rig.Client, rig.Profile(), Ct));

        Assert.Contains("key \"a\\\"b\\u000aFOREIGN: forged\" \"active\\u000aok\", bound at log index 1", stdout, StringComparison.Ordinal);
        Assert.Contains("lifecycle kinds this Forum enables: \"rot\\\"ation\"", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("\nFOREIGN", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R4_18_RotateMovesTheKeyAndPrintsTheRetireHint()
    {
        using var rig = KeyActRig.Create();
        var oldPem = Text(Path.Combine(rig.Directory, "signing-key.pem"));

        var (exit, stdout, stderr) = await KeyActRig.CaptureAsync(() => Program.RotateKeyAsync(rig.Client, rig.Session, rig.Agent, rig.Store, null, Ct));

        Assert.True(exit == 0, stderr);
        var lines = stdout.Split('\n');
        Assert.Contains("curia retire --agent 'a' --kid 'a-0000abcd'", lines);

        var profile = rig.Profile();
        Assert.StartsWith("a-", profile.Kid, StringComparison.Ordinal);
        Assert.NotEqual("a-0000abcd", profile.Kid);
        Assert.Contains($"rotated   key \"{profile.Kid}\" bound at log index 3, authorized by \"a-0000abcd\"", lines);
        Assert.Contains("\"a-0000abcd\" stays live until it is retired:", lines);
        Assert.Contains("restart any curia-mcp serving this identity; it read its key at start.", lines);

        // The old key is kept under a name derived from a hash, at the private mode; the new one is the signing key.
        var retained = Assert.Single(profile.Retained);
        Assert.Equal("a-0000abcd", retained.Kid);
        Assert.False(retained.Retired);
        Assert.Equal(oldPem, Text(rig.Store.RetainedKeyFile("a", "a-0000abcd")));
        Assert.NotEqual(oldPem, Text(Path.Combine(rig.Directory, "signing-key.pem")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(rig.Store.RetainedKeyFile("a", "a-0000abcd")));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(rig.Directory, "signing-key.pem")));
        }

        Assert.Equal(new LifecycleAct("rotation", profile.Kid, 3), Assert.Single(profile.Acts));
        Assert.Null(rig.Store.ReadToken("a"));
    }

    [Fact]
    public async Task R4_18_RotationSignsBothStatementsUnderTheRotationHeader()
    {
        using var rig = KeyActRig.Create();
        var oldPublic = KeyActRig.PublicOf(Text(Path.Combine(rig.Directory, "signing-key.pem")));

        var (exit, _, stderr) = await KeyActRig.CaptureAsync(() => Program.RotateKeyAsync(rig.Client, rig.Session, rig.Agent, rig.Store, null, Ct));
        Assert.True(exit == 0, stderr);

        var body = JsonDocument.Parse(Assert.Single(rig.Double.LifecycleBodies)).RootElement;
        var statement = body.GetProperty("statement").GetString()!;
        var parsed = JsonDocument.Parse(statement).RootElement;
        var newKid = rig.Profile().Kid;

        Assert.Equal("curia.key-rotation/1", parsed.GetProperty("typ").GetString());
        Assert.Equal(KeyActRig.AgentId, parsed.GetProperty("agent_id").GetString());
        Assert.Equal("a-0000abcd", parsed.GetProperty("authorized_by").GetString());
        Assert.Equal(newKid, parsed.GetProperty("kid").GetString());
        Assert.Equal(1, parsed.GetProperty("prev").GetInt64());

        var newPublic = KeyActRig.PublicOf(Text(Path.Combine(rig.Directory, "signing-key.pem")));
        KeyActRig.AssertSigned(statement, body.GetProperty("authorization").GetString()!, "curia-key-rotation+jws", "a-0000abcd", oldPublic);
        KeyActRig.AssertSigned(statement, body.GetProperty("possession").GetString()!, "curia-key-rotation+jws", newKid, newPublic);
    }

    [Fact]
    public async Task R10_65_AKidThatIsNoShellWordWithholdsTheRetireCommand()
    {
        using var rig = KeyActRig.Create(kid: "a b'c");
        rig.Double.KeySet = KeyActDouble.KeySetJson([Key("a b'c", 1)], [0, 1]);

        var (exit, stdout, stderr) = await KeyActRig.CaptureAsync(() => Program.RotateKeyAsync(rig.Client, rig.Session, rig.Agent, rig.Store, null, Ct));

        Assert.True(exit == 0, stderr);
        Assert.DoesNotContain("curia retire --agent", stdout, StringComparison.Ordinal);
        Assert.Contains($"curia retire and the agent and key named above ({Hints.Withheld})", stdout.Split('\n'));
        Assert.Contains("\"a b'c\" stays live until it is retired:", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void R10_65_RetireAndKeysAreCommandsOnlyForValuesAShellReadsBackAsThemselves()
    {
        Assert.Equal("curia retire --agent 'a' --kid 'a-0000abcd'", Hints.Retire("a", "a-0000abcd").Text);
        Assert.Equal("curia keys --agent 'a'", Hints.Keys("a").Text);

        foreach (var hostile in new[] { "-k", "a'b", "a\\b", "a\nb", "a!b", string.Empty })
        {
            Assert.DoesNotContain("--kid", Hints.Retire("a", hostile).Text, StringComparison.Ordinal);
            Assert.DoesNotContain("--agent", Hints.Retire(hostile, "k").Text, StringComparison.Ordinal);
            Assert.DoesNotContain("--agent", Hints.Keys(hostile).Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task R4_39_RetireRefusesTheCurrentKey()
    {
        using var rig = KeyActRig.Create();

        var (exit, _, stderr) = await KeyActRig.CaptureAsync(() => Program.RetireKeyAsync(rig.Client, rig.Session, rig.Store, rig.Profile(), "a-0000abcd", Ct));

        Assert.Equal(ExitCode.Usage, exit);
        Assert.Contains("rotate first", stderr, StringComparison.Ordinal);
        Assert.Empty(rig.Double.Requests);
    }

    [Fact]
    public async Task R4_39_RetireRefusesAKeyThisProfileDoesNotHold()
    {
        using var rig = KeyActRig.Create();

        var (exit, _, stderr) = await KeyActRig.CaptureAsync(() => Program.RetireKeyAsync(rig.Client, rig.Session, rig.Store, rig.Profile(), "stranger-1", Ct));

        Assert.Equal(ExitCode.Local, exit);
        Assert.Contains("holds no live retained key", stderr, StringComparison.Ordinal);
        Assert.Contains("curia keys --agent 'a'", stderr, StringComparison.Ordinal);
        Assert.Empty(rig.Double.Requests);
    }

    [Fact]
    public async Task R4_39_RetireEndsARetainedKeyAndKeepsItsFile()
    {
        using var rig = KeyActRig.Create();
        var newKid = await RotatedAsync(rig);
        var retainedPublic = KeyActRig.PublicOf(Text(rig.Store.RetainedKeyFile("a", "a-0000abcd")));
        rig.Double.AcceptedAt = 5;

        var (exit, stdout, stderr) = await KeyActRig.CaptureAsync(() => Program.RetireKeyAsync(rig.Client, rig.Session, rig.Store, rig.Profile(), "a-0000abcd", Ct));

        Assert.True(exit == 0, stderr);
        Assert.Contains($"retired   key \"a-0000abcd\" at log index 5; this profile signs with \"{newKid}\"", stdout, StringComparison.Ordinal);

        var profile = rig.Profile();
        Assert.True(Assert.Single(profile.Retained).Retired);
        Assert.True(File.Exists(rig.Store.RetainedKeyFile("a", "a-0000abcd")));
        Assert.Contains(new LifecycleAct("retirement", "a-0000abcd", 5), profile.Acts);
        Assert.Contains("/v1/agents/https:%2F%2Fagents.example%2Fa/keys/a-0000abcd/retire", rig.Double.Requests.Single(KeyActDouble.IsLifecyclePost), StringComparison.Ordinal);

        var body = JsonDocument.Parse(Assert.Single(rig.Double.LifecycleBodies)).RootElement;
        var statement = body.GetProperty("statement").GetString()!;
        Assert.Equal("curia.key-retirement/1", JsonDocument.Parse(statement).RootElement.GetProperty("typ").GetString());
        KeyActRig.AssertSigned(statement, body.GetProperty("authorization").GetString()!, "curia-key-retirement+jws", "a-0000abcd", retainedPublic);
        Assert.False(body.TryGetProperty("possession", out _));
    }

    [Fact]
    public async Task R4_39_RetireDoesNotSignOverAForeignAct()
    {
        using var rig = KeyActRig.Create();
        var newKid = await RotatedAsync(rig);
        rig.Double.KeySet = KeyActDouble.KeySetJson([Key("a-0000abcd", 1), Key(newKid, 3), Key("thief-1", 7)], [0, 1, 3, 7]);
        var before = rig.Fingerprint();

        var (exit, _, stderr) = await KeyActRig.CaptureAsync(() => Program.RetireKeyAsync(rig.Client, rig.Session, rig.Store, rig.Profile(), "a-0000abcd", Ct));

        Assert.Equal(ExitCode.Unverified, exit);
        Assert.Contains("FOREIGN: a rotation at log index 7 that this profile did not make", stderr, StringComparison.Ordinal);
        Assert.Equal(0, rig.Double.LifecyclePosts);
        Assert.Empty(rig.Double.LifecycleBodies);
        Assert.Equal(before, rig.Fingerprint());
    }

    [Fact]
    public async Task R4_39_RotateDoesNotSignOverAForeignAct()
    {
        using var rig = KeyActRig.Create();
        using var signer = TestSigner.Create("held-1");
        rig.Double.KeySet = KeyActDouble.KeySetJson([Key("a-0000abcd", 1), Key("thief-1", 7)], [0, 1, 7]);
        var before = rig.Fingerprint();

        var (exit, _, stderr) = await KeyActRig.CaptureAsync(() => Program.RotateKeyAsync(rig.Client, rig.Session, rig.Agent, rig.Store, signer.Command, Ct));

        Assert.Equal(ExitCode.Unverified, exit);
        Assert.Contains("FOREIGN: a rotation at log index 7 that this profile did not make", stderr, StringComparison.Ordinal);
        Assert.Contains("report it to the Forum's operator", stderr, StringComparison.Ordinal);
        Assert.Equal(0, rig.Double.LifecyclePosts);
        Assert.Equal(0, signer.Signatures);
        Assert.Equal(before, rig.Fingerprint());

        // A thief that has already retired the stolen key: a refusal for the authorizing key would never say so,
        // so the read-time check is the only place the owner learns of it.
        rig.Double.KeySet = KeyActDouble.KeySetJson([Key("a-0000abcd", 1, "retired", retiredAt: 7)], [0, 1, 7]);

        (exit, _, stderr) = await KeyActRig.CaptureAsync(() => Program.RotateKeyAsync(rig.Client, rig.Session, rig.Agent, rig.Store, null, Ct));

        Assert.Equal(ExitCode.Unverified, exit);
        Assert.Contains("FOREIGN: a retirement at log index 7 that this profile did not make", stderr, StringComparison.Ordinal);
        Assert.Equal(0, rig.Double.LifecyclePosts);
        Assert.Equal(before, rig.Fingerprint());
    }

    [Fact]
    public async Task R4_39_RotateRetriesOverItsOwnActOnly()
    {
        using var rig = KeyActRig.Create();
        var recorded = rig.Profile() with { LifecycleActs = [new LifecycleAct("retirement", "old-1", 5)] };
        rig.Store.RecordEnrollment(recorded, "2026-01-01T00:00:00Z");
        rig.Reload();

        rig.Double.KeySetSequence.Enqueue(KeyActDouble.KeySetJson([Key("a-0000abcd", 1)], [0, 1]));
        rig.Double.KeySetSequence.Enqueue(KeyActDouble.KeySetJson([Key("a-0000abcd", 1)], [0, 1, 5]));
        rig.Double.LifecycleAnswers.Enqueue((HttpStatusCode.Conflict, KeyActDouble.Problem("curia/keys/lifecycle-moved", "The identity's lifecycle has moved", "5")));

        var (exit, stdout, stderr) = await KeyActRig.CaptureAsync(() => Program.RotateKeyAsync(rig.Client, rig.Session, rig.Agent, rig.Store, null, Ct));

        Assert.True(exit == 0, stderr);
        Assert.Contains("rotated   key", stdout, StringComparison.Ordinal);
        Assert.Equal(2, rig.Double.LifecyclePosts);

        // The same new key, and a statement naming the new prev.
        var statements = rig.Double.LifecycleBodies.Select(b => JsonDocument.Parse(JsonDocument.Parse(b).RootElement.GetProperty("statement").GetString()!).RootElement).ToList();
        Assert.Equal(1, statements[0].GetProperty("prev").GetInt64());
        Assert.Equal(5, statements[1].GetProperty("prev").GetInt64());
        Assert.Equal(statements[0].GetProperty("kid").GetString(), statements[1].GetProperty("kid").GetString());

        // The act the other run recorded is kept beside this one.
        Assert.Equal(2, rig.Profile().Acts.Length);
    }

    [Fact]
    public async Task R4_39_RotateDoesNotRetryOverAForeignAct()
    {
        using var rig = KeyActRig.Create();
        rig.Double.KeySetSequence.Enqueue(KeyActDouble.KeySetJson([Key("a-0000abcd", 1)], [0, 1]));
        rig.Double.KeySetSequence.Enqueue(KeyActDouble.KeySetJson([Key("a-0000abcd", 1), Key("thief-1", 5)], [0, 1, 5]));
        rig.Double.LifecycleAnswers.Enqueue((HttpStatusCode.Conflict, KeyActDouble.Problem("curia/keys/lifecycle-moved", "The identity's lifecycle has moved", "5")));
        var before = rig.Fingerprint();

        var (exit, _, stderr) = await KeyActRig.CaptureAsync(() => Program.RotateKeyAsync(rig.Client, rig.Session, rig.Agent, rig.Store, null, Ct));

        Assert.Equal(ExitCode.Unverified, exit);
        Assert.Contains("FOREIGN: a rotation at log index 5 that this profile did not make", stderr, StringComparison.Ordinal);
        Assert.Equal(1, rig.Double.LifecyclePosts);
        Assert.Equal(before, rig.Fingerprint());
    }

    [Fact]
    public async Task R4_39_ASecondLifecycleMovedIsTheAnswerAndChangesNothing()
    {
        using var rig = KeyActRig.Create();
        rig.Double.LifecycleAnswers.Enqueue((HttpStatusCode.Conflict, KeyActDouble.Problem("curia/keys/lifecycle-moved", "The identity's lifecycle has moved", "1")));
        rig.Double.LifecycleAnswers.Enqueue((HttpStatusCode.Conflict, KeyActDouble.Problem("curia/keys/lifecycle-moved", "The identity's lifecycle has moved", "1")));
        var before = rig.Fingerprint();

        var (exit, _, stderr) = await KeyActRig.CaptureAsync(() => Program.RotateKeyAsync(rig.Client, rig.Session, rig.Agent, rig.Store, null, Ct));

        Assert.Equal(ExitCode.Rejected, exit);
        Assert.Contains("curia/keys/lifecycle-moved", stderr, StringComparison.Ordinal);
        Assert.Equal(2, rig.Double.LifecyclePosts);
        Assert.Equal(before, rig.Fingerprint());
    }

    [Fact]
    public async Task R4_39_ARefusedRotationLeavesTheProfileByteIdentical()
    {
        using var rig = KeyActRig.Create();
        rig.Double.LifecycleAnswers.Enqueue((HttpStatusCode.Conflict, KeyActDouble.Problem("curia/keys/too-many-live-keys", "Too many live keys")));
        var before = rig.Fingerprint();

        var (exit, _, _) = await KeyActRig.CaptureAsync(() => Program.RotateKeyAsync(rig.Client, rig.Session, rig.Agent, rig.Store, null, Ct));

        Assert.Equal(ExitCode.Rejected, exit);
        Assert.Equal(before, rig.Fingerprint());
    }

    [Fact]
    public async Task R4_39_ARotationAgainstAKeySetThatNamesNoLifecycleSignsNothing()
    {
        using var rig = KeyActRig.Create();
        rig.Double.KeySet = KeyActDouble.KeySetJson([Key("a-0000abcd", 1)], lifecycle: null);
        var before = rig.Fingerprint();

        var (exit, _, stderr) = await KeyActRig.CaptureAsync(() => Program.RotateKeyAsync(rig.Client, rig.Session, rig.Agent, rig.Store, null, Ct));

        Assert.Equal(ExitCode.ForumFault, exit);
        Assert.Contains("names no lifecycle", stderr, StringComparison.Ordinal);
        Assert.Equal(0, rig.Double.LifecyclePosts);
        Assert.Equal(before, rig.Fingerprint());
    }

    // ---- a key set that names a lifecycle and no binding cannot be compared (cybersecurity F-A) ------------

    private const string NoBindingType = "curia/client/key-set-names-no-binding";

    [Fact]
    public async Task R4_39_RotateRefusesAKeySetThatNamesNoBinding()
    {
        using var rig = KeyActRig.Create();
        rig.Double.KeySet = KeyActDouble.KeySetJson([Key("a-0000abcd", null)], [0, 1]);
        var before = rig.Fingerprint();

        var (exit, _, stderr) = await KeyActRig.CaptureAsync(() => Program.RotateKeyAsync(rig.Client, rig.Session, rig.Agent, rig.Store, null, Ct));

        Assert.Equal(ExitCode.ForumFault, exit);
        Assert.Contains("names no key's binding", stderr, StringComparison.Ordinal);
        Assert.Equal(0, rig.Double.LifecyclePosts);
        Assert.Equal(before, rig.Fingerprint());
    }

    [Fact]
    public async Task R4_39_RotateProceedsWhenAKeyNamesItsBinding()
    {
        using var rig = KeyActRig.Create();
        rig.Double.KeySet = KeyActDouble.KeySetJson([Key("a-0000abcd", 1)], [0, 1]);

        var (exit, _, stderr) = await KeyActRig.CaptureAsync(() => Program.RotateKeyAsync(rig.Client, rig.Session, rig.Agent, rig.Store, null, Ct));

        Assert.True(exit == 0, stderr);
        Assert.Equal(1, rig.Double.LifecyclePosts);
    }

    [Fact]
    public async Task R4_39_RotateRefusingNoBindingCallsNoSigner()
    {
        using var rig = KeyActRig.Create();
        rig.Double.KeySet = KeyActDouble.KeySetJson([Key("a-0000abcd", null)], [0, 1]);
        using var signer = TestSigner.Create("held-1");

        var (exit, _, _) = await KeyActRig.CaptureAsync(() => Program.RotateKeyAsync(rig.Client, rig.Session, rig.Agent, rig.Store, signer.Command, Ct));

        Assert.Equal(ExitCode.ForumFault, exit);
        Assert.Equal(0, signer.Signatures);
        Assert.Equal(0, rig.Double.LifecyclePosts);
    }

    [Fact]
    public async Task R4_39_RetireRefusesAKeySetThatNamesNoBinding()
    {
        using var rig = KeyActRig.Create();
        await RotatedAsync(rig);
        rig.Double.KeySet = KeyActDouble.KeySetJson([Key("a-0000abcd", null)], [0, 1, 3]);
        var before = rig.Fingerprint();

        var (exit, _, stderr) = await KeyActRig.CaptureAsync(() => Program.RetireKeyAsync(rig.Client, rig.Session, rig.Store, rig.Profile(), "a-0000abcd", Ct));

        Assert.Equal(ExitCode.ForumFault, exit);
        Assert.Contains("names no key's binding", stderr, StringComparison.Ordinal);
        Assert.Equal(0, rig.Double.LifecyclePosts);
        Assert.Equal(before, rig.Fingerprint());
    }

    [Fact]
    public void R4_39_TheNoBindingRefusalHasItsOwnType() =>
        Assert.Equal(NoBindingType, Curia.Client.ClientErrors.KeySetNamesNoBinding().Type);

    [Fact]
    public async Task R4_39_KeysExitsEightWhenNoBindingIsNamed()
    {
        using var rig = KeyActRig.Create();
        rig.Double.KeySet = KeyActDouble.KeySetJson([Key("a-0000abcd", null)], [0, 1]);

        var (exit, _, stderr) = await KeyActRig.CaptureAsync(() => Program.ShowKeysAsync(rig.Client, rig.Profile(), Ct));

        Assert.Equal(ExitCode.ForumFault, exit);
        Assert.Contains("could not compare: no key names its binding", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R4_39_KeysExitsEightWhenNoLifecycleIsNamed()
    {
        using var rig = KeyActRig.Create();
        rig.Double.KeySet = KeyActDouble.KeySetJson([Key("a-0000abcd", 1)], lifecycle: null);

        var (exit, _, stderr) = await KeyActRig.CaptureAsync(() => Program.ShowKeysAsync(rig.Client, rig.Profile(), Ct));

        Assert.Equal(ExitCode.ForumFault, exit);
        Assert.Contains("could not compare", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R4_39_KeysExitsZeroOnACleanComparison()
    {
        using var rig = KeyActRig.Create();

        var (exit, _, stderr) = await KeyActRig.CaptureAsync(() => Program.ShowKeysAsync(rig.Client, rig.Profile(), Ct));

        Assert.True(exit == 0, stderr);
    }

    // ---- the FOREIGN check on a refusal (spec section 4.9) ----------------------------------------------

    [Fact]
    public async Task R4_39_AnInvalidClientRefusalRunsTheForeignCheck()
    {
        using var rig = KeyActRig.Create();
        rig.Double.Token = (HttpStatusCode.Unauthorized, """{"error":"invalid_client","error_description":"Client authentication failed"}""");
        rig.Double.KeySet = KeyActDouble.KeySetJson([Key("a-0000abcd", 1, "retired", retiredAt: 6)], [0, 1, 6]);

        var minted = await rig.Session.AccessTokenAsync(Ct);
        Assert.True(!minted.TryGetValue(out _, out var refusal));
        Assert.Equal("invalid_client", refusal!.Error.Type);

        var (exit, _, stderr) = await KeyActRig.CaptureAsync(() => Program.RefusedAsAgentAsync(rig.Client, rig.Profile(), refusal, Ct));

        Assert.Equal(ExitCode.Unverified, exit);
        Assert.Contains("FOREIGN: a retirement at log index 6 that this profile did not make", stderr, StringComparison.Ordinal);
        Assert.Contains("report it to the Forum's operator", stderr, StringComparison.Ordinal);
        Assert.True(stderr.IndexOf("error:", StringComparison.Ordinal) < stderr.IndexOf("FOREIGN:", StringComparison.Ordinal), "the FOREIGN line follows the refusal");
    }

    [Fact]
    public async Task R4_39_ATokenKeyNotLiveRefusalRunsTheForeignCheck()
    {
        using var rig = KeyActRig.Create();
        rig.Double.Routes["GET /v1/flags"] = (HttpStatusCode.Unauthorized, KeyActDouble.Problem("curia/authn/token-key-not-live", "The token's key is not live"));
        rig.Double.KeySet = KeyActDouble.KeySetJson([Key("a-0000abcd", 1), Key("thief-1", 7)], [0, 1, 7]);

        var listed = await rig.Session.FlagsAsync(null, Ct);
        Assert.True(!listed.TryGetValue(out _, out var refusal));

        var (exit, _, stderr) = await KeyActRig.CaptureAsync(() => Program.RefusedAsAgentAsync(rig.Client, rig.Profile(), refusal!, Ct));

        Assert.Equal(ExitCode.Unverified, exit);
        Assert.Contains("FOREIGN: a rotation at log index 7 that this profile did not make", stderr, StringComparison.Ordinal);
        Assert.Contains("report it to the Forum's operator", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R4_39_ARetiredRefusalRunsTheForeignCheck()
    {
        using var rig = KeyActRig.Create();
        rig.Double.Routes["POST /v1/posts"] = (HttpStatusCode.Forbidden, KeyActDouble.Problem("curia/keys/retired", "The key was retired"));
        rig.Double.KeySet = KeyActDouble.KeySetJson([Key("a-0000abcd", 1, "retired", retiredAt: 6)], [0, 1, 6]);

        var posted = await rig.Session.SubmitAsync("{}"u8.ToArray(), Ct);
        Assert.True(!posted.TryGetValue(out _, out var refusal));
        Assert.Equal("curia/keys/retired", refusal!.Error.Type);

        var (exit, _, stderr) = await KeyActRig.CaptureAsync(() => Program.RefusedAsAgentAsync(rig.Client, rig.Profile(), refusal, Ct));

        Assert.Equal(ExitCode.Unverified, exit);
        Assert.Contains("FOREIGN: a retirement at log index 6 that this profile did not make", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R4_39_ARefusalThatIsNoSignOfAStrangerReadsNoKeySet()
    {
        using var rig = KeyActRig.Create();
        rig.Double.Routes["GET /v1/flags"] = (HttpStatusCode.Forbidden, KeyActDouble.Problem("curia/authz/denied", "Not permitted", "table-10/denied"));
        rig.Double.KeySet = KeyActDouble.KeySetJson([Key("a-0000abcd", 1), Key("thief-1", 7)], [0, 1, 7]);

        var listed = await rig.Session.FlagsAsync(null, Ct);
        Assert.True(!listed.TryGetValue(out _, out var refusal));

        var (exit, _, stderr) = await KeyActRig.CaptureAsync(() => Program.RefusedAsAgentAsync(rig.Client, rig.Profile(), refusal!, Ct));

        Assert.Equal(ExitCode.Denied, exit);
        Assert.DoesNotContain("FOREIGN", stderr, StringComparison.Ordinal);
        Assert.Equal(0, rig.Double.KeySetReads);
    }

    [Fact]
    public async Task R4_39_ARefusalWithNothingForeignKeepsItsOwnExit()
    {
        using var rig = KeyActRig.Create();
        rig.Double.Routes["GET /v1/flags"] = (HttpStatusCode.Unauthorized, KeyActDouble.Problem("curia/authn/token-key-not-live", "The token's key is not live"));

        var listed = await rig.Session.FlagsAsync(null, Ct);
        Assert.True(!listed.TryGetValue(out _, out var refusal));

        var (exit, _, stderr) = await KeyActRig.CaptureAsync(() => Program.RefusedAsAgentAsync(rig.Client, rig.Profile(), refusal!, Ct));

        Assert.Equal(ExitCode.ForumFault, exit);
        Assert.DoesNotContain("FOREIGN", stderr, StringComparison.Ordinal);
        Assert.True(rig.Double.KeySetReads >= 1, "the key set was read");
    }

    [Fact]
    public async Task R4_39_AKeySetReadByAnotherVerbRunsTheForeignCheck()
    {
        using var log = new StubLog { ForeignLifecycleIndex = 3 };
        Assert.True(log.Store.ReadProfile("alice").TryGetValue(out var profile, out _));
        var client = log.Client();
        var post = await client.GetPostAsync(StubLog.PostId, Curia.Domain.Serving.MarkingMode.None, Ct);
        Assert.True(post.TryGetValue(out var served, out _));

        var (exit, _, stderr) = await KeyActRig.CaptureAsync(
            () => Program.RenderAsync(client, [served!], StubLog.Forum, new ForeignWatch(profile!), Ct));

        Assert.Equal(ExitCode.Unverified, exit);
        Assert.Contains("FOREIGN: a lifecycle entry at log index 3 that this profile did not make", stderr, StringComparison.Ordinal);
        Assert.Contains("report it to the Forum's operator", stderr, StringComparison.Ordinal);

        // Without a watch the same read is what it always was.
        var (plain, _, plainError) = await KeyActRig.CaptureAsync(
            () => Program.RenderAsync(client, [served!], StubLog.Forum, null, Ct));
        Assert.DoesNotContain("FOREIGN", plainError, StringComparison.Ordinal);
        Assert.NotEqual(ExitCode.Unverified, plain);
    }

    [Fact]
    public async Task R4_39_AReadOfAnotherAuthorsPostComparesNothing()
    {
        using var log = new StubLog { ForeignLifecycleIndex = 3 };
        var stranger = new AgentProfile("b", "https://agents.example/someone-else", "b-1", "ES256", StubLog.Forum);
        var client = log.Client();
        var post = await client.GetPostAsync(StubLog.PostId, Curia.Domain.Serving.MarkingMode.None, Ct);
        Assert.True(post.TryGetValue(out var served, out _));

        var (_, _, stderr) = await KeyActRig.CaptureAsync(
            () => Program.RenderAsync(client, [served!], StubLog.Forum, new ForeignWatch(stranger), Ct));

        Assert.DoesNotContain("FOREIGN", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R4_39_SearchTitlesReportsAForeignEntryInTheAgentsOwnKeySet()
    {
        using var log = new StubLog { ForeignLifecycleIndex = 3 };
        Assert.True(log.Store.ReadProfile("alice").TryGetValue(out var profile, out _));
        var args = Args.Parse(["search", "x", "--titles", "--agent", "alice"], 1);

        var (exit, _, stderr) = await KeyActRig.CaptureAsync(
            () => Program.SearchPageAsync(args, "x", null, log.Client(), Curia.Domain.Serving.MarkingMode.None, profile, Ct));

        Assert.Equal(ExitCode.Unverified, exit);
        Assert.Contains("FOREIGN: a lifecycle entry at log index 3 that this profile did not make", stderr, StringComparison.Ordinal);

        var (plain, _, plainError) = await KeyActRig.CaptureAsync(
            () => Program.SearchPageAsync(args, "x", null, log.Client(), Curia.Domain.Serving.MarkingMode.None, null, Ct));
        Assert.DoesNotContain("FOREIGN", plainError, StringComparison.Ordinal);
        Assert.NotEqual(ExitCode.Unverified, plain);
    }
}
