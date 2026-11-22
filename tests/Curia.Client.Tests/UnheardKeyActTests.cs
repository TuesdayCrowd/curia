using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Curia.Client.Cli;
using Curia.Tests.Shared;
using Xunit;
using Fault = Curia.Client.Tests.KeyActDouble.LifecycleFault;

namespace Curia.Client.Tests;

/// <summary>
/// The words of errata G74 (R11.99 words, R11.100 words) as the errata publishes them, read at test time from <c>curia-whitepaper-ERRATA-AND-ADDENDUM.md</c> and never typed into a probe or taken from the code the
/// probe is about. A form is the first backtick span after its italic label; each read asserts that the label exists once and that the span follows it directly, so an edit to the paragraph turns a probe red by name.
/// </summary>
internal static class UnheardWords
{
    internal const string Settling = "R11.99 words";
    internal const string Keys = "R11.100 words";

    internal static string Span(string paragraphName, string label)
    {
        var paragraph = PublishedClientWords.Paragraph(paragraphName);
        var marker = $"*{label}:*";
        var at = paragraph.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0 || paragraph.IndexOf(marker, at + 1, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException($"the errata's {paragraphName} do not hold {marker} exactly once");

        var from = at + marker.Length;
        var open = paragraph.IndexOf('`', from);
        if (open < 0 || paragraph[from..open].Trim().Length != 0)
            throw new InvalidOperationException($"the errata's {paragraphName}: no span follows {marker} directly");

        var close = paragraph.IndexOf('`', open + 1);
        return close < 0 ? throw new InvalidOperationException($"the errata's {paragraphName}: the span after {marker} does not end") : paragraph[(open + 1)..close];
    }

    /// <summary>A UTC instant to the second, as the errata writes one (<c>2026-11-21T10:00:00Z</c>).</summary>
    internal static string Instant(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>The errata's window, 5 minutes (R11.99 (settling)).</summary>
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    /// <summary>Fills each placeholder the template holds (longest first); a placeholder left over is an error, so a probe cannot compare words it did not fill.</summary>
    internal static string Fill(string template, DateTimeOffset sent, long? index, string? newKid, string? kid, string? slug)
    {
        var filled = template;
        void Put(string placeholder, string? value)
        {
            if (value is not null) filled = filled.Replace(placeholder, value, StringComparison.Ordinal);
        }

        Put("<the rotate command>", slug is null ? null : $"curia rotate --agent {slug}");
        Put("<the retire command for <kid>>", slug is null || kid is null ? null : Hints.Retire(slug, kid).Text);
        Put("<new kid>", newKid is null ? null : NotHonouredKit.Kid(newKid));
        Put("<kid>", kid is null ? null : NotHonouredKit.Kid(kid));
        Put("<sent>", Instant(sent));
        Put("<after>", Instant(sent + Window));
        Put("<i>", index is { } i ? NotHonouredKit.Index(i) : null);

        return filled.Contains('<', StringComparison.Ordinal)
            ? throw new InvalidOperationException($"a placeholder is left in the published words: {filled}")
            : filled;
    }
}

/// <summary>
/// What the UKA probes of register item 322 (errata G74) share: a profile that holds an act made and not known (written by hand, so a probe of the settling is not a probe of the keeping), the double over it, the clock,
/// and the readings of the profile on disk that do not go through the code a probe is about.
/// </summary>
internal sealed class UnheardScene : IDisposable
{
    internal static readonly DateTimeOffset Sent = new(2026, 11, 21, 10, 0, 0, TimeSpan.Zero);

    /// <summary>The kid of the key a pending rotation generated.</summary>
    internal const string KeptKid = "a-0000feed";

    /// <summary>The log index the key set binds a recorded act at.</summary>
    internal const long BoundAt = 4;

    private UnheardScene(KeyActRig rig, ManualTimeProvider clock, string signingKid)
    {
        Rig = rig;
        Clock = clock;
        SigningKid = signingKid;
        rig.Double.SkippedMembers = "\"curia_skipped\":[],\"curia_skipped_total\":0";
    }

    internal KeyActRig Rig { get; }

    internal ManualTimeProvider Clock { get; }

    /// <summary>The kid the profile signs with when the scene is built.</summary>
    internal string SigningKid { get; }

    /// <summary>The kept key's private half (a rotation); null for a retirement.</summary>
    internal string? KeptPem { get; private init; }

    private static JsonObject Identity(KeyActRig rig) => JsonNode.Parse(File.ReadAllText(Path.Combine(rig.Directory, "identity.json")))!.AsObject();

    private static void Write(KeyActRig rig, JsonObject identity) => File.WriteAllText(Path.Combine(rig.Directory, "identity.json"), identity.ToJsonString());

    private static void AddPending(JsonObject identity, string kind, string kid) =>
        identity["pending_acts"] = new JsonArray(new JsonObject { ["kind"] = kind, ["kid"] = kid, ["sent_at"] = Sent.ToString("o", CultureInfo.InvariantCulture) });

    /// <summary>A rig on <see cref="Clock"/> and nothing else: no act, a profile as <c>curia enrol</c> leaves it.</summary>
    internal static UnheardScene Fresh(DateTimeOffset now)
    {
        var clock = new ManualTimeProvider(now);
        return new UnheardScene(KeyActRig.Create(clock: clock), clock, NotHonouredKit.OwnKid);
    }

    /// <summary>A rotation sent at <see cref="Sent"/> and not heard from: its generated key kept (a file under <c>keys/</c> at 0600, a retained key whose rotation is not in force) and the act pending. <paramref name="now"/> is the clock.</summary>
    internal static UnheardScene PendingRotation(DateTimeOffset now)
    {
        var clock = new ManualTimeProvider(Sent);
        var rig = KeyActRig.Create(clock: clock);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportPkcs8PrivateKeyPem();

        var keys = Path.Combine(rig.Directory, "keys");
        Directory.CreateDirectory(keys);
        var file = NotHonouredKit.DerivedFile(KeptKid);
        File.WriteAllText(Path.Combine(rig.Directory, file), pem);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(keys, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(Path.Combine(rig.Directory, file), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var identity = Identity(rig);
        identity["retained_keys"] = new JsonArray(new JsonObject { ["alg"] = "ES256", ["kid"] = KeptKid, ["file"] = file, ["rotation_in_force"] = false });
        AddPending(identity, "rotation", KeptKid);
        Write(rig, identity);
        rig.Reload();
        clock.Set(now);
        return new UnheardScene(rig, clock, NotHonouredKit.OwnKid) { KeptPem = pem };
    }

    /// <summary>
    /// A rotation the profile made (so it signs with a new key and holds <see cref="NotHonouredKit.OwnKid"/> as a retained key to retire), then, unless <paramref name="pending"/> is false, a retirement of that key sent at
    /// <see cref="Sent"/> and not heard from. The double serves the key set after the rotation. <paramref name="now"/> is the clock.
    /// </summary>
    internal static async Task<UnheardScene> PendingRetirementAsync(DateTimeOffset now, CancellationToken ct, bool pending = true)
    {
        var clock = new ManualTimeProvider(Sent);
        var rig = KeyActRig.Create(clock: clock);
        var (exit, _, stderr) = await KeyActRig.CaptureAsync(() => Program.RotateKeyAsync(rig.Client, rig.Session, rig.Agent, rig.Store, null, ct));
        Assert.True(exit == 0, stderr);

        var signing = rig.Profile().Kid;
        rig.Double.KeySet = KeyActDouble.KeySetJson([new KeyActDouble.Key(NotHonouredKit.OwnKid, 1), new KeyActDouble.Key(signing, rig.Double.AcceptedAt)], [0, 1, rig.Double.AcceptedAt]);
        rig.Double.Requests.Clear();
        rig.Double.LifecycleBodies.Clear();

        if (pending)
        {
            var identity = Identity(rig);
            AddPending(identity, "retirement", NotHonouredKit.OwnKid);
            Write(rig, identity);
        }

        rig.Reload();
        clock.Set(now);
        return new UnheardScene(rig, clock, signing);
    }

    /// <summary>The key set after the Forum recorded the pending rotation: the kept kid bound at <see cref="BoundAt"/> with the kept key's public half.</summary>
    internal void ServeRotationRecorded() =>
        Rig.Double.KeySet = KeyActDouble.KeySetJson(
            [new KeyActDouble.Key(NotHonouredKit.OwnKid, 1), KeyActDouble.Key.Bound(KeptKid, BoundAt, KeptPem!)], [0, 1, BoundAt]);

    /// <summary>The key set after the Forum recorded the pending retirement: the retired kid retired at <see cref="BoundAt"/> + 1.</summary>
    internal void ServeRetirementRecorded() =>
        Rig.Double.KeySet = KeyActDouble.KeySetJson(
            [new KeyActDouble.Key(NotHonouredKit.OwnKid, 1, "retired", BoundAt + 1), new KeyActDouble.Key(SigningKid, Rig.Double.AcceptedAt)], [0, 1, Rig.Double.AcceptedAt, BoundAt + 1]);

    internal Task<(int Exit, string Stdout, string Stderr)> RotateAsync(CancellationToken ct) =>
        KeyActRig.CaptureAsync(() => Program.RotateKeyAsync(Rig.Client, Rig.Session, Rig.Agent, Rig.Store, null, ct));

    internal Task<(int Exit, string Stdout, string Stderr)> RetireAsync(CancellationToken ct) =>
        KeyActRig.CaptureAsync(() => Program.RetireKeyAsync(Rig.Client, Rig.Session, Rig.Store, Rig.Profile(), NotHonouredKit.OwnKid, ct));

    /// <summary><c>curia keys</c> over the double, on the scene's clock: the verb's own clock is <see cref="TimeProvider.System"/>, and a probe passes the one it sets (errata G74, plan Amendment 2 ruling 4).</summary>
    internal Task<(int Exit, string Stdout, string Stderr)> KeysAsync(CancellationToken ct) =>
        KeyActRig.CaptureAsync(() => Program.ShowKeysAsync(Rig.Client, Rig.Profile(), Clock, ct));

    /// <summary>Every file of the profile directory but the cached token, by path and sha256, in order: what a read must leave alone.</summary>
    internal string Snapshot() =>
        string.Join("|", Directory.EnumerateFiles(Rig.Directory, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Rig.Directory, f))
            .Where(f => f != "token.json")
            .Order(StringComparer.Ordinal)
            .Select(f => f + "=" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Rig.Directory, f)))))
            .DefaultIfEmpty("(empty)"));

    /// <summary>The pending acts of the profile on disk, <c>kind kid sent &lt;instant&gt;</c> each, as the profile reader returns them.</summary>
    internal string Pending() =>
        "[" + string.Join("; ", Rig.Profile().Pending.Select(p => $"{p.Kind} {p.Kid} sent {p.SentAt.ToString("o", CultureInfo.InvariantCulture)}")) + "]";

    internal bool KeyFileHeld(string kid) => File.Exists(Path.Combine(Rig.Directory, NotHonouredKit.DerivedFile(kid)));

    public void Dispose() => Rig.Dispose();
}

