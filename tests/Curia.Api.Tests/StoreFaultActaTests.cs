using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Acta;
using Curia.Domain.Primitives;
using Curia.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// ULG51 (errata G52, R9.52): a log that is read and will not fold is the Acta's own refusal, not a fault of a store. The Acta's reads answer a malformed
/// <c>log.head</c> entry as they did before G52 (<c>500</c>, the entry's own type, a problem document with no detail) and never as <c>503</c>
/// <c>curia/log/unreadable</c> or <c>curia/store/unavailable</c>: the log was read, and a retry would find the same entry. Its own class, so its own database:
/// the malformed entry stays in the log for good.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class StoreFaultActaTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} ({e.Detail})"));

    private async Task AppendMalformedHeadAsync()
    {
        var store = forum.Services.GetRequiredService<IEventStore>();
        var actor = Require(ActorId.Create("ops"));
        var head = Require(await store.ReadByAggregateAsync(Require(AggregateId.Create(LogEntries.HeadsAggregate)), Ct));
        Require(await store.AppendAsync(
            Require(AggregateId.Create(LogEntries.HeadsAggregate)),
            Require(AggregateVersion.From(head.Count)),
            [new DomainEvent(
                Require(EventId.Create(Require(new UlidGenerator(forum.Clock).Next()).ToString())),
                Require(EventType.Create(LogEntries.HeadType)),
                actor,
                new JsonValue.Object([]))],
            Ct));
    }

    [Fact]
    public async Task R9_52_ULG51_ALogThatWillNotFoldIsTheActasOwnRefusalAndNeverAStoreFault()
    {
        await AppendMalformedHeadAsync();

        var problems = new List<string>();
        foreach (var path in new[] { "/v1/log/head", "/v1/log/proof/0", "/v1/log/consistency?from=1&to=1", "/v1/log/entries/0", "/v1/log/jwks" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            var reply = await Reply.SendAsync(forum.Client, request, Ct);
            if (reply.Status == PublishedStoreFaults.UnavailableStatus || PublishedStoreFaultTypes.Contains(reply.Type))
                problems.Add($"{path}: a log that was read and will not fold is answered as a fault of a store: {reply.Says}");
            else if (reply.Status != 500 || !reply.IsProblemWithNullDetail || !reply.Type.StartsWith("curia/acta/", StringComparison.Ordinal))
                problems.Add($"{path}: expected 500 and a curia/acta/ problem document with no detail, actual {reply.Says}");
        }

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    private static readonly string[] PublishedStoreFaultTypes = [PublishedStoreFaults.LogUnreadableType, PublishedStoreFaults.StoreUnavailableType];
}
