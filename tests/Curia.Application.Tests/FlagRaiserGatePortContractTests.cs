using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Application.Tests;

/// <summary>
/// What every <see cref="IFlagRaiserGate"/> promises, run against the in-memory adapter here and
/// against Postgres in <c>Curia.Infrastructure.Tests</c> (R11.4). R7.22 and R10.70 (errata G18,
/// review of 4b3e91a): one raiser's flags are counted and recorded one at a time.
///
/// <para>Every raiser is a fresh identifier, so a fact that fails while holding one cannot block a
/// later fact sharing the adapter's store, and every hold is disposed on every path.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public abstract class FlagRaiserGatePortContractTests
{
    /// <summary>A gate whose holds no other test's gate shares.</summary>
    protected abstract IFlagRaiserGate CreateGate();

    protected static string NewRaiser(string stem) => $"https://agents.example/{stem}-{Guid.NewGuid():N}";

    protected static IAsyncDisposable Require(Result<IAsyncDisposable> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    protected static void AssertInFlight(Result<IAsyncDisposable> result)
    {
        Assert.False(result.TryGetValue(out _, out var error), "entered a raiser whose flag is in flight");
        Assert.Equal("curia/flag/raise-in-flight", error!.Type);
    }

    [Fact]
    public async Task R7_22_ASecondEntryForOneRaiserIsRefusedUntilTheFirstIsReleased()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = CreateGate();
        var a = NewRaiser("a");

        await using (Require(await gate.TryEnterAsync(a, ct)))
        {
            var second = await gate.TryEnterAsync(a, ct);
            if (second.TryGetValue(out var leaked, out _)) await leaked!.DisposeAsync();
            AssertInFlight(second);
        }

        await using var third = Require(await gate.TryEnterAsync(a, ct));
    }

    [Fact]
    public async Task R7_22_AnotherRaiserIsNotHeldUp()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = CreateGate();

        await using var a = Require(await gate.TryEnterAsync(NewRaiser("a"), ct));
        await using var b = Require(await gate.TryEnterAsync(NewRaiser("b"), ct));
    }

    [Fact]
    public async Task R7_22_DisposingAHoldTwiceReleasesItOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = CreateGate();
        var a = NewRaiser("a");

        var first = Require(await gate.TryEnterAsync(a, ct));
        await first.DisposeAsync();
        await first.DisposeAsync();

        await using (Require(await gate.TryEnterAsync(a, ct)))
        {
            var again = await gate.TryEnterAsync(a, ct);
            if (again.TryGetValue(out var leaked, out _)) await leaked!.DisposeAsync();
            AssertInFlight(again);
        }
    }
}

[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "xUnit discovery needs the concrete class public.")]
public sealed class InMemoryFlagRaiserGateContractTests : FlagRaiserGatePortContractTests
{
    protected override IFlagRaiserGate CreateGate() => new InMemory.InMemoryFlagRaiserGate();
}
