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
    ///
    /// <para>B's refusal is the oracle for the lock spanning the fetch. A lock around a re-read and the
    /// write lets B retain 20 and A report 10 to 15 verified without comparing 15 with 20; the
    /// final-size oracle alone cannot see that.</para>
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

        // B started while A held the lock across its fetch.
        Assert.Equal(CheckOutcome.CouldNotCheck, b.Consistency.Outcome);
        Assert.Contains("held elsewhere", b.Consistency.Detail, StringComparison.Ordinal);

        Assert.Equal(CheckOutcome.Verified, first.Consistency.Outcome);
        Assert.Equal(15, Retained(first));
        Assert.Equal(15, heads.Read(StubLog.Forum)!.TreeSize);

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
        Assert.Contains("held elsewhere", result.Consistency.Detail, StringComparison.Ordinal);
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
    /// The lock's bound and its retry are both measured on the <see cref="TimeProvider"/> it is given
    /// (CS-9), each by a phase that carries information without leaning on real-time margins.
    ///
    /// <para><b>Phase 1, the bound is the provider's.</b> With the lock held and a 10 min wait, the
    /// provider is advanced past the wait at once, before any real time passes. A bound read from the
    /// wall clock sees under 10 min, parks again on a provider delay that never fires, and the 5 s
    /// real-time wait for the refusal times out.</para>
    ///
    /// <para><b>Phase 2, the retry delay is the provider's.</b> The lock is released after
    /// <c>LockAsync</c> started. After 1 s of real time it has not completed: a retry delayed on the
    /// wall clock would have tried again and taken the freed lock. Advanced 50 ms, it takes it.</para>
    /// </summary>
    [Fact]
    public async Task R6_53_TheWaitIsTheTimeProvidersNotTheWallClocks()
    {
        var ct = TestContext.Current.CancellationToken;
        var heads = new HeadStore(_home);
        var directory = heads.DirectoryFor(StubLog.Forum);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "head.lock");
        var wait = TimeSpan.FromMinutes(10);

        // Phase 1: the bound. LockAsync's first contended attempt and its delay registration run
        // before the task is returned, so advancing at once is advancing a parked wait.
        using (new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero));
            var locking = heads.LockAsync(StubLog.Forum, wait, clock, ct);
            Assert.False(locking.IsCompleted, "the lock was taken while another open held it");

            clock.Advance(wait + TimeSpan.FromMilliseconds(50));

            var refused = await locking.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.False(refused.TryGetValue(out var lease, out var refusal));
            lease?.Dispose();
            Assert.Contains("held elsewhere", refusal!.Detail, StringComparison.Ordinal);
        }

        // Phase 2: the retry.
        var held = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Task<Curia.Domain.Primitives.Result<IDisposable>> retrying;
        ManualTimeProvider retryClock;
        try
        {
            retryClock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero));
            retrying = heads.LockAsync(StubLog.Forum, wait, retryClock, ct);
            Assert.False(retrying.IsCompleted, "the lock was taken while another open held it");
        }
        finally
        {
            await held.DisposeAsync();
        }

        await Task.Delay(TimeSpan.FromSeconds(1), ct);
        Assert.False(retrying.IsCompleted, "the retry waited on the wall clock");

        retryClock.Advance(TimeSpan.FromMilliseconds(50));

        var taken = await retrying.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.True(taken.TryGetValue(out var retaken, out var failed), failed?.Detail);
        retaken!.Dispose();
    }

    /// <summary>
    /// Only contention is waited for. Any other I/O failure is refused at once, naming the path, and
    /// is never reported as held elsewhere: a false "held elsewhere" is a claim this client did not
    /// check (D37). Each case runs on a clock that is never advanced with a 10 min wait, so a retry
    /// loop could never end and shows as a timeout, with no real-time margin.
    ///
    /// <para><b>Case A</b>: a regular file where the lock's directory belongs, so the directory
    /// cannot be created. <b>Case B</b>: <c>head.lock</c> a symbolic link to itself, so opening it
    /// fails with <c>ELOOP</c>. ENOSPC and EMFILE fall to the same refusal and are not separately
    /// probed.</para>
    /// </summary>
    [Fact]
    public async Task R6_53_AnIOFailureOtherThanContentionIsRefusedAtOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var wait = TimeSpan.FromMinutes(10);

        // Case A: the directory cannot be created.
        var blocked = new HeadStore(Path.Combine(_home, "case-a"));
        var directory = blocked.DirectoryFor(StubLog.Forum);
        Directory.CreateDirectory(Path.Combine(blocked.Root, "logs"));
        await File.WriteAllTextAsync(directory, "not a directory", ct);

        var clockA = new ManualTimeProvider(new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero));
        var caseA = await blocked.LockAsync(StubLog.Forum, wait, clockA, ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.False(caseA.TryGetValue(out var leaseA, out var refusalA));
        leaseA?.Dispose();
        Assert.DoesNotContain("held elsewhere", refusalA!.Detail, StringComparison.Ordinal);
        Assert.Contains("could not be created", refusalA.Detail, StringComparison.Ordinal);
        Assert.Contains(Check.Quote(directory), refusalA.Detail, StringComparison.Ordinal);

        // Case B: head.lock is a symbolic link to itself. POSIX only, and said so rather than skipped.
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("a self-referencing symbolic link is ELOOP on POSIX");

        var looped = new HeadStore(Path.Combine(_home, "case-b"));
        var lockPath = Path.Combine(looped.DirectoryFor(StubLog.Forum), "head.lock");
        Directory.CreateDirectory(looped.DirectoryFor(StubLog.Forum));
        File.CreateSymbolicLink(lockPath, lockPath);

        var clockB = new ManualTimeProvider(new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero));
        var caseB = await looped.LockAsync(StubLog.Forum, wait, clockB, ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.False(caseB.TryGetValue(out var leaseB, out var refusalB));
        leaseB?.Dispose();
        Assert.DoesNotContain("held elsewhere", refusalB!.Detail, StringComparison.Ordinal);
        Assert.Contains("could not be locked at", refusalB.Detail, StringComparison.Ordinal);
        Assert.Contains(Check.Quote(lockPath), refusalB.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A lock the runtime opens without excluding anyone is not a lock. Under
    /// <c>DOTNET_SYSTEM_IO_DISABLEFILELOCKING=1</c> the runtime skips <c>flock</c>, and since it
    /// caches that switch at the first <c>FileStream</c>, it cannot be set in this process: each case
    /// runs in a child, this test assembly run by the xUnit in-process runner on one method, with the
    /// variable set.
    ///
    /// <para><b>Case (a)</b>, the default wiring: a default <see cref="HeadStore"/> refuses naming the
    /// switch. <b>Case (b)</b>, the self-probe on the real <c>FileStream</c> path: with the switch check
    /// bypassed, the lock is refused as not enforced, never as held elsewhere. Each case is its own
    /// child, so a run shows which failed; the runner's summary must count one test run, none failed,
    /// skipped or not run, so a filter matching nothing is red.</para>
    /// </summary>
    [Fact]
    public async Task R6_53_LockingTheRuntimeDoesNotEnforceIsCouldNotCheck()
    {
        var ct = TestContext.Current.CancellationToken;
        var home = Directory.CreateTempSubdirectory("curia-lock-child-").FullName;
        var failures = new List<string>();

        try
        {
            foreach (var method in new[]
            {
                nameof(R6_53_ChildUnderTheProcessLockingSwitch_TheDefaultWiringNamesTheSwitch),
                nameof(R6_53_ChildUnderTheProcessLockingSwitch_TheProbeFindsTheLockUnenforced),
            })
            {
                var (exit, output, error) = await RunChildAsync(method, home, ct);

                string[] summary = ["Total: 1,", "Errors: 0,", "Failed: 0,", "Skipped: 0,", "Not Run: 0,"];
                if (exit != 0 || summary.Any(part => !output.Contains(part, StringComparison.Ordinal)))
                    failures.Add($"{method}: exit {exit}\n--- stdout\n{output}\n--- stderr\n{error}");
            }
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }

        Assert.True(failures.Count == 0, string.Join("\n\n", failures));
    }

    /// <summary>
    /// Case (a) of <see cref="R6_53_LockingTheRuntimeDoesNotEnforceIsCouldNotCheck"/>. In the child,
    /// under the switch, a default <see cref="HeadStore"/> refuses naming the switch, and not as the
    /// self-probe's "not enforced". In this process's own run, where the switch is off, it locks.
    /// </summary>
    [Fact]
    public async Task R6_53_ChildUnderTheProcessLockingSwitch_TheDefaultWiringNamesTheSwitch()
    {
        var ct = TestContext.Current.CancellationToken;
        var home = ChildHome();

        if (UnderTheSwitch())
        {
            AssertTheRuntimeDoesNotLock(home);

            var locked = await new HeadStore(home).LockAsync(StubLog.Forum, TimeSpan.Zero, TimeProvider.System, ct);
            Assert.False(locked.TryGetValue(out var lease, out var refusal));
            lease?.Dispose();
            Assert.Contains(Switch, refusal!.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain("not enforced", refusal.Detail, StringComparison.Ordinal);
        }
        else
        {
            var locked = await new HeadStore(home).LockAsync(StubLog.Forum, TimeSpan.Zero, TimeProvider.System, ct);
            Assert.True(locked.TryGetValue(out var lease, out var refusal), refusal?.Detail);
            lease!.Dispose();
        }
    }

    /// <summary>
    /// Case (b) of <see cref="R6_53_LockingTheRuntimeDoesNotEnforceIsCouldNotCheck"/>. In the child,
    /// under the switch and with the switch check bypassed, the self-probe finds a second exclusive
    /// open succeeding and refuses the lock as not enforced. In this process's own run it locks.
    /// </summary>
    [Fact]
    public async Task R6_53_ChildUnderTheProcessLockingSwitch_TheProbeFindsTheLockUnenforced()
    {
        var ct = TestContext.Current.CancellationToken;
        var heads = new HeadStore(ChildHome()) { LockingDisabled = () => false };

        if (UnderTheSwitch())
        {
            AssertTheRuntimeDoesNotLock(heads.Root);

            var locked = await heads.LockAsync(StubLog.Forum, TimeSpan.Zero, TimeProvider.System, ct);
            Assert.False(locked.TryGetValue(out var lease, out var refusal));
            lease?.Dispose();
            Assert.Contains("not enforced", refusal!.Detail, StringComparison.Ordinal);
            Assert.DoesNotContain("held elsewhere", refusal.Detail, StringComparison.Ordinal);
        }
        else
        {
            var locked = await heads.LockAsync(StubLog.Forum, TimeSpan.Zero, TimeProvider.System, ct);
            Assert.True(locked.TryGetValue(out var lease, out var refusal), refusal?.Detail);
            lease!.Dispose();
        }
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

    private const string ChildHomeVariable = "CURIA_LOCK_CHILD_HOME";

    private static bool UnderTheSwitch() => Environment.GetEnvironmentVariable(Variable) == "1";

    /// <summary>The child's home, which its parent creates and deletes; this test's own otherwise.</summary>
    private string ChildHome() => Environment.GetEnvironmentVariable(ChildHomeVariable) is { Length: > 0 } home
        ? home
        : _home;

    /// <summary>
    /// The precondition of both child cases: the runtime really is not locking, so two exclusive opens
    /// of one path both succeed. Without it a red could be the runtime ignoring the variable.
    /// </summary>
    private static void AssertTheRuntimeDoesNotLock(string home)
    {
        Directory.CreateDirectory(home);
        var path = Path.Combine(home, "precondition.lock");
        using var first = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var second = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    /// <summary>
    /// Runs one method of this test assembly in a child under <c>DOTNET_SYSTEM_IO_DISABLEFILELOCKING=1</c>.
    /// The host is the one running this process: <c>DOTNET_HOST_PATH</c>, else this process if it is
    /// <c>dotnet</c>, else <c>dotnet</c> on the path. Killed, with its tree, after 60 s.
    /// </summary>
    private static async Task<(int Exit, string Output, string Error)> RunChildAsync(
        string method, string home, CancellationToken ct)
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } set
            ? set
            : Environment.ProcessPath is { } self
                && string.Equals(Path.GetFileNameWithoutExtension(self), "dotnet", StringComparison.Ordinal)
                    ? self
                    : "dotnet";

        var start = new ProcessStartInfo(host)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Curia.Client.Tests.dll"));
        start.ArgumentList.Add("-method");
        start.ArgumentList.Add($"{typeof(HeadStoreLockTests).FullName}.{method}");
        start.ArgumentList.Add("-noColor");
        start.Environment[Variable] = "1";
        start.Environment[ChildHomeVariable] = home;

        using var child = Process.Start(start)
            ?? throw new InvalidOperationException($"{host} did not start for {method}");

        var output = child.StandardOutput.ReadToEndAsync(ct);
        var error = child.StandardError.ReadToEndAsync(ct);

        try
        {
            await child.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(60), ct);
        }
        catch (TimeoutException)
        {
            child.Kill(entireProcessTree: true);
            throw;
        }

        return (child.ExitCode, await output, await error);
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
