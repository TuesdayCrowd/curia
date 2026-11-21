using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Curia.Canon.Json;
using Curia.Client.Cli;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// What the NHR probes of register item 290 (errata G73) share: the answer the Forum gives a rotation or retirement it recorded and does not act on, the key set a Forum then serves, and the readings of the profile
/// on disk that do not go through the code a probe is about (the raw <c>identity.json</c>, the key files, the statement the double received).
/// </summary>
internal static class NotHonouredKit
{
    internal const string Type = "curia/keys/not-honoured";
    internal const string Title = "The Forum does not honour the row it recorded";
    internal const string OwnKid = "a-0000abcd";

    /// <summary>The 500 a route answers: <c>{type, title, detail: null}</c> and, unless <paramref name="logIndexJson"/> is null, <c>log_index</c> with that JSON text as its value.</summary>
    internal static (HttpStatusCode, string) Answer(string? logIndexJson) =>
        (HttpStatusCode.InternalServerError,
            $"{{\"type\":\"{Type}\",\"title\":\"{Title}\",\"detail\":null" + (logIndexJson is null ? string.Empty : $",\"log_index\":{logIndexJson}") + "}");

    internal static (HttpStatusCode, string) Answer(long index) => Answer(index.ToString(CultureInfo.InvariantCulture));

    /// <summary>A key set with one skipped row at <paramref name="index"/>.</summary>
    internal static string WithSkipped(string keySet, long index, string type, string predicate) =>
        keySet[..^1] + $",\"curia_skipped\":[{{\"log_index\":{index.ToString(CultureInfo.InvariantCulture)},\"type\":\"{type}\",\"predicate\":\"{predicate}\"}}],\"curia_skipped_total\":1}}";

    /// <summary>The key set the Forum serves after a refused rotation: the profile's own key and one skipped row.</summary>
    internal static string KeySetAfterRefusedRotation(long index) =>
        WithSkipped(KeyActDouble.KeySetJson([new KeyActDouble.Key(OwnKid, 1)], [0, 1]), index, "agent.key-bound", "broken-link");

