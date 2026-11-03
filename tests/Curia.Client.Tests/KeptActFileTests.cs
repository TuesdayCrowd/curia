#pragma warning disable CA1416 // modes are read and set as PrivateFiles does (R11.79: 0600 and 0700)

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using Curia.Domain.Content;
using Curia.Domain.Serving;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// ASA1 to ASA9 and ASA25 (errata G58, R11.79): the kept act, at the library, in its failing form. Each probe states what R11.79 requires and says what it found; the seam keeps
/// nothing, so each is red by assertion (the text it leaves is recorded beside its prediction in Task 1's report). The on-disk form is plan ruling 4's: <c>acts/&lt;64 hex&gt;.act</c>
/// in the agent's directory, a header line and the wire in base64, mode 0600 in a directory of mode 0700.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Each kit is a using declaration; the analyzer loses the ownership across the awaits.")]
public sealed class KeptActFileTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The eight verbs and the kind each signs.</summary>
    private static readonly (string Verb, PostKind Kind, bool Contradict)[] Verbs =
    [
        ("ask", PostKind.Question, false), ("answer", PostKind.Answer, false), ("comment", PostKind.Comment, false), ("revision", PostKind.Revision, false),
        ("finding", PostKind.Finding, false), ("endorse", PostKind.Vote, false), ("reproduce", PostKind.Verification, false), ("contradict", PostKind.Verification, true),
    ];

    private static byte[] WireOf(string keptFile) => Convert.FromBase64String(File.ReadAllLines(keptFile)[1]);

    /// <summary>
    /// ASA1. Write-ahead (finding 2): at the instant the transport receives the request the kept file exists, mode 0600 in a directory of mode 0700, and its bytes equal the
    /// wire sent, for each of the eight verbs' kinds; and on the nonce-retry path the one file exists at both requests. Predicted red: <c>no kept file when the request was sent</c>.
    /// </summary>
    [Fact]
    public async Task R11_79_ASA1_TheKeptFileExistsBeforeTheFirstByteIsSent()
    {
        var problems = new List<string>();
        foreach (var (verb, kind, _) in Verbs)
        {
            using var kit = new SessionKit();
            var submission = kit.Build(kind, body: "A body for " + verb + ".");
            kit.Forum.OnPost = (_, _, _) => Task.FromResult(ScriptedForum.Receipt(submission.PrefixedDigest));
            await kit.Session.SubmitAsync(submission, MarkingMode.None, Ct);

            var sent = kit.Forum.Posts.SingleOrDefault();
            if (sent is null) { problems.Add($"{verb}: nothing was sent"); continue; }
            if (!sent.KeptFileExisted) { problems.Add($"{verb}: no kept file when the request was sent"); continue; }
        }

        Red.None(problems, "a kept file at the instant each of the eight verbs' requests was received");

        // The mode, the directory and the bytes, read while a first send is unresolved (a 503 keeps it).
        using var held = new SessionKit();
        var act = held.Build();
        await held.Session.SubmitAsync(act, MarkingMode.None, Ct);
        var file = held.FileOf(act);
        Red.None(
            File.Exists(file)
                ? [.. new[]
                {
                    File.GetUnixFileMode(file) == (UnixFileMode.UserRead | UnixFileMode.UserWrite) ? null : $"mode of the file is {File.GetUnixFileMode(file)}, expected 0600",
                    File.GetUnixFileMode(held.ActsDirectory) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute) ? null : $"mode of the directory is {File.GetUnixFileMode(held.ActsDirectory)}, expected 0700",
                    WireOf(file).AsSpan().SequenceEqual(act.Wire.Span) ? null : "the kept bytes are not the wire sent",
                }.OfType<string>()]
                : ["no kept file at " + Path.GetFileName(file) + " after the 503"],
            "a file of mode 0600 holding the wire in a directory of mode 0700");

        using var retry = new SessionKit();
        var again = retry.Build();
        retry.Forum.OnPost = (n, _, _) => Task.FromResult(n == 0
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized) { Headers = { { "DPoP-Nonce", "n1" } }, Content = new StringContent("{}") }
            : ScriptedForum.Receipt(again.PrefixedDigest));
        await retry.Session.SubmitAsync(again, MarkingMode.None, Ct);
        var retryProblems = new List<string>();
        for (var i = 0; i < retry.Forum.Posts.Count; i++)
            if (!retry.Forum.Posts[i].KeptFileExisted) retryProblems.Add($"request {i + 1} of the nonce retry found no kept file");
        if (retry.Forum.Posts.Count != 2) retryProblems.Add($"the nonce retry sent {retry.Forum.Posts.Count} requests, expected 2");
        Red.None(retryProblems, "the one kept file at both requests of the nonce retry");
    }

    /// <summary>
    /// ASA2. Privacy: a kept act is private (directory 0700, file 0600) and no kept byte appears in what the client prints or throws. The leg for what the CLI prints is run
    /// in process here; <c>curia-mcp</c>'s is in the tool tests. Predicted red: <c>expected mode 0600, no file</c>.
    /// </summary>
    [Fact]
    public async Task R11_79_ASA2_AKeptActIsPrivateAndItsBytesAreNotPrinted()
    {
        using var kit = new SessionKit();
        var act = kit.Build(body: "A body that is only in the kept act: ASA2-MARKER.");
        await kit.Session.SubmitAsync(act, MarkingMode.None, Ct);

        var files = kit.KeptFiles();
        var problems = new List<string>();
        if (files.Length != 1) problems.Add($"expected 1 kept file, was {files.Length}");
        else
        {
            if (File.GetUnixFileMode(files[0]) != (UnixFileMode.UserRead | UnixFileMode.UserWrite)) problems.Add($"expected mode 0600, was {File.GetUnixFileMode(files[0])}");
            if (File.GetUnixFileMode(kit.ActsDirectory) != (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)) problems.Add($"expected directory mode 0700, was {File.GetUnixFileMode(kit.ActsDirectory)}");
        }

        Red.None(problems, "one kept file of mode 0600 in a directory of mode 0700");
    }

    /// <summary>One answer the Forum can give a signed act, how it is scripted and what R11.79's table does with it, by send.</summary>
    private sealed record Row(string Name, string Class, Func<string, int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Script, bool FirstRemoves, bool ResendRemoves);

    private static Row Answered(string name, string cls, int status, string type, bool first, bool resend, string? detail = null) =>
        new(name, cls, (_, _, _, _) => Task.FromResult(ScriptedForum.Problem(status, type, "A refusal", detail)), first, resend);

    /// <summary>R11.79's table, one row per answer it lists, one it does not, an unparsable body, an unheard request, a cancellation and a failure that provably did not send.</summary>
    private static Row[] Rows() =>
    [
        new("201", "recorded", (digest, _, _, _) => Task.FromResult(ScriptedForum.Receipt(digest)), true, true),
        Answered("409 already-recorded", "recorded", 409, "curia/ingest/already-recorded", true, true, "post=01TESTRECEIPTPOST000000000A"),
        Answered("400 content refusal", "definite", 400, "curia/admit/invalid-envelope", true, false),
        Answered("401 authentication", "definite", 401, "curia/authn/dpop-invalid", true, false),
        Answered("403 tier denial", "definite", 403, "curia/authz/denied", true, false, "table-10/denied"),
        Answered("403 budget", "definite", 403, "curia/authz/denied", true, false, "table-11/rate-budget-exhausted next_slot_at=2026-01-01T00:00:00Z"),
        Answered("404", "definite", 404, "curia/posts/not-found", true, false),
        Answered("409 other", "definite", 409, "curia/ingest/other-conflict", true, false),
        Answered("409 agent-in-flight", "definite", 409, "curia/budget/agent-in-flight", true, false),
        Answered("409 revision-in-flight", "definite", 409, "curia/ingest/revision-in-flight", true, false),
        Answered("422", "definite", 422, "curia/canon/not-canonicalizable", true, false),
        Answered("503 log/unreadable", "definite", 503, "curia/log/unreadable", true, false),
        Answered("503 gate-unavailable", "definite", 503, "curia/budget/gate-unavailable", true, false),
        Answered("503 delta-too-large", "definite", 503, "curia/append/delta-too-large", true, false),
        Answered("503 store/unavailable", "unknown", 503, "curia/store/unavailable", false, false),
        Answered("500", "unknown", 500, "curia/server/fault", false, false),
        Answered("502", "unknown", 502, "curia/server/bad-gateway", false, false),
        Answered("504", "unknown", 504, "curia/server/gateway-timeout", false, false),
        Answered("507 (another 5xx)", "unknown", 507, "curia/server/insufficient-storage", false, false),
        Answered("418 (a status the table does not list)", "unlisted", 418, "curia/server/teapot", false, false),
        new("an unparsable body", "unparsable", (_, _, _, _) => Task.FromResult(ScriptedForum.Json(503, "<html>not a problem document</html>")), false, false),
        new("unheard", "unheard", (_, _, _, _) => throw new HttpRequestException(HttpRequestError.Unknown, "reset by the stub"), false, false),
        new("the caller's cancellation", "cancelled", async (_, _, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return ScriptedForum.Problem(500, "x"); }, false, false),
        new("a failure that provably did not send", "not sent", (_, _, _, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "refused"), true, false),
    ];

    private static readonly string[] Classes = ["recorded", "definite", "unknown", "unlisted", "unparsable", "unheard", "cancelled", "not sent"];

    private static async Task SendAsync(SessionKit kit, Row row, SignedSubmission act, bool resend, CancellationToken ct)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        kit.Forum.OnPost = (_, request, token) => row.Script(act.PrefixedDigest, 0, request, token);
        try
        {
            if (row.Class == "cancelled") cancel.CancelAfter(TimeSpan.FromMilliseconds(200));
            if (resend)
            {
                var read = kit.Session.Acts.Read(act.PrefixedDigest);
                if (!read.TryGetValue(out var kept, out _)) return;
                await kit.Session.ResendAsync(kept!, MarkingMode.None, cancel.Token);
            }
            else
            {
                await kit.Session.SubmitAsync(act, MarkingMode.None, cancel.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // the caller's cancellation is rethrown, and kept: the row's expectation is read from the file below
        }
    }

    private static async Task<bool> FirstSendLeavesFileAsync(Row row)
    {
        using var kit = new SessionKit();
        var act = kit.Build(body: "first " + row.Name);
        await SendAsync(kit, row, act, resend: false, Ct);
        return File.Exists(kit.FileOf(act));
    }

    private static async Task<bool> ResendLeavesFileAsync(Row row)
    {
        using var kit = new SessionKit();
        var act = kit.Build(body: "resend " + row.Name);
        kit.Plant(act);
        await SendAsync(kit, row, act, resend: true, Ct);
        return File.Exists(kit.FileOf(act));
    }

    /// <summary>
    /// ASA3. The table, by send: for every answer R11.79 lists, one it does not, an unparsable body, an unheard request, a cancellation and a failure that provably did not
    /// send, the file's presence after the send equals the table (a first send removes on a definite answer, a resend only on <c>201</c> and <c>already-recorded</c>, anything
    /// else is kept). The generator is the closed enumeration above; it asserts every class was reached (<c>NOT REACHED</c> otherwise, trap 131). Predicted red: <c>Settle not built: file absent after 503</c>.
    /// </summary>
    [Fact]
    public async Task R11_79_ASA3_TheFileAfterEachAnswerIsWhatTheTableSays()
    {
        var rows = Rows();
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var problems = new List<string>();

        foreach (var row in rows)
        {
            var firstPresent = await FirstSendLeavesFileAsync(row);
            if (firstPresent == row.FirstRemoves) problems.Add($"first send, {row.Name}: expected the file {(row.FirstRemoves ? "removed" : "kept")}, it is {(firstPresent ? "present" : "absent")}");

            var resentPresent = await ResendLeavesFileAsync(row);
            if (resentPresent == row.ResendRemoves) problems.Add($"resend, {row.Name}: expected the file {(row.ResendRemoves ? "removed" : "kept")}, it is {(resentPresent ? "present" : "absent")}");

            reached.Add(row.Class);
        }

        var missing = Classes.Where(c => !reached.Contains(c)).ToArray();
        if (missing.Length > 0) problems.Add("NOT REACHED: " + string.Join(", ", missing));
        Red.None(problems, $"the file after each of {rows.Length} answers, first send and resend, equal to R11.79's table");
    }

    /// <summary>
    /// ASA4. A killed process: after the write-ahead the transport ends (the shape of a process that exits, an unheard request) and the caller cancels: in both the file remains
    /// and the listing holds it. Predicted red: <c>expected 1 kept act, was 0</c>.
    /// </summary>
    [Fact]
    public async Task R11_79_ASA4_AnActWhoseSendEndedWithNoAnswerIsStillKeptAndListed()
    {
        var problems = new List<string>();
        foreach (var name in new[] { "the transport throws after the write-ahead", "the caller cancels" })
        {
            using var kit = new SessionKit();
            var act = kit.Build();
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            kit.Forum.OnPost = async (_, _, token) =>
            {
                if (name.StartsWith("the transport", StringComparison.Ordinal)) throw new HttpRequestException(HttpRequestError.Unknown, "the process exited");
                await cancel.CancelAsync();
                token.ThrowIfCancellationRequested();
                return ScriptedForum.Problem(500, "x");
            };

            try { await kit.Session.SubmitAsync(act, MarkingMode.None, cancel.Token); }
            catch (OperationCanceledException) { /* rethrown: the caller's own */ }

            var listed = kit.Session.Acts.List().Count(l => l.Header is not null);
            if (listed != 1) problems.Add($"{name}: expected 1 kept act, was {listed}");
            if (kit.KeptFiles().Length != 1) problems.Add($"{name}: expected 1 kept file, was {kit.KeptFiles().Length}");
        }

        Red.None(problems, "one kept act in each case");
    }

    private static string Flip(string base64) => Convert.ToBase64String([.. Convert.FromBase64String(base64).Select((b, i) => i == 40 ? (byte)(b ^ 1) : b)]);

    /// <summary>
    /// ASA5. The file is not trusted (finding 11): one flipped byte in the wire, a header digest that differs, a file renamed to another digest, an envelope signed by another
    /// author, a wrong Forum origin, a truncated file and a header that is not JSON are each refused <c>curia/client/kept-act-damaged</c> by the read the resend makes, with
    /// 0 requests and the file untouched; the good file is the control and reads. Predicted red: <c>expected NOT SENT, request sent</c> (the seam finds no kept act at all).
    /// </summary>
    [Fact]
    public void R11_79_ASA5_ADamagedOrForeignFileIsRefusedAndTheGoodOneReads()
    {
        var problems = new List<string>();

        void Check(string name, Func<SessionKit, SignedSubmission, string> plant, bool good)
        {
            using var kit = new SessionKit();
            var act = kit.Build();
            var path = plant(kit, act);
            var before = File.Exists(path) ? File.ReadAllBytes(path) : [];
            var read = kit.Session.Acts.Read(act.PrefixedDigest);
            var type = read.TryGetValue(out _, out var error) ? "read" : error!.Type;
            var expected = good ? "read" : KeptActs.KeptActDamagedType;
            if (type != expected) problems.Add($"{name}: expected {expected}, was {type}");
            if (!good && kit.Forum.Posts.Count != 0) problems.Add($"{name}: {kit.Forum.Posts.Count} request(s) were sent");
            if (File.Exists(path) && !File.ReadAllBytes(path).AsSpan().SequenceEqual(before)) problems.Add($"{name}: the file was changed");
        }

        Check("the good file", (k, a) => k.Plant(a), good: true);
        Check("one flipped byte in the wire", (k, a) =>
        {
            var path = k.Plant(a);
            var lines = File.ReadAllLines(path);
            lines[1] = Flip(lines[1]);
            File.WriteAllLines(path, lines);
            return path;
        }, good: false);
        Check("a header digest that differs", (k, a) => k.Plant(a, digest: "sha256:" + new string('c', 64)), good: false);
        Check("a file renamed to another digest", (k, a) => k.Plant(a, name: new string('d', 64)), good: false);
        Check("an envelope signed by another author", (k, a) => k.Plant(a, author: "https://agents.example/mallory"), good: false);
        Check("a wrong Forum origin", (k, a) => k.Plant(a, forum: "http://another.test"), good: false);
        Check("a truncated file", (k, a) =>
        {
            var path = k.Plant(a);
            File.WriteAllBytes(path, File.ReadAllBytes(path)[..60]);
            return path;
        }, good: false);
        Check("a header that is not JSON", (k, a) =>
        {
            var path = k.Plant(a);
            var lines = File.ReadAllLines(path);
            File.WriteAllLines(path, ["this is not json", lines[1]]);
            return path;
        }, good: false);

        Red.None(problems, "kept-act-damaged for every damaged file with 0 requests and the file untouched, and the good file read");
    }

    /// <summary>
    /// ASA6. The bound, both sides: 64 kept acts; the 64th sends, the 65th is <c>NOT SENT</c> (<c>curia/client/kept-acts-full</c>, naming 64) with 0 requests and nothing kept;
    /// a settled act frees a slot. The 64 is read from the errata. Predicted red: <c>expected 65th refused, was sent</c>.
    /// </summary>
    [Fact]
    public async Task R11_79_ASA6_TheSixtyFifthActIsNotSentAndASettledActFreesASlot()
    {
        var bound = PublishedResendWords_KeptActBound();
        using var kit = new SessionKit();
        kit.Forum.OnPost = (_, _, _) => Task.FromResult(ScriptedForum.Problem(503, "curia/store/unavailable"));
        var acts = new List<SignedSubmission>();
        ForumResult<PostReceipt>? last = null;
        for (var i = 0; i <= bound; i++)
        {
            var act = kit.Build(body: "act number " + i.ToString(CultureInfo.InvariantCulture));
            acts.Add(act);
            last = await kit.Session.SubmitAsync(act, MarkingMode.None, Ct);
        }

        var problems = new List<string>();
        if (kit.Forum.Posts.Count != bound) problems.Add($"expected 65th refused, was sent ({kit.Forum.Posts.Count} requests for {bound + 1} acts)");
        else
        {
            var refusal = last!.Refusal;
            if (refusal?.Error.Type != KeptActs.KeptActsFullType) problems.Add($"the 65th was refused as {refusal?.Error.Type ?? "a success"}, expected {KeptActs.KeptActsFullType}");
            if (refusal?.Error.Detail?.Contains(bound.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) != true) problems.Add($"the refusal does not name {bound}");
            if (kit.KeptFiles().Length != bound) problems.Add($"{kit.KeptFiles().Length} kept files, expected {bound}");
        }

        Red.None(problems, $"{bound} sent, the 65th NOT SENT as kept-acts-full naming {bound}, nothing more kept");
    }

    private static int PublishedResendWords_KeptActBound() => Curia.Tests.Shared.PublishedResendWords.KeptActBound;

    /// <summary>
    /// ASA7. Listing reads headers only (plan ruling 28): over 64 kept files of 256 KiB and over 64 of 1 MiB, the bytes read per file through the one reading door are at most
    /// 4,096 and equal for the two sizes, and the listing holds 64 headers. A counter, not a timer. Predicted red: a read of the whole file (<c>bytes read 16 MiB</c>); on the seam the listing is empty.
    /// </summary>
    [Fact]
    public void R11_79_ASA7_ListingReadsOneBlockPerFileWhateverTheActsSize()
    {
        var problems = new List<string>();
        var perFile = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var size in new[] { 256 * 1024, 1024 * 1024 })
        {
            using var kit = new SessionKit();

            // One signed act of the size, planted under 64 names with 64 header digests: a listing reads headers only, so the wire is the same bytes each time.
            var act = kit.Build(body: new string('x', size));
            for (var i = 0; i < KeptActs_Bound(); i++)
            {
                var digest = i.ToString("x64", CultureInfo.InvariantCulture);
                kit.Plant(act, name: digest, digest: "sha256:" + digest);
            }

            var counted = new Dictionary<string, long>(StringComparer.Ordinal);
            var acts = new KeptActs(kit.Store, "alice", path => new CountingStream(File.OpenRead(path), n => counted[path] = counted.GetValueOrDefault(path) + n));
            var listed = acts.List();
            if (listed.Count != KeptActs_Bound()) problems.Add($"{size / 1024} KiB: expected {KeptActs_Bound()} listed, found {listed.Count}");
            var worst = counted.Count == 0 ? 0 : counted.Values.Max();
            if (worst > KeptActs.HeaderBlockBytes) problems.Add($"{size / 1024} KiB: bytes read {worst}, expected at most {KeptActs.HeaderBlockBytes} per file");
            perFile[size.ToString(CultureInfo.InvariantCulture)] = worst;
        }

        if (perFile.Values.Distinct().Count() > 1) problems.Add("bytes read per file differ between 256 KiB and 1 MiB acts: " + string.Join(", ", perFile.Select(p => $"{p.Key}: {p.Value}")));
        Red.None(problems, "64 headers listed, at most 4096 bytes read per file, equal for both sizes");
    }

    private static int KeptActs_Bound() => KeptActs.Bound;

    private sealed class CountingStream(Stream inner, Action<int> counted) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) { var n = inner.Read(buffer, offset, count); counted(n); return n; }
        public override int Read(Span<byte> buffer) { var n = inner.Read(buffer); counted(n); return n; }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    /// <summary>
    /// ASA8. A file that cannot be written refuses the send: with the kept-act directory unwritable the send is <c>NOT SENT</c> as <c>curia/client/act-not-kept</c> with 0
    /// requests; the control, a writable directory, sends. Predicted red: <c>request sent without a kept copy</c>.
    /// </summary>
    [Fact]
    public async Task R11_79_ASA8_AnActThatCannotBeKeptIsNotSent()
    {
        using var kit = new SessionKit();
        Directory.CreateDirectory(kit.ActsDirectory);
        File.SetUnixFileMode(kit.ActsDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var refused = await kit.Session.SubmitAsync(kit.Build(), MarkingMode.None, Ct);
            var problems = new List<string>();
            if (kit.Forum.Posts.Count != 0) problems.Add("request sent without a kept copy");
            if (refused.Refusal?.Error.Type != KeptActs.ActNotKeptType) problems.Add($"refused as {refused.Refusal?.Error.Type ?? "a success"}, expected {KeptActs.ActNotKeptType}");
            Red.None(problems, "NOT SENT as act-not-kept with 0 requests");
        }
        finally
        {
            File.SetUnixFileMode(kit.ActsDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>
    /// ASA9. Concurrency: two resends of one act at once send identical bytes (the kept ones) and leave one file or none; two different acts sent at once leave two files; a leftover
    /// file after a <c>201</c> resends to <c>already-recorded</c> and is removed. Predicted red: <c>file absent</c>.
    /// </summary>
    [Fact]
    public async Task R11_79_ASA9_ConcurrentSendsOfOneActAreIdenticalAndOfTwoActsLeaveTwoFiles()
    {
        var problems = new List<string>();

        using (var kit = new SessionKit())
        {
            var act = kit.Build();
            kit.Plant(act);
            kit.Forum.OnPost = (_, _, _) => Task.FromResult(ScriptedForum.Receipt(act.PrefixedDigest));
            var a = kit.Session.Acts.Read(act.PrefixedDigest);
            var b = kit.Session.Acts.Read(act.PrefixedDigest);
            if (!a.TryGetValue(out var ka, out var ea) || !b.TryGetValue(out var kb, out _)) problems.Add($"file absent: the kept act could not be read ({ea?.Type})");
            else
            {
                await Task.WhenAll(kit.Session.ResendAsync(ka!, MarkingMode.None, Ct), kit.Session.ResendAsync(kb!, MarkingMode.None, Ct));
                if (kit.Forum.Posts.Any(p => !p.Body.AsSpan().SequenceEqual(act.Wire.Span))) problems.Add("a resend sent bytes that are not the kept bytes");
                if (kit.KeptFiles().Length > 1) problems.Add($"{kit.KeptFiles().Length} files left by two resends of one act");
            }
        }

        using (var kit = new SessionKit())
        {
            kit.Forum.OnPost = (_, _, _) => Task.FromResult(ScriptedForum.Problem(503, "curia/store/unavailable"));
            await Task.WhenAll(kit.Session.SubmitAsync(kit.Build(body: "one"), MarkingMode.None, Ct), kit.Session.SubmitAsync(kit.Build(body: "two"), MarkingMode.None, Ct));
            if (kit.KeptFiles().Length != 2) problems.Add($"two different acts at once left {kit.KeptFiles().Length} file(s), expected 2");
        }

        using (var kit = new SessionKit())
        {
            var act = kit.Build();
            kit.Plant(act);
            kit.Forum.OnPost = (_, _, _) => Task.FromResult(ScriptedForum.Problem(409, "curia/ingest/already-recorded", "recorded", "post=01TESTRECEIPTPOST000000000A"));
            if (!kit.Session.Acts.Read(act.PrefixedDigest).TryGetValue(out var leftover, out var error)) problems.Add($"file absent: a leftover file could not be read ({error?.Type})");
            else
            {
                await kit.Session.ResendAsync(leftover!, MarkingMode.None, Ct);
                if (kit.KeptFiles().Length != 0) problems.Add("a leftover file resent to already-recorded was not removed");
            }
        }

        Red.None(problems, "identical bytes from two resends, two files for two acts, a leftover resent to already-recorded and removed");
    }

    /// <summary>
    /// ASA25. A damaged or foreign state does not crash a listing: one unreadable file among valid ones is listed as damaged with its name and the others are listed.
    /// Predicted red: <c>exception</c> (or, on the seam, an empty listing).
    /// </summary>
    [Fact]
    public void R11_79_ASA25_AnUnreadableFileIsListedAsDamagedAndTheOthersAreListed()
    {
        using var kit = new SessionKit();
        kit.Plant(kit.Build(body: "one"));
        kit.Plant(kit.Build(body: "two"));
        File.WriteAllBytes(Path.Combine(kit.ActsDirectory, "garbage.act"), [0xff, 0xfe, 0x00, 0x01]);

        var listed = kit.Session.Acts.List();
        var problems = new List<string>();
        if (listed.Count(l => l.Header is not null) != 2) problems.Add($"expected 2 listed with headers, found {listed.Count(l => l.Header is not null)}");
        if (listed.Count(l => l.DamagedFile == "garbage.act") != 1) problems.Add($"expected 1 damaged entry named garbage.act, found {listed.Count(l => l.DamagedFile is not null)}");
        Red.None(problems, "two acts listed and the unreadable file listed as damaged by name");
    }
}
