using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Curia.Application.Ports;
using Curia.Application.Retrieval;
using Curia.Domain.Primitives;
using Curia.Domain.Search;
using Curia.Infrastructure.Embedder;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Infrastructure.Tests.Embedder;

/// <summary>
/// Errata G65 (D10a), SEM3 (runner half), SEM9 to SEM12, SEM39, SEM40 and SEM48, model-free: the adapter over a fake runner and the synthetic vocabulary. Every probe first asserts that the
/// fake runner was reached where it should be, so an embedder that answers without running anything cannot pass for one that runs.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class EmbedderBehaviourTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>An index that stores nothing and accepts everything: the probes here are about the embedder.</summary>
    private sealed class AcceptingIndex : IVectorIndex
    {
        public Task<Result<IndexedVector>> UpsertAsync(string digest, string postId, long sequence, Embedding embedding, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IndexedVector>.Ok(new IndexedVector(digest, postId, sequence)));

        public Task<Result<System.Collections.Immutable.ImmutableArray<VectorMatch>>> NearestAsync(Embedding query, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<System.Collections.Immutable.ImmutableArray<VectorMatch>>.Ok([]));

        public Task<Result<long>> CountAsync(EmbeddingModel model, CancellationToken cancellationToken = default) => Task.FromResult(Result<long>.Ok(0));

        public Task<Result<IReadOnlySet<string>>> IndexedDigestsAsync(EmbeddingModel model, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlySet<string>>.Ok(new HashSet<string>()));
    }

    private static string TypeOf(Result<Embedding> result) => result.Match(_ => "(a vector)", e => e.Type);

    private static EmbedderRig Checked()
    {
        var rig = EmbedderRig.Create();
        rig.Embedder.Check();
        return rig;
    }

    // ---- SEM9: totality ----

    public static TheoryData<string, string> Hostile() => new()
    {
        { "U+FFFE", Cp.Of(0xFFFE) },
        { "a lone high surrogate", "hello " + Cp.Unit(0xD800) + " world" },
        { "a lone low surrogate", "hello " + Cp.Unit(0xDC00) + " world" },
        { "NUL", "hello" + Cp.Unit(0) + "world" },
        { "the empty string", string.Empty },
        { "white space", " \t\r\n " },
        { "one mebibyte", string.Concat(Enumerable.Repeat("hello world ", 1024 * 1024 / 12)) },
    };

    /// <summary>
    /// SEM9. Any input answers a vector, <c>NoFeatures</c> or <c>ModelFault</c> and none throws. Predicted red on the seam: <c>expected a vector, curia/embedding/no-features or
    /// curia/embedding/model-fault; got curia/embedding/model-not-ready</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(Hostile))]
    public async Task R11_92_SEM9_AnyInputAnswersAVectorNoFeaturesOrAFault(string name, string text)
    {
        using var rig = Checked();

        var result = await rig.Embedder.EmbedAsync(text, EmbeddingRole.Passage, Ct);

        var type = TypeOf(result);
        var allowed = new[] { "(a vector)", EmbeddingErrors.NoFeatures().Type, EmbeddingErrors.ModelFault().Type };
        Assert.True(allowed.Contains(type), $"{name}: expected a vector, {allowed[1]} or {allowed[2]}; got {type}");
    }

    // ---- SEM10: error text ----

    /// <summary>
    /// SEM10. A runner that throws an exception whose message holds a file path and the input gives a fixed <c>ModelFault</c> whose detail is the exception's type name and nothing else: the
    /// <c>Error</c> holds neither the path nor the input. (Through the HTTP layer: SEM24.) Predicted red on the seam: <c>expected curia/embedding/model-fault, got curia/embedding/model-not-ready</c>.
    /// </summary>
    [Fact]
    public async Task R11_92_SEM10_AnExceptionsMessageNeverReachesTheError()
    {
        using var rig = Checked();
        const string secret = "/srv/secret-path/model.onnx";
        rig.Runner.Behaviour = (_, _) => throw new InvalidOperationException($"could not read {secret} while embedding 'hello world'");

        var result = await rig.Embedder.EmbedAsync("hello world", EmbeddingRole.Passage, Ct);

        Assert.True(rig.Runner.Runs >= 1, "NOT REACHED: the runner was not asked to run");
        var error = result.Match(_ => throw new InvalidOperationException("the embedder answered a vector from a runner that threw"), e => e);
        Assert.Equal(EmbeddingErrors.ModelFault().Type, error.Type);
        Assert.Equal(nameof(InvalidOperationException), error.Detail);
        foreach (var text in new[] { error.Type, error.Title, error.Detail ?? string.Empty })
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
            Assert.DoesNotContain("hello world", text, StringComparison.Ordinal);
        }
    }

    // ---- SEM12: placeable ----

    /// <summary>
    /// SEM12 (model-free half). A text of only unknown tokens (an emoji) is <c>NoFeatures</c>; one known token among unknowns is placed; the empty string is <c>NoFeatures</c>. The integration form
    /// is Task 0 M16's measurement and cannot be run on the seam (no adapter to put under the Forum): two different emoji-only questions by one agent on one board, the second
    /// <c>409 curia/posts/duplicate-question</c>, <c>cosine=1.000 lexical_overlap=1.000 model=bge-small-en-v1.5@1</c>, exit 3 (ruling 31). Predicted red on the seam: <c>expected curia/embedding/no-features,
    /// got curia/embedding/model-not-ready</c>.
    /// </summary>
    [Fact]
    public async Task R9_64_SEM12_ATextOfOnlyUnknownTokensHasNoFeaturesAndOneKnownTokenIsPlaced()
    {
        using var rig = Checked();
        var emoji = Cp.Of(0x1F600);

        Assert.Equal(EmbeddingErrors.NoFeatures().Type, TypeOf(await rig.Embedder.EmbedAsync(emoji, EmbeddingRole.Passage, Ct)));
        Assert.Equal(EmbeddingErrors.NoFeatures().Type, TypeOf(await rig.Embedder.EmbedAsync(emoji + " " + Cp.Of(0x1F680), EmbeddingRole.Passage, Ct)));
        Assert.Equal(EmbeddingErrors.NoFeatures().Type, TypeOf(await rig.Embedder.EmbedAsync(string.Empty, EmbeddingRole.Passage, Ct)));
        Assert.Equal("(a vector)", TypeOf(await rig.Embedder.EmbedAsync("hello " + emoji, EmbeddingRole.Passage, Ct)));
        Assert.True(rig.Runner.Runs >= 1, "NOT REACHED: no run happened, so the placed text was not embedded");
    }

    // ---- SEM11: concurrency ----

    /// <summary>
    /// SEM11. Eight callers through a counting runner: never more than two inside; a caller whose token is cancelled before it enters runs nothing; a waiter cancelled after it is queued leaves, and
    /// its place is given to the next. Predicted red on the seam: <c>NOT REACHED: the runner was entered 0 times</c>.
    /// </summary>
    [Fact]
    public async Task R11_92_SEM11_NoMoreThanTwoRunAtOnceAndACancelledWaiterRunsNothing()
    {
        using var rig = Checked();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Runner.Behaviour = async (input, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return FakeRunner.HiddenStateFor(input);
        };

        var calls = Enumerable.Range(0, 8).Select(i => rig.Embedder.EmbedAsync("hello " + EmbedderRig.Words[i], EmbeddingRole.Passage, Ct)).ToArray();
        await WaitUntilAsync(() => rig.Runner.Runs >= 2, "NOT REACHED: the runner was entered " + rig.Runner.Runs + " times, expected 2 before the release");
        Assert.True(rig.Runner.MaxInside <= 2, $"expected at most 2 inside, saw {rig.Runner.MaxInside}");

        using var early = new CancellationTokenSource();
        await early.CancelAsync();
        var before = rig.Runner.Runs;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Embedder.EmbedAsync("hello", EmbeddingRole.Passage, early.Token));
        Assert.Equal(before, rig.Runner.Runs);

        using var queuedThenCancelled = new CancellationTokenSource();
        var cancelled = rig.Embedder.EmbedAsync("hello world", EmbeddingRole.Passage, queuedThenCancelled.Token);
        await queuedThenCancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        release.SetResult();
        var results = await Task.WhenAll(calls);
        Assert.All(results, r => Assert.Equal("(a vector)", TypeOf(r)));
        Assert.True(rig.Runner.MaxInside <= 2, $"expected at most 2 inside, saw {rig.Runner.MaxInside}");
        Assert.Equal(8, rig.Runner.Runs);
    }

    /// <summary>
    /// SEM11, arrival order (ruling 23). Both places held, twenty callers queued one at a time (each seen queued before the next is added), the places released one at a time: the runner is entered
    /// in the order the callers arrived. Predicted red on the seam: <c>caller 0 was never seen queued</c> (the seam's embedder has no gate).
    /// </summary>
    [Fact]
    public async Task R11_92_SEM11_CallersAreAdmittedInTheOrderTheyArrived()
    {
        using var rig = Checked();
        var holds = new List<TaskCompletionSource>();
        var arrivals = new List<string>();
        rig.Runner.Behaviour = async (input, ct) =>
        {
            var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (holds) holds.Add(hold);
            lock (arrivals) arrivals.Add(string.Join(',', input.InputIds));
            await hold.Task.WaitAsync(ct);
            return FakeRunner.HiddenStateFor(input);
        };

        var first = Enumerable.Range(0, 2).Select(i => rig.Embedder.EmbedAsync("hello " + Cp.Of('a' + i), EmbeddingRole.Passage, Ct)).ToList();
        await WaitUntilAsync(() => rig.Runner.Runs >= 2, "NOT REACHED: the runner was entered " + rig.Runner.Runs + " times, expected 2 before the queue is built");

        var expected = new List<string>();
        var queued = new List<Task<Result<Embedding>>>();
        for (var i = 0; i < 20; i++)
        {
            var text = "hello " + EmbedderRig.Words[(i % 20) + 18];
            expected.Add(string.Join(',', BgeInput.Build(new Curia.Infrastructure.Embedder.Tokenization.WordPieceTokenizer(EmbedderRig.Vocabulary), text, EmbeddingRole.Passage, EmbedderRig.QueryPrefix, 512).InputIds));
            queued.Add(rig.Embedder.EmbedAsync(text, EmbeddingRole.Passage, Ct));
            var index = i;
            await WaitUntilAsync(() => rig.Embedder.Gate.Queued >= index + 1, $"caller {index} was never seen queued (the gate holds {rig.Embedder.Gate.Queued} waiting)");
        }

        for (var released = 0; released < 22; released++)
        {
            await WaitUntilAsync(() => { lock (holds) return holds.Count > released; }, $"NOT REACHED: {released} runs started, expected {released + 1}");
            TaskCompletionSource hold;
            lock (holds) hold = holds[released];
            hold.SetResult();
        }

        await Task.WhenAll(first.Concat(queued));
        var admitted = arrivals.Skip(2).ToList();
        var passedOver = Enumerable.Range(0, admitted.Count).FirstOrDefault(i => admitted[i] != expected[i], -1);
        Assert.True(passedOver < 0, $"expected admission order [0..19] but waiter {passedOver} was admitted while an earlier one was still queued");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string failure)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, failure);
            await Task.Delay(10, Ct);
        }
    }

    // ---- SEM3, the runner half ----

    /// <summary>
    /// SEM3 (runner half, ruling 23). A run that waits for its caller, a clock advanced 2 seconds: <c>CommittedPostIndexer</c> answers <c>Pending</c> with <c>curia/retrieval/index-bound</c>, and the run
    /// is asked to stop (it sees its token cancelled) and its place is free: a second text is embedded at once. Predicted red on the seam: <c>NOT REACHED: the runner was not entered</c>.
    /// </summary>
    [Fact]
    public async Task R11_92_SEM3_WhenTheBoundExpiresTheRunIsAskedToStopAndItsPlaceIsFree()
    {
        using var rig = Checked();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = true;
        rig.Runner.Behaviour = async (input, ct) =>
        {
            if (first)
            {
                first = false;
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }

            return FakeRunner.HiddenStateFor(input);
        };

        var start = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        var clock = new TimerManualTimeProvider(start);
        var post = new SearchablePost(
            "post-1", "sha256:" + new string('a', 64), "postgres", Curia.Domain.Content.PostKind.Question, "hello", "hello world", [], "https://agents.example/a", 1);
        var backlog = new IndexBacklog();
        var committed = new CommittedPostIndexer(new EmbeddingIndexer(rig.Embedder, new AcceptingIndex()), backlog, clock, new IndexSweep(start));

        var step = committed.IndexAsync(post, Ct);
        await WaitUntilAsync(() => entered.Task.IsCompleted, "NOT REACHED: the runner was not entered");
        clock.Advance(CommittedPostIndexer.Bound);
        var outcome = await step.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal("curia/retrieval/index-bound", outcome.FaultType);
        await WaitUntilAsync(() => rig.Runner.StopsObserved >= 1, "the run was never asked to stop: its token was not cancelled when the bound expired");
        var second = await rig.Embedder.EmbedAsync("hello world", EmbeddingRole.Passage, Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal("(a vector)", TypeOf(second));
    }

    // ---- SEM39, SEM40: the shape and the warm-up ----

    /// <summary>
    /// SEM39. The session is asked for two intra-op threads and one inter-op thread, and the gate admits two. Predicted red on the seam: <c>the loader was never asked to load</c>.
    /// </summary>
    [Fact]
    public void R11_92_SEM39_TheSessionRunsWithTwoIntraOpThreadsOneInterOpThreadAndAGateOfTwo()
    {
        using var rig = Checked();

        Assert.True(rig.Loader.LoadCalls == 1, $"the loader was asked to load {rig.Loader.LoadCalls} time(s), expected once");
        Assert.Equal(2, rig.Loader.LastOptions!.IntraOpNumThreads);
        Assert.Equal(1, rig.Loader.LastOptions.InterOpNumThreads);
        Assert.Equal(2, rig.Embedder.Gate.Limit);
    }

    /// <summary>
    /// SEM40. The start runs at least three inferences on a text of 510 content tokens (512 ids) through the runner before it returns. Predicted red on the seam: <c>expected at least 3 warm-up
    /// runs of 512 ids, saw 0</c>.
    /// </summary>
    [Fact]
    public void R11_92_SEM40_TheSessionIsWarmedWithThreeLongestTextsBeforeTheFirstRequest()
    {
        using var rig = Checked();

        var longest = rig.Runner.SequenceLengths.Count(n => n == 512);
        Assert.True(longest >= 3, $"expected at least 3 warm-up runs of 512 ids, saw {longest}");
    }

    // ---- SEM48: a native library that cannot load ----

    public static TheoryData<string> LoaderFaults() => new() { "DllNotFoundException", "BadImageFormatException", "EntryPointNotFoundException" };

    /// <summary>
    /// SEM48. A loader that throws what a platform without the native library throws, with a message holding a path, stops the start with an <c>InvalidOperationException</c> that names
    /// <c>Microsoft.ML.OnnxRuntime</c> and the operating system and architecture and holds neither the exception's message nor a path; a loader that works boots. Predicted red on the seam:
    /// <c>Assert.Throws() Failure: No exception was thrown</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(LoaderFaults))]
    public void R11_91_SEM48_ANativeLibraryThatCannotLoadIsARefusalNamingThePackageAndThePlatform(string fault)
    {
        using var rig = EmbedderRig.Create();
        const string path = "/home/someone/.nuget/packages/microsoft.ml.onnxruntime/1.31.0/runtimes/linux-x64/native/libonnxruntime.so";
        Exception thrown = fault switch
        {
            "DllNotFoundException" => new DllNotFoundException("unable to load " + path),
            "BadImageFormatException" => new BadImageFormatException("wrong architecture: " + path),
            _ => new EntryPointNotFoundException("OrtGetApiBase in " + path),
        };
        var loader = new FakeLoader(rig.Runner) { Throws = thrown };
        using var embedder = rig.Over(rig.Manifest, loader);

        var refusal = Assert.Throws<InvalidOperationException>(embedder.Check);

        Assert.Contains("Microsoft.ML.OnnxRuntime", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(RuntimeInformation.OSDescription, refusal.Message, StringComparison.Ordinal);
        Assert.Contains(RuntimeInformation.ProcessArchitecture.ToString(), refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(thrown.Message, refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("libonnxruntime", refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(rig.Directory, refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>SEM48, the served control: a loader that works boots and the embedder answers a vector.</summary>
    [Fact]
    public async Task R11_91_SEM48_ALoaderThatWorksBoots()
    {
        using var rig = Checked();

        Assert.Equal("(a vector)", TypeOf(await rig.Embedder.EmbedAsync("hello world", EmbeddingRole.Passage, Ct)));
    }
}