    internal static string Hex(string kid) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(kid)));

    internal static string DerivedFile(string kid) => "keys/" + Hex(kid) + ".pem";

    /// <summary>The statement, and the members of it a probe compares, of the <paramref name="which"/>th lifecycle POST the double received.</summary>
    internal sealed record Received(string Statement, string Kid, string X, string Y, string? Authorization);

    internal static Received ReceivedBy(KeyActDouble forum, int which = 0)
    {
        using var body = JsonDocument.Parse(forum.LifecycleBodies[which]);
        var statement = body.RootElement.GetProperty("statement").GetString()!;
        using var parsed = JsonDocument.Parse(statement);
        var root = parsed.RootElement;
        var jwk = root.TryGetProperty("jwk", out var j) ? j : default;
        return new(
            statement,
            root.GetProperty("kid").GetString()!,
            jwk.ValueKind == JsonValueKind.Object ? jwk.GetProperty("x").GetString()! : string.Empty,
            jwk.ValueKind == JsonValueKind.Object ? jwk.GetProperty("y").GetString()! : string.Empty,
            body.RootElement.TryGetProperty("authorization", out var a) ? a.GetString() : null);
    }

    /// <summary>The public half of a PEM private key as a JWK's <c>x</c> and <c>y</c>.</summary>
    internal static (string X, string Y) PublicHalf(string privatePem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privatePem);
        var q = key.ExportParameters(includePrivateParameters: false).Q;
        return (Base64Url(q.X!), Base64Url(q.Y!));
    }

    /// <summary>The SubjectPublicKeyInfo of the key a JWK's <c>x</c> and <c>y</c> name.</summary>
    internal static byte[] SpkiOf(string x, string y)
    {
        using var key = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = FromBase64Url(x), Y = FromBase64Url(y) },
        });
        return key.ExportSubjectPublicKeyInfo();
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static byte[] FromBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded + new string('=', (4 - padded.Length % 4) % 4));
    }

    /// <summary>The raw <c>identity.json</c>, read without the profile reader.</summary>
    internal static JsonObject Identity(KeyActRig rig) => JsonNode.Parse(File.ReadAllText(Path.Combine(rig.Directory, "identity.json")))!.AsObject();

    /// <summary>The acts of a raw identity, each <c>kind kid index</c> and, where the member says so, <c>in_force=...</c>.</summary>
    internal static string Acts(JsonObject identity) =>
        "[" + string.Join("; ", (identity["lifecycle_acts"]?.AsArray() ?? []).Select(a =>
            $"{a!["kind"]} {a["kid"]} {a["log_index"]}" + (a["in_force"] is { } f ? $" in_force={f.ToJsonString()}" : string.Empty))) + "]";

    /// <summary>How a kept key stands on disk: its raw <c>retained_keys</c> entry, its file and mode, and whether its public half is the one <paramref name="received"/> named.</summary>
    internal static string KeptKey(KeyActRig rig, string kid, Received received)
    {
        var entry = (JsonNode.Parse(File.ReadAllText(Path.Combine(rig.Directory, "identity.json")))!["retained_keys"]?.AsArray() ?? [])
            .FirstOrDefault(k => (string?)k!["kid"] == kid);
        if (entry is null) return $"key {kid} is not kept";

        var file = (string?)entry["file"];
        var onDisk = file is null ? null : Path.Combine(rig.Directory, file);
        var held = onDisk is not null && File.Exists(onDisk);
        var mode = !held ? "no file" : OperatingSystem.IsWindows() ? "600" : Convert.ToString((int)File.GetUnixFileMode(onDisk!) & 0x1FF, 8);
        var same = held && PublicHalf(File.ReadAllText(onDisk!)) == (received.X, received.Y);
        return $"key {kid} is kept: rotation_in_force={(entry["rotation_in_force"] is { } r ? r.ToJsonString() : "absent")}, file {file}, mode {mode}, public half {(same ? "is" : "is not")} the statement's jwk";
    }

    /// <summary>What a profile's signing identity is, as bytes: <c>signing-key.pem</c> and <c>dpop-key.pem</c>, and the identity's kid, alg and signer.</summary>
    internal static string SigningIdentity(KeyActRig rig)
    {
        var identity = Identity(rig);
        return $"kid={identity["kid"]} alg={identity["alg"]} signer={identity["signer"]?.ToJsonString() ?? "none"} "
            + $"signing={Hex(File.ReadAllText(Path.Combine(rig.Directory, "signing-key.pem")))} dpop={Hex(File.ReadAllText(Path.Combine(rig.Directory, "dpop-key.pem")))}";
    }

    /// <summary>Everything a verb printed, in one text: the words are read from the errata and where they are printed is not the probe's business.</summary>
    internal static string Everything(string stdout, string stderr) => stdout + "\n" + stderr;

    internal static string Kid(string kid) => DisplayLiteral.Of(kid);

    internal static string Index(long index) => index.ToString(CultureInfo.InvariantCulture);

    /// <summary>The refused rotation's words with their placeholders filled: the three lines, and the retire command between the second and the third.</summary>
    internal static (string Says, string Keeps, string Command, string Report) RotationWords(NotInForceWords.RotationWords words, KeyActRig rig, string newKid, long? index) =>
        (Fill(words.Says, rig.Slug, newKid, index),
            Fill(words.Keeps, rig.Slug, newKid, index),
            Hints.Retire(rig.Slug, newKid).Text,
            Fill(words.Report, rig.Slug, newKid, index));

    internal static string Fill(string template, string slug, string newKid, long? index)
    {
        _ = slug;
        var filled = template;
        if (index is { } i && filled.Contains("<i>", StringComparison.Ordinal)) filled = filled.Replace("<i>", Index(i), StringComparison.Ordinal);
        if (filled.Contains("<new kid>", StringComparison.Ordinal)) filled = filled.Replace("<new kid>", Kid(newKid), StringComparison.Ordinal);
        if (filled.Contains("<kid>", StringComparison.Ordinal)) filled = filled.Replace("<kid>", Kid(OwnKid), StringComparison.Ordinal);
        return filled;
    }

    /// <summary>Whether the text holds each of the pieces, in this order.</summary>
    internal static bool SaysInOrder(string text, params string[] pieces)
    {
        var folded = PublishedClientWords.Fold(text);
        var from = 0;
        foreach (var piece in pieces)
        {
            var at = folded.IndexOf(PublishedClientWords.Fold(piece), from, StringComparison.Ordinal);
            if (at < 0) return false;
            from = at + 1;
        }

        return true;
    }

    internal static string SkippedSecondForm(long index, string type, string predicate) =>
        PublishedClientWords.SkippedMade
            .Replace("<type>", type, StringComparison.Ordinal)
            .Replace("<i>", Index(index), StringComparison.Ordinal)
            .Replace("<predicate>", predicate, StringComparison.Ordinal);

    internal static string SkippedFirstForm(long index, string type, string predicate) =>
        PublishedClientWords.SkippedNotMade
            .Replace("<type>", type, StringComparison.Ordinal)
            .Replace("<i>", Index(index), StringComparison.Ordinal)
            .Replace("<predicate>", predicate, StringComparison.Ordinal);
}

