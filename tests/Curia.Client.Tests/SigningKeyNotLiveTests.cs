using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Curia.Client.Cli;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>A key a probe's profile holds: its kid and its PEM private half, the one source of both the key files the profile is seeded with and the public half the key set serves.</summary>
internal sealed record HeldKey(string Kid, string Pem)
{
    internal static HeldKey New(string kid)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new HeldKey(kid, key.ExportPkcs8PrivateKeyPem());
    }

    /// <summary>The key as the double's key set serves it: bound at <paramref name="logIndex"/> with this key's own public half.</summary>
    internal KeyActDouble.Key Served(long logIndex, string status = "active", long? retiredAt = null) => KeyActDouble.Key.Bound(Kid, logIndex, Pem, status, retiredAt);
}

/// <summary>
/// What the UDO probes of register item 323 (errata G75) share: the profile of the lockout written by hand (the keys, the kept files and the acts as <c>identity.json</c> holds them), the Forum the double plays, the real
/// <c>curia</c> command run in process over the double, and the readings of the profile on disk that do not go through the code a probe is about. A probe's oracle for what the Forum binds and for what was sent is the
/// double's scripted key set and its request log, never the profile.
/// </summary>
internal static class SigningKeyNotLiveKit
{
    internal const string Slug = "a";

    /// <summary>Writes the profile: the key it signs with, the keys it keeps (each with whether its rotation is in force) and the acts it recorded. Every key file is written at 0600.</summary>
    internal static void Seed(
        KeyActRig rig,
        HeldKey signing,
        IEnumerable<(HeldKey Key, bool RotationInForce)> retained,
        IEnumerable<(string Kind, string Kid, long Index, bool InForce)> acts)
    {
        var path = Path.Combine(rig.Directory, "identity.json");
        var identity = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        identity["kid"] = signing.Kid;
        File.WriteAllText(Path.Combine(rig.Directory, "signing-key.pem"), signing.Pem);

        var keys = new JsonArray();
        foreach (var (key, rotationInForce) in retained)
        {
            var file = NotHonouredKit.DerivedFile(key.Kid);
            Directory.CreateDirectory(Path.Combine(rig.Directory, "keys"));
            var onDisk = Path.Combine(rig.Directory, file);
            File.WriteAllText(onDisk, key.Pem);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(onDisk, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            var entry = new JsonObject { ["alg"] = "ES256", ["kid"] = key.Kid, ["file"] = file };
            if (!rotationInForce) entry["rotation_in_force"] = false;
            keys.Add(entry);
        }

        var recorded = new JsonArray();
        foreach (var (kind, kid, index, inForce) in acts)
        {
            var act = new JsonObject { ["kid"] = kid, ["kind"] = kind, ["log_index"] = index };
            if (!inForce) act["in_force"] = false;
            recorded.Add(act);
        }

        identity["retained_keys"] = keys;
        identity["lifecycle_acts"] = recorded;
        File.WriteAllText(path, identity.ToJsonString());
    }

    /// <summary>
    /// The scene Task 0 produced for real: A (a rotation to K1 at 2) not in force and its key kept; B (a rotation to K2 at 3) in force and the key the profile signs with; K0 retained and live. The key set lists K0 and K1
    /// <c>active</c> with their public halves, does not list K2, and says row 3 is skipped (<c>broken-link</c>); the lifecycle holds 2.
    /// </summary>
    internal static (HeldKey K0, HeldKey K1, HeldKey K2) Lockout(KeyActRig rig)
    {
        var (k0, k1, k2) = (HeldKey.New("alice-k0"), HeldKey.New("alice-k1"), HeldKey.New("alice-k2"));
        Seed(rig, k2, [(k0, true), (k1, false)], [("rotation", k1.Kid, 2, false), ("rotation", k2.Kid, 3, true)]);
        rig.Double.KeySet = KeyActDouble.KeySetJson([k0.Served(1), k1.Served(2)], [0, 1, 2], skipped: [(3, "agent.key-bound", "broken-link")]);
        return (k0, k1, k2);
    }

    /// <summary>The wire's answer to a token request for a key the Forum does not serve live.</summary>
    internal static readonly (HttpStatusCode, string) InvalidClient =
        (HttpStatusCode.Unauthorized, """{"error":"invalid_client","error_description":"The client could not be authenticated"}""");

    /// <summary>Runs the real <c>curia</c> command in process over the rig's double and profile store. The caller is in the <c>console</c> collection: the streams, the environment and the client factory are process-wide.</summary>
    internal static async Task<(int Exit, string Stdout, string Stderr)> CliAsync(KeyActRig rig, params string[] args)
    {
        ArgumentNullException.ThrowIfNull(rig);
        var savedHome = Environment.GetEnvironmentVariable("CURIA_CLIENT_HOME");
        var savedFactory = Program.HttpClientFactory;
        try
        {
            Environment.SetEnvironmentVariable("CURIA_CLIENT_HOME", rig.Home);
            Program.HttpClientFactory = forum => new HttpClient(new NonDisposing(rig.Double)) { BaseAddress = forum, Timeout = TimeSpan.FromSeconds(30) };
            return await ConsoleCapture.CaptureAsync(() => Program.RunAsync(args)).ConfigureAwait(false);
        }
        finally
        {
            Program.HttpClientFactory = savedFactory;
            Environment.SetEnvironmentVariable("CURIA_CLIENT_HOME", savedHome);
        }
    }

    /// <summary>The double outlives each command's client, which disposes its handler.</summary>
    private sealed class NonDisposing(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override void Dispose(bool disposing) => base.Dispose(false);
    }

    /// <summary>Every file under the profile's directory, by relative path and the sha256 of its bytes: what "byte-identical" means for a verb that must change nothing.</summary>
    internal static string Tree(KeyActRig rig) =>
        string.Join("|", Directory.EnumerateFiles(rig.Directory, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(rig.Directory, f).Replace('\\', '/') + "=" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f))))
            .Order(StringComparer.Ordinal));

    internal static string Kid(string kid) => NotHonouredKit.Kid(kid);

    /// <summary>The use-key command for a kid, as the line prints it: the retire command's form for an agent and a kid, with the verb the errata names.</summary>
    internal static string UseKeyCommand(string kid) => Hints.Retire(Slug, kid).Text.Replace("curia retire", "curia use-key", StringComparison.Ordinal);

    private static string Once(string template, string placeholder, string value)
    {
        var at = template.IndexOf(placeholder, StringComparison.Ordinal);
        if (at < 0 || template.IndexOf(placeholder, at + 1, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException($"the published words do not hold {placeholder} exactly once: {template}");

        return template.Replace(placeholder, value, StringComparison.Ordinal);
    }

    /// <summary>What the line says of the kid: the key set does not list it, or lists it as a status (a display literal).</summary>
    internal static string Standing(string? status) => status is null ? "does not list" : "lists as " + Kid(status);

    /// <summary>The line for a profile that holds keys the Forum serves, filled from the errata's words; <paramref name="held"/> is each held key's kid and the log index the key set binds it at, in the key set's order.</summary>
    internal static string HeldLine(string signing, string? status, params (string Kid, long Index)[] held)
    {
        var list = string.Join(", ", held.Select(h => $"{Kid(h.Kid)} (bound at log index {h.Index.ToString(CultureInfo.InvariantCulture)})"));
        var line = SigningKeyNotLiveWords.HeldForm();
        line = Once(line, "<held keys>", list);
        line = Once(line, "<does not list | lists as <status>>", Standing(status));
        line = Once(line, "<n>", held.Length.ToString(CultureInfo.InvariantCulture));
        line = Once(line, "<the use-key command for the key bound last>", UseKeyCommand(held.MaxBy(h => h.Index).Kid));
        return Once(line, "<kid>", Kid(signing));
    }

    /// <summary>What <c>use-key</c> prints for <paramref name="adopted"/> when the profile holds <paramref name="others"/> (kids, in the key set's order) the Forum serves live: the plain result, then the errata's second form filled (the agent a shell word; <c>&lt;that kid&gt;</c> left).</summary>
    internal static string ResultWithOthers(string adopted, params string[] others)
    {
        var second = SigningKeyNotLiveWords.UseKeyResultWithOthers();
        second = Once(second, "<other keys>", string.Join(", ", others.Select(Kid)));
        var retire = Hints.Retire(Slug, "x").Text;   // curia retire --agent <word> --kid <word>: the agent's shell word as the client prints it
        const string From = "--agent ";
        var word = retire[(retire.IndexOf(From, StringComparison.Ordinal) + From.Length)..retire.IndexOf(" --kid ", StringComparison.Ordinal)];
        second = Once(second, "<slug>", word);
        return PlainResult(adopted) + " " + second;
    }

    /// <summary>What <c>use-key</c> prints when no other held key is live.</summary>
    internal static string PlainResult(string adopted) => SigningKeyNotLiveWords.UseKeyResult().Replace("<kid>", Kid(adopted), StringComparison.Ordinal);

    /// <summary>The command the held-keys line prints, read between its <c>(the highest log index): </c> and <c>. That command reads</c> in the output of <c>curia keys</c>; a marker for a line that is not there.</summary>
    internal static async Task<string> PrintedCommandAsync(KeyActRig rig)
    {
        var (_, keys, _) = await CliAsync(rig, "keys", "--agent", Slug).ConfigureAwait(false);
        const string Open = "(the highest log index): ";
        const string Close = ". That command reads";
        var from = keys.IndexOf(Open, StringComparison.Ordinal);
        var to = from < 0 ? -1 : keys.IndexOf(Close, from, StringComparison.Ordinal);
        return from >= 0 && to > from ? keys[(from + Open.Length)..to].Trim() : "<the held-keys line is not in the output>";
    }

    /// <summary>The line for a profile whose held keys the Forum serves none of.</summary>
    internal static string NoHeldLine(string signing, string? status)
    {
        var line = SigningKeyNotLiveWords.NoHeldForm();
        line = Once(line, "<does not list | lists as <status>>", Standing(status));
        line = Once(line, "<agent id>", Kid(KeyActRig.AgentId));
        return Once(line, "<kid>", Kid(signing));
    }

    /// <summary>The rotation <c>NOW IN FORCE</c> line while the signing key is not live.</summary>
    internal static string RotationLineWhileNotLive(string newKid, long index)
    {
        var line = SigningKeyNotLiveWords.RotationLineWhileNotLive();
        line = Once(line, "<i>", index.ToString(CultureInfo.InvariantCulture));
        return Once(line, "<new kid>", Kid(newKid));
    }

    /// <summary>The rotation <c>NOW IN FORCE</c> line as G73 words it (the signing key is live), for the signing kid <paramref name="signing"/>.</summary>
    internal static string OriginalRotationLine(string signing, string newKid, long index) =>
        NotInForceWords.Fill(
            NotInForceWords.NowInForceRotation(),
            ("<i>", NotHonouredKit.Index(index)), ("<new kid>", Kid(newKid)), ("<kid>", Kid(signing)),
            ("<the retire command for the new kid>", Hints.Retire(Slug, newKid).Text));

    internal static string Mode(string path) => OperatingSystem.IsWindows() ? "600" : Convert.ToString((int)File.GetUnixFileMode(path) & 0x1FF, 8);

    /// <summary>
    /// The line's own count: the number of times the line itself is written, which begins <c>SIGNING KEY NOT LIVE:</c>. The errata's rotation line, while the signing key is not live, ends by naming the line ("see SIGNING KEY NOT LIVE
    /// below"), so the bare words would count that reference as a second line; the colon is what the line has and the reference does not (Task 2: the count restated, the assertion that it is written once unchanged).
    /// </summary>
    internal static int Mentions(string text) => System.Text.RegularExpressions.Regex.Count(text, "SIGNING KEY NOT LIVE:", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Fails with the whole of both texts and the output the verb wrote, so a red probe's text is what the record keeps.</summary>
    internal static void Red(string expected, string actual, string output) =>
        Assert.True(string.Equals(expected, actual, StringComparison.Ordinal), $"Expected: {expected}  Actual: {actual}\n--- output ---\n{output.Trim()}");
}

/// <summary>
/// UDO1 to UDO3b (errata G75, R11.101, R11.96 (add.) (rev.), register item 323): what <c>curia keys</c> says when the key the profile signs with is not one the Forum serves live. Driven as the command runs, in process,
/// over <see cref="KeyActDouble"/>. Every word is read from the errata (<see cref="SigningKeyNotLiveWords"/>). The oracle for "the Forum binds it" is the double's scripted key set, and for "nothing was changed" the bytes
/// of every file under the profile.
/// </summary>
[Collection("console")]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Each rig is a using declaration; the analyzer loses the ownership across the awaits.")]
public sealed class SigningKeyNotLiveKeysTests
{
    /// <summary>
    /// UDO1: the scene. <c>curia keys</c> prints the held-keys form naming K2 (not listed), listing K0 and K1 and not K2, once, after the SKIPPED lines and before the lifecycle-kinds line; exits 6; changes no byte of the
    /// profile; and the rotation <c>NOW IN FORCE</c> line carries R11.96 (add.) (rev.)'s tail and does not offer to end the key and rotate again. Red on the tree: the base prints the SKIPPED line and the NOW IN FORCE line with
    /// "end this one and rotate again" and no word that the signing key is not live.
    /// </summary>
    [Fact]
    public async Task UDO1_TheScenePrintsTheHeldKeysFormAndChangesNothing()
    {
        using var rig = KeyActRig.Create();
        var (k0, k1, k2) = SigningKeyNotLiveKit.Lockout(rig);
        var before = SigningKeyNotLiveKit.Tree(rig);

        var (exit, stdout, stderr) = await SigningKeyNotLiveKit.CliAsync(rig, "keys", "--agent", SigningKeyNotLiveKit.Slug);

        var line = SigningKeyNotLiveKit.HeldLine(k2.Kid, null, (k0.Kid, 1), (k1.Kid, 2));
        var rotation = SigningKeyNotLiveKit.RotationLineWhileNotLive(k1.Kid, 2);
        var folded = PublishedClientWords.Fold(stdout);
        var at = folded.IndexOf(PublishedClientWords.Fold(line), StringComparison.Ordinal);
        var ordered = at >= 0 && at > folded.LastIndexOf("SKIPPED:", StringComparison.Ordinal) && at < folded.IndexOf("lifecycle kinds this Forum enables", StringComparison.Ordinal);
        var actual = $"exit {exit}; the profile is {(before == SigningKeyNotLiveKit.Tree(rig) ? "byte-identical" : "CHANGED")}; "
            + $"the held-keys line: {(at >= 0 ? "said" : "not said")}, written {SigningKeyNotLiveKit.Mentions(stdout)} time(s), {(ordered ? "after the SKIPPED lines and before the lifecycle-kinds line" : "not in its place")}; "
            + $"the rotation line with the tail: {(PublishedClientWords.Says(stdout, rotation) ? "said" : "not said")}; offers to rotate again: {(stdout.Contains("end this one and rotate again", StringComparison.Ordinal) ? "yes" : "no")}";
        var expected = "exit 6; the profile is byte-identical; the held-keys line: said, written 1 time(s), after the SKIPPED lines and before the lifecycle-kinds line; the rotation line with the tail: said; offers to rotate again: no";

        SigningKeyNotLiveKit.Red(expected, actual, stdout + "\n" + stderr + "\n--- the line, from the errata ---\n" + line + "\n" + rotation);
    }

    /// <summary>
    /// UDO1b: the signing key is listed <c>retired</c> by a stranger's act. FOREIGN stands beside the line, no SKIPPED line is written (the key set names no skipped row), and the line names <c>retired</c> and lists K0, the
    /// one key the profile holds that the Forum serves. The line stands without a SKIPPED line: it is decided from the signing kid and <c>keys[]</c> alone (Amendment 1 ruling 5).
    /// </summary>
    [Fact]
    public async Task UDO1b_ARetiredSigningKeyIsNamedBesideForeignAndNoSkippedLine()
    {
        using var rig = KeyActRig.Create();
        var (k0, k1) = (HeldKey.New("alice-k0"), HeldKey.New("alice-k1"));
        SigningKeyNotLiveKit.Seed(rig, k1, [(k0, true)], [("rotation", k1.Kid, 2, true)]);
        rig.Double.KeySet = KeyActDouble.KeySetJson([k0.Served(1), k1.Served(2, "retired", retiredAt: 3)], [0, 1, 2, 3], skipped: []);

        var (exit, stdout, stderr) = await SigningKeyNotLiveKit.CliAsync(rig, "keys", "--agent", SigningKeyNotLiveKit.Slug);

        var line = SigningKeyNotLiveKit.HeldLine(k1.Kid, "retired", (k0.Kid, 1));
        var actual = $"exit {exit}; the line naming retired: {(PublishedClientWords.Says(stdout, line) ? "said" : "not said")}, written {SigningKeyNotLiveKit.Mentions(stdout)} time(s); "
            + $"FOREIGN: {(stdout.Contains("FOREIGN:", StringComparison.Ordinal) ? "present" : "absent")}; SKIPPED line: {(stdout.Contains("SKIPPED:", StringComparison.Ordinal) ? "present" : "absent")}";
        var expected = "exit 6; the line naming retired: said, written 1 time(s); FOREIGN: present; SKIPPED line: absent";

        SigningKeyNotLiveKit.Red(expected, actual, stdout + "\n" + stderr + "\n--- the line, from the errata ---\n" + line);
    }

    /// <summary>UDO1c: the signing key is listed <c>compromised</c>: the line names <c>compromised</c>, once, and the exit is 6.</summary>
    [Fact]
    public async Task UDO1c_ACompromisedSigningKeyIsNamedAsCompromised()
    {
        using var rig = KeyActRig.Create();
        var (k0, k1) = (HeldKey.New("alice-k0"), HeldKey.New("alice-k1"));
        SigningKeyNotLiveKit.Seed(rig, k1, [(k0, true)], [("rotation", k1.Kid, 2, true)]);
        rig.Double.KeySet = KeyActDouble.KeySetJson([k0.Served(1), k1.Served(2, "compromised")], [0, 1, 2], skipped: []);

        var (exit, stdout, stderr) = await SigningKeyNotLiveKit.CliAsync(rig, "keys", "--agent", SigningKeyNotLiveKit.Slug);

        var line = SigningKeyNotLiveKit.HeldLine(k1.Kid, "compromised", (k0.Kid, 1));
        var actual = $"exit {exit}; the line naming compromised: {(PublishedClientWords.Says(stdout, line) ? "said" : "not said")}, written {SigningKeyNotLiveKit.Mentions(stdout)} time(s)";
        var expected = "exit 6; the line naming compromised: said, written 1 time(s)";

        SigningKeyNotLiveKit.Red(expected, actual, stdout + "\n" + stderr + "\n--- the line, from the errata ---\n" + line);
    }

    /// <summary>
    /// UDO1d: the key set lists no key at all: the Forum binds nothing this profile holds, and the no-held-key form says so ("do not enrol again", report to the operator). Exit 6: a profile with no key to sign with is a
    /// finding, not a comparison that could not be made.
    /// </summary>
    [Fact]
    public async Task UDO1d_AnEmptyKeySetPrintsTheNoHeldKeyForm()
    {
        using var rig = KeyActRig.Create();
        var (k0, k1) = (HeldKey.New("alice-k0"), HeldKey.New("alice-k1"));
        SigningKeyNotLiveKit.Seed(rig, k1, [(k0, true)], [("rotation", k1.Kid, 2, true)]);
        rig.Double.KeySet = KeyActDouble.KeySetJson([], [0, 1], skipped: []);

        var (exit, stdout, stderr) = await SigningKeyNotLiveKit.CliAsync(rig, "keys", "--agent", SigningKeyNotLiveKit.Slug);

        var line = SigningKeyNotLiveKit.NoHeldLine(k1.Kid, null);
        var actual = $"exit {exit}; the no-held-key line: {(PublishedClientWords.Says(stdout, line) ? "said" : "not said")}, written {SigningKeyNotLiveKit.Mentions(stdout)} time(s)";
        var expected = "exit 6; the no-held-key line: said, written 1 time(s)";

        SigningKeyNotLiveKit.Red(expected, actual, stdout + "\n" + stderr + "\n--- the line, from the errata ---\n" + line);
    }

    /// <summary>
    /// UDO1e (non-property (f)): the key set serves K0 with its own public half and serves K1's kid <c>active</c> with a stranger's public half, K2 absent. The line lists exactly K0 (<c>&lt;n&gt;</c> is 1) and does not
    /// name K1, because a kid alone could be a stranger's. Exit 6.
    /// </summary>
    [Fact]
    public async Task UDO1e_AHeldKidServedWithAnotherPublicHalfIsNotInTheHeldList()
    {
        using var rig = KeyActRig.Create();
        var (k0, k1, _) = SigningKeyNotLiveKit.Lockout(rig);
        var stranger = HeldKey.New(k1.Kid);
        rig.Double.KeySet = KeyActDouble.KeySetJson([k0.Served(1), stranger.Served(2)], [0, 1, 2], skipped: [(3, "agent.key-bound", "broken-link")]);

        var (exit, stdout, stderr) = await SigningKeyNotLiveKit.CliAsync(rig, "keys", "--agent", SigningKeyNotLiveKit.Slug);

        var line = SigningKeyNotLiveKit.HeldLine("alice-k2", null, (k0.Kid, 1));
        var naming = PublishedClientWords.Fold($"{SigningKeyNotLiveKit.Kid(k1.Kid)} (bound at log index 2)");
        var lines = stdout.Split('\n').Where(l => l.Contains("SIGNING KEY NOT LIVE", StringComparison.Ordinal)).ToArray();
        var actual = $"exit {exit}; the line with exactly K0 listed: {(PublishedClientWords.Says(stdout, line) ? "said" : "not said")}, written {SigningKeyNotLiveKit.Mentions(stdout)} time(s); "
            + $"K1 named in the held list: {(lines.Length == 1 && PublishedClientWords.Fold(lines[0]).Contains(naming, StringComparison.Ordinal) ? "yes" : "no")}";
        var expected = "exit 6; the line with exactly K0 listed: said, written 1 time(s); K1 named in the held list: no";

        SigningKeyNotLiveKit.Red(expected, actual, stdout + "\n" + stderr + "\n--- the line, from the errata ---\n" + line);
    }

    /// <summary>
    /// UDO15 (Amendment 5 ruling 1): the held-form line's command names the held key with the greatest log index, not a position. Three held keys the key set serves live: K0 (index 1), K1 (index 2) and a key the key
    /// set gives no log index (<c>Sorted</c> puts it last), with <c>keys[]</c> served as K1, the index-less key, K0. The printed command names K1. Controls in the one probe: the same scene without the index-less key
    /// names K1, and the single-held scene names its one key. Red against <c>heldKeys[0]</c> (K0) and against the last of <c>Sorted</c> (the index-less key).
    /// </summary>
    [Fact]
    public async Task UDO15_TheCommandNamesTheKeyBoundLastNotAPosition()
    {
        using var rig = KeyActRig.Create();
        var (k0, k1, k2) = SigningKeyNotLiveKit.Lockout(rig);
        var kx = HeldKey.New("alice-kx");
        SigningKeyNotLiveKit.Seed(rig, k2, [(k0, true), (k1, false), (kx, true)], [("rotation", k1.Kid, 2, false), ("rotation", k2.Kid, 3, true)]);
        var noIndex = kx.Served(1) with { LogIndex = null };
        rig.Double.KeySet = KeyActDouble.KeySetJson([k1.Served(2), noIndex, k0.Served(1)], [0, 1, 2], skipped: [(3, "agent.key-bound", "broken-link")]);
        var three = await SigningKeyNotLiveKit.PrintedCommandAsync(rig);

        rig.Double.KeySet = KeyActDouble.KeySetJson([k1.Served(2), k0.Served(1)], [0, 1, 2], skipped: [(3, "agent.key-bound", "broken-link")]);
        var two = await SigningKeyNotLiveKit.PrintedCommandAsync(rig);

        using var single = KeyActRig.Create();
        var (s0, s1) = (HeldKey.New("alice-k0"), HeldKey.New("alice-k1"));
        SigningKeyNotLiveKit.Seed(single, s1, [(s0, true)], [("rotation", s1.Kid, 2, true)]);
        single.Double.KeySet = KeyActDouble.KeySetJson([s0.Served(1), s1.Served(2, "retired", retiredAt: 3)], [0, 1, 2, 3], skipped: []);
        var one = await SigningKeyNotLiveKit.PrintedCommandAsync(single);

        string Names(string command) =>
            command == SigningKeyNotLiveKit.UseKeyCommand(k0.Kid) ? "K0"
            : command == SigningKeyNotLiveKit.UseKeyCommand(k1.Kid) ? "K1"
            : command == SigningKeyNotLiveKit.UseKeyCommand(kx.Kid) ? "the index-less key"
            : $"<{command}>";
        var actual = $"three held keys (K1, index-less, K0 served): names {Names(three)}; without the index-less key: names {Names(two)}; the single-held scene: names {Names(one)}";
        SigningKeyNotLiveKit.Red("three held keys (K1, index-less, K0 served): names K1; without the index-less key: names K1; the single-held scene: names K0", actual, "the three-key scene's command: " + three);
    }

    /// <summary>
    /// UDO3 (control, the vacuity row): the signing key is listed <c>active</c> and K1 is kept (G73's scene): no line, exit 6 and G73's words unedited. Green on the tree for the wrong reason: the line does not exist, so it
    /// is not printed. It carries information only through FUDO11 (the detection ignores the signing key's standing and prints the line whenever a retained key's rotation is not in force); FUDO12 carries UDO3b, not this probe.
    /// </summary>
    [Fact]
    public async Task UDO3_AnActiveSigningKeyPrintsNoLineAndG73sWordsStand_Control()
    {
        using var rig = KeyActRig.Create();
        var (k0, k1) = (HeldKey.New("alice-k0"), HeldKey.New("alice-k1"));
        SigningKeyNotLiveKit.Seed(rig, k0, [(k1, false)], [("rotation", k1.Kid, 2, false)]);
        rig.Double.KeySet = KeyActDouble.KeySetJson([k0.Served(1), k1.Served(2)], [0, 1, 2], skipped: []);

        var (exit, stdout, stderr) = await SigningKeyNotLiveKit.CliAsync(rig, "keys", "--agent", SigningKeyNotLiveKit.Slug);

        var original = SigningKeyNotLiveKit.OriginalRotationLine(k0.Kid, k1.Kid, 2);
        var actual = $"exit {exit}; the line written {SigningKeyNotLiveKit.Mentions(stdout + stderr)} time(s); G73's words: {(PublishedClientWords.Says(stdout, original) ? "said" : "not said")}";
        var expected = "exit 6; the line written 0 time(s); G73's words: said";

        SigningKeyNotLiveKit.Red(expected, actual, stdout + "\n" + stderr);
    }

    /// <summary>UDO3b (control): the key set cannot be read: no line (no key set, no claim), exit 8 as today.</summary>
    [Fact]
    public async Task UDO3b_AnUnreadableKeySetPrintsNoLine_Control()
    {
        using var rig = KeyActRig.Create();
        SigningKeyNotLiveKit.Lockout(rig);
        rig.Double.Routes["GET /v1/jwks"] = (HttpStatusCode.ServiceUnavailable, KeyActDouble.Problem("curia/server/unavailable", "The Forum is unavailable"));

        var (exit, stdout, stderr) = await SigningKeyNotLiveKit.CliAsync(rig, "keys", "--agent", SigningKeyNotLiveKit.Slug);

        var actual = $"exit {exit}; the line written {SigningKeyNotLiveKit.Mentions(stdout + stderr)} time(s)";
        SigningKeyNotLiveKit.Red("exit 8; the line written 0 time(s)", actual, stdout + "\n" + stderr);
    }

    /// <summary>
    /// UDO7 (control): a profile item 290 wrote, with a kept not-in-force key and a signing key the Forum serves, renders byte-identically to what it rendered (G73's words, unedited); and, once <c>use-key</c> exists, the
    /// files it writes read back (the profile reads, the agent loads). On the tree the second half is not exercised by construction: <c>use-key</c> is an unknown verb, exit 1, and the probe says so in its text.
    /// </summary>
    [Fact]
    public async Task UDO7_AProfile290WroteRendersAsItDidAndTheFilesUseKeyWritesReadBack_Control()
    {
        using var rig = KeyActRig.Create();
        var (_, k1, _) = SigningKeyNotLiveKit.Lockout(rig);

        using var control = KeyActRig.Create();
        var (c0, c1) = (HeldKey.New("alice-k0"), HeldKey.New("alice-k1"));
        SigningKeyNotLiveKit.Seed(control, c0, [(c1, false)], [("rotation", c1.Kid, 2, false)]);
        control.Double.KeySet = KeyActDouble.KeySetJson([c0.Served(1), c1.Served(2)], [0, 1, 2], skipped: []);
        var (_, stdout, _) = await SigningKeyNotLiveKit.CliAsync(control, "keys", "--agent", SigningKeyNotLiveKit.Slug);
        var renders = PublishedClientWords.Says(stdout, SigningKeyNotLiveKit.OriginalRotationLine(c0.Kid, c1.Kid, 2));

        var (adopted, _, adoptedError) = await SigningKeyNotLiveKit.CliAsync(rig, "use-key", "--agent", SigningKeyNotLiveKit.Slug, "--kid", k1.Kid);
        string roundTrip;
        if (adopted == 0)
        {
            var reads = rig.Store.ReadProfile(rig.Slug).TryGetValue(out var profile, out _) && profile!.Kid == k1.Kid;
            var loads = rig.Store.Load(rig.Slug).TryGetValue(out var agent, out _);
            agent?.Dispose();
            roundTrip = $"exercised: the profile {(reads ? "reads" : "does not read")} and the agent {(loads ? "loads" : "does not load")}";
        }
        else
        {
            roundTrip = adoptedError.Contains("unknown command", StringComparison.Ordinal) ? "not exercised (use-key is an unknown verb)" : $"use-key exited {adopted}";
        }

        var ok = renders && (roundTrip.StartsWith("not exercised", StringComparison.Ordinal) || roundTrip == "exercised: the profile reads and the agent loads");
        Assert.True(ok, $"Expected: G73's rotation line renders unedited; the files use-key writes read back  Actual: G73's line {(renders ? "renders" : "DOES NOT render")}; round trip {roundTrip}");
    }
}

/// <summary>
/// UDO2 (errata G75, R11.101): the refusal footnote. After <c>invalid_client</c>, <c>curia/authn/token-key-not-live</c> and <c>curia/keys/retired</c> (the refusals after which a client reads the key set, R4.39) the
/// line is written after the refusal's own words and the exit is 6; after a refusal of another type (a <c>400</c>) none is (control). Each type is its own row: the cached-token path meets
/// <c>token-key-not-live</c> first and the cold path meets <c>invalid_client</c> (Task 0, M1), and an owner meets either.
/// </summary>
[Collection("console")]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Each rig is a using declaration; the analyzer loses the ownership across the awaits.")]
public sealed class SigningKeyNotLiveRefusalTests
{
    /// <summary>
    /// UDO2: in the scene, an authenticated verb refused as <paramref name="refusal"/> prints the held-keys line after the refusal's own words and exits 6. Red on the tree: the refusal and the SKIPPED line, and no line
    /// that says the signing key is not live.
    /// </summary>
    [Theory]
    [InlineData("invalid_client")]
    [InlineData("curia/authn/token-key-not-live")]
    [InlineData("curia/keys/retired")]
    public async Task UDO2_ARefusalThatTriggersTheKeySetReadIsFollowedByTheLine(string refusal)
    {
        using var rig = KeyActRig.Create();
        var (k0, k1, k2) = SigningKeyNotLiveKit.Lockout(rig);
        switch (refusal)
        {
            case "invalid_client":
                rig.Double.Token = SigningKeyNotLiveKit.InvalidClient;
                break;
            case "curia/authn/token-key-not-live":
                rig.Store.WriteToken(rig.Slug, new CachedToken("cached", DateTimeOffset.UtcNow.AddHours(1), null));
                rig.Double.Routes["GET /v1/budget"] = (HttpStatusCode.Unauthorized, KeyActDouble.Problem(refusal, "The key the token was issued on is not live"));
                break;
            default:
                rig.Double.Routes["GET /v1/budget"] = (HttpStatusCode.Forbidden, KeyActDouble.Problem(refusal, "The key is retired"));
                break;
        }

        var (exit, stdout, stderr) = await SigningKeyNotLiveKit.CliAsync(rig, "budget", "--agent", SigningKeyNotLiveKit.Slug);

        var everything = stdout + "\n" + stderr;
        var line = SigningKeyNotLiveKit.HeldLine(k2.Kid, null, (k0.Kid, 1), (k1.Kid, 2));
        var folded = PublishedClientWords.Fold(everything);
        var refusalAt = folded.IndexOf("problem type", StringComparison.Ordinal);
        var lineAt = folded.IndexOf(PublishedClientWords.Fold(line), StringComparison.Ordinal);
        var actual = $"exit {exit}; the refusal's own words: {(refusalAt >= 0 ? "written" : "not written")}; the held-keys line: {(lineAt >= 0 ? "said" : "not said")}, written {SigningKeyNotLiveKit.Mentions(everything)} time(s), "
            + (lineAt >= 0 && refusalAt >= 0 && lineAt > refusalAt ? "after the refusal" : "not after the refusal");
        var expected = "exit 6; the refusal's own words: written; the held-keys line: said, written 1 time(s), after the refusal";

        SigningKeyNotLiveKit.Red(expected, actual, everything + "\n--- the line, from the errata ---\n" + line);
    }

    /// <summary>UDO2 (control, green on the tree by design): a refusal of another type is not a trigger, so no line is written: the refusal's own exit stands. It carries information through FUDO2's sibling: a footnote after every refusal.</summary>
    [Fact]
    public async Task UDO2_ARefusalOfAnotherTypePrintsNoLine_Control()
    {
        using var rig = KeyActRig.Create();
        SigningKeyNotLiveKit.Lockout(rig);
        rig.Double.Routes["GET /v1/budget"] = (HttpStatusCode.BadRequest, KeyActDouble.Problem("curia/budget/unreadable", "The request could not be read"));

        var (exit, stdout, stderr) = await SigningKeyNotLiveKit.CliAsync(rig, "budget", "--agent", SigningKeyNotLiveKit.Slug);

        var actual = $"the line written {SigningKeyNotLiveKit.Mentions(stdout + stderr)} time(s); a SKIPPED line: {((stdout + stderr).Contains("SKIPPED:", StringComparison.Ordinal) ? "present" : "absent")}; the refusal's exit is not 6: {exit != 6}";
        SigningKeyNotLiveKit.Red("the line written 0 time(s); a SKIPPED line: absent; the refusal's exit is not 6: True", actual, stdout + "\n" + stderr);
    }
}

/// <summary>
/// UDO4 and UDO5 (errata G75, R11.101): <c>curia use-key --agent &lt;slug&gt; --kid &lt;kid&gt;</c>. The oracle for "signs nothing and sends nothing" is the double's request log (no <c>POST</c> of any route: no
/// token request, no lifecycle post, no other), and for what the profile now holds, the raw files. On the tree the verb is unknown (exit 1, usage), so the first assertion of each probe is the one that reads red.
/// </summary>
[Collection("console")]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Each rig is a using declaration; the analyzer loses the ownership across the awaits.")]
public sealed class SigningKeyNotLiveUseKeyTests
{
    private const string Verb = "use-key";

    private static async Task<(int Exit, string Stdout, string Stderr)> UseKeyAsync(KeyActRig rig, string? kid) =>
        await SigningKeyNotLiveKit.CliAsync(rig, kid is null ? [Verb, "--agent", SigningKeyNotLiveKit.Slug] : [Verb, "--agent", SigningKeyNotLiveKit.Slug, "--kid", kid]);

    private static string Pem(string path) => File.ReadAllText(path).Trim();

    /// <summary>
    /// UDO4: in the scene, <c>use-key --kid K1</c> exits 0; <c>signing-key.pem</c> is K1's file and the identity's kid is K1; the cached token is gone; A is recorded in force at 2 and B not in force at 3; K2 is kept
    /// under <c>keys/</c> at 0600 with its own bytes and its rotation not in force; the double saw no <c>POST</c> of any kind; and <c>curia keys</c> then writes no line. Red on the tree: the verb is unknown, exit 1.
    /// </summary>
    [Fact]
    public async Task UDO4_UseKeyMakesTheProfileSignWithAHeldKeyTheForumServesAndSendsNothing()
    {
        using var rig = KeyActRig.Create();
        var (k0, k1, k2) = SigningKeyNotLiveKit.Lockout(rig);
        rig.Store.WriteToken(rig.Slug, new CachedToken("cached", DateTimeOffset.UtcNow.AddHours(1), null));

        var (exit, stdout, stderr) = await UseKeyAsync(rig, k1.Kid);

        var identity = NotHonouredKit.Identity(rig);
        var signing = File.Exists(Path.Combine(rig.Directory, "signing-key.pem")) ? Pem(Path.Combine(rig.Directory, "signing-key.pem")) : "absent";
        var kept = (identity["retained_keys"]?.AsArray() ?? []).FirstOrDefault(k => (string?)k!["kid"] == k2.Kid);
        var keptFile = kept is null ? null : Path.Combine(rig.Directory, (string?)kept["file"] ?? "none");
        var keptState = kept is null
            ? "K2 is not kept"
            : $"K2 is kept: rotation_in_force={(kept["rotation_in_force"] is { } r ? r.ToJsonString() : "absent")}, mode {(keptFile is not null && File.Exists(keptFile) ? SigningKeyNotLiveKit.Mode(keptFile) : "no file")}, bytes {(keptFile is not null && File.Exists(keptFile) && Pem(keptFile) == k2.Pem.Trim() ? "its own" : "not its own")}";
        var (keysExit, afterKeys, afterKeysError) = await SigningKeyNotLiveKit.CliAsync(rig, "keys", "--agent", SigningKeyNotLiveKit.Slug);

        var actual = $"exit {exit}; signing-key.pem is {(signing == k1.Pem.Trim() ? "K1's" : "not K1's")}; kid {identity["kid"]}; token.json {(rig.Store.ReadToken(rig.Slug) is null ? "gone" : "kept")}; "
            + $"acts {NotHonouredKit.Acts(identity)}; {keptState}; POSTs seen {rig.Double.AllPosts}; result with K0 named: {(PublishedClientWords.Says(stdout, SigningKeyNotLiveKit.ResultWithOthers(k1.Kid, k0.Kid)) ? "said" : "not said")}; curia keys then: {AfterUseKey(keysExit, afterKeys, afterKeysError, k1.Kid)}";
        var expected = $"exit 0; signing-key.pem is K1's; kid {k1.Kid}; token.json gone; acts [rotation {k1.Kid} 2; rotation {k2.Kid} 3 in_force=false]; K2 is kept: rotation_in_force=false, mode 600, bytes its own; POSTs seen 0; result with K0 named: said; curia keys then: {AfterUseKeyExpected(k1.Kid, withNowInForce: false)}";

        SigningKeyNotLiveKit.Red(expected, actual, stdout + "\n" + stderr + "\n--- curia keys after ---\n" + afterKeys + afterKeysError);
    }

    /// <summary>
    /// What <c>curia keys</c> says after <c>use-key</c>, read as the output and the exit (Amendment 2 ruling 3(a)): the silence of an error would satisfy "the line is not written", so the report must begin with the Forum-word
    /// header, hold the <c>key ... active</c> line of the key the profile now signs with, exit 6, hold one <c>SKIPPED:</c> line for log index 3 (the key set still says the Forum does not act on row 3), and hold no
    /// <c>MISSING:</c>, <c>FOREIGN:</c> or <c>SIGNING KEY NOT LIVE</c> line, nor a <c>NOW IN FORCE:</c> line unless the profile's rotation A is one the Forum acts on and the profile has not adopted.
    /// </summary>
    private static string AfterUseKey(int exit, string stdout, string stderr, string signingKid)
    {
        var folded = PublishedClientWords.Fold(stdout);
        var skipped = System.Text.RegularExpressions.Regex.Matches(stdout, @"SKIPPED:[^\n]*").Select(m => m.Value).ToArray();
        string Has(string text) => stdout.Contains(text, StringComparison.Ordinal) ? "present" : "absent";
        return $"exit {exit}; header {(folded.StartsWith(PublishedClientWords.Fold("these lines are the Forum's word"), StringComparison.Ordinal) ? "first" : "not first")}; "
            + $"key {signingKid} active {(folded.Contains(PublishedClientWords.Fold($"key {SigningKeyNotLiveKit.Kid(signingKid)} active"), StringComparison.Ordinal) ? "present" : "absent")}; "
            + $"SKIPPED lines {skipped.Length}, for index 3: {(skipped.Length == 1 && skipped[0].Contains("at log index 3", StringComparison.Ordinal) ? "yes" : "no")}; "
            + $"MISSING {Has("MISSING:")}; FOREIGN {Has("FOREIGN:")}; NOW IN FORCE {Has("NOW IN FORCE:")}; SIGNING KEY NOT LIVE written {SigningKeyNotLiveKit.Mentions(stdout + stderr)} time(s)";
    }

    private static string AfterUseKeyExpected(string signingKid, bool withNowInForce) =>
        $"exit 6; header first; key {signingKid} active present; SKIPPED lines 1, for index 3: yes; MISSING absent; FOREIGN absent; NOW IN FORCE {(withNowInForce ? "present" : "absent")}; SIGNING KEY NOT LIVE written 0 time(s)";

    /// <summary>
    /// UDO4, the other key: <c>use-key --kid K0</c> exits 0 and A's <c>NOW IN FORCE</c> line then stands with G73's own words, remedy included (the signing key is live now, so the remedy can run).
    /// </summary>
    [Fact]
    public async Task UDO4_UseKeyOfK0LeavesG73sNowInForceWordsStanding()
    {
        using var rig = KeyActRig.Create();
        var (k0, k1, _) = SigningKeyNotLiveKit.Lockout(rig);

        var (exit, stdout, stderr) = await UseKeyAsync(rig, k0.Kid);
        var (keysExit, keys, keysError) = await SigningKeyNotLiveKit.CliAsync(rig, "keys", "--agent", SigningKeyNotLiveKit.Slug);

        var original = SigningKeyNotLiveKit.OriginalRotationLine(k0.Kid, k1.Kid, 2);
        var actual = $"exit {exit}; POSTs seen {rig.Double.AllPosts}; result with K1 named: {(PublishedClientWords.Says(stdout, SigningKeyNotLiveKit.ResultWithOthers(k0.Kid, k1.Kid)) ? "said" : "not said")}; A's NOW IN FORCE line with G73's words: {(PublishedClientWords.Says(keys, original) ? "said" : "not said")}; curia keys then: {AfterUseKey(keysExit, keys, keysError, k0.Kid)}";
        var expected = $"exit 0; POSTs seen 0; result with K1 named: said; A's NOW IN FORCE line with G73's words: said; curia keys then: {AfterUseKeyExpected(k0.Kid, withNowInForce: true)}";

        SigningKeyNotLiveKit.Red(expected, actual, stdout + "\n" + stderr + "\n--- curia keys after ---\n" + keys + keysError);
    }

    /// <summary>
    /// UDO5: every refusal of <c>use-key</c> writes nothing and sends nothing, and is answered by <c>use-key</c> itself (not the unknown-verb usage). Rows: a kid not held (exit 6); a kid held and served <c>active</c>
    /// with a different public half (exit 6; red for a kid-only reading); a kid held and served <c>retired</c>, then <c>compromised</c> (exit 6); a signing key that is live (exit 1); an unreadable key set (exit 8); no
    /// <c>--kid</c> (exit 1). On the tree every row reads the same unknown-verb answer (exit 1, usage): a row that expects exit 1 is kept red by the third part of its text, the answer being use-key's; the rows carry
    /// information beyond the verb's absence through, among others, FUDO3 (the kid-only comparison), FUDO4 (proceeding while the signing key is live) and FUDO14 (the cached token deleted before anything is checked): the
    /// falsification records name more rows than these.
    /// </summary>
    [Theory]
    [InlineData("not-held", 6)]
    [InlineData("different-half", 6)]
    [InlineData("served-retired", 6)]
    [InlineData("served-compromised", 6)]
    [InlineData("signing-key-live", 1)]
    [InlineData("key-set-unreadable", 8)]
    [InlineData("no-kid", 1)]
    public async Task UDO5_EveryRefusalWritesNothingSendsNothingAndIsUseKeysOwn(string row, int exitCode)
    {
        using var rig = KeyActRig.Create();
        var (k0, k1, k2) = SigningKeyNotLiveKit.Lockout(rig);
        rig.Store.WriteToken(rig.Slug, new CachedToken("cached", DateTimeOffset.UtcNow.AddHours(1), null));
        var kid = k1.Kid;
        switch (row)
        {
            case "not-held":
                kid = "alice-not-held";
                break;
            case "different-half":
                var stranger = HeldKey.New("alice-k1").Served(2);
                rig.Double.KeySet = KeyActDouble.KeySetJson([k0.Served(1), stranger], [0, 1, 2], skipped: [(3, "agent.key-bound", "broken-link")]);
                break;
            case "served-retired":
                rig.Double.KeySet = KeyActDouble.KeySetJson([k0.Served(1), k1.Served(2, "retired", retiredAt: 4)], [0, 1, 2, 4], skipped: [(3, "agent.key-bound", "broken-link")]);
                break;
            case "served-compromised":
                rig.Double.KeySet = KeyActDouble.KeySetJson([k0.Served(1), k1.Served(2, "compromised")], [0, 1, 2], skipped: [(3, "agent.key-bound", "broken-link")]);
                break;
            case "signing-key-live":
                rig.Double.KeySet = KeyActDouble.KeySetJson([k0.Served(1), k1.Served(2), k2.Served(3)], [0, 1, 2, 3]);
                break;
            case "key-set-unreadable":
                rig.Double.Routes["GET /v1/jwks"] = (HttpStatusCode.ServiceUnavailable, KeyActDouble.Problem("curia/server/unavailable", "The Forum is unavailable"));
                break;
            default:
                break;
        }

        var before = SigningKeyNotLiveKit.Tree(rig);
        var (exit, stdout, stderr) = await UseKeyAsync(rig, row == "no-kid" ? null : kid);

        var actual = $"exit {exit}; the profile is {(before == SigningKeyNotLiveKit.Tree(rig) ? "byte-identical" : "CHANGED")}; POSTs seen {rig.Double.AllPosts}; "
            + (stderr.Contains("unknown command", StringComparison.Ordinal) ? "answered as an unknown command" : "answered by use-key");
        var expected = $"exit {exitCode}; the profile is byte-identical; POSTs seen 0; answered by use-key";

        SigningKeyNotLiveKit.Red(expected, actual, stdout + "\n" + stderr);
    }

    /// <summary>
    /// The printed command read as a POSIX shell reads this restricted form (Amendment 4): words separated by single spaces, each a bare word of <c>[A-Za-z0-9._/=-]</c> or one single-quoted word with no embedded
    /// single quote. Anything else (an expansion, a double quote, a backslash, an operator, an unterminated quote) is not a plain command: null.
    /// </summary>
    private static List<string>? PlainShellWords(string command)
    {
        var words = new List<string>();
        var i = 0;
        while (i < command.Length)
        {
            if (command[i] == '\'')
            {
                var close = command.IndexOf('\'', i + 1);
                if (close < 0) return null;
                words.Add(command[(i + 1)..close]);
                i = close + 1;
            }
            else
            {
                var start = i;
                while (i < command.Length && command[i] != ' ')
                {
                    var c = command[i];
                    if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '/' or '=' or '-')) return null;
                    i++;
                }

                words.Add(command[start..i]);
            }

            if (i < command.Length)
            {
                if (command[i] != ' ' || i + 1 >= command.Length) return null;
                i++;
            }
        }

        return words;
    }

    /// <summary>
    /// UDO11 (Amendment 3 ruling 3): the command the held-form line prints is a command the verb accepts. In the lockout scene, <c>curia keys</c>'s line is read between <c>(the highest log index): </c> and
    /// <c>. That command reads</c>, read as plain shell words (Amendment 4), and run through the same CLI against the same rig: it must exit 0, send nothing, and make the key bound last (K1, Amendment 5) the signing
    /// key, record the rotation at index 2 as in force, and leave <c>curia keys</c> with no <c>NOW IN FORCE</c> line.
    /// </summary>
    [Fact]
    public async Task UDO11_TheCommandTheLinePrintsIsOneTheVerbAccepts()
    {
        using var rig = KeyActRig.Create();
        var (_, k1, _) = SigningKeyNotLiveKit.Lockout(rig);
        var (_, keys, keysError) = await SigningKeyNotLiveKit.CliAsync(rig, "keys", "--agent", SigningKeyNotLiveKit.Slug);

        const string Open = "(the highest log index): ";
        const string Close = ". That command reads";
        var from = keys.IndexOf(Open, StringComparison.Ordinal);
        var to = from < 0 ? -1 : keys.IndexOf(Close, from, StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from, $"the held-form line is not in curia keys' output\n--- output ---\n{keys}{keysError}");
        var command = keys[(from + Open.Length)..to].Trim();
        var tokens = PlainShellWords(command);
        Assert.True(tokens is not null, $"not a plain command: {command}");
        Assert.True(tokens!.Count > 2 && tokens[0] == "curia" && tokens[1] == "use-key", $"not a use-key command: {command}");

        var (exit, stdout, stderr) = await SigningKeyNotLiveKit.CliAsync(rig, [.. tokens.Skip(1)]);
        var signing = rig.Store.ReadProfile(rig.Slug).TryGetValue(out var profile, out _) ? profile!.Kid : "<profile unreadable>";

        var identity = NotHonouredKit.Identity(rig);
        var rotationAt2 = (identity["lifecycle_acts"]?.AsArray() ?? []).FirstOrDefault(a => (long?)a!["log_index"] == 2);
        var recorded = rotationAt2 is null ? "absent" : rotationAt2["in_force"] is { } f ? $"in_force={f.ToJsonString()}" : "in force";
        var (_, after, afterError) = await SigningKeyNotLiveKit.CliAsync(rig, "keys", "--agent", SigningKeyNotLiveKit.Slug);

        var actual = $"exit {exit}; POSTs seen {rig.Double.AllPosts}; signing kid {signing}; the act at index 2 is {recorded}; curia keys then: NOW IN FORCE {(after.Contains("NOW IN FORCE:", StringComparison.Ordinal) ? "present" : "absent")}";
        var expected = $"exit 0; POSTs seen 0; signing kid {k1.Kid}; the act at index 2 is in force; curia keys then: NOW IN FORCE absent";
        SigningKeyNotLiveKit.Red(expected, actual, $"command: {command}\n" + stdout + "\n" + stderr + "\n--- curia keys after ---\n" + after + afterError);
    }

    /// <summary>
    /// UDO16 (Amendment 5 ruling 3): the second form of <c>use-key</c>'s result. (a) K1 adopted while K0 is also held and served live: the plain result, then the errata's second form filled (K0 a display literal, the agent
    /// a shell word, <c>&lt;that kid&gt;</c> left). (b) The key set serves only one held public half: the plain result exactly, nothing after it. (c) Two others: both listed in the key set's order, the key just adopted not
    /// among them. (d) The command the form prints, with <c>&lt;that kid&gt;</c> replaced by an other key's kid, is a plain <c>curia retire --agent &lt;slug&gt; --kid &lt;kid&gt;</c>; it is not run.
    /// </summary>
    [Fact]
    public async Task UDO16_TheResultNamesTheOtherLiveKeysAndOnlyThen()
    {
        using var rig = KeyActRig.Create();
        var (k0, k1, _) = SigningKeyNotLiveKit.Lockout(rig);
        var (_, a, aError) = await UseKeyAsync(rig, k1.Kid);

        using var single = KeyActRig.Create();
        var (s0, s1) = (HeldKey.New("alice-k0"), HeldKey.New("alice-k1"));
        SigningKeyNotLiveKit.Seed(single, s1, [(s0, true)], [("rotation", s1.Kid, 2, true)]);
        single.Double.KeySet = KeyActDouble.KeySetJson([s0.Served(1), s1.Served(2, "retired", retiredAt: 3)], [0, 1, 2, 3], skipped: []);
        var (_, b, bError) = await UseKeyAsync(single, s0.Kid);

        using var many = KeyActRig.Create();
        var (m0, m1, m2) = SigningKeyNotLiveKit.Lockout(many);
        var mx = HeldKey.New("alice-kx");
        SigningKeyNotLiveKit.Seed(many, m2, [(m0, true), (m1, false), (mx, true)], [("rotation", m1.Kid, 2, false), ("rotation", m2.Kid, 3, true)]);
        many.Double.KeySet = KeyActDouble.KeySetJson([m1.Served(2), mx.Served(4), m0.Served(1)], [0, 1, 2, 4], skipped: [(3, "agent.key-bound", "broken-link")]);
        var (_, c, cError) = await UseKeyAsync(many, m1.Kid);

        var secondForm = PublishedClientWords.Fold(a);
        const string Marker = "once a token works: ";
        var at = secondForm.IndexOf(Marker, StringComparison.Ordinal);
        var printed = at < 0 ? "<no second form>" : secondForm[(at + Marker.Length)..].Trim().TrimEnd('.').Replace("<that kid>", k0.Kid, StringComparison.Ordinal);
        var words = PlainShellWords(printed);
        var asWords = words is null ? "not plain shell words" : string.Join(" ", words);

        string Exact(string text, string expected) => PublishedClientWords.Fold(text).Trim() == PublishedClientWords.Fold(expected).Trim() ? "exact" : "different";
        var actual = $"(a) the result with K0 named: {Exact(a, SigningKeyNotLiveKit.ResultWithOthers(k1.Kid, k0.Kid))}; (b) one held key, the plain result: {Exact(b, SigningKeyNotLiveKit.PlainResult(s0.Kid))}; "
            + $"(c) two others, in the key set's order: {Exact(c, SigningKeyNotLiveKit.ResultWithOthers(m1.Kid, m0.Kid, mx.Kid))}; (d) the printed command as words: {asWords}";
        var expected = $"(a) the result with K0 named: exact; (b) one held key, the plain result: exact; (c) two others, in the key set's order: exact; (d) the printed command as words: curia retire --agent {SigningKeyNotLiveKit.Slug} --kid {k0.Kid}";
        SigningKeyNotLiveKit.Red(expected, actual, $"--- (a) ---\n{a}{aError}\n--- (b) ---\n{b}{bError}\n--- (c) ---\n{c}{cError}");
    }

    /// <summary>
    /// UDO13 (Amendment 3 ruling 4): <c>use-key</c> names its agent. With no <c>--agent</c>, an empty one and a blank one, the one profile the rig holds is not taken: exit 1, no byte of the profile changed, nothing
    /// sent, and the usage of <c>use-key</c> (not the unknown-command answer).
    /// </summary>
    [Theory]
    [InlineData("no-agent")]
    [InlineData("blank-agent")]
    [InlineData("whitespace-agent")]
    public async Task UDO13_UseKeyWithoutAnAgentRefusesAndTakesNoProfile(string row)
    {
        using var rig = KeyActRig.Create();
        var (_, k1, _) = SigningKeyNotLiveKit.Lockout(rig);
        string[] args = row switch
        {
            "no-agent" => [Verb, "--kid", k1.Kid],
            "blank-agent" => [Verb, "--agent", "", "--kid", k1.Kid],
            _ => [Verb, "--agent", "  ", "--kid", k1.Kid],
        };
        var before = SigningKeyNotLiveKit.Tree(rig);

        var (exit, stdout, stderr) = await SigningKeyNotLiveKit.CliAsync(rig, args);

        var actual = $"exit {exit}; the profile is {(before == SigningKeyNotLiveKit.Tree(rig) ? "byte-identical" : "CHANGED")}; POSTs seen {rig.Double.AllPosts}; "
            + $"stderr has the use-key usage: {stderr.Contains("usage: curia use-key", StringComparison.Ordinal)}; stderr says unknown command: {stderr.Contains("unknown command", StringComparison.Ordinal)}";
        var expected = "exit 1; the profile is byte-identical; POSTs seen 0; stderr has the use-key usage: True; stderr says unknown command: False";
        SigningKeyNotLiveKit.Red(expected, actual, stdout + "\n" + stderr);
    }

    /// <summary>
    /// UDO14 (Amendment 3 ruling 7): the acts, read from the profile and not from any printed text. <c>use-key --kid K0</c> (the key the enrolment bound, at the key set's first binding) records no rotation naming K0 and
    /// leaves the act count as it was; <c>use-key --kid K1</c> leaves exactly one act naming K1 (A, the rotation at 2 the profile already recorded: UDO4's K1 test does not record a new one, this is its pair).
    /// </summary>
    [Theory]
    [InlineData("k0")]
    [InlineData("k1")]
    public async Task UDO14_UseKeyRecordsNoActForTheEnrolmentKeyAndNoNewActForAnyOther(string key)
    {
        using var rig = KeyActRig.Create();
        var (k0, k1, _) = SigningKeyNotLiveKit.Lockout(rig);
        var target = key == "k0" ? k0 : k1;
        int Count() => rig.Store.ReadProfile(rig.Slug).TryGetValue(out var p, out _) ? p!.Acts.Length : -1;
        int Naming(string kid) => rig.Store.ReadProfile(rig.Slug).TryGetValue(out var p, out _) ? p!.Acts.Count(a => a.Kid == kid) : -1;
        var before = Count();

        var (exit, stdout, stderr) = await UseKeyAsync(rig, target.Kid);

        var actual = $"exit {exit}; acts {Count()} (were {before}); acts naming {target.Kid} {Naming(target.Kid)}";
        var expected = $"exit 0; acts {before} (were {before}); acts naming {target.Kid} {(key == "k0" ? 0 : 1)}";
        SigningKeyNotLiveKit.Red(expected, actual, stdout + "\n" + stderr);
    }
}