/// <summary>
/// UKA1 to UKA8, UKA10 and UKA11 (errata G74, R11.99, R11.100, register item 322): a rotation or a retirement the client did not hear an answer to. Driven in process through the methods <c>Main</c> calls, over
/// <see cref="KeyActDouble"/>, on <see cref="ManualTimeProvider"/>. Every oracle for "the Forum recorded it" is the request body the double received; every word is read from the errata
/// (<see cref="UnheardWords"/>). Written red against the unmodified tree (Task 1): the recorded red text of each is in the task record.
/// </summary>
[Collection("console")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class UnheardKeyActTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Sent = UnheardScene.Sent;

    private static string Instant(DateTimeOffset at) => at.ToString("o", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------------------------------------ UKA1

    public static TheoryData<string, string> NotHeardRows()
    {
        var rows = new TheoryData<string, string>();
        foreach (var act in new[] { "rotate", "retire" })
            foreach (var row in new[] { "503", "500-internal-error", "502", "504", "unheard-after-the-request-is-read", "cancelled-send", "unheard-on-the-challenge-hop", "201-unreadable-body", "200-empty-object" })
                rows.Add(act, row);

        return rows;
    }

    private static void Script(KeyActDouble forum, string row, CancellationTokenSource cancel)
    {
        switch (row)
        {
            case "503":
                forum.LifecycleAnswers.Enqueue((HttpStatusCode.ServiceUnavailable, KeyActDouble.Problem("curia/store/unavailable", "store unavailable")));
                break;
            case "500-internal-error":
                forum.LifecycleAnswers.Enqueue((HttpStatusCode.InternalServerError, KeyActDouble.Problem("curia/internal-error", "refused")));
                break;
            case "502":
                forum.LifecycleAnswers.Enqueue((HttpStatusCode.BadGateway, KeyActDouble.Problem("curia/gateway", "bad gateway")));
                break;
            case "504":
                forum.LifecycleAnswers.Enqueue((HttpStatusCode.GatewayTimeout, KeyActDouble.Problem("curia/gateway", "gateway timeout")));
                break;
            case "unheard-after-the-request-is-read":
                forum.LifecycleFaults.Enqueue(Fault.AnswerLost);
                break;
            case "cancelled-send":
                forum.LifecycleFaults.Enqueue(Fault.WaitForCancel);
                forum.OnLifecycleRequest = _ => cancel.Cancel();
                break;
            case "201-unreadable-body":
                forum.LifecycleAnswers.Enqueue((HttpStatusCode.Created, "not json"));
                break;
            case "200-empty-object":
                forum.LifecycleAnswers.Enqueue((HttpStatusCode.OK, "{}"));
                break;
            case "unheard-on-the-challenge-hop":
                forum.Challenge = true;
                forum.LifecycleFaults.Enqueue(Fault.AnswerLost);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(row), row, null);
        }
    }

    /// <summary>
    /// UKA1: an answer of 500 or above, a send whose answer was not read, a send the caller cancelled and an unheard first hop keep the act as made and not known (R11.99). The profile holds one pending act with the
    /// clock's time at the send and no index; a rotation's generated key is under <c>keys/</c> at 0600 and its public half is the <c>jwk</c> of the statement the double received; the signing and DPoP keys are
    /// byte-identical; the cached token stays; the exit is 8 (a cancelled send ends in the caller's exception instead). Red on the tree: <c>Expected: one pending act ...  Actual: ... pending [] ... key ... is not kept</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(NotHeardRows))]
    public async Task UKA1_AnActNotHeardFromIsKeptAsMadeAndNotKnown(string act, string row)
    {
        using var scene = act == "rotate" ? UnheardScene.Fresh(Sent) : await UnheardScene.PendingRetirementAsync(Sent, Ct, pending: false);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        Script(scene.Rig.Double, row, cancel);
        var identityBefore = NotHonouredKit.SigningIdentity(scene.Rig);
        var keysBefore = Directory.Exists(Path.Combine(scene.Rig.Directory, "keys"))
            ? string.Join(",", Directory.GetFiles(Path.Combine(scene.Rig.Directory, "keys")).Select(f => Path.GetFileName(f) + "=" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f)))).Order(StringComparer.Ordinal))
            : "absent";

        string exit;
        try
        {
            var run = act == "rotate"
                ? await KeyActRig.CaptureAsync(() => Program.RotateKeyAsync(scene.Rig.Client, scene.Rig.Session, scene.Rig.Agent, scene.Rig.Store, null, cancel.Token))
                : await KeyActRig.CaptureAsync(() => Program.RetireKeyAsync(scene.Rig.Client, scene.Rig.Session, scene.Rig.Store, scene.Rig.Profile(), NotHonouredKit.OwnKid, cancel.Token));
            exit = row == "cancelled-send" ? "ended without the caller's exception" : $"exit {run.Exit}";
        }
        catch (OperationCanceledException)
        {
            exit = "ended by the caller's cancellation";
        }

        var received = NotHonouredKit.ReceivedBy(scene.Rig.Double);
        var keysAfter = Directory.Exists(Path.Combine(scene.Rig.Directory, "keys"))
            ? string.Join(",", Directory.GetFiles(Path.Combine(scene.Rig.Directory, "keys")).Select(f => Path.GetFileName(f) + "=" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f)))).Order(StringComparer.Ordinal))
            : "absent";
        var kept = act == "rotate"
            ? NotHonouredKit.KeptKey(scene.Rig, received.Kid, received)
            : $"keys/ {(keysBefore == keysAfter ? "unchanged" : "CHANGED")}";
        var actual = $"{exit}; pending {scene.Pending()}; signing identity {(identityBefore == NotHonouredKit.SigningIdentity(scene.Rig) ? "unchanged" : "CHANGED")}; "
            + $"token.json {(scene.Rig.Store.ReadToken(scene.Rig.Slug) is null ? "gone" : "kept")}; {kept}";

        var pendingKid = act == "rotate" ? received.Kid : NotHonouredKit.OwnKid;
        var expected = $"{(row == "cancelled-send" ? "ended by the caller's cancellation" : "exit 8")}; pending [{(act == "rotate" ? "rotation" : "retirement")} {pendingKid} sent {Instant(Sent)}]; signing identity unchanged; token.json kept; "
            + (act == "rotate"
                ? $"key {received.Kid} is kept: rotation_in_force=false, file {NotHonouredKit.DerivedFile(received.Kid)}, mode 600, public half is the statement's jwk"
                : "keys/ unchanged");

        // A cancelled send ends by the exception where the verb lets it propagate; a verb that returned an exit has kept the act all the same, and what is compared is the keeping.
        if (row == "cancelled-send") actual = actual.Replace("ended without the caller's exception", "ended by the caller's cancellation", StringComparison.Ordinal);
        WholeText.Same(expected, actual);
    }

    // ------------------------------------------------------------------------------------------------ UKA2

    public static TheoryData<string, string> DefiniteRows()
    {
        var rows = new TheoryData<string, string>();
        foreach (var act in new[] { "rotate", "retire" })
            foreach (var row in new[] { "400", "401", "403", "409-lifecycle-moved-twice", "409-not-live", "connection-refused" })
                rows.Add(act, row);

        return rows;
    }

    private static void ScriptDefinite(KeyActDouble forum, string row)
    {
        switch (row)
        {
            case "400":
                forum.LifecycleAnswers.Enqueue((HttpStatusCode.BadRequest, KeyActDouble.Problem("curia/keys/statement-unreadable", "refused")));
                break;
            case "401":
                forum.LifecycleAnswers.Enqueue((HttpStatusCode.Unauthorized, KeyActDouble.Problem("invalid_client", "refused")));
                break;
            case "403":
                forum.LifecycleAnswers.Enqueue((HttpStatusCode.Forbidden, KeyActDouble.Problem("curia/keys/lifecycle-not-enabled", "refused")));
                break;
            case "409-lifecycle-moved-twice":
                forum.LifecycleAnswers.Enqueue((HttpStatusCode.Conflict, KeyActDouble.Problem("curia/keys/lifecycle-moved", "refused")));
                forum.LifecycleAnswers.Enqueue((HttpStatusCode.Conflict, KeyActDouble.Problem("curia/keys/lifecycle-moved", "refused")));
                break;
            case "409-not-live":
                forum.LifecycleAnswers.Enqueue((HttpStatusCode.Conflict, KeyActDouble.Problem("curia/keys/not-live", "refused")));
                break;
            case "connection-refused":
                forum.LifecycleFaults.Enqueue(Fault.ConnectionRefused);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(row), row, null);
        }
    }

    private static async Task RunAsync(UnheardScene scene, string act)
    {
        if (act == "rotate") await scene.RotateAsync(Ct);
        else await scene.RetireAsync(Ct);
    }

    /// <summary>
    /// UKA2 (control, green on the tree by design: nothing is kept there at all): a refusal the Forum answered below 500 and a send that provably did not leave the host leave the profile byte-identical, with no
    /// <c>keys/</c> directory after a rotation. It carries information through FUKA2 (the act and key kept on every refusal). The at-request half is <see cref="UKA2_TheKeyIsOnDiskWhenTheRequestIsWritten"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(DefiniteRows))]
    public async Task UKA2_ADefiniteRefusalLeavesTheProfileAsItWas_Control(string act, string row)
    {
        using var scene = act == "rotate" ? UnheardScene.Fresh(Sent) : await UnheardScene.PendingRetirementAsync(Sent, Ct, pending: false);
        ScriptDefinite(scene.Rig.Double, row);
        var before = scene.Snapshot();
        var directories = Directory.Exists(Path.Combine(scene.Rig.Directory, "keys"));

        await RunAsync(scene, act);

        var actual = $"{(scene.Snapshot() == before ? "the profile is as it was" : "the profile CHANGED")}; keys/ {(Directory.Exists(Path.Combine(scene.Rig.Directory, "keys")) == directories ? "as it was" : "CHANGED")}; pending {scene.Pending()}";
        WholeText.Same("the profile is as it was; keys/ as it was; pending []", actual);
    }

    /// <summary>
    /// UKA2, the write-ahead half: at the moment the double receives each request of a rotation, the generated key is already under <c>keys/</c> (and, for a retirement, the act is already pending). Red on the tree:
    /// <c>Expected: kept at each of the n requests  Actual: absent at n of n</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(DefiniteRows))]
    public async Task UKA2_TheKeyIsOnDiskWhenTheRequestIsWritten(string act, string row)
    {
        using var scene = act == "rotate" ? UnheardScene.Fresh(Sent) : await UnheardScene.PendingRetirementAsync(Sent, Ct, pending: false);
        ScriptDefinite(scene.Rig.Double, row);
        var seen = new List<bool>();
        scene.Rig.Double.OnLifecycleRequest = body =>
        {
            using var request = System.Text.Json.JsonDocument.Parse(body);
            using var statement = System.Text.Json.JsonDocument.Parse(request.RootElement.GetProperty("statement").GetString()!);
            var kid = statement.RootElement.GetProperty("kid").GetString()!;
            seen.Add(act == "rotate"
                ? scene.KeyFileHeld(kid)
                : scene.Rig.Store.ReadProfile(scene.Rig.Slug).TryGetValue(out var profile, out _) && profile!.Pending.Any(p => p.Kid == kid));
        };

        await RunAsync(scene, act);

        var held = seen.Count(s => s);
        WholeText.Same($"kept at each of the {seen.Count} requests", held == seen.Count ? $"kept at each of the {seen.Count} requests" : $"absent at {seen.Count - held} of {seen.Count} requests");
        Assert.NotEmpty(seen);
    }

    // ------------------------------------------------------------------------------------------------ UKA4

    /// <summary>
    /// UKA4: unknown inside the window. The key set shows no new key; the clock is at 4:59. <c>curia keys</c> prints <c>UNKNOWN</c> and exits 8 and writes nothing; <c>curia rotate</c> and <c>curia retire</c> exit 8
    /// with no lifecycle POST in the double's log, the profile byte-identical and the act kept. Red on the tree: <c>Expected: exit 8; 0 lifecycle POSTs  Actual: exit 0; 1 lifecycle POSTs</c> (a second rotation posted).
    /// </summary>
    [Theory]
    [InlineData("keys", "rotation")]
    [InlineData("rotate", "rotation")]
    [InlineData("keys", "retirement")]
    [InlineData("retire", "retirement")]
    public async Task UKA4_InsideTheWindowAnActNotHeardFromIsUnknownAndNothingIsSent(string verb, string kind)
    {
        using var scene = kind == "rotation" ? UnheardScene.PendingRotation(Sent + TimeSpan.FromSeconds(299)) : await UnheardScene.PendingRetirementAsync(Sent + TimeSpan.FromSeconds(299), Ct);
        var before = scene.Snapshot();

        var (exit, stdout, _) = verb switch
        {
            "keys" => await scene.KeysAsync(Ct),
            "rotate" => await scene.RotateAsync(Ct),
            _ => await scene.RetireAsync(Ct),
        };

        var actual = $"exit {exit}; {scene.Rig.Double.LifecyclePosts} lifecycle POSTs; the profile {(scene.Snapshot() == before ? "is as it was" : "CHANGED")}; "
            + (verb == "keys" ? $"{(stdout.Contains("UNKNOWN: ", StringComparison.Ordinal) ? "an" : "no")} UNKNOWN line; " : string.Empty)
            + $"pending {scene.Pending()}";
        var pendingKid = kind == "rotation" ? UnheardScene.KeptKid : NotHonouredKit.OwnKid;
        var expected = "exit 8; 0 lifecycle POSTs; the profile is as it was; "
            + (verb == "keys" ? "an UNKNOWN line; " : string.Empty)
            + $"pending [{kind} {pendingKid} sent {Instant(Sent)}]";
        WholeText.Same(expected, actual);
    }

    /// <summary>
    /// UKA4b: the row lands late. The same scene one second later (5:00), the key set now binding the kept kid with the kept key's public half: <c>curia rotate</c> settles. It exits 0, posts nothing, signs
    /// with the kept key from then on, deletes the cached token, and the pending act is gone. Red on the tree: <c>Expected: exit 0; 0 lifecycle POSTs ...  Actual: exit 6; 0 lifecycle POSTs ...</c> (the entry is FOREIGN).
    /// </summary>
    [Fact]
    public async Task UKA4b_ARowThatLandsLateIsSettledAndNotSentAgain()
    {
        using var scene = UnheardScene.PendingRotation(Sent + TimeSpan.FromSeconds(299));
        scene.Clock.Advance(TimeSpan.FromSeconds(1));
        scene.ServeRotationRecorded();
        scene.Rig.Store.WriteToken(scene.Rig.Slug, new CachedToken("t", scene.Clock.GetUtcNow().AddMinutes(4), null));

        var (exit, _, _) = await scene.RotateAsync(Ct);

        WholeText.Same(SettledExpected(), SettledActual(exit, scene));
    }

    private static string SettledExpected() =>
        $"exit 0; 0 lifecycle POSTs; the profile signs with {UnheardScene.KeptKid} using the kept key; token.json gone; pending []; the act recorded at {UnheardScene.BoundAt}";

    private static string SettledActual(int exit, UnheardScene scene)
    {
        var profile = scene.Rig.Profile();
        var signingFile = Path.Combine(scene.Rig.Directory, "signing-key.pem");
        var signs = File.Exists(signingFile) && NotHonouredKit.PublicHalf(File.ReadAllText(signingFile)) == NotHonouredKit.PublicHalf(scene.KeptPem!) ? "using the kept key" : "using another key";
        return $"exit {exit}; {scene.Rig.Double.LifecyclePosts} lifecycle POSTs; the profile signs with {profile.Kid} {signs}; token.json {(scene.Rig.Store.ReadToken(scene.Rig.Slug) is null ? "gone" : "kept")}; pending {scene.Pending()}; "
            + $"the act recorded at {string.Join(",", profile.Acts.Where(a => a.Kind == LifecycleAct.Rotation && a.Kid == UnheardScene.KeptKid).Select(a => a.LogIndex).DefaultIfEmpty(-1))}";
    }

    // ------------------------------------------------------------------------------------------------ UKA5

    /// <summary>
    /// UKA5, the read half (control, green on the tree by design): at 5:00 on a clean key set <c>curia keys</c> writes nothing (<c>identity.json</c> and <c>keys/</c> byte-identical). It carries information
    /// through FUKA8 (a read that settles). The line it prints is <see cref="UKA5_PastTheWindowACleanKeySetSaysNotRecorded"/>'s.
    /// </summary>
    [Fact]
    public async Task UKA5_APastTheWindowReadWritesNothing_Control()
    {
        using var scene = UnheardScene.PendingRotation(Sent + UnheardWords.Window);
        var before = scene.Snapshot();

        await scene.KeysAsync(Ct);

        WholeText.Same("the profile is as it was", scene.Snapshot() == before ? "the profile is as it was" : "the profile CHANGED");
    }

    /// <summary>UKA5: at 5:00 on a clean key set <c>curia keys</c> prints <c>NOT RECORDED</c> and exits 0. Red on the tree: <c>Expected: exit 0; a NOT RECORDED line  Actual: exit 0; no NOT RECORDED line</c>.</summary>
    [Fact]
    public async Task UKA5_PastTheWindowACleanKeySetSaysNotRecorded()
    {
        using var scene = UnheardScene.PendingRotation(Sent + UnheardWords.Window);

        var (exit, stdout, _) = await scene.KeysAsync(Ct);

        WholeText.Same("exit 0; a NOT RECORDED line", $"exit {exit}; {(stdout.Contains("NOT RECORDED: ", StringComparison.Ordinal) ? "a" : "no")} NOT RECORDED line");
    }

    /// <summary>
    /// UKA5: <c>curia rotate</c> past the window on a clean key set removes the kept key and the act, says so, then posts exactly one rotation whose <c>jwk</c> differs from the dropped key's, answered <c>201</c>, and the
    /// profile signs with the new key. Red on the tree: the dropped key is still held, the drop words are not said.
    /// </summary>
    [Fact]
    public async Task UKA5_PastTheWindowRotateDropsTheKeptKeyAndRotatesOnce()
    {
        using var scene = UnheardScene.PendingRotation(Sent + UnheardWords.Window);
        var dropped = NotHonouredKit.PublicHalf(scene.KeptPem!);

        var (exit, stdout, stderr) = await scene.RotateAsync(Ct);

        var received = scene.Rig.Double.LifecycleBodies.Count == 1 ? NotHonouredKit.ReceivedBy(scene.Rig.Double) : null;
        var words = UnheardWords.Fill(UnheardWords.Span(UnheardWords.Settling, "Dropped, a rotation"), Sent, null, UnheardScene.KeptKid, null, null);
        var profile = scene.Rig.Profile();
        var signingFile = Path.Combine(scene.Rig.Directory, "signing-key.pem");
        var signsWithNew = received is not null && profile.Kid == received.Kid && NotHonouredKit.PublicHalf(await File.ReadAllTextAsync(signingFile, Ct)) == (received.X, received.Y);

        var actual = $"exit {exit}; {scene.Rig.Double.LifecyclePosts} lifecycle POST; the posted jwk {(received is null ? "none" : (received.X, received.Y) == dropped ? "is the dropped key's" : "differs from the dropped key's")}; "
            + $"the dropped key {(scene.KeyFileHeld(UnheardScene.KeptKid) || profile.Retained.Any(k => k.Kid == UnheardScene.KeptKid) ? "is still held" : "is gone")}; pending {scene.Pending()}; "
            + $"the profile {(signsWithNew ? "signs with the new key" : "does not sign with the new key")}; the drop words {(PublishedClientWords.Says(stdout + "\n" + stderr, words) ? "said" : "not said")}";
        var expected = "exit 0; 1 lifecycle POST; the posted jwk differs from the dropped key's; the dropped key is gone; pending []; the profile signs with the new key; the drop words said";
        WholeText.Same(expected, actual);
    }

    // ------------------------------------------------------------------------------------------------ UKA6

    /// <summary>
    /// UKA6: a skipped row is not a license to delete. Past the window, on a key set whose <c>curia_skipped</c> lists an <c>agent.key-bound</c> at index 6 (an index the profile has recorded no act at), the kept key is
    /// not removed and <c>curia rotate</c> proceeds with one rotation. <b>Green on the tree</b> (nothing is removed there at all): it carries information through FUKA7 (the drop ignores a skipped row). The
    /// plan's second clause, that <c>NOW IN FORCE</c> fires when the kid is later bound, is not asserted: G73's <c>NOW IN FORCE</c> reads an act at a log index and a profile that holds the act with no index has none
    /// (NHR6); the task record says so.
    /// </summary>
    [Fact]
    public async Task UKA6_ASkippedRowOfTheSameKindKeepsTheKey_Control()
    {
        using var scene = UnheardScene.PendingRotation(Sent + UnheardWords.Window);
        scene.Rig.Double.KeySet = NotHonouredKit.WithSkipped(scene.Rig.Double.KeySet, 6, "agent.key-bound", "broken-link");

        var (exit, _, _) = await scene.RotateAsync(Ct);

        var profile = scene.Rig.Profile();
        var actual = $"exit {exit}; {scene.Rig.Double.LifecyclePosts} lifecycle POST; the kept key {(scene.KeyFileHeld(UnheardScene.KeptKid) ? "is present" : "was removed")}; "
            + $"the kept key {(profile.Retained.Any(k => k.Kid == UnheardScene.KeptKid && !k.RotationInForce) ? "is held as a key whose rotation is not in force" : "is not held so")}; "
            + $"no act recorded at an index: {(profile.Acts.All(a => a.Kid != UnheardScene.KeptKid) ? "yes" : "no")}; pending {scene.Pending()}";
        WholeText.Same("exit 0; 1 lifecycle POST; the kept key is present; the kept key is held as a key whose rotation is not in force; no act recorded at an index: yes; pending []", actual);
    }

    /// <summary>
    /// UKA6, the other kind: a skipped <c>agent.key-retired</c> row cannot be a rotation's, so past the window it is UKA5's drop. <b>Not green on the tree</b> (the plan's brief called this control green; the drop is new
    /// behaviour): <c>Expected: the kept key is gone  Actual: the kept key is present</c>.
    /// </summary>
    [Fact]
    public async Task UKA6_ASkippedRowOfTheOtherKindIsTheDrop()
    {
        using var scene = UnheardScene.PendingRotation(Sent + UnheardWords.Window);
        scene.Rig.Double.KeySet = NotHonouredKit.WithSkipped(scene.Rig.Double.KeySet, 6, "agent.key-retired", "broken-link");

        var (exit, _, _) = await scene.RotateAsync(Ct);

        WholeText.Same(
            "exit 0; the kept key is gone",
            $"exit {exit}; the kept key {(scene.KeyFileHeld(UnheardScene.KeptKid) || scene.Rig.Profile().Retained.Any(k => k.Kid == UnheardScene.KeptKid) ? "is present" : "is gone")}");
    }

    private static string HeldActual(UnheardScene scene, int exit)
    {
        var profile = scene.Rig.Profile();
        return $"exit {exit}; {scene.Rig.Double.LifecyclePosts} lifecycle POST; the kept key {(scene.KeyFileHeld(UnheardScene.KeptKid) ? "is present" : "was removed")}; "
            + $"the kept key {(profile.Retained.Any(k => k.Kid == UnheardScene.KeptKid && !k.RotationInForce) ? "is held as a key whose rotation is not in force" : "is not held so")}; "
            + $"no act recorded at an index: {(profile.Acts.All(a => a.Kid != UnheardScene.KeptKid) ? "yes" : "no")}; pending {scene.Pending()}";
    }

    /// <summary>
    /// UKA13c (errata G74, R11.99 (settling), the cell that cannot be told from a stranger's): past the window, a key set that says neither which rows it skips nor its lifecycle (the Forum's log would not fold: no
    /// <c>curia_skipped</c> member and no <c>curia_lifecycle</c>) is read as holding a skipped row, because an absence of a list is not a list of none. The kept key is not removed, the record is, and no act is
    /// recorded; the verb goes on and then stops at its own check that the key set names no lifecycle (exit 8, nothing sent). <b>Narrower than the plan's wording:</b> <see cref="KeyActDouble"/> serves no
    /// <c>curia_skipped</c> member in <i>any</i> key set, UKA5, UKA7 and UKA11's "clean" ones included, and those probes expect the drop; a key set that gives its lifecycle and no list is read as skipping none.
    /// The guard it holds is <c>KeyActSettling.CouldBeSkipped</c>'s first line: reverted to <c>false</c> this probe goes red (<c>the kept key was removed</c>).
    /// </summary>
    [Fact]
    public async Task UKA13c_AKeySetThatSaysNeitherItsSkippedRowsNorItsLifecycleHoldsTheKey()
    {
        using var scene = UnheardScene.PendingRotation(Sent + UnheardWords.Window);
        scene.Rig.Double.SkippedMembers = null;
        scene.Rig.Double.KeySet = KeyActDouble.KeySetJson([new KeyActDouble.Key(NotHonouredKit.OwnKid, 1)], lifecycle: null);

        var (exit, _, _) = await scene.RotateAsync(Ct);

        WholeText.Same("exit 8; 0 lifecycle POST; the kept key is present; the kept key is held as a key whose rotation is not in force; no act recorded at an index: yes; pending []", HeldActual(scene, exit));
    }

    /// <summary>
    /// UKA13 (errata G74, R11.99 (settling), Amendment 3 ruling 3): past the window, a key set that gives its lifecycle and no <c>curia_skipped</c> is held: an absent list is not a list of none. Exit 0, one rotation,
    /// the key kept as a key whose rotation is not in force. The guard is <c>KeyActSettling.CouldBeSkipped</c>'s first line.
    /// </summary>
    [Fact]
    public async Task UKA13_AKeySetWithALifecycleAndNoSkippedListHoldsTheKey()
    {
        using var scene = UnheardScene.PendingRotation(Sent + UnheardWords.Window);
        scene.Rig.Double.SkippedMembers = null;
        scene.Rig.Double.KeySet = KeyActDouble.KeySetJson([new KeyActDouble.Key(NotHonouredKit.OwnKid, 1)], [0, 1]);

        var (exit, _, _) = await scene.RotateAsync(Ct);

        WholeText.Same("exit 0; 1 lifecycle POST; the kept key is present; the kept key is held as a key whose rotation is not in force; no act recorded at an index: yes; pending []", HeldActual(scene, exit));
    }

    /// <summary>
    /// UKA13b (errata G74, R11.99 (settling)): past the window, a key set whose <c>curia_skipped_total</c> is larger than the rows it lists (the list holds at most 20) and lists none of the act's kind could be hiding
    /// the act's row past the list, so the kept key is held as UKA6's is: exit 0 and one rotation, the key kept as a key whose rotation is not in force, no act recorded, no record pending. The guard it holds is the
    /// last line of <c>KeyActSettling.CouldBeSkipped</c>: reverted to <c>false</c> this probe goes red.
    /// </summary>
    [Fact]
    public async Task UKA13b_ATotalLargerThanTheListedRowsWithNoneOfTheKindHoldsTheKey()
    {
        using var scene = UnheardScene.PendingRotation(Sent + UnheardWords.Window);
        var rows = string.Join(",", Enumerable.Range(10, 20).Select(i => $"{{\"log_index\":{i},\"type\":\"agent.key-retired\",\"predicate\":\"broken-link\"}}"));
        var keySet = KeyActDouble.KeySetJson([new KeyActDouble.Key(NotHonouredKit.OwnKid, 1)], [0, 1]);
        scene.Rig.Double.KeySet = keySet[..^1] + $",\"curia_skipped\":[{rows}],\"curia_skipped_total\":25}}";

        var (exit, _, _) = await scene.RotateAsync(Ct);

        WholeText.Same("exit 0; 1 lifecycle POST; the kept key is present; the kept key is held as a key whose rotation is not in force; no act recorded at an index: yes; pending []", HeldActual(scene, exit));
    }

    // ------------------------------------------------------------------------------------------------ UKA7

    /// <summary>
    /// UKA7: a retirement not heard from, settled by the key set. Recorded (the kid retired in the key set): <c>curia retire</c> marks the key retired, sends nothing, exits 0. Inside the window and not recorded:
    /// refused, exit 8, nothing sent. After it: the act is dropped, the retirement sent, answered <c>201</c>, the key marked retired. Red on the tree for the first two; the third is green but for the drop words.
    /// </summary>
    [Theory]
    [InlineData("recorded", "00:00:30")]
    [InlineData("inside-the-window", "00:04:59")]
    [InlineData("after-the-window", "00:05:00")]
    public async Task UKA7_ARetirementNotHeardFromIsSettledByTheKeySet(string case_, string age)
    {
        using var scene = await UnheardScene.PendingRetirementAsync(Sent + TimeSpan.Parse(age, CultureInfo.InvariantCulture), Ct);
        if (case_ == "recorded") scene.ServeRetirementRecorded();

        var (exit, stdout, stderr) = await scene.RetireAsync(Ct);

        var profile = scene.Rig.Profile();
        var retired = profile.Retained.Any(k => k.Kid == NotHonouredKit.OwnKid && k.Retired);
        var drop = PublishedClientWords.Says(stdout + "\n" + stderr, UnheardWords.Fill(UnheardWords.Span(UnheardWords.Settling, "Dropped, a retirement"), Sent, null, null, NotHonouredKit.OwnKid, null));
        var actual = $"exit {exit}; {scene.Rig.Double.LifecyclePosts} lifecycle POSTs; the key {(retired ? "is marked retired" : "is not marked retired")}; pending {scene.Pending()}"
            + (case_ == "after-the-window" ? $"; the drop words {(drop ? "said" : "not said")}" : string.Empty);
        var expected = case_ switch
        {
            "recorded" => "exit 0; 0 lifecycle POSTs; the key is marked retired; pending []",
            "inside-the-window" => $"exit 8; 0 lifecycle POSTs; the key is not marked retired; pending [retirement {NotHonouredKit.OwnKid} sent {Instant(Sent)}]",
            _ => "exit 0; 1 lifecycle POSTs; the key is marked retired; pending []; the drop words said",
        };
        WholeText.Same(expected, actual);
    }

    // ------------------------------------------------------------------------------------------------ UKA8

    /// <summary>
    /// UKA8: the kid and the key. (a) The key set binds the kept kid at the act's index with a different public half; (b) it binds another kid at that index. Both are <c>FOREIGN</c>, neither is <c>RECORDED</c>, and
    /// <c>curia rotate</c> adopts nothing (it signs and sends nothing). <b>A control on the tree</b>: <c>FOREIGN</c> is what the tree prints for any entry it did not make. It carries information only through FUKA4 (settling
    /// compares the kid only) and FUKA9 (Compare counts any pending index as the profile's own).
    /// </summary>
    [Theory]
    [InlineData("a-different-public-half")]
    [InlineData("another-kid-at-the-index")]
    public async Task UKA8_AnEntryThatIsNotTheKeptKeyIsForeignAndNothingIsAdopted_Control(string case_)
    {
        using var scene = UnheardScene.PendingRotation(Sent + TimeSpan.FromMinutes(1));
        scene.Rig.Double.KeySet = KeyActDouble.KeySetJson(
            [new KeyActDouble.Key(NotHonouredKit.OwnKid, 1), new KeyActDouble.Key(case_ == "a-different-public-half" ? UnheardScene.KeptKid : "k-stranger", UnheardScene.BoundAt)], [0, 1, UnheardScene.BoundAt]);
        var before = scene.Snapshot();

        var keys = await scene.KeysAsync(Ct);
        var rotate = await scene.RotateAsync(Ct);

        var actual = $"curia keys: exit {keys.Exit}, {(keys.Stdout.Contains("FOREIGN: ", StringComparison.Ordinal) ? "a" : "no")} FOREIGN line, {(keys.Stdout.Contains("RECORDED: ", StringComparison.Ordinal) ? "a" : "no")} RECORDED line; "
            + $"curia rotate: exit {rotate.Exit}, {scene.Rig.Double.LifecyclePosts} lifecycle POSTs, the profile {(scene.Snapshot() == before ? "is as it was" : "CHANGED")}";
        WholeText.Same("curia keys: exit 6, a FOREIGN line, no RECORDED line; curia rotate: exit 6, 0 lifecycle POSTs, the profile is as it was", actual);
    }

    // ------------------------------------------------------------------------------------------------ UKA10

    private static void WriteRaw(UnheardScene scene, Action<JsonObject> edit)
    {
        var path = Path.Combine(scene.Rig.Directory, "identity.json");
        var identity = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        edit(identity);
        File.WriteAllText(path, identity.ToJsonString());
    }

    /// <summary>
    /// UKA10: the profile's <c>pending_acts</c> member round-trips: an act recorded through the store is the act the profile reader returns, and the file holds the member only while an act is pending. Red on the seam
    /// (the writer drops the member): <c>Expected: pending [rotation k-new sent ...]  Actual: pending []</c>.
    /// </summary>
    [Fact]
    public void UKA10_ThePendingMembersRoundTrip()
    {
        using var scene = UnheardScene.Fresh(Sent);
        var absentWhenEmpty = !File.ReadAllText(Path.Combine(scene.Rig.Directory, "identity.json")).Contains("pending_acts", StringComparison.Ordinal);

        scene.Rig.Store.RecordPendingAct(scene.Rig.Profile(), new PendingKeyAct("rotation", "k-new", Sent));

        var raw = JsonNode.Parse(File.ReadAllText(Path.Combine(scene.Rig.Directory, "identity.json")))!.AsObject();
        var members = raw["pending_acts"] is JsonArray array ? array.Count.ToString(CultureInfo.InvariantCulture) : "no";
        WholeText.Same(
            $"absent while empty: True; pending [rotation k-new sent {Instant(Sent)}]; a pending_acts member of 1 act",
            $"absent while empty: {absentWhenEmpty}; pending {scene.Pending()}; a pending_acts member of {members} act");
    }

    /// <summary>UKA10 (control, green on the tree by design): a profile of the shape item 290 wrote (a retained key whose rotation is not in force, an act not in force) is read and rendered again byte-identically: the seam adds nothing to what 290 wrote.</summary>
    [Fact]
    public void UKA10_AProfileItem290WroteRendersByteIdentically_Control()
    {
        using var scene = UnheardScene.Fresh(Sent);
        WriteRaw(scene, identity =>
        {
            identity["retained_keys"] = new JsonArray(new JsonObject { ["alg"] = "ES256", ["kid"] = "k-kept", ["file"] = NotHonouredKit.DerivedFile("k-kept"), ["rotation_in_force"] = false });
            identity["lifecycle_acts"] = new JsonArray(new JsonObject { ["kid"] = "k-kept", ["kind"] = "rotation", ["log_index"] = 7, ["in_force"] = false });
        });

        scene.Rig.Store.RecordEnrollment(scene.Rig.Profile(), "2026-11-21T10:00:00Z");
        var first = File.ReadAllBytes(Path.Combine(scene.Rig.Directory, "identity.json"));
        scene.Rig.Store.RecordEnrollment(scene.Rig.Profile(), "2026-11-21T10:00:00Z");
        var second = File.ReadAllBytes(Path.Combine(scene.Rig.Directory, "identity.json"));

        WholeText.Same("byte-identical; no pending_acts member", $"{(first.AsSpan().SequenceEqual(second) ? "byte-identical" : "DIFFERENT")}; {(Encoding.UTF8.GetString(second).Contains("pending_acts", StringComparison.Ordinal) ? "a pending_acts member" : "no pending_acts member")}");
    }

    /// <summary>UKA10: a malformed <c>pending_acts</c> member is <c>MalformedProfile</c>, whatever the malformation. Red on the seam: <c>Expected: refused as curia/client/malformed-profile  Actual: read</c>.</summary>
    [Theory]
    [InlineData("\"not an array\"")]
    [InlineData("[{\"kind\":\"rotation\",\"sent_at\":\"2026-11-21T10:00:00.0000000+00:00\"}]")]
    [InlineData("[{\"kind\":\"resend\",\"kid\":\"k\",\"sent_at\":\"2026-11-21T10:00:00.0000000+00:00\"}]")]
    [InlineData("[{\"kind\":\"rotation\",\"kid\":\"k\",\"sent_at\":\"yesterday\"}]")]
    [InlineData("[{\"kind\":\"rotation\",\"kid\":\"\",\"sent_at\":\"2026-11-21T10:00:00.0000000+00:00\"}]")]
    [InlineData("[{\"kind\":\"rotation\",\"kid\":\"k\"}]")]
    public void UKA10_AMalformedPendingMemberIsAMalformedProfile(string memberJson)
    {
        using var scene = UnheardScene.Fresh(Sent);
        WriteRaw(scene, identity => identity["pending_acts"] = JsonNode.Parse(memberJson));

        var read = scene.Rig.Store.ReadProfile(scene.Rig.Slug);

        WholeText.Same("refused as curia/client/malformed-profile", read.TryGetValue(out _, out var error) ? "read" : "refused as " + error!.Type);
    }

    // ------------------------------------------------------------------------------------------------ UKA11

    public static TheoryData<string> WordRows() =>
    [
        "recorded-rotation", "recorded-retirement", "unknown-rotation", "unknown-retirement", "not-recorded-rotation", "not-recorded-retirement",
        "after-503-rotation", "after-unheard-rotation", "after-503-retirement", "after-unheard-retirement",
        "settled-rotation", "settled-retirement", "refused-rotation", "refused-retirement",
    ];

    /// <summary>
    /// UKA11: the words are the errata's, read at test time. Each of the three <c>curia keys</c> lines for a rotation and for a retirement; the paragraph printed after a failure the client did not hear an answer
    /// to (a <c>503</c> and an answer lost, rotation and retirement); the settle line and the refusal inside the window. Red on the tree: <c>Expected: the errata's words  Actual: not said</c>, with the words in
    /// the message.
    /// </summary>
    [Theory]
    [MemberData(nameof(WordRows))]
    public async Task UKA11_TheWordsAreTheErratas(string row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var rotation = row.EndsWith("rotation", StringComparison.Ordinal);
        var kindWord = rotation ? "rotation" : "retirement";
        var inside = Sent + TimeSpan.FromSeconds(299);
        var after = Sent + UnheardWords.Window;
        var which = row[..row.LastIndexOf('-')];

        string template, output;
        string? newKid = null;
        var kid = NotHonouredKit.OwnKid;
        long? index = null;
        if (which is "after-503" or "after-unheard")
        {
            using var scene = rotation ? UnheardScene.Fresh(Sent) : await UnheardScene.PendingRetirementAsync(Sent, Ct, pending: false);
            if (which == "after-503") scene.Rig.Double.LifecycleAnswers.Enqueue((HttpStatusCode.ServiceUnavailable, KeyActDouble.Problem("curia/store/unavailable", "store unavailable")));
            else scene.Rig.Double.LifecycleFaults.Enqueue(Fault.AnswerLost);

            var run = rotation ? await scene.RotateAsync(Ct) : await scene.RetireAsync(Ct);
            output = run.Stdout + "\n" + run.Stderr;
            newKid = rotation ? NotHonouredKit.ReceivedBy(scene.Rig.Double).Kid : null;
            template = UnheardWords.Span(UnheardWords.Settling, rotation ? "After a rotation not heard from" : "After a retirement not heard from");
            var failureWords = Filled(template, Sent, index, newKid, kid, scene.Rig.Slug);
            WholeText.Same(Said(true, failureWords), Said(PublishedClientWords.Says(output, failureWords), failureWords));
            return;
        }

        using var held = rotation ? UnheardScene.PendingRotation(which is "unknown" or "refused" ? inside : after) : await UnheardScene.PendingRetirementAsync(which is "unknown" or "refused" ? inside : after, Ct);
        if (rotation) newKid = UnheardScene.KeptKid;
        switch (which)
        {
            case "recorded":
                if (rotation) held.ServeRotationRecorded();
                else held.ServeRetirementRecorded();
                index = rotation ? UnheardScene.BoundAt : UnheardScene.BoundAt + 1;
                template = UnheardWords.Span(UnheardWords.Keys, $"Recorded, a {kindWord}");
                output = (await held.KeysAsync(Ct)).Stdout;
                break;
            case "unknown":
                template = UnheardWords.Span(UnheardWords.Keys, $"Unknown, a {kindWord}");
                output = (await held.KeysAsync(Ct)).Stdout;
                break;
            case "not-recorded":
                template = UnheardWords.Span(UnheardWords.Keys, $"Not recorded, a {kindWord}");
                output = (await held.KeysAsync(Ct)).Stdout;
                break;
            case "settled":
                if (rotation) held.ServeRotationRecorded();
                else held.ServeRetirementRecorded();
                index = rotation ? UnheardScene.BoundAt : UnheardScene.BoundAt + 1;
                template = UnheardWords.Span(UnheardWords.Settling, $"Settled, a {kindWord}");
                var settled = rotation ? await held.RotateAsync(Ct) : await held.RetireAsync(Ct);
                output = settled.Stdout + "\n" + settled.Stderr;
                break;
            default:
                template = UnheardWords.Span(UnheardWords.Settling, $"Refused inside the window, a {kindWord}");
                var refused = rotation ? await held.RotateAsync(Ct) : await held.RetireAsync(Ct);
                output = refused.Stdout + "\n" + refused.Stderr;
                break;
        }

        var words = Filled(template, Sent, index, newKid, kid, held.Rig.Slug);
        WholeText.Same(Said(true, words), Said(PublishedClientWords.Says(output, words), words));
    }

    private static string Filled(string template, DateTimeOffset sent, long? index, string? newKid, string kid, string slug) =>
        UnheardWords.Fill(template, sent, index, newKid, kid, slug);

    private static string Said(bool said, string template) => (said ? "the errata's words said: " : "the errata's words NOT said: ") + template;

    /// <summary><c>curia help rotate</c> and <c>curia help retire</c> carry the errata's help clauses (UKA11). Red on the tree: <c>Expected: ... in curia help rotate  Actual: not there</c>.</summary>
    [Theory]
    [InlineData("rotate", "Help, rotate")]
    [InlineData("retire", "Help, retire")]
    public void UKA11_TheHelpCarriesTheClause(string verb, string label)
    {
        var clause = UnheardWords.Span(UnheardWords.Settling, label);
        var help = PublishedClientWords.Fold(Help.For(verb) ?? string.Empty);

        WholeText.Same($"curia help {verb} carries \"{clause}\"", help.Contains(PublishedClientWords.Fold(clause), StringComparison.Ordinal) ? $"curia help {verb} carries \"{clause}\"" : $"curia help {verb} does not carry it");
    }
}