/// <summary>
/// NHR3, NHR4, NHR6, NHR7 and NHR10 (errata G73, R11.98, register item 290): <c>curia rotate</c> against a Forum that answers the rotation <c>500 curia/keys/not-honoured</c> and names the row's log index. Driven in
/// process through the method <c>Main</c> calls, over <see cref="KeyActDouble"/>, as <see cref="KeyActVerbTests"/> drive it. Every index is the one the double served; the kept key is compared with the <c>jwk</c> of the
/// statement the double received, never with what the profile writer wrote. Every word is read from the errata (<see cref="NotInForceWords"/>).
/// </summary>
[Collection("console")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class NotHonouredRotationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const long Index = 7;

    /// <summary>NHR3: a not-honoured rotation is recorded and its key kept. Red on the tree: <c>Expected: exit 6 ...  Actual: exit 8 ...</c>.</summary>
    [Fact]
    public async Task NHR3_ANotHonouredRotationIsRecordedAndItsKeyIsKept()
    {
        using var rig = KeyActRig.Create();
        rig.Double.LifecycleAnswers.Enqueue(NotHonouredKit.Answer(Index));
        rig.Double.KeySet = NotHonouredKit.KeySetAfterRefusedRotation(Index);
        var before = NotHonouredKit.SigningIdentity(rig);

        var (exit, _, _) = await KeyActRig.CaptureAsync(() => Program.RotateKeyAsync(rig.Client, rig.Session, rig.Agent, rig.Store, null, Ct));

        var received = NotHonouredKit.ReceivedBy(rig.Double);
        var after = NotHonouredKit.SigningIdentity(rig);
        var identity = NotHonouredKit.Identity(rig);
        var actual = $"exit {exit}; "
            + (before == after ? "the signing key, the DPoP key and the identity's kid, alg and signer are unchanged" : $"CHANGED: {before} -> {after}")
            + $"; token.json {(rig.Store.ReadToken(rig.Slug) is null ? "gone" : "kept")}; acts {NotHonouredKit.Acts(identity)}; {NotHonouredKit.KeptKey(rig, received.Kid, received)}";
        var expected = $"exit {ExitCode.Unverified}; the signing key, the DPoP key and the identity's kid, alg and signer are unchanged; token.json kept; "
            + $"acts [rotation {received.Kid} {Index} in_force=false]; key {received.Kid} is kept: rotation_in_force=false, file {NotHonouredKit.DerivedFile(received.Kid)}, mode 600, public half is the statement's jwk";

        WholeText.Same(expected, actual);
    }

    /// <summary>NHR4: the words of a rotation whose index the Forum named, then the second form of the SKIPPED line at that index; the first form nowhere; the key set read after the POST. Red on the tree.</summary>
    [Fact]
    public async Task NHR4_ARefusedRotationSaysTheWordsAndThenTheSecondFormOfTheSkippedLine()
    {
        using var rig = KeyActRig.Create();
        rig.Double.LifecycleAnswers.Enqueue(NotHonouredKit.Answer(Index));
        rig.Double.KeySet = NotHonouredKit.KeySetAfterRefusedRotation(Index);

        var (_, stdout, stderr) = await KeyActRig.CaptureAsync(() => Program.RotateKeyAsync(rig.Client, rig.Session, rig.Agent, rig.Store, null, Ct));

        var everything = NotHonouredKit.Everything(stdout, stderr);
        var newKid = NotHonouredKit.ReceivedBy(rig.Double).Kid;
        var (says, keeps, command, report) = NotHonouredKit.RotationWords(NotInForceWords.RotationNamed(), rig, newKid, Index);
        var second = NotHonouredKit.SkippedSecondForm(Index, "agent.key-bound", "broken-link");
        var first = NotHonouredKit.SkippedFirstForm(Index, "agent.key-bound", "broken-link");
        var post = rig.Double.Requests.FindIndex(KeyActDouble.IsLifecyclePost);
        var keySetAfter = post >= 0 && rig.Double.Requests.Skip(post + 1).Contains("GET /v1/jwks");

        var actual = $"the rotation's words, the retire command and the closing line: {(NotHonouredKit.SaysInOrder(everything, says, keeps, command, report) ? "said in order" : "not said")}; "
            + $"the second SKIPPED form at {Index}: {(PublishedClientWords.Says(everything, second) ? "said" : "not said")}; "
            + $"the first form: {(PublishedClientWords.Says(everything, first) ? "said" : "absent")}; the key set read after the POST: {(keySetAfter ? "yes" : "no")}";
        var expected = $"the rotation's words, the retire command and the closing line: said in order; the second SKIPPED form at {Index}: said; the first form: absent; the key set read after the POST: yes";

        Assert.True(
            expected == actual,
            $"Expected: {expected}  Actual: {actual}\n--- the words, as read from the errata and filled ---\n{says}\n{keeps}\n{command}\n{report}\n{second}\n--- output ---\n{everything.Trim()}");
    }

    /// <summary>
    /// NHR6: an answer whose <c>log_index</c> parses and is no whole number of at least 0 records no act and still keeps the key, with the words for a rotation whose index was not named. Theory over the member's JSON:
    /// absent, <c>-1</c>, <c>"7"</c>, <c>null</c>. A number the profile refuses (<c>1.5</c>, <c>9223372036854775808</c>, <c>1e400</c>) is not read at all and is NHR6b's. Red on the tree.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("-1")]
    [InlineData("\"7\"")]
    [InlineData("null")]
    public async Task NHR6_AnAnswerWithoutAUsableIndexRecordsNoActAndKeepsTheKey(string? logIndexJson)
    {
        using var rig = KeyActRig.Create();
        rig.Double.LifecycleAnswers.Enqueue(NotHonouredKit.Answer(logIndexJson));
        rig.Double.KeySet = NotHonouredKit.KeySetAfterRefusedRotation(Index);

        var (exit, stdout, stderr) = await KeyActRig.CaptureAsync(() => Program.RotateKeyAsync(rig.Client, rig.Session, rig.Agent, rig.Store, null, Ct));

        var received = NotHonouredKit.ReceivedBy(rig.Double);
        var (says, keeps, command, report) = NotHonouredKit.RotationWords(NotInForceWords.RotationNoIndex(), rig, received.Kid, index: null);
        var everything = NotHonouredKit.Everything(stdout, stderr);
        var actual = $"exit {exit}; acts {NotHonouredKit.Acts(NotHonouredKit.Identity(rig))}; {NotHonouredKit.KeptKey(rig, received.Kid, received)}; "
            + $"the words for no index: {(NotHonouredKit.SaysInOrder(everything, says, keeps, command, report) ? "said in order" : "not said")}";
        var expected = $"exit {ExitCode.Unverified}; acts []; key {received.Kid} is kept: rotation_in_force=false, file {NotHonouredKit.DerivedFile(received.Kid)}, mode 600, public half is the statement's jwk; the words for no index: said in order";

        WholeText.Same(expected, actual);
    }

    /// <summary>
    /// NHR6b (control, green on the tree by design; Amendment 3 ruling 2, R6.33, R6.12): a problem document the profile refuses (a number that is not an integer or is out of range, anywhere in it) is unreadable as a
    /// whole and is not read for its type, so the refusal is <c>curia/client/unreadable-problem</c>, exit 8, and nothing is recorded or kept. Rows are the numbers the profile refuses, measured (not reasoned):
    /// <c>1.5</c>, <c>9223372036854775808</c>, <c>1e400</c>. A positive control in the same method (the same scene with <c>log_index</c> 7) shows the fingerprint and the <c>keys/</c> check able to see a change.
    /// It carries information through FNHR15.
    /// </summary>
    [Theory]
    [InlineData("1.5")]
    [InlineData("9223372036854775808")]
    [InlineData("1e400")]
    public async Task NHR6b_AProblemDocumentTheProfileRefusesKeepsNothing(string logIndexJson)
    {
        using (var control = KeyActRig.Create())
        {
            control.Double.LifecycleAnswers.Enqueue(NotHonouredKit.Answer(Index));
            control.Double.KeySet = NotHonouredKit.KeySetAfterRefusedRotation(Index);
            var controlBefore = control.Fingerprint();
            await KeyActRig.CaptureAsync(() => Program.RotateKeyAsync(control.Client, control.Session, control.Agent, control.Store, null, Ct));
            var controlKept = Directory.Exists(Path.Combine(control.Directory, "keys")) ? Directory.GetFiles(Path.Combine(control.Directory, "keys")).Length : 0;
            var controlSaw = control.Fingerprint() != controlBefore && controlKept == 1 && NotHonouredKit.Acts(NotHonouredKit.Identity(control)) != "[]";
            Assert.True(controlSaw, "the control (log_index 7) must change the profile: an act recorded, a key under keys/, a different fingerprint");
        }

        using var rig = KeyActRig.Create();
        rig.Double.LifecycleAnswers.Enqueue(NotHonouredKit.Answer(logIndexJson));
        rig.Double.KeySet = NotHonouredKit.KeySetAfterRefusedRotation(Index);
        var before = rig.Fingerprint();

        var (exit, stdout, stderr) = await KeyActRig.CaptureAsync(() => Program.RotateKeyAsync(rig.Client, rig.Session, rig.Agent, rig.Store, null, Ct));

        var kept = Directory.Exists(Path.Combine(rig.Directory, "keys")) ? Directory.GetFiles(Path.Combine(rig.Directory, "keys")).Length : 0;
        var everything = NotHonouredKit.Everything(stdout, stderr);
        var type = everything.Contains("(\"curia/client/unreadable-problem\")", StringComparison.Ordinal) ? "curia/client/unreadable-problem"
            : everything.Contains("curia/keys/not-honoured", StringComparison.Ordinal) ? "curia/keys/not-honoured" : "another";
        var actual = $"exit {exit}; type {type}; acts {NotHonouredKit.Acts(NotHonouredKit.Identity(rig))}; "
            + (rig.Fingerprint() == before ? "the profile's record is as it was" : "the profile's record CHANGED: " + before + " -> " + rig.Fingerprint()) + $"; {kept} files under keys/";
        var expected = $"exit {ExitCode.ForumFault}; type curia/client/unreadable-problem; acts []; the profile's record is as it was; 0 files under keys/";

        WholeText.Same(expected, actual);
    }

    /// <summary>
    /// NHR7 (control, green on the tree by design): every other refusal of <c>curia rotate</c> that the Forum answered below 500 keeps nothing. The rig's fingerprint (<c>identity.json</c>, <c>signing-key.pem</c>, the listing of <c>keys/</c>) is
    /// equal before and after, and there is no file under <c>keys/</c>. Its rows are the answers below 500: a <c>5xx</c> and a request not heard from keep the act as not yet known (errata G74, R11.99), and
    /// their rows are <c>UKA1</c>'s (the <c>500 curia/internal-error</c> row moved there, register item 322). It carries information through FNHR6.
    /// </summary>
    [Theory]
    [InlineData(400, "curia/keys/statement-unreadable", 1)]
    [InlineData(403, "curia/keys/lifecycle-not-enabled", 1)]
    [InlineData(404, "curia/keys/unknown-key", 1)]
    [InlineData(409, "curia/keys/too-many-live-keys", 1)]
    [InlineData(409, "curia/keys/lifecycle-moved", 2)]
    [InlineData(429, "curia/keys/rotation-budget-exhausted", 1)]
    public async Task NHR7_EveryOtherRefusalOfARotationKeepsNothing_Control(int status, string type, int answers)
    {
        using var rig = KeyActRig.Create();
        for (var i = 0; i < answers; i++)
            rig.Double.LifecycleAnswers.Enqueue(((HttpStatusCode)status, KeyActDouble.Problem(type, "refused")));
        var before = rig.Fingerprint();

        await KeyActRig.CaptureAsync(() => Program.RotateKeyAsync(rig.Client, rig.Session, rig.Agent, rig.Store, null, Ct));

        var kept = Directory.Exists(Path.Combine(rig.Directory, "keys")) ? Directory.GetFiles(Path.Combine(rig.Directory, "keys")).Length : 0;
        WholeText.Same(
            "the profile's record is as it was; 0 files under keys/",
            $"{(rig.Fingerprint() == before ? "the profile's record is as it was" : "the profile's record CHANGED: " + before + " -> " + rig.Fingerprint())}; {kept} files under keys/");
    }

    /// <summary>
    /// NHR10: the kept key retires. After NHR3's refusal (the Forum now honours the row: its key set binds the kept key at the index), <c>curia retire --kid &lt;kept&gt;</c> finds the key, the double receives a
    /// retirement whose authorization verifies under the <c>jwk</c> of the rotation statement the double received, and on the double's <c>201</c> the key is marked retired. Red on the tree.
    /// </summary>
    [Fact]
    public async Task NHR10_TheKeptKeyRetiresAfterTheForumActsOnItsRotation()
    {
        using var rig = KeyActRig.Create();
        rig.Double.LifecycleAnswers.Enqueue(NotHonouredKit.Answer(Index));
        rig.Double.KeySet = NotHonouredKit.KeySetAfterRefusedRotation(Index);
        await KeyActRig.CaptureAsync(() => Program.RotateKeyAsync(rig.Client, rig.Session, rig.Agent, rig.Store, null, Ct));
        var rotation = NotHonouredKit.ReceivedBy(rig.Double);

        // The Forum now acts on the row: its key set binds the kept key at the index, and nothing is skipped.
        rig.Double.KeySet = KeyActDouble.KeySetJson([new KeyActDouble.Key(NotHonouredKit.OwnKid, 1), new KeyActDouble.Key(rotation.Kid, Index)], [0, 1, Index]);
        rig.Double.Requests.Clear();
        rig.Reload();
        var (exit, _, stderr) = await KeyActRig.CaptureAsync(() => Program.RetireKeyAsync(rig.Client, rig.Session, rig.Store, rig.Profile(), rotation.Kid, Ct));

        string verdict;
        if (rig.Double.LifecycleBodies.Count < 2)
        {
            verdict = "no retirement was sent";
        }
        else
        {
            var retirement = NotHonouredKit.ReceivedBy(rig.Double, 1);
            try
            {
                KeyActRig.AssertSigned(retirement.Statement, retirement.Authorization!, "curia-key-retirement+jws", rotation.Kid, NotHonouredKit.SpkiOf(rotation.X, rotation.Y));
                verdict = "the authorization verifies under the rotation statement's jwk";
            }
            catch (Xunit.Sdk.XunitException ex)
            {
                verdict = "the authorization does not verify under the rotation statement's jwk: " + ex.Message;
            }
        }

        var retired = NotHonouredKit.Identity(rig)["retained_keys"]?.AsArray().Any(k => (string?)k!["kid"] == rotation.Kid && k["retired"] is { } r && r.ToJsonString() == "true") == true;
        WholeText.Same(
            "exit 0; the authorization verifies under the rotation statement's jwk; the kept key is marked retired",
            $"exit {exit}; {verdict}; the kept key is {(retired ? "marked retired" : "not marked retired")}" + (exit == 0 ? string.Empty : $" (stderr: {stderr.Trim()})"));
    }
}

