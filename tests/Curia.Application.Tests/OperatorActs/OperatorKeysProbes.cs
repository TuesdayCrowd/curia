using System.Diagnostics.CodeAnalysis;
using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.Application.Projections;
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
/// PR N2 (errata G70, R6.66), Task 2: the third key class. A key of one class is never presented as another and the class is the first publication's (OAS11, OAS11b), the Acta and the
/// verifier list the same keys per class (OAS11c), and the publishers refuse each other's keys by <c>kid</c> and by public key (OAS19). The expected strings are written from R6.66 and
/// Amendment 2 ruling 40, never from the code under test.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class OperatorKeysProbes
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ActorId OperatorName = Require(ActorId.Create("operator:ops"));

    private static AppendedEvent Entry(KeyActHarness harness, long index, string type, string aggregate, string kid, JsonValue.Object? jwk)
    {
        var members = new List<KeyValuePair<string, JsonValue>> { new("kid", new JsonValue.String(kid)) };
        if (jwk is not null)
        {
            members.Add(new("jwk", jwk));
            members.Add(new("valid_from", new JsonValue.String("2026-09-01T00:00:00Z")));
        }

        return LogEvents.At(index, aggregate, harness.Clock.GetUtcNow(), $"key-entry-{index}", type, "operator:ops", new JsonValue.Object([.. members]));
    }

    private static string Kids(IEnumerable<string> kids) => $"[{string.Join(", ", kids)}]";

    private static string Lists(PublishedKeys published) =>
        $"log keys {Kids(published.LogKeys.Select(k => k.Kid))}; declaration keys {Kids(published.DeclarationKeys.Select(k => k.Kid))}; operator keys {Kids(published.OperatorKeys.Select(k => k.Kid))}";

    private static string Lists(ActaLog acta) =>
        $"log keys {Kids(acta.Keys.Select(k => k.Kid))}; declaration keys {Kids(acta.DeclarationKeys.Select(k => k.Kid))}; operator keys {Kids(acta.OperatorKeys.Select(k => k.Kid))}";

    /// <summary>OAS11: a key of one class is never presented as another, and the class is the first publication's.</summary>
    [Fact]
    public void OAS11_AKeyOfOneClassIsNeverPresentedAsAnotherAndTheClassIsTheFirstPublications()
    {
        var harness = new KeyActHarness();
        using var logKey = new InMemoryLifecycleStatementSigner("log-1");
        using var declarationKey = new InMemoryLifecycleStatementSigner("declaration-1");
        using var operatorKey = new InMemoryOperatorActSigner("operator-1");
        var log = new[]
        {
            Entry(harness, 0, LogEntries.KeyType, LogEntries.KeysAggregate, "log-1", logKey.PublicJwk()),
            Entry(harness, 1, LogEntries.DeclarationKeyType, LogEntries.DeclarationKeysAggregate, "declaration-1", declarationKey.PublicJwk()),
            Entry(harness, 2, LogEntries.OperatorKeyType, LogEntries.OperatorKeysAggregate, "malformed", null),
            Entry(harness, 3, LogEntries.OperatorKeyType, LogEntries.OperatorKeysAggregate, "operator-1", operatorKey.PublicJwk()),
            Entry(harness, 4, LogEntries.KeyType, LogEntries.KeysAggregate, "operator-1", operatorKey.PublicJwk()), // the operator key republished as a log key later
        };

        Assert.Equal(
            "log keys [log-1]; declaration keys [declaration-1]; operator keys [operator-1]",
            Lists(PublishedKeys.OfLog(log)));
    }

    /// <summary>OAS11b: an operator entry whose key was first published as a log key holds no position, by <c>kid</c> and by public key; an operator key no other class holds is listed (the control).</summary>
    [Fact]
    public void OAS11b_AnOperatorEntryForAKeyFirstPublishedAsALogKeyHoldsNoPosition()
    {
        var harness = new KeyActHarness();
        using var logKey = new InMemoryLifecycleStatementSigner("log-1");
        using var sameKeyOtherKid = new InMemoryLifecycleStatementSigner("operator-x");
        using var fresh = new InMemoryOperatorActSigner("operator-1");
        var log = new[]
        {
            Entry(harness, 0, LogEntries.KeyType, LogEntries.KeysAggregate, "log-1", logKey.PublicJwk()),
            Entry(harness, 1, LogEntries.OperatorKeyType, LogEntries.OperatorKeysAggregate, "log-1", logKey.PublicJwk()),
            Entry(harness, 2, LogEntries.OperatorKeyType, LogEntries.OperatorKeysAggregate, "operator-x", sameKeyOtherKid.PublicJwk()),
            Entry(harness, 3, LogEntries.OperatorKeyType, LogEntries.OperatorKeysAggregate, "operator-1", fresh.PublicJwk()),
        };

        Assert.Equal(
            "log keys [log-1]; declaration keys []; operator keys [operator-1]",
            Lists(PublishedKeys.OfLog(log)));
    }

    /// <summary>OAS11c: the Acta (what the Forum serves) and the verifier (what a reader decides on) list the same <c>kid</c>s per class, for the shapes of vectors 10 (first published as a log key), 11 (first as the operator key) and 12 (two operator keys).</summary>
    [Fact]
    public void OAS11c_TheActaAndTheVerifierListTheSameKeysPerClass()
    {
        var harness = new KeyActHarness();
        using var logKey = new InMemoryLifecycleStatementSigner("log-1");
        using var declarationKey = new InMemoryLifecycleStatementSigner("declaration-1");
        using var first = new InMemoryOperatorActSigner("operator-1");
        using var second = new InMemoryOperatorActSigner("operator-2");
        using var republished = first.WithKid("operator-1-as-log");

        var shapes = new Dictionary<string, AppendedEvent[]>
        {
            ["vector 10: a log key, then an operator entry for it"] =
            [
                Entry(harness, 0, LogEntries.KeyType, LogEntries.KeysAggregate, "log-1", logKey.PublicJwk()),
                Entry(harness, 1, LogEntries.OperatorKeyType, LogEntries.OperatorKeysAggregate, "log-1", logKey.PublicJwk()),
            ],
            ["vector 11: an operator key, then a log key and a declaration key republishing it"] =
            [
                Entry(harness, 0, LogEntries.OperatorKeyType, LogEntries.OperatorKeysAggregate, "operator-1", first.PublicJwk()),
                Entry(harness, 1, LogEntries.KeyType, LogEntries.KeysAggregate, "operator-1-as-log", republished.PublicJwk()),
                Entry(harness, 2, LogEntries.DeclarationKeyType, LogEntries.DeclarationKeysAggregate, "operator-1", first.PublicJwk()),
                Entry(harness, 3, LogEntries.KeyType, LogEntries.KeysAggregate, "log-1", logKey.PublicJwk()),
            ],
            ["vector 12: two operator keys, and a repeated kid"] =
            [
                Entry(harness, 0, LogEntries.OperatorKeyType, LogEntries.OperatorKeysAggregate, "operator-1", first.PublicJwk()),
                Entry(harness, 1, LogEntries.OperatorKeyType, LogEntries.OperatorKeysAggregate, "operator-2", second.PublicJwk()),
                Entry(harness, 2, LogEntries.OperatorKeyType, LogEntries.OperatorKeysAggregate, "operator-2", second.PublicJwk()),
                Entry(harness, 3, LogEntries.DeclarationKeyType, LogEntries.DeclarationKeysAggregate, "declaration-1", declarationKey.PublicJwk()),
            ],
        };

        var disagreements = new List<string>();
        foreach (var (name, log) in shapes)
        {
            var acta = Require(ActaLog.Fold(log));
            var verifier = Lists(PublishedKeys.OfLog(log));
            if (Lists(acta) != verifier) disagreements.Add($"{name}: acta [{Lists(acta)}] verifier [{verifier}]");
        }

        Assert.True(disagreements.Count == 0, string.Join(Environment.NewLine, disagreements));
    }

    /// <summary>
    /// The characterization of item 273's fourth type: <c>ActaLog.Fold</c> fails the whole Acta closed on a <c>log.operator-key</c> entry with no <c>jwk</c>, as it does on its two siblings, so one such row
    /// makes the Acta unservable. This pins an OPEN defect (register item 273); closing 273 edits this test.
    /// </summary>
    [Fact]
    public void ActaLog_AMalformedOperatorKeyEntryFailsTheActaClosed_Item273()
    {
        var harness = new KeyActHarness();
        var refused = ActaLog.Fold([Entry(harness, 0, LogEntries.OperatorKeyType, LogEntries.OperatorKeysAggregate, "operator-1", null)]);

        Assert.Equal("curia/acta/malformed-operator-key log_index=0", refused.Match(_ => "folded", e => $"{e.Type} {e.Detail}"));
    }

    private static async Task<int> CountAsync(KeyActHarness harness) =>
        Require(await harness.Events.ReadAllAsync(Ct).ConfigureAwait(false)).Count;

    private static async Task AppendKeyAsync(KeyActHarness harness, string type, string aggregate, string kid, JsonValue.Object jwk)
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
            [new DomainEvent(Require(EventId.Create($"01K4CQ1V9P3R5T7W9Y1A3C5E{count:D2}")), Require(EventType.Create(type)), OperatorName, payload)],
            Ct).ConfigureAwait(false));
    }

    private static async Task<string> OutcomeAsync(KeyActHarness harness, Func<KeyActHarness, Task<Result<long>>> publish)
    {
        var before = await CountAsync(harness).ConfigureAwait(false);
        var result = await publish(harness).ConfigureAwait(false);
        var after = await CountAsync(harness).ConfigureAwait(false);
        return result.Match(_ => $"ok (appended {after - before})", e => $"{e.Type} (appended {after - before})");
    }

    private static Task<string> PublishOperatorAsync(KeyActHarness harness, IOperatorActSigner key) =>
        OutcomeAsync(harness, h => new PublishOperatorKey(h.Events, h.Clock).PublishAsync(key, OperatorName, Ct));

    private static Task<string> PublishDeclarationAsync(KeyActHarness harness, ILifecycleStatementSigner key) =>
        OutcomeAsync(harness, h => new PublishDeclarationKey(h.Events, h.Clock).PublishAsync(key, OperatorName, Ct));

    /// <summary>The operator key is published in <paramref name="type"/>'s class already: under its own <c>kid</c> with other key material, or under another <c>kid</c> with its public key.</summary>
    private static async Task<string> OperatorKeyAlreadyAsync(string type, string aggregate, bool byPublicKey)
    {
        var harness = new KeyActHarness();
        using var key = new InMemoryOperatorActSigner("operator-1");
        if (byPublicKey)
        {
            using var sameKeyOtherKid = key.WithKid("other-kid");
            await AppendKeyAsync(harness, type, aggregate, "other-kid", sameKeyOtherKid.PublicJwk()).ConfigureAwait(false);
        }
        else
        {
            using var otherMaterial = new InMemoryLifecycleStatementSigner("operator-1");
            await AppendKeyAsync(harness, type, aggregate, "operator-1", otherMaterial.PublicJwk()).ConfigureAwait(false);
        }

        return await PublishOperatorAsync(harness, key).ConfigureAwait(false);
    }

    /// <summary>The declaration key is published in <paramref name="type"/>'s class already: under its own <c>kid</c> with other key material, or under another <c>kid</c> with its public key.</summary>
    private static async Task<string> DeclarationKeyAlreadyAsync(string type, string aggregate, bool byPublicKey)
    {
        var harness = new KeyActHarness();
        using var declaration = new InMemoryLifecycleStatementSigner("declaration-1");
        if (byPublicKey)
        {
            using var sameKeyOtherKid = new InMemoryLifecycleStatementSigner("other-kid");
            await AppendKeyAsync(harness, type, aggregate, "other-kid", sameKeyOtherKid.PublicJwk()).ConfigureAwait(false);
        }
        else
        {
            using var otherMaterial = new InMemoryOperatorActSigner("declaration-1");
            await AppendKeyAsync(harness, type, aggregate, "declaration-1", otherMaterial.PublicJwk()).ConfigureAwait(false);
        }

        return await PublishDeclarationAsync(harness, declaration).ConfigureAwait(false);
    }

    /// <summary>
    /// OAS19: the publishers refuse each other's keys, in both directions, by <c>kid</c> and by public key, and a second operator key; each refusal appends nothing. The controls come first
    /// (each publisher publishes its first key). The types are literals: a test that asserted the constant would pass on any string.
    /// </summary>
    [Fact]
    public async Task OAS19_ThePublishersRefuseEachOthersKeys()
    {
        var results = new List<string>();

        // Controls: each publisher publishes its first key.
        var controls = new KeyActHarness();
        using var controlOperator = new InMemoryOperatorActSigner("operator-1");
        using var controlDeclaration = new InMemoryLifecycleStatementSigner("declaration-1");
        results.Add("control operator: " + await PublishOperatorAsync(controls, controlOperator));
        var controlsToo = new KeyActHarness();
        results.Add("control declaration: " + await PublishDeclarationAsync(controlsToo, controlDeclaration));

        // The operator publisher: its key is already a log key, by kid and by public key under another kid.
        foreach (var (name, type, aggregate) in new[] { ("log key", LogEntries.KeyType, LogEntries.KeysAggregate), ("declaration key", LogEntries.DeclarationKeyType, LogEntries.DeclarationKeysAggregate) })
        {
            results.Add($"operator key already a {name} by kid: " + await OperatorKeyAlreadyAsync(type, aggregate, byPublicKey: false));
            results.Add($"operator key already a {name} by public key: " + await OperatorKeyAlreadyAsync(type, aggregate, byPublicKey: true));
        }

        // A second operator key.
        var twice = new KeyActHarness();
        using var firstOperator = new InMemoryOperatorActSigner("operator-1");
        using var secondOperator = new InMemoryOperatorActSigner("operator-2");
        await new PublishOperatorKey(twice.Events, twice.Clock).PublishAsync(firstOperator, OperatorName, Ct);
        results.Add("second operator key: " + await PublishOperatorAsync(twice, secondOperator));

        // The declaration publisher: its key is already an operator key, by kid and by public key; and a log key by public key under another kid (the new leg).
        foreach (var (name, type, aggregate) in new[] { ("operator key", LogEntries.OperatorKeyType, LogEntries.OperatorKeysAggregate), ("log key", LogEntries.KeyType, LogEntries.KeysAggregate) })
        {
            results.Add($"declaration key already a {name} by kid: " + await DeclarationKeyAlreadyAsync(type, aggregate, byPublicKey: false));
            results.Add($"declaration key already a {name} by public key: " + await DeclarationKeyAlreadyAsync(type, aggregate, byPublicKey: true));
        }

        Assert.Equal(
            [
                "control operator: ok (appended 1)",
                "control declaration: ok (appended 1)",
                "operator key already a log key by kid: curia/keys/operator-key-is-log-key (appended 0)",
                "operator key already a log key by public key: curia/keys/operator-key-is-log-key (appended 0)",
                "operator key already a declaration key by kid: curia/keys/operator-key-is-declaration-key (appended 0)",
                "operator key already a declaration key by public key: curia/keys/operator-key-is-declaration-key (appended 0)",
                "second operator key: curia/keys/operator-key-already-published (appended 0)",
                "declaration key already a operator key by kid: curia/keys/declaration-key-is-operator-key (appended 0)",
                "declaration key already a operator key by public key: curia/keys/declaration-key-is-operator-key (appended 0)",
                "declaration key already a log key by kid: curia/keys/declaration-key-is-log-key (appended 0)",
                "declaration key already a log key by public key: curia/keys/declaration-key-is-log-key (appended 0)",
            ],
            results);
    }
}
