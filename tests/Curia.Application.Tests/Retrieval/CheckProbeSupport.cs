using System.Security.Cryptography;
using System.Text;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Retrieval;
using Curia.Application.Tests.InMemory;
using Curia.Domain;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Curia.Domain.Search;
using Curia.Canon.Json;
using static Curia.Application.Tests.Retrieval.RetrievalTestLog;

namespace Curia.Application.Tests.Retrieval;

/// <summary>
/// A log that is generated, not stored (errata G54's probes CPF9 to CPF14, CPF21 and CPF30): event <c>i</c> is a <c>post.accepted</c> of a question whose id, digest and
/// text are a function of <c>i</c>, served in pages as the real reader serves them, so a log of twenty thousand events costs the test no append. It keeps no page it
/// served (a probe that asks whether the check holds one must not have the reader hold it for it), only a <see cref="WeakReference"/> to each, and it is the counting
/// reader of the plan: the scans it served, the size each read asked for, and a hook that runs at the start of every scan read.
/// </summary>
internal sealed class GeneratedLog(int count) : IEventReader
{
    private static readonly DateTimeOffset Stamp = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly List<WeakReference> _pages = [];
    private readonly Dictionary<int, string> _texts = [];
    private int _scanReads;

    /// <summary>Events in the log.</summary>
    internal int Count { get; } = count;

    /// <summary>The seq of an event whose text is not placeable by the model (a title and a body with no letter or digit); the others are placeable.</summary>
    internal HashSet<int> Unplaceable { get; } = [];

    /// <summary>The instant the probe is driving the catch-up at: set before each tick, read by the scans it records.</summary>
    internal DateTimeOffset Now { get; set; }

    /// <summary>Every whole-log read (a forward read from the start asking for a page), in order: the instant <see cref="Now"/> held when it began.</summary>
    internal List<DateTimeOffset> ScanStarts { get; } = [];

    /// <summary>Every forward read, whatever it asked for.</summary>
    internal int ForwardReads { get; private set; }

    /// <summary>The largest <c>maxCount</c> any forward read asked for.</summary>
    internal int LargestAsk { get; private set; }

    /// <summary>Runs at the start of every whole-log page read with its 1-based number within the scan; may throw (the log gone unreadable mid-scan) or act (an exit arriving during it).</summary>
    internal Action<int>? OnScanRead { get; set; }

    /// <summary>Whole-log page reads (those that ask for a page from the start or after a page) that throw <see cref="LogUnreadableException"/> from this number on; 0 for none.</summary>
    internal int UnreadableFromScanRead { get; set; }

    /// <summary>The pages served that are still alive, by their weak references.</summary>
    internal int PagesAlive
    {
        get
        {
            lock (_pages) return _pages.Count(p => p.IsAlive);
        }
    }

    /// <summary>Whether the first page ever served is still alive.</summary>
    internal bool FirstPageAlive
    {
        get
        {
            lock (_pages) return _pages.Count > 0 && _pages[0].IsAlive;
        }
    }

    internal static string PostId(int seq) => $"gen-post-{seq:D7}";