/// <summary>
/// UKA9 (errata G74, R11.99, register item 322): the process that sends a rotation is killed after the Forum has read the request and before it answers, and the profile holds the act and the key. The real
/// <c>curia</c> in a child process against <see cref="RawStubForum"/>, which serves the key set and the two lifecycle routes and never answers the POST; the child is killed by its process id once the stub has read
/// the request. Red on the tree: <c>Expected: pending [rotation k] and key present  Actual: pending [] and key absent</c>.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class UnheardKeyActProcessTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UKA9_AProcessKilledMidSendLeavesThePendingActAndTheKey()
    {
        using var stub = new RawStubForum(UnheardBehaviour.Stall) { ServeLifecycle = true };
        using var home = new CliHome(stub.Address);
        stub.KeySet = KeyActDouble.KeySetJson([new KeyActDouble.Key("alice-1", 1)], [0, 1]);

        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "curia.dll"));
        foreach (var arg in new[] { "rotate", "--agent", "alice" }) start.ArgumentList.Add(arg);
        start.Environment["CURIA_CLIENT_HOME"] = home.Path;

        using var process = Process.Start(start)!;
        var processId = process.Id;
        try
        {
            _ = process.StandardOutput.ReadToEndAsync(Ct);
            _ = process.StandardError.ReadToEndAsync(Ct);

            var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
            while (!stub.Requests.Any(r => r.StartsWith("POST /v1/agents/", StringComparison.Ordinal)) && DateTimeOffset.UtcNow < deadline && !process.HasExited)
                await Task.Delay(50, Ct);

            Assert.True(stub.Requests.Any(r => r.StartsWith("POST /v1/agents/", StringComparison.Ordinal)), "the child never sent the rotation: " + string.Join(", ", stub.Requests));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }

        Assert.True(processId > 0);
        Assert.True(stub.Submissions.IsEmpty);
        Assert.True(stub.LifecycleBodies.TryPeek(out var body));
        using var request = System.Text.Json.JsonDocument.Parse(body!);
        using var statement = System.Text.Json.JsonDocument.Parse(request.RootElement.GetProperty("statement").GetString()!);
        var kid = statement.RootElement.GetProperty("kid").GetString()!;

        var store = new ProfileStore(home.Path);
        Assert.True(store.ReadProfile("alice").TryGetValue(out var profile, out var error), error?.Title);
        var pending = "[" + string.Join("; ", profile!.Pending.Select(p => $"{p.Kind} {p.Kid}")) + "]";
        var keyHeld = File.Exists(Path.Combine(store.DirectoryFor("alice"), NotHonouredKit.DerivedFile(kid)));

        WholeText.Same($"pending [rotation {kid}] and key present", $"pending {pending} and key {(keyHeld ? "present" : "absent")}");
    }
}
