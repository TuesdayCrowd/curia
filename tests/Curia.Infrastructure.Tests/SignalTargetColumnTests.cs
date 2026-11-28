using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Tests.Shared;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Primitives;
using Npgsql;
using Xunit;

namespace Curia.Infrastructure.Tests;

/// <summary>
/// R11.6 (add.), db/0007: the signal target of a vote or verification is a column the append writes from the signed envelope, and the audit holds the column to the envelope. SIGC1 is the column's value
/// (read back from the database, not from the writer); SIGC2 the audit's two findings (NULL, and a value that is another post's); SIGC3 the CHECK, which bounds every index over it; SIGC4 the grant, which
/// is INSERT and no UPDATE.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "xUnit discovers a concrete test class only when it is public.")]
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Every interpolated identifier is a schema name this fixture generated from a GUID's hex digits.")]
[Collection(PostgresCollectionDefinition.Name)]
public sealed class SignalTargetColumnTests(PostgresDatabaseFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} {e.Detail}"));

    private async Task<(PostgresEventStore Store, string Schema)> NewStoreAsync()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(Ct);
        return (new PostgresEventStore(fixture.AppRoleDataSource, TimeProvider.System, fixture.PermissiveLifecycleGate(), schema), schema);
    }

    private static async Task AppendAsync(PostgresEventStore store, DomainEvent e) =>
        Require(await store.AppendAsync(Require(AggregateId.Create(e.Id.Value)), AggregateVersion.New, [e], Ct));

    private async Task<string?> ColumnAsync(string schema, string eventId)
    {
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand($"SELECT signal_target FROM \"{schema}\".events WHERE event_id = @id;", connection);
        command.Parameters.AddWithValue("id", eventId);
        return await command.ExecuteScalarAsync(Ct) is string s ? s : null;
    }

    private async Task AdminAsync(string sql)
    {
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>SIGC1: the column of a vote and of a verification holds the target of its envelope; the column of a post of another kind, and of an event that is no post, is NULL.</summary>
    [Fact]
    public async Task R11_6_SIGC1_TheAppendWritesTheEnvelopesTargetAndNothingElse()
    {
        var (store, schema) = await NewStoreAsync();
        var vote = SignalRows.Make("vote", 11, SignalRows.Digest(1), "n");
        var verification = SignalRows.Make("verification", 12, SignalRows.Digest(2), "n");
        var finding = SignalRows.Make("vote", 13, SignalRows.Digest(3), "n");
        finding = finding with { Payload = new JsonValue.Object([.. ((JsonValue.Object)finding.Payload).Members.Where(m => m.Key != "kind"), new("kind", new JsonValue.String("finding"))]) };
        var plain = new DomainEvent(Require(EventId.Create("plain")), Require(EventType.Create("test.event")), null, new JsonValue.Object([new("target", new JsonValue.String(SignalRows.Digest(4)))]));

        foreach (var e in new[] { vote, verification, finding, plain }) await AppendAsync(store, e);

        string?[] columns = [await ColumnAsync(schema, vote.Id.Value), await ColumnAsync(schema, verification.Id.Value), await ColumnAsync(schema, finding.Id.Value), await ColumnAsync(schema, "plain")];
        Assert.Equal<IEnumerable<string?>>([SignalRows.Digest(1), SignalRows.Digest(2), null, null], columns);

        var read = Require(await store.ReadAllAsync(Ct));
        Assert.Equal<IEnumerable<string?>>([SignalRows.Digest(1), SignalRows.Digest(2), null, null], read.Select(r => r.StoredSignalTarget));
    }

    /// <summary>SIGC2: the audit says nothing of an honest log, names a NULL column as missing and a column that is another post's as mismatched, and is silent about a post that is no vote.</summary>
    [Fact]
    public async Task R11_6_SIGC2_TheAuditFindsANullAndAWrongColumn()
    {
        var (store, schema) = await NewStoreAsync();
        var first = SignalRows.Make("vote", 21, SignalRows.Digest(1), "n");
        var second = SignalRows.Make("verification", 22, SignalRows.Digest(2), "n");
        await AppendAsync(store, first);
        await AppendAsync(store, second);

        var honest = SignalTargetAudit.Check(Require(await store.ReadAllAsync(Ct)));
        Assert.Equal((0, 0), (honest.Mismatched.Count, honest.Missing.Count));

        await AdminAsync($"UPDATE \"{schema}\".events SET signal_target = NULL WHERE event_id = '{first.Id.Value}';");
        await AdminAsync($"UPDATE \"{schema}\".events SET signal_target = '{SignalRows.Digest(9)}' WHERE event_id = '{second.Id.Value}';");

        var planted = SignalTargetAudit.Check(Require(await store.ReadAllAsync(Ct)));
        Assert.Equal([1L], planted.Mismatched);
        Assert.Equal([0L], planted.Missing);
    }

    /// <summary>SIGC3: the column's CHECK is the form VERIFY required (71 bytes, <c>sha256:</c> and 64 lowercase hex digits) and only on a <c>post.accepted</c> row, so no index over it can be unbounded.</summary>
    [Theory]
    [InlineData("post.accepted", "sha256:abc")]
    [InlineData("post.accepted", "sha256:0000000000000000000000000000000000000000000000000000000000000000x")]
    [InlineData("post.accepted", "SHA256:0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("post.accepted", "sha256:000000000000000000000000000000000000000000000000000000000000000G")]
    [InlineData("test.event", "sha256:0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task R11_6_SIGC3_TheCheckRefusesWhatIsNotAPrefixedDigestOnAPost(string eventType, string value)
    {
        var (_, schema) = await NewStoreAsync();

        var refused = await Assert.ThrowsAsync<PostgresException>(() => AdminAsync(
            $"INSERT INTO \"{schema}\".events (event_id, event_type, aggregate_id, payload, signal_target) VALUES ('c3', '{eventType}', 'c3', '{{}}', '{value}');"));
        Assert.Equal("23514", refused.SqlState);
    }

    /// <summary>SIGC4: the application role may INSERT the column and holds no UPDATE of it (nor of the leaf), so a written target is never changed by the Forum.</summary>
    [Fact]
    public async Task R11_6_SIGC4_TheRoleInsertsTheColumnAndNeverUpdatesIt()
    {
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT has_column_privilege(@role, 'public.events', 'signal_target', 'INSERT'), has_column_privilege(@role, 'public.events', 'signal_target', 'UPDATE'), has_column_privilege(@role, 'public.events', 'leaf_hash', 'UPDATE');",
            connection);
        command.Parameters.AddWithValue("role", fixture.RoleName);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        Assert.Equal((true, false, false), (reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2)));
    }
}