/// <summary>
/// UDO8 (errata G75, R11.101): the words of the help and of the verb's result are the errata's, read at test time: <c>curia keys --help</c>'s added clause, <c>curia use-key --help</c>'s usage line and paragraph, the
/// exit table's row 6 and the result <c>use-key</c> prints. Red on the tree: the base's help has none of them and the verb does not exist.
/// </summary>
[Collection("console")]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Each rig is a using declaration; the analyzer loses the ownership across the awaits.")]
public sealed class SigningKeyNotLiveHelpTests
{
    [Fact]
    public async Task UDO8_TheHelpAndTheResultSayWhatTheErrataSays()
    {
        var keysHelp = PublishedClientWords.Fold(Help.For("keys") ?? "<no help>");
        var useKeyHelp = PublishedClientWords.Fold(Help.For("use-key") ?? "<no help>");
        var rowSix = PublishedClientWords.Fold(HelpTextTests.ExitCodeEntry(6));

        using var rig = KeyActRig.Create();
        var (k0, k1, _) = SigningKeyNotLiveKit.Lockout(rig);
        var (exit, stdout, stderr) = await SigningKeyNotLiveKit.CliAsync(rig, "use-key", "--agent", SigningKeyNotLiveKit.Slug, "--kid", k1.Kid);
        var result = SigningKeyNotLiveKit.PlainResult(k1.Kid);
        var withOthers = SigningKeyNotLiveKit.ResultWithOthers(k1.Kid, k0.Kid);

        bool Has(string text, string published) => text.Contains(PublishedClientWords.Fold(published), StringComparison.Ordinal);
        string Said(bool says) => says ? "said" : "not said";
        var actual = $"keys help clause: {Said(Has(keysHelp, SigningKeyNotLiveWords.KeysHelpClause()))}; use-key usage line: {Said(Has(useKeyHelp, SigningKeyNotLiveWords.UseKeyUsage()))}; "
            + $"use-key paragraph: {Said(Has(useKeyHelp, SigningKeyNotLiveWords.UseKeyParagraph()))}; exit 6 row clause: {Said(Has(rowSix, SigningKeyNotLiveWords.ExitSixClause()))}; "
            + $"use-key result (exit {exit}): {Said(PublishedClientWords.Says(stdout, result))}; with the others sentence: {Said(PublishedClientWords.Says(stdout, withOthers))}";
        var expected = "keys help clause: said; use-key usage line: said; use-key paragraph: said; exit 6 row clause: said; use-key result (exit 0): said; with the others sentence: said";

        SigningKeyNotLiveKit.Red(expected, actual, stdout + "\n" + stderr + "\n--- the result, from the errata ---\n" + withOthers);
    }