    internal static string DigestOf(int seq) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(PostId(seq))));

    private string CanonicalAt(int seq)
    {
        lock (_texts)
        {
            if (_texts.TryGetValue(seq, out var cached)) return cached;
            var unplaceable = Unplaceable.Contains(seq);
            var canonical = Canonical(
                PostKind.Question,
                "https://agents.example/generated",
                "generated",
                unplaceable ? "???" : $"Generated question {seq} about quokka habitat",
                unplaceable ? "!!!" : $"Body of generated question {seq}: the quokka and its habitat number {seq}",
                []);
            _texts[seq] = canonical;
            return canonical;
        }
    }

    /// <summary>Event <paramref name="seq"/> (1-based) as the log serves it.</summary>
    internal AppendedEvent EventAt(int seq)
    {
        var postId = PostId(seq);
        var payload = new JsonValue.Object(
        [
            new("post_id", new JsonValue.String(postId)),
            new("canonical", new JsonValue.String(CanonicalAt(seq))),
            new("signature", new JsonValue.String("sig")),
            new("digest", new JsonValue.String(DigestOf(seq))),
            new("author", new JsonValue.String("https://agents.example/generated")),
            new("board", new JsonValue.String("generated")),
            new("kind", new JsonValue.String("question")),
        ]);
        return new AppendedEvent(
            new EventSequence(seq),
            Require(AggregateId.Create(postId)),
            ServerTimestamp.At(Stamp),
            new DomainEvent(
                Require(EventId.Create(postId)),
                Require(EventType.Create(PostProjector.PostAcceptedType)),
                Require(ActorId.Create("https://agents.example/generated")),
                payload));
    }

    /// <summary>The post event <paramref name="seq"/> carries, as the route would hand it to the indexer.</summary>
    internal SearchablePost PostAt(int seq) => SearchProjector.TryRead(EventAt(seq)) ?? throw new InvalidOperationException("the generated event is not a searchable post");

    public Task<Result<IReadOnlyList<AppendedEvent>>> ReadByAggregateAsync(AggregateId aggregateId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<IReadOnlyList<AppendedEvent>>.Ok([]));

    public Task<Result<IReadOnlyList<AppendedEvent>>> ReadForwardAsync(EventSequence afterSeq, int? maxCount = null, CancellationToken cancellationToken = default)
    {
        ForwardReads++;
        var ask = maxCount ?? int.MaxValue;
        LargestAsk = Math.Max(LargestAsk, ask == int.MaxValue ? 0 : ask);

        var isScanRead = ask >= EventReaderExtensions.PageSize;
        if (isScanRead)
        {
            _scanReads = afterSeq.Value == 0 ? 1 : _scanReads + 1;
            if (afterSeq.Value == 0) ScanStarts.Add(Now);
            OnScanRead?.Invoke(_scanReads);
            if (UnreadableFromScanRead > 0 && _scanReads >= UnreadableFromScanRead)
                throw new LogUnreadableException("generated log unreadable mid-scan", new InvalidOperationException("armed by the test"));
        }

        var first = (int)afterSeq.Value + 1;
        var take = Math.Max(0, Math.Min(ask, Count - first + 1));
        var page = new List<AppendedEvent>(take);
        for (var seq = first; seq < first + take; seq++) page.Add(EventAt(seq));
        lock (_pages) _pages.Add(new WeakReference(page));
        return Task.FromResult(Result<IReadOnlyList<AppendedEvent>>.Ok(page));
    }
}

/// <summary>An index that holds a gate: the next upserts wait until the probe releases them, and the probe is told when one has arrived (the plan's "gated indexer").</summary>
internal sealed class GatedIndex(IVectorIndex inner) : IVectorIndex
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task Entered => _entered.Task;

    internal void Release() => _released.TrySetResult();

    public async Task<Result<IndexedVector>> UpsertAsync(string digest, string postId, long sequence, Embedding embedding, CancellationToken cancellationToken = default)
    {
        _entered.TrySetResult();
        await _released.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await inner.UpsertAsync(digest, postId, sequence, embedding, cancellationToken).ConfigureAwait(false);
    }

    public Task<Result<System.Collections.Immutable.ImmutableArray<VectorMatch>>> NearestAsync(Embedding query, int limit, CancellationToken cancellationToken = default) =>
        inner.NearestAsync(query, limit, cancellationToken);

    public Task<Result<long>> CountAsync(EmbeddingModel model, CancellationToken cancellationToken = default) => inner.CountAsync(model, cancellationToken);

    public Task<Result<IReadOnlySet<string>>> IndexedDigestsAsync(EmbeddingModel model, CancellationToken cancellationToken = default) => inner.IndexedDigestsAsync(model, cancellationToken);
}

/// <summary>
/// The types of errata G54 over a generated log: an index that can fail, the backlog, the sweep and the catch-up, with every event of the log indexed except the ones
/// the probe names (by seq), indexed by the hashed embedder into the index and never by the code under test.
/// </summary>
internal sealed class CheckRig
{
    internal static readonly DateTimeOffset Start = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    internal CheckRig(GeneratedLog log)
    {
        Log = log;
        Clock = new ManualTimeProvider(Start);
        Embedder = new HashedNGramEmbedder();
        Inner = new InMemoryVectorIndex();
        Index = new FlakyIndex(Inner);
        Indexer = new EmbeddingIndexer(Embedder, Index);
        Backlog = new IndexBacklog();
        Sweep = new IndexSweep(Start);
        Committed = new CommittedPostIndexer(Indexer, Backlog, Clock, Sweep);
        CatchUp = new EmbeddingCatchUp(Indexer, Backlog, Sweep, Clock);
    }

