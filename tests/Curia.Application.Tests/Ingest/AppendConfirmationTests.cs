using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ingest;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Retrieval;
using Curia.Application.Tests.InMemory;
using Curia.Application.Tests.Retrieval;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Application.Tests.Ingest;

/// <summary>
/// AFC11 and AFC16(a), AFC28 (errata G55, R8.84): the confirm of a faulted append at the unit, over an in-memory log and counting or failing readers. The confirm reads the
/// events after a sequence, in pages, and matches a digest by <see cref="PostProjector.FirstWithDigest"/>, the rule R8.78 refuses a repeat by; every fault of the reader is an
/// outcome. A probe is written in its failing form and is red on the seam, where the confirm is declared and reports nothing found.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AppendConfirmationTests
{
    private const string Author = "https://agents.example/asker";
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(PublishedConfirmWords.ConfirmBoundSeconds);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void Said(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    private static string Digest(string seed) => "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed)));

    private static async Task AppendPostAsync(InMemoryEventStore store, string postId, string digest)
    {
        var members = new List<KeyValuePair<string, JsonValue>>
        {
            new("post_id", new JsonValue.String(postId)),
            new("canonical", new JsonValue.String(RetrievalTestLog.Canonical(PostKind.Question, Author, "b", "A title?", "A body of " + postId, []))),
            new("signature", new JsonValue.String("sig")),
            new("digest", new JsonValue.String(digest)),
            new("author", new JsonValue.String(Author)),
            new("board", new JsonValue.String("b")),
            new("kind", new JsonValue.String(PostKinds.Wire(PostKind.Question))),
        };
        RetrievalTestLog.Require(await store.AppendAsync(
            RetrievalTestLog.Require(AggregateId.Create(postId)),
            AggregateVersion.New,
            [new DomainEvent(
                RetrievalTestLog.Require(EventId.Create(postId)),
                RetrievalTestLog.Require(EventType.Create(PostProjector.PostAcceptedType)),
                RetrievalTestLog.Require(ActorId.Create(Author)),
                new JsonValue.Object([.. members]))],
            Ct).ConfigureAwait(false));
    }

    private static string Says(ConfirmOutcome outcome) =>
        outcome.Match(f => $"found {f.PostId} at sequence {f.Sequence.Value}", () => "absent", () => "unreadable", () => "bound", () => "stopped");

    /// <summary>A reader that fails as the fault says; a reader never answers a call it was not asked.</summary>
    private sealed class FailingReader(Func<CancellationToken, Task<Result<IReadOnlyList<AppendedEvent>>>> fail) : IEventReader
    {
        public Task<Result<IReadOnlyList<AppendedEvent>>> ReadByAggregateAsync(AggregateId aggregateId, CancellationToken cancellationToken = default) => fail(cancellationToken);

        public Task<Result<IReadOnlyList<AppendedEvent>>> ReadForwardAsync(EventSequence afterSeq, int? maxCount = null, CancellationToken cancellationToken = default) => fail(cancellationToken);
    }

    /// <summary>
    /// AFC11. The predicate and the paging. A log holding the digest at the second page: found, with the page-two sequence, in two reads; the digest before the anchor: not
    /// matched; a log holding the digest twice: the first in log order, the post <see cref="PostProjector.FirstWithDigest"/> names; every fault of the reader (a
    /// <see cref="LogUnreadableException"/>, a <see cref="TimeoutException"/>, an exception nothing names, the host stopping) is an outcome and the confirm never throws.
    /// </summary>
    [Fact]
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The fact is that the confirm throws nothing: any exception it let out is reported as a leg's actual text.")]
    public async Task R8_84_AFC11_TheConfirmMatchesByTheOnePredicateInPagesAndEveryFaultIsAnOutcome()
    {
        var clock = RetrievalTestLog.Clock();
        var store = new InMemoryEventStore(clock, new PermissiveLifecycleAppendGate());
        for (var i = 0; i < EventReaderExtensions.PageSize; i++) await AppendPostAsync(store, $"filler-{i:D5}", Digest($"filler-{i}"));
        var target = Digest("target");
        await AppendPostAsync(store, "target-first", target);
        await AppendPostAsync(store, "target-second", target);
        var counting = new CountingEventStore(store);
        var whole = RetrievalTestLog.Require(await store.ReadAllAsync(Ct));
        var first = PostProjector.FirstWithDigest(whole, target) ?? throw new InvalidOperationException("the fixture holds no post of the digest");

        var legs = new List<(string Expected, string Actual)>();
        async Task<string> RunAsync(IEventReader reader, EventSequence after, string digest, CancellationToken host)
        {
            try
            {
                return Says(await AppendConfirmation.ConfirmAsync(reader, after, digest, clock, Bound, host).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                return $"threw {ex.GetType().Name}";
            }
        }

        var found = await RunAsync(counting, EventSequence.Zero, target, Ct);
        legs.Add(($"the digest on the second page: found {first.Post.PostId} at sequence {first.Sequence.Value}, in 2 reads", $"the digest on the second page: {found}, in {counting.Reads} reads"));
        // Anchored after the last event: both posts of the digest are before the anchor. (Anchored at the first of them, the delta would hold the second, which carries
        // the same digest and is correctly found; the probe as first written expected "absent" there, which the one predicate does not give.)
        var anchored = await RunAsync(store, whole[^1].Seq, target, Ct);
        legs.Add(("the digest before the anchor: absent", $"the digest before the anchor: {anchored}"));
        var unmatched = await RunAsync(store, EventSequence.Zero, Digest("never"), Ct);
        legs.Add(("a digest the log does not hold: absent", $"a digest the log does not hold: {unmatched}"));

        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync().ConfigureAwait(true);
        var faults = new (string Name, string Expected, Func<CancellationToken, Task<Result<IReadOnlyList<AppendedEvent>>>> Fail, CancellationToken Host)[]
        {
            ("a LogUnreadableException", "unreadable", _ => throw new LogUnreadableException("armed by the probe"), default),
            ("a TimeoutException", "unreadable", _ => throw new TimeoutException("armed by the probe"), default),
            ("an exception nothing names", "unreadable", _ => throw new InvalidOperationException("armed by the probe"), default),
            ("the host stopping", "stopped", token => Task.Delay(Timeout.Infinite, token).ContinueWith<Result<IReadOnlyList<AppendedEvent>>>(_ => throw new OperationCanceledException(token), TaskScheduler.Default), stopping.Token),
        };
        foreach (var (name, expected, fail, host) in faults)
            legs.Add(($"{name}: {expected}", $"{name}: {await RunAsync(new FailingReader(fail), EventSequence.Zero, target, host)}"));

        Said(string.Join(" | ", legs.Select(l => l.Expected)), string.Join(" | ", legs.Select(l => l.Actual)));
    }
}