    /// <summary>
    /// UDO12 (Amendment 3 ruling 5): the top-level help names the recovery verb: it holds the errata's use-key usage line cut at its first <c> [</c> clause and the words <c>SIGNING KEY NOT LIVE</c>.
    /// </summary>
    [Fact]
    public void UDO12_TheTopLevelHelpNamesTheRecoveryVerb()
    {
        var top = PublishedClientWords.Fold(Help.Usage);
        var usage = SigningKeyNotLiveWords.UseKeyUsage();
        var cut = usage.IndexOf(" [", StringComparison.Ordinal);
        var line = PublishedClientWords.Fold(cut < 0 ? usage : usage[..cut]);
        string Said(bool says) => says ? "said" : "not said";
        var actual = $"use-key usage line: {Said(top.Contains(line, StringComparison.Ordinal))}; SIGNING KEY NOT LIVE: {Said(top.Contains(PublishedClientWords.Fold("SIGNING KEY NOT LIVE"), StringComparison.Ordinal))}";
        SigningKeyNotLiveKit.Red("use-key usage line: said; SIGNING KEY NOT LIVE: said", actual, $"usage line sought: {line}\n--- top-level help ---\n{Help.Usage}");
    }
}

/// <summary>
/// UDO10 (errata G75, an errata self-agreement check; no code under test): R11.101's published rotation line while the signing key is not live is R11.96 (add.)'s rotation line with <c>, but signs with &lt;kid&gt;</c>
/// removed from its first sentence and everything from <c>To sign with a new key instead</c> on replaced by R11.96 (add.) (rev.)'s sentence, so the two published copies of one sentence cannot drift. Green when written;
/// it goes red when either copy is edited in the errata.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class SigningKeyNotLiveErrataAgreementTests
{
    [Fact]
    public void UDO10_TheTwoPublishedCopiesOfTheRevisedRotationLineAgree()
    {
        var original = NotInForceWords.NowInForceRotation();
        const string Signs = ", but signs with <kid>";
        const string Cut = "To sign with a new key instead";
        var cut = original.IndexOf(Cut, StringComparison.Ordinal);
        Assert.True(cut > 0 && original.Contains(Signs, StringComparison.Ordinal), "R11.96 (add.)'s rotation words no longer hold the two phrases this probe derives the revised line from");

        var derived = original[..cut].Replace(Signs, string.Empty, StringComparison.Ordinal) + SigningKeyNotLiveWords.RevisedTail();
        var published = SigningKeyNotLiveWords.RotationLineWhileNotLive();

        SigningKeyNotLiveKit.Red(published, derived, "R11.101's words and R11.96 (add.) (rev.) with R11.96 (add.)");
    }
}
