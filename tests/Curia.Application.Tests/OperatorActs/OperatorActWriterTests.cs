using System.Diagnostics.CodeAnalysis;
using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.Application.Tests.Credentials;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Acta;
using Curia.Domain.Primitives;
using Xunit;
using static Curia.Application.Tests.Credentials.KeyActHarness;

namespace Curia.Application.Tests.OperatorActs;

/// <summary>
/// PR N2 (errata G70), Task 4: the writers' probes. OAS19d (register item 300): a publisher's expected version is the count of its aggregate's events in the log it read, and not the length of a
/// projection, which counts only the keys that hold a position (R6.66). The expectations are written from R6.66's rule (c) and from the aggregate's own count, never from a run.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class OperatorActWriterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ActorId OperatorName = Require(ActorId.Create("operator:ops"));

    private static async Task<int> CountAsync(KeyActHarness harness) =>
        Require(await harness.Events.ReadAllAsync(Ct).ConfigureAwait(false)).Count;

    private static async Task AppendKeyRowAsync(KeyActHarness harness, string type, string aggregate, string kid, JsonValue.Object jwk)
    {
        var count = await CountAsync(harness).ConfigureAwait(false);
        var version = Require(await harness.Events.ReadAllAsync(Ct).ConfigureAwait(false)).Count(e => string.Equals(e.AggregateId.Value, aggregate, StringComparison.Ordinal));
        var payload = new JsonValue.Object(
        [
            new("kid", new JsonValue.String(kid)),
            new("jwk", jwk),
            new("valid_from", new JsonValue.String(LogLeaf.RenderServerTimestamp(ServerTimestamp.At(harness.Clock.GetUtcNow())))),
        ]);
        Require(await harness.Events.AppendAsync(
            Require(AggregateId.Create(aggregate)),
            Require(AggregateVersion.From(version)),
            [new DomainEvent(Require(EventId.Create($"01K4CQ1V9P3R5T7W9Y1A3C5F{count:D2}")), Require(EventType.Create(type)), OperatorName, payload)],
            Ct).ConfigureAwait(false));
    }

    /// <summary>
    /// OAS19d: the first declaration key is published whatever rows of its aggregate hold no position. Legs: a <c>log.declaration-key</c> row that republishes the operator key's public key under
    /// another <c>kid</c>, and one that carries another key under the operator key's <c>kid</c> (each holds no position by R6.66 rule (c)); and the control, which has no such row. Each leg
    /// states the outcome and the index the key took, which is the log's length at the call.
    /// </summary>
    [Theory]
    [InlineData("the operator key's public key under another kid")]
    [InlineData("another key under the operator key's kid")]
    [InlineData("no such row (control)")]
    public async Task OAS19d_ThePublisherOfTheFirstDeclarationKeyCountsItsAggregatesEventsNotItsKeys(string leg)
    {
        ArgumentNullException.ThrowIfNull(leg);
        var harness = new KeyActHarness();
        using var operatorKey = new InMemoryOperatorActSigner("operator-1");
        using var declarationKey = new InMemoryLifecycleStatementSigner("declaration-1");
        Require(await new PublishOperatorKey(harness.Events, harness.Clock).PublishAsync(operatorKey, OperatorName, Ct));

        // The legs whose point is that the keys are equal say so: the republishing row carries the operator key's own public key.
        using var sameKeyOtherKid = operatorKey.WithKid("operator-1-as-declaration");
        using var otherMaterial = new InMemoryOperatorActSigner("operator-1");
        if (leg.StartsWith("the operator key's public key", StringComparison.Ordinal))
            await AppendKeyRowAsync(harness, LogEntries.DeclarationKeyType, LogEntries.DeclarationKeysAggregate, "operator-1-as-declaration", sameKeyOtherKid.PublicJwk());
        else if (leg.StartsWith("another key", StringComparison.Ordinal))
            await AppendKeyRowAsync(harness, LogEntries.DeclarationKeyType, LogEntries.DeclarationKeysAggregate, "operator-1", otherMaterial.PublicJwk());

        Assert.NotEqual(operatorKey.PublicJwk().Members.Single(m => m.Key == "x").Value, declarationKey.PublicJwk().Members.Single(m => m.Key == "x").Value);

        var length = await CountAsync(harness);
        var result = await new PublishDeclarationKey(harness.Events, harness.Clock).PublishAsync(declarationKey, OperatorName, Ct);
        var grown = await CountAsync(harness) - length;

        Assert.Equal(
            $"ok (index {length}, appended 1)",
            result.Match(index => $"ok (index {index}, appended {grown})", error => $"{error.Type} (appended {grown})"));
    }
}
