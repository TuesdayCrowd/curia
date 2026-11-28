using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Retrieval;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Curia.Domain.Search;
using Curia.Infrastructure.Search;
using Npgsql;
using Xunit;

namespace Curia.Infrastructure.Tests;

/// <summary>
/// Errata G88 (R9.75, R9.77, R11.9, R11.114 to R11.116), against real Postgres and the application role's grants: the append writes a post's index rows in its own transaction (SI6), the role can insert and
/// select and nothing else (SI7), a dropped index is rebuilt byte for byte by an insert-only reconcile (SI7), the audit finds a planted, a changed and a missing row from the log alone (SI7), and the start
/// settles the schema, the tokenizer and the completeness before the Forum listens (SI15). The oracle for what a post's rows are is <see cref="SearchIndex.IndexTerms"/> over <see cref="SearchProjector.TryRead"/>
/// (the same predicate the lexical corpus uses), never the adapter's own reading; the oracle for the grants is <c>has_table_privilege</c> and the statements themselves.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "xUnit discovers a concrete test class only when it is public.")]
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Every interpolated identifier is a schema name or role name the fixture generated from a literal prefix and a GUID's hex digits.")]
[Collection(PostgresCollectionDefinition.Name)]
public sealed class SearchIndexTests(PostgresDatabaseFixture fixture)
{
    private const string Author = "https://agents.example/alice";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} {e.Detail}"));

    private static SearchIndexRules Rules => ShippedSearchIndexRules.Shipped;

    private static string Canonical(string title, string body, string board, string[] tags)
    {
        var members = ImmutableArray.CreateBuilder<KeyValuePair<string, JsonValue>>();
        members.Add(new("v", new JsonValue.Number(PostEnvelope.CurrentVersion)));
        members.Add(new("kind", new JsonValue.String(PostKinds.Wire(PostKind.Question))));
        members.Add(new("author", new JsonValue.String(Author)));
        members.Add(new("board", new JsonValue.String(board)));
        members.Add(new("title", new JsonValue.String(title)));
        members.Add(new("body", new JsonValue.String(body)));
        members.Add(new("code_blocks", new JsonValue.Array([])));
        members.Add(new("refs", new JsonValue.Array([])));
        members.Add(new("tags", new JsonValue.Array([.. tags.Select(t => new JsonValue.String(t))])));
        members.Add(new("content_type", new JsonValue.String(PostEnvelope.RequiredContentType)));
        members.Add(new("created_at", new JsonValue.String(Start.ToString("o", CultureInfo.InvariantCulture))));
        members.Add(new("nonce", new JsonValue.String("00112233445566778899aabbccddeeff")));
        return Encoding.UTF8.GetString(Require(CanonicalJson.CanonicalizeWithNfc(new JsonValue.Object(members.ToImmutable()))).Span);
    }

    private async Task<(PostgresEventStore Store, PostgresSearchIndex Index, string Schema)> NewAsync()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(Ct);
        var store = new PostgresEventStore(fixture.AppRoleDataSource, new ManualTimeProvider(Start), fixture.PermissiveLifecycleGate(), schema);
        return (store, new PostgresSearchIndex(fixture.AppRoleDataSource, schema), schema);
    }

    private static async Task<AppendedEvent> AppendPostAsync(
        PostgresEventStore store, string postId, string title = "Member ordering in JCS", string body = "How does JCS order object members?", string board = "canonicalization", string[]? tags = null)
    {
        var payload = new JsonValue.Object(
        [
            new("post_id", new JsonValue.String(postId)),
            new("canonical", new JsonValue.String(Canonical(title, body, board, tags ?? ["jcs"]))),
            new("signature", new JsonValue.String("sig")),
            new("digest", new JsonValue.String("sha-256:" + postId)),
            new("author", new JsonValue.String(Author)),
            new("board", new JsonValue.String(board)),
            new("kind", new JsonValue.String("question")),
        ]);

        var appended = Require(await store.AppendAsync(
            Require(AggregateId.Create(postId)), AggregateVersion.New,
            [new DomainEvent(Require(EventId.Create(postId)), Require(EventType.Create(PostProjector.PostAcceptedType)), Require(ActorId.Create(Author)), payload)], Ct));
        return appended[0];
    }

    private async Task AdminAsync(string sql)
    {
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture);
    }

    /// <summary>Every stored row of the schema's index, one line each, sorted, with the one column that is time (<c>indexed_at</c>) left out: what a rebuild must reproduce byte for byte.</summary>
    private async Task<string> SnapshotAsync(string schema)
    {
        var lines = new List<string>();
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync(Ct);
        await using (var docs = new NpgsqlCommand(
            $"SELECT 'doc|' || seq || '|' || index_version || '|' || post_id || '|' || digest || '|' || board || '|' || kind || '|' || author || '|' || array_to_string(tags_folded, ',') || '|' || terms_truncated FROM \"{schema}\".search_docs;", connection))
        await using (var reader = await docs.ExecuteReaderAsync(Ct))
            while (await reader.ReadAsync(Ct)) lines.Add(reader.GetString(0));
        await using (var terms = new NpgsqlCommand(
            $"SELECT 'term|' || index_version || '|' || term || '|' || seq || '|' || title_tf || '|' || tag_tf || '|' || body_tf FROM \"{schema}\".search_terms;", connection))
        await using (var reader = await terms.ExecuteReaderAsync(Ct))
            while (await reader.ReadAsync(Ct)) lines.Add(reader.GetString(0));
        lines.Sort(StringComparer.Ordinal);
        return string.Join('\n', lines);
    }

    private static async Task<List<StoredIndexEntry>> StoredAsync(PostgresSearchIndex index)
    {
        var all = new List<StoredIndexEntry>();
        await foreach (var entry in index.ReadAllAsync(SearchIndex.Version, Ct)) all.Add(entry);
        return all;
    }

    // ---- SI6 ----

    /// <summary>
    /// SI6, R9.75: the append writes the post's rows in its own transaction, and they are the rows <see cref="SearchIndex.IndexTerms"/> makes of the post the lexical corpus reads: found by the next call, with no
    /// reconcile between. Red on the unmodified tree: no row exists.
    /// </summary>
    [Fact]
    public async Task R9_75_SI6_TheAppendWritesThePostsRowsAndTheyAreTheOracles()
    {
        var (store, index, _) = await NewAsync();

        var row = await AppendPostAsync(store, "si6a", title: "Rust and rust", body: "the borrow checker, the the", tags: ["Rust", "rust", "Cargo"]);

        var expected = SearchIndex.IndexTerms(SearchProjector.TryRead(row)!, Rules.Fold);
        var stored = await StoredAsync(index);
        Assert.Single(stored);
        Assert.True(stored[0].HasDocument);
        Assert.Equal(expected with { Terms = [], TagsFolded = [] }, stored[0].Post with { Terms = [], TagsFolded = [] });
        Assert.Equal(expected.TagsFolded, stored[0].Post.TagsFolded);
        Assert.Equal(expected.Terms.OrderBy(t => t.Key, StringComparer.Ordinal), stored[0].Post.Terms.OrderBy(t => t.Key, StringComparer.Ordinal));
        Assert.Contains(stored[0].Post.Terms, t => t is { Key: "the", BodyTf: 3 });   // R9.72's list is a query's: a stop word is stored
        Assert.Contains(stored[0].Post.Terms, t => t is { Key: "RUST", TagTf: 2 });   // Rust and rust are two tags
    }

    /// <summary>SI6b, R11.115: a post whose rows cannot be written is not stored: neither the event nor the document is there. Red on the unmodified tree: the event is stored and nothing else is asked.</summary>
    [Fact]
    public async Task R11_115_SI6_APostThatCannotBeIndexedIsNotStored()
    {
        var (store, _, schema) = await NewAsync();
        await AdminAsync($"REVOKE INSERT ON \"{schema}\".search_terms FROM \"{fixture.RoleName}\";");

        var refused = await Record.ExceptionAsync(() => AppendPostAsync(store, "si6b"));

        Assert.NotNull(refused);
        Assert.Equal((0L, 0L), (await CountAsync($"SELECT count(*) FROM \"{schema}\".events;"), await CountAsync($"SELECT count(*) FROM \"{schema}\".search_docs;")));

        await AdminAsync($"GRANT INSERT ON \"{schema}\".search_terms TO \"{fixture.RoleName}\";");
        await AppendPostAsync(store, "si6b-control");
        Assert.Equal((1L, 1L), (await CountAsync($"SELECT count(*) FROM \"{schema}\".events;"), await CountAsync($"SELECT count(*) FROM \"{schema}\".search_docs;")));
    }

    /// <summary>SI6c: an event that is not a discussion post writes no rows (SI9's other half; the kinds are <c>PostKinds.IsDiscussion</c>'s).</summary>
    [Fact]
    public async Task R9_75_SI9_AnEventThatIsNotAPostWritesNoRows()
    {
        var (store, _, schema) = await NewAsync();
        Require(await store.AppendAsync(
            Require(AggregateId.Create("not-a-post")), AggregateVersion.New,
            [new DomainEvent(Require(EventId.Create("np1")), Require(EventType.Create("test.event")), null, new JsonValue.Object([new("n", new JsonValue.Number(1))]))], Ct));

        Assert.Equal((1L, 0L, 0L), (await CountAsync($"SELECT count(*) FROM \"{schema}\".events;"), await CountAsync($"SELECT count(*) FROM \"{schema}\".search_docs;"), await CountAsync($"SELECT count(*) FROM \"{schema}\".search_terms;")));
    }

    /// <summary>SI9, R9.77: a 10,000-character token is stored under its hash key and a 4,100-key post is capped at 4,096 and marked; a tag spelled as a hash key is hashed, not stored as typed.</summary>
    [Fact]
    public async Task R9_77_SI9_ALongKeyIsHashedAndAHugePostIsCappedAndMarked()
    {
        var (store, index, _) = await NewAsync();
        var longToken = new string('x', 10_000);
        var distinct = string.Join(' ', Enumerable.Range(0, 4_100).Select(i => "w" + i.ToString("D4", CultureInfo.InvariantCulture)));
        var forged = SearchIndex.Key(new string('a', 65));   // '#' and 43 characters: a key an author can type as a tag

        await AppendPostAsync(store, "si9-long", body: longToken, tags: [forged]);
        await AppendPostAsync(store, "si9-huge", body: distinct);

        var stored = await StoredAsync(index);
        var longPost = stored.Single(e => e.Post.PostId == "si9-long").Post;
        var hugePost = stored.Single(e => e.Post.PostId == "si9-huge").Post;
        Assert.Contains(longPost.Terms, t => t.Key == SearchIndex.Key(longToken) && t.BodyTf == 1);
        Assert.All(longPost.Terms, t => Assert.True(Encoding.UTF8.GetByteCount(t.Key) <= 65, "a key over the btree ceiling was stored"));
        Assert.DoesNotContain(longPost.TagsFolded, k => k == forged);
        Assert.Equal((SearchIndex.MaximumTerms, true), (hugePost.Terms.Length, hugePost.TermsTruncated));
        Assert.False(longPost.TermsTruncated);
    }

    // ---- SI7 ----

    /// <summary>SI7, R11.114: the role holds SELECT and INSERT on each table and no UPDATE, DELETE or TRUNCATE, read from the catalogue and from the statements the role then cannot run.</summary>
    [Fact]
    public async Task R11_114_SI7_TheRoleCanSelectAndInsertAndNothingElse()
    {
        var (store, index, schema) = await NewAsync();
        await AppendPostAsync(store, "si7a");

        var granted = new List<string>();
        foreach (var table in new[] { "search_docs", "search_terms", "search_index_versions" })
            foreach (var privilege in new[] { "SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE" })
                if (await CountAsync($"SELECT CASE WHEN has_table_privilege('{fixture.RoleName}', '\"{schema}\".{table}', '{privilege}') THEN 1 ELSE 0 END;") == 1) granted.Add($"{table}:{privilege}");

        Assert.Equal(["search_docs:INSERT", "search_docs:SELECT", "search_index_versions:INSERT", "search_index_versions:SELECT", "search_terms:INSERT", "search_terms:SELECT"], granted.Order(StringComparer.Ordinal).ToArray());

        foreach (var statement in new[]
        {
            $"UPDATE \"{schema}\".search_terms SET body_tf = 99;",
            $"UPDATE \"{schema}\".search_docs SET board = 'x';",
            $"DELETE FROM \"{schema}\".search_terms;",
            $"DELETE FROM \"{schema}\".search_docs;",
            $"TRUNCATE \"{schema}\".search_terms;",
        })
        {
            await using var connection = await fixture.AppRoleDataSource.OpenConnectionAsync(Ct);
            await using var command = new NpgsqlCommand(statement, connection);
            var denied = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct));
            Assert.Equal("42501", denied.SqlState);
        }

        var schemaAnswer = Require(await index.CheckSchemaAsync(Ct));
        Assert.Equal(new SearchIndexSchema(true, true, true, false), schemaAnswer);
    }

    /// <summary>SI7, R11.9: with the rows dropped as the owner, the insert-only reconcile (the role's own) rebuilds them byte for byte, and a second run inserts nothing.</summary>
    [Fact]
    public async Task R11_9_SI7_ADroppedIndexIsRebuiltByteForByteByAnInsertOnlyReconcile()
    {
        var (store, index, schema) = await NewAsync();
        for (var i = 0; i < 6; i++) await AppendPostAsync(store, "si7b-" + i, title: "Post " + i, body: "alpha beta gamma " + i + " the", tags: i % 2 == 0 ? ["Alpha"] : []);
        var built = await SnapshotAsync(schema);
        Assert.NotEmpty(built);

        await AdminAsync($"DELETE FROM \"{schema}\".search_terms; DELETE FROM \"{schema}\".search_docs;");
        var first = Require(await SearchIndexReconcile.RunAsync(store, index, Rules, cancellationToken: Ct));
        var second = Require(await SearchIndexReconcile.RunAsync(store, index, Rules, cancellationToken: Ct));

        Assert.Equal((6L, 0L, 6L, 0L), (first.Posts, first.AlreadyIndexed, first.Inserted, first.StillMissing));
        Assert.Equal((6L, 6L, 0L, 0L), (second.Posts, second.AlreadyIndexed, second.Inserted, second.StillMissing));
        Assert.Equal(built, await SnapshotAsync(schema));
    }

    /// <summary>
    /// SI7, R11.114, non-property (a): the audit recomputes from the log and finds a changed term (<c>different</c>), a document for no post (<c>extra</c>), a post whose rows were removed (<c>missing</c>)
    /// and a term planted under a post that does not hold it, whatever the planter spelled the key as; and a reconcile does not replace a wrong row (the audit still finds it).
    /// </summary>
    [Fact]
    public async Task R11_114_SI7_TheAuditFindsAPlantedAChangedAndAMissingRow()
    {
        var (store, index, schema) = await NewAsync();
        var a = await AppendPostAsync(store, "si7c-a", body: "alpha beta");
        var b = await AppendPostAsync(store, "si7c-b", body: "gamma delta");
        var c = await AppendPostAsync(store, "si7c-c", body: "epsilon zeta");
        var d = await AppendPostAsync(store, "si7c-d", body: "eta theta");

        var clean = Require(await SearchIndexAudit.RunAsync(store, index, Rules, Ct));
        Assert.Equal((4L, 0L), (clean.Posts, clean.FindingCount));   // the control: a clean index is no finding

        await AdminAsync($"UPDATE \"{schema}\".search_terms SET body_tf = 9 WHERE seq = {a.Seq.Value} AND term = 'alpha';");
        await AdminAsync($"INSERT INTO \"{schema}\".search_terms (index_version, term, seq, title_tf, tag_tf, body_tf) VALUES (1, '{SearchIndex.Key(new string('q', 65))}', {b.Seq.Value}, 0, 1, 0);");
        await AdminAsync($"DELETE FROM \"{schema}\".search_terms WHERE seq = {c.Seq.Value}; DELETE FROM \"{schema}\".search_docs WHERE seq = {c.Seq.Value};");
        await AdminAsync($"INSERT INTO \"{schema}\".search_docs (seq, index_version, post_id, digest, board, kind, author, tags_folded, terms_truncated) VALUES (9999, 1, 'ghost', 'sha-256:ghost', 'b', 'question', 'x', '{{}}', false);");

        var audited = Require(await SearchIndexAudit.RunAsync(store, index, Rules, Ct));
        var reconciled = Require(await SearchIndexReconcile.RunAsync(store, index, Rules, cancellationToken: Ct));
        var after = Require(await SearchIndexAudit.RunAsync(store, index, Rules, Ct));

        Assert.Equal(
            [$"different {a.Seq.Value}", $"different {b.Seq.Value}", $"missing {c.Seq.Value}", "extra 9999"],
            audited.Findings.Select(f => $"{f.Kind} {f.Seq}").ToArray());
        Assert.Equal(1L, reconciled.Inserted);                      // the removed post's rows come back
        Assert.Equal(3L, after.FindingCount);                        // the changed row, the planted term and the ghost are not replaced and not removed: the owner's
        Assert.DoesNotContain(after.Findings, f => f.Kind == "missing");
        _ = d;
    }

    // ---- SI15 ----

    /// <summary>SI15a, R11.114: a database without db/0008's tables is refused at start, naming that it is not applied, and writes nothing.</summary>
    [Fact]
    public async Task R11_114_SI15_AStartWithoutTheTablesIsRefused()
    {
        var (store, _, _) = await NewAsync();
        var without = await fixture.CreateIsolatedOperationalSchemaAsync(Ct);   // a schema that holds the operational tables and no search table
        await using var index = new PostgresSearchIndex(fixture.AppRoleDataSource, without);

        var answer = await SearchIndexStartCheck.RunAsync(store, index, Rules, cancellationToken: Ct);

        Assert.False(answer.TryGetValue(out _, out var error));
        Assert.Equal("curia/search-index/not-applied", error!.Type);
    }

    /// <summary>SI15b, R11.114: a missing index (the GIN on the folded tags) is as refused as a missing table.</summary>
    [Fact]
    public async Task R11_114_SI15_AStartWithoutAnIndexIsRefused()
    {
        var (store, index, schema) = await NewAsync();
        await AdminAsync($"DROP INDEX \"{schema}\".search_docs_tags_folded;");

        var answer = await SearchIndexStartCheck.RunAsync(store, index, Rules, cancellationToken: Ct);

        Assert.False(answer.TryGetValue(out _, out var error));
        Assert.Equal("curia/search-index/not-applied", error!.Type);
    }

    /// <summary>SI15c, R11.116: a runtime whose letter table is not the committed one is refused before any digest is recorded.</summary>
    [Fact]
    public async Task R11_116_SI15_ARuntimeThatIsNotTheCommittedOneIsRefusedAndRecordsNothing()
    {
        var (store, index, schema) = await NewAsync();
        var drifted = new SearchIndexRules(Rules.Fold, Rules.FoldDigest, Rules.CommittedTokenizerDigest, () => [.. new byte[32]]);

        var answer = await SearchIndexStartCheck.RunAsync(store, index, drifted, cancellationToken: Ct);

        Assert.False(answer.TryGetValue(out _, out var error));
        Assert.Equal("curia/search-index/tokenizer-changed", error!.Type);
        Assert.Equal(0L, await CountAsync($"SELECT count(*) FROM \"{schema}\".search_index_versions;"));
    }

    /// <summary>
    /// SI15d, R11.116: a version recorded under other digests (a tokenizer or a table changed under the same number) is refused, naming the version. Each digest alone: the tokenizer's differing with the
    /// table's right, and the table's differing with the tokenizer's right.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task R11_116_SI15_AVersionRecordedUnderOtherDigestsIsRefused(bool tokenizerDiffers, bool foldDiffers)
    {
        var (store, index, schema) = await NewAsync();
        var zero = "decode(repeat('00', 32), 'hex')";
        var tokenizer = tokenizerDiffers ? zero : $"decode('{Convert.ToHexStringLower([.. Rules.RuntimeTokenizerDigest()])}', 'hex')";
        var fold = foldDiffers ? zero : $"decode('{Convert.ToHexStringLower([.. Rules.FoldDigest])}', 'hex')";
        await AdminAsync($"INSERT INTO \"{schema}\".search_index_versions (index_version, tokenizer_digest, fold_digest) VALUES (1, {tokenizer}, {fold});");

        var answer = await SearchIndexStartCheck.RunAsync(store, index, Rules, cancellationToken: Ct);

        Assert.False(answer.TryGetValue(out _, out var error));
        Assert.Equal("curia/search-index/version-digests-differ", error!.Type);
        Assert.Contains("index_version=1", error.Detail, StringComparison.Ordinal);
    }

    /// <summary>SI7, R11.114: inserting the rows of a post that already has them writes nothing and raises nothing, and a row that exists is never replaced, whatever the new rows say.</summary>
    [Fact]
    public async Task R11_114_SI7_AnInsertOfAPostThatHasRowsLeavesThemAlone()
    {
        var (store, index, schema) = await NewAsync();
        var row = await AppendPostAsync(store, "si7d", body: "alpha beta");
        var before = await SnapshotAsync(schema);
        var other = SearchIndex.IndexTerms(SearchProjector.TryRead(row)! with { Body = "something else entirely" }, Rules.Fold);

        var inserted = Require(await index.InsertAsync(SearchIndex.Version, [other], Ct));

        Assert.Equal(0L, inserted);
        Assert.Equal(before, await SnapshotAsync(schema));
    }

    /// <summary>
    /// SI15f, R11.114: a reconcile that cannot make a post's rows appear (an index whose writes are swallowed) leaves it missing, the count after it says so, and the start refuses it as incomplete: the Forum
    /// does not start with a post that search would drop and say nothing.
    /// </summary>
    [Fact]
    public async Task R11_114_SI15_APostTheReconcileCouldNotWriteIsRefusedAsIncomplete()
    {
        var (store, real, schema) = await NewAsync();
        await AppendPostAsync(store, "si15f");
        await AdminAsync($"DELETE FROM \"{schema}\".search_terms; DELETE FROM \"{schema}\".search_docs;");
        var swallowing = new SwallowingInserts(real);

        var answer = await SearchIndexStartCheck.RunAsync(store, swallowing, Rules, cancellationToken: Ct);

        Assert.False(answer.TryGetValue(out _, out var error));
        Assert.Equal("curia/search-index/incomplete", error!.Type);
        Assert.Contains("missing=1", error.Detail, StringComparison.Ordinal);
    }

    /// <summary>An index whose inserts report success and write nothing: the one way a reconcile can finish and leave a post without its rows.</summary>
    private sealed class SwallowingInserts(PostgresSearchIndex inner) : ISearchIndex
    {
        public Task<Result<SearchIndexSchema>> CheckSchemaAsync(CancellationToken cancellationToken = default) => inner.CheckSchemaAsync(cancellationToken);

        public Task<Result<SearchIndexVersionRecord?>> ReadVersionAsync(short version, CancellationToken cancellationToken = default) => inner.ReadVersionAsync(version, cancellationToken);

        public Task<Result<bool>> RecordVersionAsync(short version, ImmutableArray<byte> tokenizerDigest, ImmutableArray<byte> foldDigest, CancellationToken cancellationToken = default) =>
            inner.RecordVersionAsync(version, tokenizerDigest, foldDigest, cancellationToken);

        public Task<Result<IReadOnlySet<long>>> IndexedSeqsAsync(short version, CancellationToken cancellationToken = default) => inner.IndexedSeqsAsync(version, cancellationToken);

        public Task<Result<long>> InsertAsync(short version, IReadOnlyList<IndexedPost> posts, CancellationToken cancellationToken = default) => Task.FromResult(Result<long>.Ok(posts.Count));

        public IAsyncEnumerable<StoredIndexEntry> ReadAllAsync(short version, CancellationToken cancellationToken = default) => inner.ReadAllAsync(version, cancellationToken);
    }

    /// <summary>SI15e: the control. A first start records the version and fills the posts the index lacks; a second start records nothing and inserts nothing; a role that can also UPDATE is reported and still starts.</summary>
    [Fact]
    public async Task R11_114_SI15_AStartRecordsTheVersionReconcilesAndRunsTwice()
    {
        var (store, index, schema) = await NewAsync();
        for (var i = 0; i < 3; i++) await AppendPostAsync(store, "si15e-" + i);
        await AdminAsync($"DELETE FROM \"{schema}\".search_terms; DELETE FROM \"{schema}\".search_docs;");

        var first = Require(await SearchIndexStartCheck.RunAsync(store, index, Rules, cancellationToken: Ct));
        var second = Require(await SearchIndexStartCheck.RunAsync(store, index, Rules, cancellationToken: Ct));
        await AdminAsync($"GRANT UPDATE ON \"{schema}\".search_terms TO \"{fixture.RoleName}\";");
        var wide = Require(await SearchIndexStartCheck.RunAsync(store, index, Rules, cancellationToken: Ct));

        Assert.Equal((true, 3L, 0L), (first.RecordedVersion, first.Reconcile.Inserted, first.Reconcile.StillMissing));
        Assert.Equal((false, 0L), (second.RecordedVersion, second.Reconcile.Inserted));
        Assert.True(wide.Schema.CanUpdateOrDelete);
        Assert.Equal(1L, await CountAsync($"SELECT count(*) FROM \"{schema}\".search_index_versions;"));
    }

    // ---- SI10 ----

    /// <summary>
    /// SI10, R11.114: a new index version is written beside the old; the old version's rows are not read as the new one's, not changed and not removed. Red when the seqs a reconcile counts as held are those of
    /// every version (it would write nothing under version 2).
    /// </summary>
    [Fact]
    public async Task R11_114_SI10_ANewVersionIsWrittenBesideTheOldAndTheOldIsLeftAlone()
    {
        var (store, index, schema) = await NewAsync();
        for (var i = 0; i < 3; i++) await AppendPostAsync(store, "si10-" + i);
        var first = await SnapshotAsync(schema);
        var next = new SearchIndexRules(Rules.Fold, Rules.FoldDigest, Rules.CommittedTokenizerDigest, version: 2);

        var reconciled = Require(await SearchIndexReconcile.RunAsync(store, index, next, cancellationToken: Ct));

        Assert.Equal((3L, 0L, 3L, 0L), (reconciled.Posts, reconciled.AlreadyIndexed, reconciled.Inserted, reconciled.StillMissing));
        Assert.Equal((3L, 3L), (await CountAsync($"SELECT count(*) FROM \"{schema}\".search_docs WHERE index_version = 1;"), await CountAsync($"SELECT count(*) FROM \"{schema}\".search_docs WHERE index_version = 2;")));
        Assert.Equal(first, string.Join('\n', (await SnapshotAsync(schema)).Split('\n').Where(l => l.StartsWith("doc|", StringComparison.Ordinal) ? l.Split('|')[2] == "1" : l.Split('|')[1] == "1")));
    }

    // ---- SI7 (collation) ----

    /// <summary>R11.114, FSI38: the two key columns are collated <c>C</c>, read from the catalogue: a btree under an operating system's collation is the index that corrupts on an upgrade.</summary>
    [Fact]
    public async Task R11_114_SI7_TheKeyColumnsAreCollatedC()
    {
        var (_, _, schema) = await NewAsync();

        var collations = new List<string>();
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT c.relname || '.' || a.attname || '=' || coalesce(co.collname, '') FROM pg_attribute a JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace " +
            "LEFT JOIN pg_collation co ON co.oid = a.attcollation WHERE n.nspname = @schema AND ((c.relname = 'search_terms' AND a.attname = 'term') OR (c.relname = 'search_docs' AND a.attname = 'tags_folded')) ORDER BY 1;", connection);
        command.Parameters.AddWithValue("schema", schema);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) collations.Add(reader.GetString(0));

        Assert.Equal(["search_docs.tags_folded=C", "search_terms.term=C"], collations);
    }
}
