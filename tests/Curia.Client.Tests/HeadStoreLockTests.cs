using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R6.53 (add.): a client that retains a head holds one exclusive lock, shared by every process of
/// that head, across read, fetch, compare and replace; a wait past its bound, or a lock that is
/// unavailable or switched off, reports could not be checked and touches nothing.
///
/// <para><b>Held across the fetch, not only the write.</b> A compare-and-swap at write time would
/// refuse 15 over 20 but would report 10 to 15 verified without comparing 15 with 20, and a fork
/// between them is what R6.53 keeps the head to find. The race fact is written so that a lock
/// around the write alone still leaves it red.</para>
///
/// <para><b>The cross-process facts fail, never skip, when they cannot run.</b> A child
/// <c>python3</c> holding <c>flock</c> is the property under test; a fact that returned early without
/// one would report R6.53 (add.) as tested on a machine where it never ran.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class HeadStoreLockTests : IDisposable
{
    private const string Switch = "System.IO.DisableFileLocking";
    private const string Variable = "DOTNET_SYSTEM_IO_DISABLEFILELOCKING";

    private readonly string _home = Directory.CreateTempSubdirectory("curia-head-lock-").FullName;
    private readonly List<StubLog> _logs = [];

    public void Dispose()
    {
        foreach (var log in _logs) log.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Two verifications over one retained head at 10: A is served 15 and held at its consistency
    /// fetch until B, served 20, has returned. Without the lock B reads 10, retains 20 and says so,
    /// and A then writes 15 over it -- the retained head moved backwards and a verification reported
    /// retaining a head that is no longer retained.
    /// </summary>
    [Fact]
    public async Task R6_53_TwoVerificationsInterleavedLeaveTheLargerHead()
    {
        var ct = TestContext.Current.CancellationToken;
        var log = Log(treeSize: 20);
        var heads = new HeadStore(_home);

        log.HeadTreeSize = 10;
        await VerifyAsync(log.Client(), heads, TimeSpan.FromSeconds(30));
        Assert.Equal(10, heads.Read(StubLog.Forum)!.TreeSize);
        log.HeadTreeSize = 20;

        var atFetch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var handlerA = new Served(log, 15, async (_, token) =>
        {
            atFetch.TrySetResult();
            await release.Task.WaitAsync(token);
            return null;
        });
        using var handlerB = new Served(log, 20);
        using var servedA = new HttpClient(handlerA, disposeHandler: false) { BaseAddress = StubLog.Forum };
        using var servedB = new HttpClient(handlerB, disposeHandler: false) { BaseAddress = StubLog.Forum };

        var a = VerifyAsync(new ForumClient(servedA, StubLog.Forum), heads, TimeSpan.FromSeconds(30));
        await atFetch.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);

        PostVerification b;
        try
        {
            b = await VerifyAsync(new ForumClient(servedB, StubLog.Forum), heads, TimeSpan.FromSeconds(1));
        }
        finally
        {
            release.TrySetResult();
        }

        var first = await a;

        var reported = new[] { first, b }.Select(Retained).OfType<long>().ToList();
        Assert.NotEmpty(reported);

        Assert.Equal(reported.Max(), heads.Read(StubLog.Forum)!.TreeSize);
    }

    /// <summary>
    /// Another process holds the lock -- a second <c>curia verify</c>, here a child <c>python3</c>
    /// holding <c>flock(LOCK_EX)</c>, which is what .NET's <c>FileShare.None</c> is on Unix. The wait
    /// runs out: consistency could not be checked, the origin is named, and the retained bytes are
    /// the bytes they were.
    /// </summary>
    [Fact]
    public async Task R6_53_ALockHeldElsewhereIsCouldNotCheckAndTouchesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var log = Log(treeSize: 8);
        var heads = new HeadStore(_home);

        log.HeadTreeSize = 4;
        await VerifyAsync(log.Client(), heads, TimeSpan.FromSeconds(30));
        var retained = Path.Combine(heads.DirectoryFor(StubLog.Forum), "head.json");
        var before = await File.ReadAllBytesAsync(retained, ct);
        log.HeadTreeSize = 8;

        using var holder = FlockHolder.Start(Path.Combine(heads.DirectoryFor(StubLog.Forum), "head.lock"));
        Assert.Equal("held", await holder.ReadLineAsync(ct));

        var result = await VerifyAsync(log.Client(), heads, TimeSpan.FromSeconds(1));

        Assert.Equal(CheckOutcome.CouldNotCheck, result.Consistency.Outcome);
        Assert.Contains(HeadStore.Origin(StubLog.Forum), result.Consistency.Detail, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllBytesAsync(retained, ct));
    }

    /// <summary>
    /// With the runtime's file locking switched off, <c>FileShare.None</c> excludes nobody, so a lock
    /// "taken" then is a race reported as a check. Read from the switch and from the variable, as the
    /// runtime reads both; a store that finds it off refuses, naming the switch, and touches nothing.
    /// </summary>
    [Fact]
    public async Task R6_53_SwitchedOffLockingIsCouldNotCheck()
    {
        var ct = TestContext.Current.CancellationToken;

        Assert.True(HeadStore.FileLockingDisabled(name => name == Switch ? true : null, _ => null));
        Assert.True(HeadStore.FileLockingDisabled(_ => null, name => name == Variable ? "1" : null));
        Assert.True(HeadStore.FileLockingDisabled(_ => null, name => name == Variable ? "true" : null));
        Assert.False(HeadStore.FileLockingDisabled(_ => null, _ => null));
        Assert.False(HeadStore.FileLockingDisabled(_ => null, name => name == Variable ? "0" : null));
        Assert.False(HeadStore.FileLockingDisabled(_ => null, name => name == Variable ? "false" : null));

        var log = Log(treeSize: 8);
        var heads = new HeadStore(_home) { LockingDisabled = () => true };

        log.HeadTreeSize = 4;
        await VerifyAsync(log.Client(), new HeadStore(_home), TimeSpan.FromSeconds(30));
        var retained = Path.Combine(heads.DirectoryFor(StubLog.Forum), "head.json");
        var before = await File.ReadAllBytesAsync(retained, ct);
        log.HeadTreeSize = 8;

        var locked = await heads.LockAsync(StubLog.Forum, TimeSpan.Zero, TimeProvider.System, ct);
        Assert.False(locked.TryGetValue(out var held, out var refusal));
        held?.Dispose();
        Assert.Contains(Switch, refusal!.Detail, StringComparison.Ordinal);

        var result = await VerifyAsync(log.Client(), heads, TimeSpan.FromSeconds(1));

        Assert.Equal(CheckOutcome.CouldNotCheck, result.Consistency.Outcome);
        Assert.Contains(Switch, result.Consistency.Detail, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllBytesAsync(retained, ct));
    }

    /// <summary>
    /// Every way out of the consistency check gives the lock back: a refused fetch, a cancelled one,
    /// equal heads (no write), and a write. After each, a zero-wait lock is taken at once.
    /// </summary>
    [Fact]
    public async Task R6_53_TheLockIsReleasedOnEveryPath()
    {
        var ct = TestContext.Current.CancellationToken;
        var log = Log(treeSize: 8);
        var heads = new HeadStore(_home);

        log.HeadTreeSize = 4;
        await VerifyAsync(log.Client(), heads, TimeSpan.FromSeconds(30));
        log.HeadTreeSize = 8;

        // A refused fetch.
        using (var refuses = new Served(log, 8, (_, _) => Task.FromResult<HttpResponseMessage?>(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(
                    """{"type":"curia/stub/unavailable","title":"The stub withholds this proof"}""",
                    Encoding.UTF8,
                    "application/problem+json"),
            })))
        using (var refusing = new HttpClient(refuses, disposeHandler: false) { BaseAddress = StubLog.Forum })
        {
            var refused = await VerifyAsync(new ForumClient(refusing, StubLog.Forum), heads, TimeSpan.FromSeconds(30));
            Assert.Equal(CheckOutcome.CouldNotCheck, refused.Consistency.Outcome);
            await AssertLockIsFreeAsync(heads, ct);
        }

        // A cancelled fetch.
        using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct))
        using (var cancels = new Served(log, 8, async (_, token) =>
        {
            await cancel.CancelAsync();
            token.ThrowIfCancellationRequested();
            return null;
        }))
        using (var cancelling = new HttpClient(cancels, disposeHandler: false) { BaseAddress = StubLog.Forum })
        {
            var verifier = new PostVerifier(
                new ForumClient(cancelling, StubLog.Forum), heads, TimeProvider.System, TimeSpan.FromSeconds(30));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => verifier.VerifyAsync(StubLog.PostId, MarkingMode.None, cancel.Token));
            await AssertLockIsFreeAsync(heads, ct);
        }

        Assert.Equal(4, heads.Read(StubLog.Forum)!.TreeSize);

        // A write.
        var written = await VerifyAsync(log.Client(), heads, TimeSpan.FromSeconds(30));
        Assert.Equal(8, Retained(written));
        await AssertLockIsFreeAsync(heads, ct);

        // Equal heads: nothing is written.
        var equal = await VerifyAsync(log.Client(), heads, TimeSpan.FromSeconds(30));
        Assert.Equal(CheckOutcome.Verified, equal.Consistency.Outcome);
        Assert.Contains("the served head is the one this client retained", equal.Consistency.Detail, StringComparison.Ordinal);
        await AssertLockIsFreeAsync(heads, ct);
    }

    /// <summary>
    /// The lock's wait is measured on the <see cref="TimeProvider"/> it is given (CS-9). On a clock
    /// that does not move, a held lock is waited for however much wall-clock time passes; advanced
    /// past the wait, the clock ends it.
    /// </summary>
    [Fact]
    public async Task R6_53_TheWaitIsTheTimeProvidersNotTheWallClocks()
    {
        var ct = TestContext.Current.CancellationToken;
        var heads = new HeadStore(_home);
        var directory = heads.DirectoryFor(StubLog.Forum);
        Directory.CreateDirectory(directory);

        using var held = new FileStream(
            Path.Combine(directory, "head.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero));
        var wait = TimeSpan.FromMilliseconds(200);

        var locking = heads.LockAsync(StubLog.Forum, wait, clock, ct);

        await Task.Delay(TimeSpan.FromSeconds(1), ct);
        Assert.False(locking.IsCompleted, "the lock's wait ended on the wall clock");

        clock.Advance(wait + TimeSpan.FromMilliseconds(50));

        var result = await locking.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.False(result.TryGetValue(out var lease, out _));
        lease?.Dispose();
    }

    /// <summary>
    /// The first verification against a Forum has no <c>logs/&lt;origin&gt;</c> directory to lock in.
    /// The lock creates it, and the head is retained as before.
    /// </summary>
    [Fact]
    public async Task R6_53_AFirstVerificationTakesTheLock()
    {
        var ct = TestContext.Current.CancellationToken;

        var fresh = new HeadStore(Path.Combine(_home, "fresh"));
        Assert.False(Directory.Exists(fresh.DirectoryFor(StubLog.Forum)));

        var locked = await fresh.LockAsync(StubLog.Forum, TimeSpan.Zero, TimeProvider.System, ct);
        Assert.True(locked.TryGetValue(out var lease, out var refusal), refusal?.Detail);
        lease!.Dispose();

        var log = Log();
        var heads = new HeadStore(Path.Combine(_home, "first"));
        Assert.False(Directory.Exists(heads.DirectoryFor(StubLog.Forum)));

        var result = await VerifyAsync(log.Client(), heads, TimeSpan.FromSeconds(30));

        Assert.Equal(CheckOutcome.CouldNotCheck, result.Consistency.Outcome);
        Assert.Contains("retained no earlier head", result.Consistency.Detail, StringComparison.Ordinal);
        Assert.Equal(log.HeadTreeSize, heads.Read(StubLog.Forum)!.TreeSize);
    }

    /// <summary>The tree size a verification reported retaining, or null when it retained nothing.</summary>
    private static long? Retained(PostVerification verification) =>
        verification.Consistency.Detail.Contains("now the retained one", StringComparison.Ordinal)
        || verification.Consistency.Detail.Contains("is now retained", StringComparison.Ordinal)
            ? verification.AnchorTreeSize
            : null;

    private static async Task AssertLockIsFreeAsync(HeadStore heads, CancellationToken ct)
    {
        var locked = await heads.LockAsync(StubLog.Forum, TimeSpan.Zero, TimeProvider.System, ct);
        Assert.True(locked.TryGetValue(out var lease, out var refusal), refusal?.Detail);
        lease!.Dispose();
    }

    private static async Task<PostVerification> VerifyAsync(ForumClient forum, HeadStore heads, TimeSpan lockWait)
    {
        var verifier = new PostVerifier(forum, heads, TimeProvider.System, lockWait);
        var result = await verifier.VerifyAsync(StubLog.PostId, MarkingMode.None, TestContext.Current.CancellationToken);

        Assert.True(result.TryGetValue(out var verification, out var refusal), refusal?.Summary);
        return verification!;
    }

    private StubLog Log(int treeSize = 5, int postIndex = 2)
    {
        var log = new StubLog(treeSize, postIndex);
        _logs.Add(log);
        return log;
    }

    /// <summary>
    /// The stub Forum, serving a head of a size this handler chooses and, optionally, intercepting
    /// the consistency fetch. Everything else is forwarded to the stub as a fresh request.
    /// </summary>
    private sealed class Served(
        StubLog log,
        int headSize,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage?>>? consistency = null) : HttpMessageHandler
    {
        private readonly HttpClient _inner = log.RawClient();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path == "/v1/log/head")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(log.HeadJson(headSize), Encoding.UTF8, "application/json"),
                };
            }

            if (path == "/v1/log/consistency" && consistency is not null
                && await consistency(request, cancellationToken) is { } answered)
            {
                return answered;
            }

            using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
                forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);

            return await _inner.SendAsync(forwarded, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary><c>tests/Shared/flock-hold.py</c> in a child process, holding the lock until disposed.</summary>
    private sealed class FlockHolder : IDisposable
    {
        private readonly Process _process;

        private FlockHolder(Process process) => _process = process;

        internal static FlockHolder Start(string path)
        {
            // POSIX only, and said so rather than skipped.
            if (OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("flock-hold.py takes a POSIX flock");

            var start = new ProcessStartInfo("python3")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add(Script());
            start.ArgumentList.Add(path);

            return new FlockHolder(Process.Start(start)
                ?? throw new InvalidOperationException("python3 flock-hold.py did not start"));
        }

        internal async Task<string?> ReadLineAsync(CancellationToken ct) =>
            await _process.StandardOutput.ReadLineAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(30), ct);

        public void Dispose()
        {
            try
            {
                _process.StandardInput.Close();
                if (!_process.WaitForExit(TimeSpan.FromSeconds(10))) _process.Kill(entireProcessTree: true);
            }
            finally
            {
                _process.Dispose();
            }
        }

        private static string Script()
        {
            for (var at = new DirectoryInfo(AppContext.BaseDirectory); at is not null; at = at.Parent)
            {
                var candidate = Path.Combine(at.FullName, "tests", "Shared", "flock-hold.py");
                if (File.Exists(candidate)) return candidate;
            }

            throw new InvalidOperationException(
                "tests/Shared/flock-hold.py was not found above " + AppContext.BaseDirectory);
        }
    }
}
