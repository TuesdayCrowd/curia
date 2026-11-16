using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Curia.Client.Cli;
using Curia.Canon.Json;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// NHR9 and NHR9b (errata G73, R11.96 (add.), register item 290): <c>curia keys</c> reads the profile's record of an act not in force. The profile is written by hand (<c>identity.json</c> edited as text), so the
/// probes are red by assertion on a tree that ignores <c>in_force</c> and not by the wording of any writer. Driven in process through <c>Program.ShowKeysAsync</c>. Words are read from the errata
/// (<see cref="NotInForceWords"/>).
/// </summary>
[Collection("console")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class NowInForceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string NewKid = "k-new";

    /// <summary>The slug <see cref="KeyActRig.Create"/> gives its profile.</summary>
    private const string Slug = "a";

    /// <summary>
    /// Writes the profile's record of one act by hand. A rotation: the kept key <see cref="NewKid"/> (a key file under <c>keys/</c>) and an act at <paramref name="index"/>; a retirement: the profile signs with
    /// <see cref="NewKid"/> after a recorded rotation at 3, and its retained key <c>a-0000abcd</c> has a retirement recorded at <paramref name="index"/>. <paramref name="notInForce"/> false writes neither new member.
    /// </summary>
    private static void Seed(KeyActRig rig, string kind, long index, bool notInForce)
    {
        var path = Path.Combine(rig.Directory, "identity.json");
        var identity = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var acts = new JsonArray();
        var retained = new JsonArray();

        if (kind == "rotation")
        {
            var file = NotHonouredKit.DerivedFile(NewKid);
            Directory.CreateDirectory(Path.Combine(rig.Directory, "keys"));
            using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            File.WriteAllText(Path.Combine(rig.Directory, file), key.ExportPkcs8PrivateKeyPem());
            var kept = new JsonObject { ["alg"] = "ES256", ["kid"] = NewKid, ["file"] = file };
            var act = new JsonObject { ["kid"] = NewKid, ["kind"] = "rotation", ["log_index"] = index };
            if (notInForce)
            {
                kept["rotation_in_force"] = false;
                act["in_force"] = false;
            }

            retained.Add(kept);
            acts.Add(act);
        }
        else
        {
            var file = NotHonouredKit.DerivedFile(NotHonouredKit.OwnKid);
            identity["kid"] = NewKid;
            retained.Add(new JsonObject { ["alg"] = "ES256", ["kid"] = NotHonouredKit.OwnKid, ["file"] = file });
            acts.Add(new JsonObject { ["kid"] = NewKid, ["kind"] = "rotation", ["log_index"] = 3 });
            var act = new JsonObject { ["kid"] = NotHonouredKit.OwnKid, ["kind"] = "retirement", ["log_index"] = index };
            if (notInForce) act["in_force"] = false;
            acts.Add(act);
        }

        identity["retained_keys"] = retained;
        identity["lifecycle_acts"] = acts;
        File.WriteAllText(path, identity.ToJsonString());
    }

    private static string KeySetFor(string kind, long index) =>
        kind == "rotation"
            ? KeyActDouble.KeySetJson([new KeyActDouble.Key(NotHonouredKit.OwnKid, 1), new KeyActDouble.Key(NewKid, index)], [0, 1, index])
            : KeyActDouble.KeySetJson(
                [new KeyActDouble.Key(NotHonouredKit.OwnKid, 1, "retired", index), new KeyActDouble.Key(NewKid, 3)], [0, 1, 3, index]);

    private static async Task<(int Exit, string Stdout)> KeysAsync(string kind, long index, bool notInForce)
    {
        using var rig = KeyActRig.Create();
        Seed(rig, kind, index, notInForce);
        rig.Double.KeySet = KeySetFor(kind, index);

        var (exit, stdout, _) = await KeyActRig.CaptureAsync(() => Program.ShowKeysAsync(rig.Client, rig.Profile(), Ct));
        return (exit, stdout);
    }

    private static string Words(string kind, long index) => kind == "rotation"
        ? NotInForceWords.Fill(
            NotInForceWords.NowInForceRotation(),
            ("<i>", NotHonouredKit.Index(index)), ("<new kid>", NotHonouredKit.Kid(NewKid)), ("<kid>", NotHonouredKit.Kid(NotHonouredKit.OwnKid)),
            ("<the retire command for the new kid>", Hints.Retire(Slug, NewKid).Text))
        : NotInForceWords.Fill(
            NotInForceWords.NowInForceRetirement(),
            ("<i>", NotHonouredKit.Index(index)), ("<kid>", NotHonouredKit.Kid(NotHonouredKit.OwnKid)));

    private static string Summary(string kind, long index, int exit, string joined)
    {
        var stdout = joined;
        return $"exit {exit}; {(PublishedClientWords.Says(stdout, Words(kind, index)) ? "the NOW IN FORCE line" : "no NOW IN FORCE line")}; "
            + (stdout.Contains("FOREIGN", StringComparison.Ordinal) ? "a FOREIGN line" : "no FOREIGN line");
    }

    /// <summary>
    /// NHR9: <c>NOW IN FORCE</c> reads the state. (a) a rotation recorded not in force at 7 for the kept key, the key set binds that key active at 7: the line, no FOREIGN, exit 6. (b) the same for a retirement
    /// (the old key retired at 9). (c) the control, in the same test: the same key set and the same act with no <c>in_force</c> member: exit 0 and no line, which shows the member is what is read. Red on the tree:
    /// <c>Expected: ... exit 6 ...  Actual: ... exit 0 ...</c> for (a) and (b); (c) is green on both.
    /// </summary>
    [Fact]
    public async Task NHR9_NowInForceReadsTheStateOfTheAct()
    {
        var (a, aOut) = await KeysAsync("rotation", 7, notInForce: true);
        var (b, bOut) = await KeysAsync("retirement", 9, notInForce: true);
        var (c, cOut) = await KeysAsync("rotation", 7, notInForce: false);

        var actual = $"(a) {Summary("rotation", 7, a, aOut)} | (b) {Summary("retirement", 9, b, bOut)} | (c) {Summary("rotation", 7, c, cOut)}";
        const string expected = "(a) exit 6; the NOW IN FORCE line; no FOREIGN line | (b) exit 6; the NOW IN FORCE line; no FOREIGN line | (c) exit 0; no NOW IN FORCE line; no FOREIGN line";

        Assert.True(
            expected == actual,
            $"Expected: {expected}  Actual: {actual}\n--- the words, as read from the errata and filled ---\n(a) {Words("rotation", 7)}\n(b) {Words("retirement", 9)}");
    }

    /// <summary>
    /// NHR9b: a recorded index does not hide a stranger. A rotation recorded not in force at 7 for the kept key; the key set binds another key at 7: a FOREIGN line at 7, the act MISSING (R11.96 (add.): "FOREIGN and
    /// the act MISSING, as for an act in force"), no <c>NOW IN FORCE</c>, exit 6. Red on the tree: the act counts as recorded at 7, so the report is clean.
    /// </summary>
    [Fact]
    public async Task NHR9b_ARecordedIndexForAnotherKeyIsForeignAndTheActMissing()
    {
        using var rig = KeyActRig.Create();
        Seed(rig, "rotation", 7, notInForce: true);
        rig.Double.KeySet = KeyActDouble.KeySetJson([new KeyActDouble.Key(NotHonouredKit.OwnKid, 1), new KeyActDouble.Key("thief-1", 7)], [0, 1, 7]);

        var (exit, stdout, _) = await KeyActRig.CaptureAsync(() => Program.ShowKeysAsync(rig.Client, rig.Profile(), Ct));

        var actual = $"exit {exit}; "
            + (stdout.Contains("FOREIGN: a rotation at log index 7 that this profile did not make", StringComparison.Ordinal) ? "a FOREIGN line at 7" : "no FOREIGN line at 7") + "; "
            + (stdout.Contains("MISSING: this profile's rotation at log index 7", StringComparison.Ordinal) ? "the act MISSING" : "the act not MISSING") + "; "
            + (stdout.Contains("NOW IN FORCE", StringComparison.Ordinal) ? "a NOW IN FORCE line" : "no NOW IN FORCE line");

        WholeText.Same("exit 6; a FOREIGN line at 7; the act MISSING; no NOW IN FORCE line", actual);
    }
}