/// <summary>NHR5 (errata G73, R11.98): <c>curia retire</c> against a Forum that answers the retirement <c>500 curia/keys/not-honoured</c> and names the row's index.</summary>
[Collection("console")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class NotHonouredRetirementTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// NHR5: after a recorded rotation, a retirement answered not-honoured at 9 exits 6, records the retirement not in force at 9, leaves the retained key not retired, and says the retirement's words. Red on the tree:
    /// <c>Expected: exit 6 ...  Actual: exit 8 ...</c>.
    /// </summary>
    [Fact]
    public async Task NHR5_ANotHonouredRetirementIsRecordedAndTheKeyIsNotMarkedRetired()
    {
        const long at = 9;
        using var rig = KeyActRig.Create();
        var (rotated, _, rotateError) = await KeyActRig.CaptureAsync(() => Program.RotateKeyAsync(rig.Client, rig.Session, rig.Agent, rig.Store, null, Ct));
        Assert.True(rotated == 0, rotateError);
        var newKid = rig.Profile().Kid;
        var rotationAt = rig.Double.AcceptedAt;
        rig.Reload();
        rig.Double.Requests.Clear();
        rig.Double.LifecycleBodies.Clear();
        rig.Double.KeySet = NotHonouredKit.WithSkipped(
            KeyActDouble.KeySetJson([new KeyActDouble.Key(NotHonouredKit.OwnKid, 1), new KeyActDouble.Key(newKid, rotationAt)], [0, 1, rotationAt]), at, "agent.key-retired", "broken-link");
        rig.Double.LifecycleAnswers.Enqueue(NotHonouredKit.Answer(at));

        var (exit, stdout, stderr) = await KeyActRig.CaptureAsync(() => Program.RetireKeyAsync(rig.Client, rig.Session, rig.Store, rig.Profile(), NotHonouredKit.OwnKid, Ct));

        var words = NotInForceWords.RetirementNamed();
        var says = NotHonouredKit.Fill(words.Says, rig.Slug, newKid, at);
        var follows = NotHonouredKit.Fill(words.Follows, rig.Slug, newKid, at);
        var identity = NotHonouredKit.Identity(rig);
        var oldKey = identity["retained_keys"]!.AsArray().Single(k => (string?)k!["kid"] == NotHonouredKit.OwnKid)!;
        var everything = NotHonouredKit.Everything(stdout, stderr);

        var actual = $"exit {exit}; acts {NotHonouredKit.Acts(identity)}; the old key's retired member: {(oldKey["retired"] is { } r ? r.ToJsonString() : "absent")}; "
            + $"the retirement's words: {(NotHonouredKit.SaysInOrder(everything, says, follows) ? "said in order" : "not said")}";
        var expected = $"exit {ExitCode.Unverified}; acts [rotation {newKid} {rotationAt}; retirement {NotHonouredKit.OwnKid} {at} in_force=false]; the old key's retired member: absent; the retirement's words: said in order";

        WholeText.Same(expected, actual);
    }
}
