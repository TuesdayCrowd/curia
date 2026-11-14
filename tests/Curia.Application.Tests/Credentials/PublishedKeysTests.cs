using System.Diagnostics.CodeAnalysis;
using Curia.Application.Credentials;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Acta;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;
using Xunit;
using static Curia.Application.Tests.Credentials.KeyActHarness;

namespace Curia.Application.Tests.Credentials;

/// <summary>
/// LRV33 (errata G68, ruling 34): <c>PublishedKeys</c> keeps its classes in lists of their own, each with its own header <c>typ</c>, so that N2's operator-acts key is an addition and not a
/// rewrite. A key of one class is never presented as the other, and an entry that does not read as a key is no key.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class PublishedKeysTests
{
    private static AppendedEvent Entry(KeyActHarness harness, long index, string type, string aggregate, string kid, InMemoryLifecycleStatementSigner key, bool wellFormed = true)
    {
        var payload = wellFormed
            ? new JsonValue.Object([new("kid", new JsonValue.String(kid)), new("jwk", key.PublicJwk()), new("valid_from", new JsonValue.String("2026-09-01T00:00:00Z"))])
            : new JsonValue.Object([new("kid", new JsonValue.String(kid))]);
        return LogEvents.At(index, aggregate, harness.Clock.GetUtcNow(), $"key-entry-{index}", type, "operator:ops", payload);
    }

    [Fact]
    public void LRV33_TheClassesAreSeparateListsEachWithItsOwnHeaderTyp()
    {
        Assert.Equal(
            [("log.key", "curia-head+jws"), ("log.declaration-key", "curia-key-compromise+jws")],
            PublishedKeys.Classes.ToArray());
    }

    [Fact]
    public void LRV33_AKeyOfOneClassIsNeverPresentedAsTheOther()
    {
        var harness = new KeyActHarness();
        using var logKey = new InMemoryLifecycleStatementSigner("log-1");
        using var declarationKey = new InMemoryLifecycleStatementSigner("declaration-1");
        var log = new[]
        {
            Entry(harness, 0, LogEntries.KeyType, LogEntries.KeysAggregate, "log-1", logKey),
            Entry(harness, 1, LogEntries.DeclarationKeyType, LogEntries.DeclarationKeysAggregate, "declaration-1", declarationKey),
            Entry(harness, 2, LogEntries.DeclarationKeyType, LogEntries.DeclarationKeysAggregate, "malformed-1", declarationKey, wellFormed: false),
        };

        var published = PublishedKeys.OfLog(log);

        WholeText.Same(
            "log keys [log-1]; declaration keys [declaration-1]",
            $"log keys [{string.Join(", ", published.LogKeys.Select(k => k.Kid))}]; declaration keys [{string.Join(", ", published.DeclarationKeys.Select(k => k.Kid))}]");
    }
}