/// <summary>
/// NHR8 (errata G73, R11.98, spec section 4.12): the profile format. Rows: a round trip of a not-in-force act and a kept key; two goldens (identity files a profile with neither member renders to, captured from the
/// renderer before the members existed, <c>Golden/</c>); <c>in_force</c> and <c>rotation_in_force</c> of <c>"no"</c>, <c>0</c> and <c>null</c> are each a malformed profile naming the member; and an absent member reads
/// as in force. Red on the seam: the round trip and the six malformed rows (the members are neither rendered nor read); the goldens and the absent row are green by design.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class NotInForceProfileTests : IDisposable
{
    private static readonly Uri Forum = new("https://forum.example/");

    private readonly string _home = Directory.CreateTempSubdirectory("curia-nhr8-").FullName;

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private ProfileStore Store => new(_home);

    private AgentProfile Created(string slug)
    {
        Assert.True(Store.Create(slug, "https://agents.example/golden", "g-0000abcd", Forum).TryGetValue(out var agent, out var error), error?.Title);
        var profile = agent!.Profile;
        agent.Dispose();
        return profile;
    }

    private static string Golden(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curia.sln"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Curia.sln not found"), "tests", "Curia.Client.Tests", "Golden", name));
    }

    private string Identity(string slug) => File.ReadAllText(Path.Combine(Store.DirectoryFor(slug), "identity.json"));

    [Theory]
    [InlineData("round-trip", null, null)]
    [InlineData("golden", "retained-only", null)]
    [InlineData("golden", "acts-and-retained", null)]
    [InlineData("malformed", "in_force", "\"no\"")]
    [InlineData("malformed", "in_force", "0")]
    [InlineData("malformed", "in_force", "null")]
    [InlineData("malformed", "rotation_in_force", "\"no\"")]
    [InlineData("malformed", "rotation_in_force", "0")]
    [InlineData("malformed", "rotation_in_force", "null")]
    [InlineData("absent", null, null)]
    public void NHR8_TheProfileFormat(string row, string? what, string? raw)
    {
        switch (row)
        {
            case "round-trip": RoundTrip(); break;
            case "golden": Rendered(what!); break;
            case "malformed": Malformed(what!, raw!); break;
            default: Absent(); break;
        }
    }

    private static string Key(string kid) => NotHonouredKit.DerivedFile(kid);

    private void RoundTrip()
    {
        var profile = Created("trip");
        Store.RecordEnrollment(
            profile with
            {
                RetainedKeys = [new RetainedKey("k-new", "ES256", Key("k-new"), RotationInForce: false)],
                LifecycleActs = [new LifecycleAct(LifecycleAct.Rotation, "k-new", 7, InForce: false)],
            },
            "2026-01-01T00:00:00.0000000+00:00");

        Assert.True(Store.ReadProfile("trip").TryGetValue(out var read, out var error), error?.Detail);
        var actual = $"act in_force={Assert.Single(read!.Acts).InForce}; kept key rotation_in_force={Assert.Single(read.Retained).RotationInForce}";

        WholeText.Same("act in_force=False; kept key rotation_in_force=False", actual);
    }

    private void Rendered(string which)
    {
        var profile = Created(which);
        var shaped = which == "retained-only"
            ? profile with { RetainedKeys = [new RetainedKey("g-old-1", "ES256", Key("g-old-1"))] }
            : profile with
            {
                RetainedKeys = [new RetainedKey("g-old-1", "ES256", Key("g-old-1"), Retired: true), new RetainedKey("g-old-2", "ES256", Signer: "signer-command --kid g-old-2")],
                LifecycleActs = [new LifecycleAct(LifecycleAct.Rotation, "g-0000abcd", 3), new LifecycleAct(LifecycleAct.Retirement, "g-old-1", 5)],
            };
        Store.RecordEnrollment(shaped, "2026-01-01T00:00:00.0000000+00:00");

        WholeText.Same(Golden($"identity-{which}.json"), Identity(which));
    }

    private void Malformed(string member, string raw)
    {
        var profile = Created("bad");
        Store.RecordEnrollment(profile, "2026-01-01T00:00:00.0000000+00:00");
        var path = Path.Combine(Store.DirectoryFor("bad"), "identity.json");
        var identity = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var value = JsonNode.Parse(raw);
        if (member == "in_force")
        {
            identity["lifecycle_acts"] = new JsonArray(new JsonObject { ["kid"] = "k-new", ["kind"] = "rotation", ["log_index"] = 7, ["in_force"] = value });
        }
        else
        {
            identity["retained_keys"] = new JsonArray(new JsonObject { ["alg"] = "ES256", ["kid"] = "k-new", ["file"] = Key("k-new"), ["rotation_in_force"] = value });
        }

        File.WriteAllText(path, identity.ToJsonString());

        var read = Store.ReadProfile("bad");
        var actual = read.TryGetValue(out _, out var error)
            ? "the profile reads"
            : $"{error!.Type} {error.Detail}";

        Assert.True(
            actual.StartsWith("curia/client/malformed-profile ", StringComparison.Ordinal) && actual.Contains(member, StringComparison.Ordinal),
            $"Expected: curia/client/malformed-profile naming {member} (value {raw})  Actual: {actual}");
    }

    private void Absent()
    {
        var profile = Created("absent");
        Store.RecordEnrollment(
            profile with { RetainedKeys = [new RetainedKey("k-new", "ES256", Key("k-new"))], LifecycleActs = [new LifecycleAct(LifecycleAct.Rotation, "k-new", 7)] },
            "2026-01-01T00:00:00.0000000+00:00");

        Assert.True(Store.ReadProfile("absent").TryGetValue(out var read, out var error), error?.Detail);
        WholeText.Same(
            "act in_force=True; kept key rotation_in_force=True",
            $"act in_force={Assert.Single(read!.Acts).InForce}; kept key rotation_in_force={Assert.Single(read.Retained).RotationInForce}");
    }
}

/// <summary>
/// NHR13 (errata G73, R11.98): the help says it. A weak probe, and said to be: <c>curia help rotate</c>, <c>curia help retire</c> and the exit-6 paragraph of the usage text each hold the phrase
/// <c>not in force</c>. The full clause is compared in Task 2 against the text the plan's As-run records; the cold read is its real check. Red on the tree: <c>Expected: ... Actual: ... absent</c>.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class NotInForceHelpTests
{
    [Fact]
    public void NHR13_TheHelpOfRotateRetireAndExitSixSaysNotInForce()
    {
        var exitSix = Regex.Match(Help.Usage, @"^ *6  .*?(?=^ *7  )", RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        Assert.True(exitSix.Success, "the usage text holds no exit-6 paragraph");

        string Says(string? text) => text is not null && text.Contains("not in force", StringComparison.Ordinal) ? "says not in force" : "absent";
        var actual = $"help rotate {Says(Help.For("rotate"))}; help retire {Says(Help.For("retire"))}; the exit-6 paragraph {Says(exitSix.Value)}";

        WholeText.Same("help rotate says not in force; help retire says not in force; the exit-6 paragraph says not in force", actual);
    }
}
