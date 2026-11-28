using System.Diagnostics.CodeAnalysis;
using System.Text;
using Curia.Application.Ports;
using Curia.Application.Reads;
using Curia.Tests.Shared;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Infrastructure.Tests;

/// <summary>
/// R11.6 (add.): every index <c>db/</c> builds over a payload member is total and bounded over every input ADMIT admits. These probes use only what the store and the read plan already offered
/// before the rule, so they are red on a tree whose <c>db/0007</c> indexes the author's own words: a NUL escape in a signed envelope (D61 item 12: ADMIT admits <c>\u0000</c>; a jsonb cannot hold
/// U+0000, and an index expression that casts the canonical text to jsonb raises on it) and a <c>parent</c> or <c>board</c> of up to R6.39's 256 KiB (a btree row holds about 2,700 bytes) are
/// each a valid post, and a valid post is never a store fault.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "xUnit discovers a concrete test class only when it is public.")]
[SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "A seeded generator of incompressible-looking test strings.")]
[Collection(PostgresCollectionDefinition.Name)]
public sealed class SignalTargetAdmissionTests(PostgresDatabaseFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} {e.Detail}"));

    private async Task<PostgresEventStore> NewStoreAsync() =>
        new(fixture.AppRoleDataSource, TimeProvider.System, fixture.PermissiveLifecycleGate(), await fixture.CreateIsolatedSchemaAsync(Ct));

    private static async Task AppendAsync(PostgresEventStore store, DomainEvent e) =>
        Require(await store.AppendAsync(Require(AggregateId.Create(e.Id.Value)), AggregateVersion.New, [e], Ct));

    /// <summary>Printable ASCII from a seeded generator: no run to compress, so a long value is as long in the index as it is in the document.</summary>
    private static string Noise(int length, int seed)
    {
        var random = new Random(seed);
        var text = new StringBuilder(length);
        for (var i = 0; i < length; i++) text.Append((char)random.Next(0x21, 0x7F));
        return text.ToString();
    }

    /// <summary>SIG1: a vote and a verification whose signed envelope holds a NUL escape in a member that is not the target append, and the signal clause still finds them: the signal is read, not only stored.</summary>
    [Theory]
    [InlineData("vote")]
    [InlineData("verification")]
    public async Task R11_6_SIG1_ANulEscapeInASignedEnvelopeIsAppendedAndTheSignalStillCounts(string kind)
    {
        var store = await NewStoreAsync();
        var target = SignalRows.Digest(7);
        var row = SignalRows.Make(kind, 1, target, "a\0b");
        Assert.Contains("\\u0000", ((JsonValue.String)((JsonValue.Object)row.Payload).Members.Single(m => m.Key == "canonical").Value).Value, StringComparison.Ordinal);

        await AppendAsync(store, row);

        var slice = await store.ReadAsync(ReadPlan.Of(ReadClause.SignalsOn([target])), Ct);
        Assert.Equal([row.Id.Value], slice.Rows.Select(r => r.Event.Event.Id.Value));
    }

    /// <summary>SIG2: a post whose <c>parent</c> is 3,000 and 262,144 bytes of printable noise (a revision's parent is unread by decision, G38) appends: no index holds the author's string.</summary>
    [Theory]
    [InlineData(3_000)]
    [InlineData(262_144)]
    public async Task R11_6_SIG2_AParentOfAnyAdmittedLengthIsAppended(int length)
    {
        var store = await NewStoreAsync();
        var row = SignalRows.Make("vote", 2, SignalRows.Digest(8), "n");
        var payload = (JsonValue.Object)row.Payload;
        await AppendAsync(store, row with { Payload = new JsonValue.Object([.. payload.Members.Where(m => m.Key != "parent"), new("parent", new JsonValue.String(Noise(length, length)))]) });

        Assert.Single(Require(await store.ReadAllAsync(Ct)));
    }

    /// <summary>SIG3: the same for <c>board</c>, which is capped by R6.39 alone (<c>BoardName.MaxUtf8Bytes</c> 262,144).</summary>
    [Theory]
    [InlineData(3_000)]
    [InlineData(262_144)]
    public async Task R11_6_SIG3_ABoardOfAnyAdmittedLengthIsAppended(int length)
    {
        var store = await NewStoreAsync();
        var row = SignalRows.Make("vote", 3, SignalRows.Digest(9), "n");
        var payload = (JsonValue.Object)row.Payload;
        await AppendAsync(store, row with { Payload = new JsonValue.Object([.. payload.Members.Where(m => m.Key != "board"), new("board", new JsonValue.String(Noise(length, length + 1)))]) });

        Assert.Single(Require(await store.ReadAllAsync(Ct)));
    }
}
