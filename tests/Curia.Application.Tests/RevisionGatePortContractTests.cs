using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ingest;
using Curia.Application.Ports;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Application.Tests;

/// <summary>
/// What every <see cref="IRevisionGate"/> promises, run against the in-memory adapter here and against Postgres in <c>Curia.Infrastructure.Tests</c>
/// (R11.4). R8.77 (errata G45): one post's revisions are entered one at a time, by the digest the revision names as its <c>prev</c>. Probes RVO19 to
/// RVO21 and RVO54; the shape of <see cref="FlagRaiserGatePortContractTests"/>.
///
/// <para>Every digest is a fresh value, so a fact that fails while holding one cannot block a later fact sharing the adapter's store, and every hold is
/// disposed on every path.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public abstract class RevisionGatePortContractTests
{
    /// <summary>A gate whose holds no other test's gate shares.</summary>
    protected abstract IRevisionGate CreateGate();

    protected static string NewDigest() => "sha256:" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

    protected static IAsyncDisposable Require(Result<IAsyncDisposable> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    protected static async Task<string> OutcomeAsync(Result<IAsyncDisposable> result)
    {
        if (result.TryGetValue(out var hold, out var error))
        {
            await hold!.DisposeAsync().ConfigureAwait(false);
            return "entered";
        }

        return error!.Type;
    }

    /// <summary>RVO19.</summary>
    [Fact]
    public async Task R8_77_ASecondEntryForOneDigestIsRefusedUntilTheFirstIsReleased()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = CreateGate();
        var digest = NewDigest();

        string whileHeld;
        await using (Require(await gate.TryEnterAsync(digest, ct)))
            whileHeld = await OutcomeAsync(await gate.TryEnterAsync(digest, ct));

        var afterRelease = await OutcomeAsync(await gate.TryEnterAsync(digest, ct));

        Assert.Equal(
            $"{IngestErrors.RevisionInFlightType} while held; entered after release",
            $"{whileHeld} while held; {afterRelease} after release");
    }

    /// <summary>RVO20. A control: held by no named falsification row (a gate keyed by a constant would turn it red).</summary>
    [Fact]
    public async Task R8_77_AnotherDigestIsNotHeldUp()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = CreateGate();

        await using var a = Require(await gate.TryEnterAsync(NewDigest(), ct));
        await using var b = Require(await gate.TryEnterAsync(NewDigest(), ct));
    }

    /// <summary>RVO21. Disposing a hold twice releases it once: the third entry (after the second dispose) is refused while a fourth holds it.</summary>
    [Fact]
    public async Task R8_77_DisposingAHoldTwiceReleasesItOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = CreateGate();
        var digest = NewDigest();

        var first = Require(await gate.TryEnterAsync(digest, ct));
        await first.DisposeAsync();
        await first.DisposeAsync();

        string whileFourthHolds;
        await using (Require(await gate.TryEnterAsync(digest, ct)))
            whileFourthHolds = await OutcomeAsync(await gate.TryEnterAsync(digest, ct));

        Assert.Equal(IngestErrors.RevisionInFlightType, whileFourthHolds);
    }

    /// <summary>
    /// RVO54. A hold disposed a second time does not release a later holder's entry: A enters P and disposes; B enters P; A disposes again; C's entry
    /// is refused while B holds, and entered once B has released. RVO21 disposes twice before anyone else enters, so it cannot tell a second dispose that
    /// releases the digest from one that does nothing.
    /// </summary>
    [Fact]
    public async Task R8_77_RVO54_ASecondDisposeOfAnEarlierHoldDoesNotReleaseALaterHolder()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = CreateGate();
        var digest = NewDigest();

        var a = Require(await gate.TryEnterAsync(digest, ct));
        await a.DisposeAsync();
        var b = Require(await gate.TryEnterAsync(digest, ct));
        await a.DisposeAsync();

        string whileBHolds;
        try
        {
            whileBHolds = await OutcomeAsync(await gate.TryEnterAsync(digest, ct));
        }
        finally
        {
            await b.DisposeAsync();
        }

        var afterBReleases = await OutcomeAsync(await gate.TryEnterAsync(digest, ct));

        Assert.Equal(
            $"{IngestErrors.RevisionInFlightType} while B holds; entered after B releases",
            $"{whileBHolds} while B holds; {afterBReleases} after B releases");
    }
}

[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "xUnit discovery needs the concrete class public.")]
public sealed class InMemoryRevisionGateContractTests : RevisionGatePortContractTests
{
    protected override IRevisionGate CreateGate() => new InMemory.InMemoryRevisionGate();
}