    internal GeneratedLog Log { get; }

    internal ManualTimeProvider Clock { get; }

    internal HashedNGramEmbedder Embedder { get; }

    internal InMemoryVectorIndex Inner { get; }

    internal FlakyIndex Index { get; }

    internal EmbeddingIndexer Indexer { get; }

    internal IndexBacklog Backlog { get; }

    internal IndexSweep Sweep { get; }

    internal CommittedPostIndexer Committed { get; }

    internal EmbeddingCatchUp CatchUp { get; }

    /// <summary>Indexes every event of the log except those in <paramref name="absent"/>, straight into the inner index (the FlakyIndex counts nothing of it).</summary>
    internal async Task IndexAllExceptAsync(IReadOnlySet<int> absent, CancellationToken ct)
    {
        for (var seq = 1; seq <= Log.Count; seq++)
        {
            if (absent.Contains(seq) || Log.Unplaceable.Contains(seq)) continue;
            var post = Log.PostAt(seq);
            var embedded = Require(Embedder.Embed(EmbeddedText.Of(post)));
            Require(await Inner.UpsertAsync(post.Digest, post.PostId, post.Sequence, embedded, ct).ConfigureAwait(false));
        }
    }

    /// <summary>The digests the index holds now.</summary>
    internal async Task<HashSet<string>> HeldAsync(CancellationToken ct) =>
        [.. Require(await Index.IndexedDigestsAsync(Embedder.Model, ct).ConfigureAwait(false))];

    /// <summary>The digests the backlog holds, whenever they fall due.</summary>
    internal string[] Enrolled() => [.. Backlog.Due(DateTimeOffset.MaxValue).Select(e => e.Digest)];

    /// <summary>One tick of the catch-up at <paramref name="now"/> (the loop's own call), the log told what instant it is.</summary>
    internal Task<CatchUpResult> TickAsync(DateTimeOffset now, CancellationToken ct)
    {
        Log.Now = now;
        return CatchUp.RunOnceAsync(Log, now, ct);
    }
}

/// <summary>
/// Holds the private lock of an <see cref="IndexSweep"/> or an <see cref="IndexBacklog"/> on a thread of its own, so a probe can stop a check or a search at the one
/// call that waits for it, and run what it likes (on that same thread, which owns the lock) before letting it go. The lock is the class's own, found by name.
/// </summary>
internal sealed class HeldLock
{
    private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _go = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _done;

    private HeldLock(object owner, Action whileHeld)
    {
        var gate = (Lock)(owner.GetType().GetField("_gate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(owner)
            ?? throw new InvalidOperationException("the class holds no lock named _gate"));
        _done = Task.Factory.StartNew(
            () =>
            {
                using (gate.EnterScope())
                {
                    _held.SetResult();
                    _go.Task.Wait(TimeSpan.FromSeconds(60));
                    whileHeld();
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    /// <summary>Takes the lock of <paramref name="owner"/>; <paramref name="whileHeld"/> runs on the holding thread when <see cref="ReleaseAsync"/> is called, before the lock is let go.</summary>
    internal static HeldLock Take(object owner, Action whileHeld) => new(owner, whileHeld);

    /// <summary>Completes when the lock is held.</summary>
    internal Task Held => _held.Task;

    /// <summary>Runs the held action, lets the lock go, and completes when that is done.</summary>
    internal Task ReleaseAsync()
    {
        _go.TrySetResult();
        return _done;
    }
}

/// <summary>An embedder that runs a probe's action the first time it is asked to embed, then answers as the embedder it wraps does.</summary>
internal sealed class HookedEmbedder(ITextEmbedder inner, Action onFirstEmbed) : ITextEmbedder
{
    private int _asked;

    public EmbeddingModel Model => inner.Model;

    public Result<Embedding> Embed(string text)
    {
        if (Interlocked.Increment(ref _asked) == 1) onFirstEmbed();
        return inner.Embed(text);
    }
}
