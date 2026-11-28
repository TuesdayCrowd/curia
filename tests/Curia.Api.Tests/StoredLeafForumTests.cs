using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Canon.Acta;
using Curia.Domain.Acta;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Primitives;
using Curia.OperatorTool;
using Curia.Tests.Shared;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;
using static Curia.Api.Tests.BoardListProbe;
using static Curia.Api.Tests.NameProbe;

namespace Curia.Api.Tests;

/// <summary>
/// R6.69 (errata G87, db/0007), through the Forum as the application role (the fixture's host connects as it, with the grants a deployment has): SLF1 the proofs the routes serve are byte for byte the
/// same whether the leaves come from the column or are hashed from the rows, and the backfill verb fills what is missing; SLF2 is the harm and its detection (a wrong leaf the owner plants is found by
/// <c>lifecycle-audit</c>, refused by <c>sign-head</c>, and makes the served proofs differ from the tree of the rows), restored afterwards; SLF3 a Forum over a database without the column does not start.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Every statement is a constant or built from the fixture's own generated names.")]
[Collection("role-forum")]
public sealed class StoredLeafForumTests(RoleForumFixture forum) : IClassFixture<RoleForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> ListAsync(string board)
    {
        var reply = await GetAsync(forum.Client, "/v1/boards/" + Uri.EscapeDataString(board) + "/posts", ct: Ct);
        Assert.True(reply.Status == HttpStatusCode.OK, reply.Says);
        return reply.Body;
    }

    private async Task<(int Exit, string Out, string Err)> OperatorAsync(string[] args, string? logPem = null)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await OperatorCommands.RunAsync(
            args, forum.ConnectionString, forum.Clock, forum.PermissiveGate(), OperatorCommands.LoadDeclarationSigner, stdout, stderr, Ct,
            readLogSigningKeyPem: logPem is null ? null : () => logPem, readDeclarationSigningKeyPem: null);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, admin);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private async Task AdminAsync(string sql)
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, admin);
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>SLF1: the board listing's proofs, served from the stored leaves, equal byte for byte what the Forum serves when no row has a leaf (the recomputation); the backfill then fills every row.</summary>
    [Fact]
    public async Task R6_69_SLF1_TheProofsServedFromTheColumnAreTheProofsServedFromTheRows()
    {
        var board = Name("slf1");
        await StoreAsync(forum, board, 7, 3, Ct);
        Assert.Equal(0L, await CountAsync("SELECT count(*) FROM events WHERE seq > 0 AND leaf_hash IS NULL;"));   // every row the Forum appended carries its leaf

        var fromTheColumn = await ListAsync(board);
        await AdminAsync("UPDATE events SET leaf_hash = NULL;");
        var fromTheRows = await ListAsync(board);

        var total = await CountAsync("SELECT count(*) FROM events WHERE seq > 0;");
        var backfill = await OperatorAsync(["backfill-leaves"]);
        var afterBackfill = await ListAsync(board);

        Assert.True(fromTheColumn == fromTheRows, "the served listing differs between stored leaves and recomputed leaves");
        Assert.Equal((0, $"filled {total.ToString(CultureInfo.InvariantCulture)}  already_held 0  signal_targets_filled 0"), (backfill.Exit, backfill.Out.Trim()));
        Assert.True(afterBackfill == fromTheColumn, "the served listing moved after the backfill");
        Assert.Equal(0, (await OperatorAsync(["lifecycle-audit"])).Exit);
    }

    /// <summary>
    /// SLF2: the owner plants a wrong leaf on a post's row. <c>lifecycle-audit</c> exits 7 and names the index, <c>sign-head</c> exits 7 and writes nothing, and a proof the Forum then serves for another post does
    /// not verify against the tree the rows make (the client's check, with the oracle hashing every leaf from its row). With the leaf put back, the audit is quiet and <c>sign-head</c> signs.
    /// </summary>
    [Fact]
    public async Task R6_69_SLF2_AWrongLeafThePlantedByTheOwnerIsFoundRefusedAndBreaksTheProofsItServes()
    {
        var board = Name("slf2");
        var stored = await StoreAsync(forum, board, 5, 2, Ct);
        var wrongRow = await CountAsync($"SELECT seq FROM events WHERE event_id = '{stored[1].PostId}';");
        var original = await ScalarTextAsync($"SELECT encode(leaf_hash, 'hex') FROM events WHERE seq = {wrongRow};");
        var logPem = Curia.OperatorTool.LogSigningKey.GeneratePem();
        var before = await CountAsync("SELECT count(*) FROM events;");

        await AdminAsync($"UPDATE events SET leaf_hash = decode(repeat('00', 32), 'hex') WHERE seq = {wrongRow};");
        try
        {
            var audit = await OperatorAsync(["lifecycle-audit"]);
            var signed = await OperatorAsync(["sign-head", "--by", "ops"], logPem);
            var after = await CountAsync("SELECT count(*) FROM events;");

            var log = (await forum.Services.GetRequiredService<IEventReader>().ReadAllAsync(Ct)).Match(r => r, e => throw new InvalidOperationException(e.Type));
            var tree = ActaLog.FoldRecomputed(log).Match(a => a, e => throw new InvalidOperationException(e.Type));
            var broken = 0;
            using var listing = JsonDocument.Parse(await ListAsync(board));
            foreach (var post in listing.RootElement.EnumerateArray())
            {
                var proof = post.GetProperty("inclusion_proof");
                var index = (int)proof.GetProperty("log_index").GetInt64();
                var size = (int)proof.GetProperty("tree_size").GetInt64();
                var path = proof.GetProperty("audit_path").EnumerateArray().Select(h => LogEntries.Unprefixed(h.GetString())!.Value).ToImmutableArrayOf();
                if (!MerkleTree.VerifyInclusion(tree.Leaves[index].AsSpan(), index, size, path, tree.RootAt(size).AsSpan())) broken++;
            }

            Assert.Equal((7, true, 7, true, before), (audit.Exit, audit.Out.Contains($"mismatched  log_index={(wrongRow - 1).ToString(CultureInfo.InvariantCulture)}", StringComparison.Ordinal), signed.Exit, signed.Err.Contains("stored leaf", StringComparison.Ordinal), after));
            Assert.True(broken >= 1, "no proof served after the planted leaf failed the client's check, so the harm the audit exists for was not shown");
        }
        finally
        {
            await AdminAsync($"UPDATE events SET leaf_hash = decode('{original}', 'hex') WHERE seq = {wrongRow};");
        }

        Assert.Equal(0, (await OperatorAsync(["lifecycle-audit"])).Exit);
        Assert.Equal(0, (await OperatorAsync(["sign-head", "--by", "ops"], logPem)).Exit);
    }

    /// <summary>
    /// SLF4 (R11.6 (add.)): a vote whose <c>signal_target</c> is NULL (a row older than the column) does not count toward a finding, so <c>lifecycle-audit</c> says so and exits 7 instead of passing; the owner's
    /// <c>backfill-leaves</c> fills it from the envelope and the audit is then quiet. A column that is another post's target is named as mismatched and the backfill never replaces it.
    /// </summary>
    [Fact]
    public async Task R11_6_SLF4_ANullSignalTargetIsAFindingTheBackfillFillsAndAWrongOneIsNeverRepaired()
    {
        var store = forum.Services.GetRequiredService<IEventStore>();
        var vote = SignalRows.Make("vote", 4_001, SignalRows.Digest(4_001), "n");
        var verification = SignalRows.Make("verification", 4_002, SignalRows.Digest(4_002), "n");
        foreach (var e in new[] { vote, verification })
            (await store.AppendAsync(AggregateId.Create(e.Id.Value).Match(v => v, x => throw new InvalidOperationException(x.Type)), AggregateVersion.New, [e], Ct)).Match(v => v, x => throw new InvalidOperationException(x.Type));

        Assert.Equal(0, (await OperatorAsync(["lifecycle-audit"])).Exit);

        await AdminAsync($"UPDATE events SET signal_target = NULL WHERE event_id = '{vote.Id.Value}';");
        await AdminAsync($"UPDATE events SET signal_target = '{SignalRows.Digest(9)}' WHERE event_id = '{verification.Id.Value}';");
        var found = await OperatorAsync(["lifecycle-audit"]);
        var backfill = await OperatorAsync(["backfill-leaves"]);
        var after = await OperatorAsync(["lifecycle-audit"]);

        Assert.Equal(7, found.Exit);
        Assert.Contains("signal-targets  mismatched 1  missing 1", found.Out, StringComparison.Ordinal);
        Assert.Contains("a vote or verification with no signal_target does not count toward a finding", found.Out, StringComparison.Ordinal);
        Assert.Equal((0, "filled 0  already_held " + (await CountAsync("SELECT count(*) FROM events WHERE seq > 0;")).ToString(CultureInfo.InvariantCulture) + "  signal_targets_filled 1"), (backfill.Exit, backfill.Out.Trim()));
        Assert.Equal(7, after.Exit);
        Assert.Contains("signal-targets  mismatched 1  missing 0", after.Out, StringComparison.Ordinal);

        await AdminAsync($"UPDATE events SET signal_target = '{SignalRows.Digest(4_002)}' WHERE event_id = '{verification.Id.Value}';");
        Assert.Equal(0, (await OperatorAsync(["lifecycle-audit"])).Exit);
    }

    /// <summary>SLF3: a Forum whose database has no <c>events.leaf_hash</c> (or whose role cannot write it) says so at Error (7014) and does not start.</summary>
    [Fact]
    public async Task R6_69_SLF3_AForumWithoutTheLeafColumnDoesNotStart()
    {
        using var log = new LogCapture();
        await using var host = forum.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ILoggerProvider>(log);
            services.AddSingleton<IEventsPositionGrant>(new LeafGrant(canWriteLeaf: false));
        }));

        var started = Record.Exception(() => _ = host.Services);

        Assert.NotNull(started);
        Assert.Contains(log.All, e => e.EventId == 7014 && e.Level == LogLevel.Error);
    }

    /// <summary>SLF3b: the real grant. With <c>INSERT</c> on <c>leaf_hash</c> revoked from the role, the Forum does not start and says why; with it granted, it does (the control).</summary>
    [Fact]
    public async Task R6_69_SLF3b_TheRealGrantDecidesTheStart()
    {
        await AdminAsync($"REVOKE INSERT (leaf_hash) ON events FROM \"{forum.RoleName}\";");
        try
        {
            using var refused = new LogCapture();
            await using var withoutIt = forum.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<ILoggerProvider>(refused)));
            var started = Record.Exception(() => _ = withoutIt.Services);
            Assert.NotNull(started);
            Assert.DoesNotContain("hasn't been initialized", started.Message, StringComparison.Ordinal);
            Assert.Contains(refused.All, e => e.EventId == 7014 && e.Level == LogLevel.Error);
        }
        finally
        {
            await AdminAsync($"GRANT INSERT (leaf_hash) ON events TO \"{forum.RoleName}\";");
        }

        using var control = new LogCapture();
        await using var withIt = forum.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<ILoggerProvider>(control)));
        Assert.Null(Record.Exception(() => _ = withIt.Services));
        Assert.DoesNotContain(control.All, e => e.EventId == 7014);
    }

    /// <summary>SLF3c (R11.6 (add.)): the second column. With <c>INSERT</c> on <c>signal_target</c> revoked from the role the Forum does not start and says why (7014); the leaf's grant alone is not enough.</summary>
    [Fact]
    public async Task R11_6_SLF3c_TheSignalTargetGrantDecidesTheStartToo()
    {
        await AdminAsync($"REVOKE INSERT (signal_target) ON events FROM \"{forum.RoleName}\";");
        try
        {
            using var refused = new LogCapture();
            await using var withoutIt = forum.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<ILoggerProvider>(refused)));
            var started = Record.Exception(() => _ = withoutIt.Services);
            Assert.NotNull(started);
            Assert.DoesNotContain("hasn't been initialized", started.Message, StringComparison.Ordinal);
            Assert.Contains(refused.All, e => e.EventId == 7014 && e.Level == LogLevel.Error);
        }
        finally
        {
            await AdminAsync($"GRANT INSERT (signal_target) ON events TO \"{forum.RoleName}\";");
        }
    }

    private async Task<string> ScalarTextAsync(string sql)
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, admin);
        return (string)(await command.ExecuteScalarAsync(Ct))!;
    }

    private sealed class LeafGrant(bool canWriteLeaf) : IEventsPositionGrant
    {
        public Task<Result<bool>> RoleCanNameSeqAsync(CancellationToken cancellationToken = default) => Task.FromResult(Result<bool>.Ok(false));

        public Task<Result<bool>> RoleCanWriteLeafAsync(CancellationToken cancellationToken = default) => Task.FromResult(Result<bool>.Ok(canWriteLeaf));
    }
}

internal static class ImmutableArrayExtensions
{
    internal static System.Collections.Immutable.ImmutableArray<System.Collections.Immutable.ImmutableArray<byte>> ToImmutableArrayOf(this IEnumerable<System.Collections.Immutable.ImmutableArray<byte>> hashes) =>
        [.. hashes];
}
