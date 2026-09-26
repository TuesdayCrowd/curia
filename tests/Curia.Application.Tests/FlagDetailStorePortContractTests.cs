using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Application.Tests;

/// <summary>
/// What every <see cref="IFlagDetailStore"/> promises, run against the in-memory adapter here and
/// against Postgres in <c>Curia.Infrastructure.Tests</c> (R11.4, R11.21, R11.22). Two adapters that
/// happen to agree are not a contract; this is.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public abstract class FlagDetailStorePortContractTests
{
    /// <summary>A fresh, empty store: no test sees another's rows.</summary>
    protected abstract IFlagDetailStore CreateStore();

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    private static FlagDetail Detail(string eventId, string rationale = "looks like spam") =>
        new(eventId, "01JPOST0000000000000000001", "https://agents.example/reporter", rationale, "c2FsdA");

    [Fact]
    public async Task R11_32_AnEmptyStoreReadsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Empty(Require(await CreateStore().ReadAllAsync(ct)));
    }

    /// <summary>Every member comes back exactly as appended — a store that normalized one would break the commitment.</summary>
    [Fact]
    public async Task R11_32_AnAppendedDetailReadsBackVerbatim()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var detail = Detail("01JFLAG000000000000000001", "Café — naïve rationale\nwith a second line");

        Require(await store.AppendAsync(detail, ct));

        Assert.Equal(detail, Assert.Single(Require(await store.ReadAllAsync(ct))));
    }

    /// <summary>
    /// Ordered by event id, ordinally. Appended in the reverse order, so an adapter returning
    /// insertion order comes back wrong (trap 10: store fixtures in the order a wrong implementation
    /// would return them).
    /// </summary>
    [Fact]
    public async Task R11_32_DetailsReadBackInEventIdOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();

        Require(await store.AppendAsync(Detail("01JFLAG000000000000000002"), ct));
        Require(await store.AppendAsync(Detail("01JFLAG000000000000000001"), ct));

        Assert.Equal(
            ["01JFLAG000000000000000001", "01JFLAG000000000000000002"],
            Require(await store.ReadAllAsync(ct)).Select(d => d.EventId));
    }

    /// <summary>Append-only: a second row for the same event is refused, and the first stands unchanged.</summary>
    [Fact]
    public async Task R11_32_ASecondDetailForTheSameEventIsRefusedAndTheFirstStands()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var first = Detail("01JFLAG000000000000000001", "the first rationale");

        Require(await store.AppendAsync(first, ct));

        Assert.False((await store.AppendAsync(Detail("01JFLAG000000000000000001", "a replacement"), ct))
            .TryGetValue(out _, out var error));
        Assert.Equal("curia/flag/detail-exists", error!.Type);
        Assert.Equal(first, Assert.Single(Require(await store.ReadAllAsync(ct))));
    }

    /// <summary>
    /// R11.21: the in-memory adapter accepts exactly what Postgres accepts. A <c>text</c> column
    /// cannot hold U+0000, so neither adapter may take one — and the refusal names no content.
    /// </summary>
    [Theory]
    [InlineData("event\0id", "p", "r", "why", "s")]
    [InlineData("e", "post\0id", "r", "why", "s")]
    [InlineData("e", "p", "raiser\0", "why", "s")]
    [InlineData("e", "p", "r", "a rationale with \0 in it", "s")]
    [InlineData("e", "p", "r", "why", "sa\0lt")]
    [InlineData("e", "p", "r", "", "s")]
    public async Task R11_21_AMemberTheStoreCannotHoldIsRefusedByName(
        string eventId, string postId, string raisedBy, string rationale, string salt)
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();

        Assert.False((await store.AppendAsync(new FlagDetail(eventId, postId, raisedBy, rationale, salt), ct))
            .TryGetValue(out _, out var error));
        Assert.Equal("curia/flag/detail-unstorable", error!.Type);
        Assert.DoesNotContain("rationale with", error.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.Empty(Require(await store.ReadAllAsync(ct)));
    }
}

/// <summary>R11.4's in-memory adapter, held to the contract.</summary>
[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "xUnit discovery needs the concrete class public; every [Fact] is inherited, so the analyzer's test-class heuristic does not see it.")]
public sealed class InMemoryFlagDetailStoreContractTests : FlagDetailStorePortContractTests
{
    protected override IFlagDetailStore CreateStore() => new InMemory.InMemoryFlagDetailStore();
}
