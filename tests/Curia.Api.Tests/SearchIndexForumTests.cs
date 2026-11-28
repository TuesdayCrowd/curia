using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using Curia.Application.Retrieval;
using Curia.OperatorTool;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;
using static Curia.Api.Tests.BoardListProbe;
using static Curia.Api.Tests.NameProbe;

namespace Curia.Api.Tests;

/// <summary>
/// Errata G88, db/0008, through the Forum as the application role (the fixture's host connects as it, with the grants a deployment has): SIF1 the rows the route's own appends wrote are the log's, a vote leaves none
/// (SI9) and <c>search-audit</c> says so; SIF2 <c>reindex-search</c> run with the application role's own connection string rebuilds what the owner removed, and <c>search-audit</c> finds a planted row and exits 7
/// (LSI2's shape); SIF3 a Forum over a database without the tables, or under a runtime whose letter table is not the build's, does not start and says why (R11.114, R11.116); SIF4 the query cap (R9.79).
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Every statement is a constant or built from the fixture's own generated names.")]
[Collection("role-forum")]
public sealed class SearchIndexForumTests(RoleForumFixture forum) : IClassFixture<RoleForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(int Exit, string Out, string Err)> OperatorAsync(string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        // The application role's own connection string: reindex-search and search-audit need no owner role.
        var exit = await OperatorCommands.RunAsync(args, forum.RoleConnectionString, forum.Clock, forum.PermissiveGate(), OperatorCommands.LoadDeclarationSigner, stdout, stderr, Ct);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, admin);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture);
    }

    private async Task AdminAsync(string sql)
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, admin);
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>
    /// SIF1: every discussion post the route stored has its document, a vote the route stored has none (SI9), and the audit is quiet. The oracle for the post count is the events table by SQL, a different
    /// reading from the index's own.
    /// </summary>
    [Fact]
    public async Task R9_75_SIF1_TheRoutesAppendsWriteTheRowsAndAVoteWritesNone()
    {
        var board = Name("sif1");
        var stored = await StoreAsync(forum, board, 4, 2, Ct);
        var documentsBefore = await CountAsync("SELECT count(*) FROM search_docs;");
        await StoreVoteAsync(forum, board, stored[0].Digest, Ct);

        var posts = await CountAsync("SELECT count(*) FROM events WHERE seq > 0 AND event_type = 'post.accepted' AND payload->>'kind' IN ('question','answer','finding','comment','revision');");
        var documents = await CountAsync("SELECT count(*) FROM search_docs;");
        var audit = await OperatorAsync(["search-audit"]);

        Assert.Equal(posts, documents);
        Assert.Equal(documentsBefore, documents);                  // the vote added none
        Assert.Equal(0, audit.Exit);
        Assert.Equal($"posts {posts.ToString(CultureInfo.InvariantCulture)}  findings 0", audit.Out.Trim());
    }

    /// <summary>
    /// SIF2: the owner removes a post's rows, and <c>reindex-search</c> (the role's own connection) writes them back and says how many; the owner then changes a row and plants another, and
    /// <c>search-audit</c> exits 7 naming each by <c>seq</c>, and a second <c>reindex-search</c> replaces neither. The owner puts the rows right and the audit is quiet.
    /// </summary>
    [Fact]
    public async Task R11_114_SIF2_ReindexRebuildsWhatTheOwnerRemovedAndTheAuditFindsWhatIsPlanted()
    {
        var board = Name("sif2");
        var stored = await StoreAsync(forum, board, 3, 1, Ct);
        var seqs = new List<long>();
        foreach (var post in stored) seqs.Add(await CountAsync($"SELECT seq FROM events WHERE event_id = '{post.PostId}';"));
        var total = await CountAsync("SELECT count(*) FROM search_docs;");

        await AdminAsync($"DELETE FROM search_terms WHERE seq = {seqs[0]}; DELETE FROM search_docs WHERE seq = {seqs[0]};");
        var missing = await OperatorAsync(["search-audit"]);
        var rebuilt = await OperatorAsync(["reindex-search"]);
        var quiet = await OperatorAsync(["search-audit"]);

        var term = await CountAsync($"SELECT count(*) FROM search_terms WHERE seq = {seqs[1]};");
        await AdminAsync($"UPDATE search_terms SET body_tf = body_tf + 7 WHERE seq = {seqs[1]} AND term = (SELECT min(term) FROM search_terms WHERE seq = {seqs[1]});");
        await AdminAsync("INSERT INTO search_docs (seq, index_version, post_id, digest, board, kind, author, tags_folded, terms_truncated) VALUES (987654321, 1, 'ghost', 'sha-256:ghost', 'b', 'question', 'x', '{}', false);");
        try
        {
            var planted = await OperatorAsync(["search-audit"]);
            var again = await OperatorAsync(["reindex-search"]);
            var stillPlanted = await OperatorAsync(["search-audit"]);

            Assert.Equal((7, true), (missing.Exit, missing.Out.Contains($"missing  seq={seqs[0].ToString(CultureInfo.InvariantCulture)}", StringComparison.Ordinal)));
            Assert.Equal(0, rebuilt.Exit);
            Assert.Contains($"already_indexed {(total - 1).ToString(CultureInfo.InvariantCulture)}  inserted 1  still_missing 0", rebuilt.Out, StringComparison.Ordinal);
            Assert.Equal(0, quiet.Exit);
            Assert.True(term > 0);
            Assert.Equal(7, planted.Exit);
            Assert.Contains($"different  seq={seqs[1].ToString(CultureInfo.InvariantCulture)}", planted.Out, StringComparison.Ordinal);
            Assert.Contains("extra  seq=987654321", planted.Out, StringComparison.Ordinal);
            Assert.Equal(0, again.Exit);
            Assert.Contains("inserted 0", again.Out, StringComparison.Ordinal);       // a wrong row is the audit's, never this verb's
            Assert.Equal(7, stillPlanted.Exit);
        }
        finally
        {
            await AdminAsync("DELETE FROM search_terms WHERE seq = 987654321; DELETE FROM search_docs WHERE seq = 987654321;");
            await AdminAsync($"UPDATE search_terms SET body_tf = body_tf - 7 WHERE seq = {seqs[1]} AND term = (SELECT min(term) FROM search_terms WHERE seq = {seqs[1]});");
        }

        Assert.Equal(0, (await OperatorAsync(["search-audit"])).Exit);
    }

    /// <summary>SIF3a, R11.114: a Forum whose database lacks a table of db/0008 says so at Error (7018) and does not start; with it back, it does (the control).</summary>
    [Fact]
    public async Task R11_114_SIF3_AForumWithoutTheTablesDoesNotStart()
    {
        await AdminAsync("ALTER TABLE search_terms RENAME TO search_terms_hidden;");
        try
        {
            using var refused = new LogCapture();
            await using var without = forum.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<ILoggerProvider>(refused)));
            var started = Record.Exception(() => without.CreateClient());

            Assert.NotNull(started);
            Assert.Contains("curia/search-index/not-applied", started.ToString(), StringComparison.Ordinal);
            Assert.Contains(refused.All, e => e.EventId == 7018 && e.Level == LogLevel.Error);
        }
        finally
        {
            await AdminAsync("ALTER TABLE search_terms_hidden RENAME TO search_terms;");
        }

        using var control = new LogCapture();
        await using var with = forum.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<ILoggerProvider>(control)));
        using var client = with.CreateClient();
        using var health = await client.GetAsync(new Uri("/health", UriKind.Relative), Ct);
        Assert.Equal(200, (int)health.StatusCode);
        Assert.DoesNotContain(control.All, e => e.EventId == 7018);
        Assert.Contains(control.All, e => e.EventId == 7016);
    }

    /// <summary>SIF3b, R11.116: a Forum whose runtime's letter table is not the build's does not start, and records no digest.</summary>
    [Fact]
    public async Task R11_116_SIF3_AForumUnderADifferentRuntimeDoesNotStart()
    {
        var shipped = Curia.Infrastructure.Search.ShippedSearchIndexRules.Shipped;
        var drifted = new SearchIndexRules(shipped.Fold, shipped.FoldDigest, shipped.CommittedTokenizerDigest, () => [.. new byte[32]]);
        using var log = new LogCapture();
        await using var host = forum.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ILoggerProvider>(log);
            services.AddSingleton(drifted);
        }));

        var started = Record.Exception(() => host.CreateClient());

        Assert.NotNull(started);
        Assert.Contains("curia/search-index/tokenizer-changed", started.ToString(), StringComparison.Ordinal);
        Assert.Contains(log.All, e => e.EventId == 7018 && e.Level == LogLevel.Error);
    }

    /// <summary>SIF4, R9.79: 32 distinct terms are searched, 33 are refused 400 before anything is read, repeats and the ignored words cost none, and the refusal echoes nothing of the query.</summary>
    [Fact]
    public async Task R9_79_SIF4_AQueryOfMoreThanThirtyTwoDistinctTermsIsRefused()
    {
        string Query(int distinct, string prefix) => Uri.EscapeDataString(string.Join(' ', Enumerable.Range(0, distinct).Select(i => prefix + i.ToString("D3", CultureInfo.InvariantCulture))));
        async Task<(HttpStatusCode Status, string Body)> SearchAsync(string q)
        {
            using var response = await forum.Client.GetAsync(new Uri("/v1/search?q=" + q, UriKind.Relative), Ct);
            return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        }

        var served = await SearchAsync(Query(32, "okterm"));
        var repeated = await SearchAsync(Uri.EscapeDataString(string.Join(' ', Enumerable.Repeat(string.Join(' ', Enumerable.Range(0, 32).Select(i => "okterm" + i.ToString("D3", CultureInfo.InvariantCulture))), 3)) + " the and of"));
        var refused = await SearchAsync(Query(33, "secretterm"));

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.BadRequest), (served.Status, repeated.Status, refused.Status));
        Assert.Contains("curia/search/query-too-many-terms", refused.Body, StringComparison.Ordinal);
        Assert.Contains("terms=33; limit=32", refused.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("secretterm", refused.Body, StringComparison.Ordinal);
    }
}
