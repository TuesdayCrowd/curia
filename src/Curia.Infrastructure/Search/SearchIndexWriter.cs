using System.Diagnostics.CodeAnalysis;
using Curia.Domain.Search;
using Npgsql;
using NpgsqlTypes;

namespace Curia.Infrastructure.Search;

/// <summary>
/// The one writer of <c>search_docs</c> and <c>search_terms</c> (errata G88, R11.115). The event store's append calls it on the append's own connection and transaction, so a post that cannot be indexed is
/// not stored; the reconcile calls it with <c>ON CONFLICT DO NOTHING</c>, so a row that exists is never replaced. No other type inserts into these tables (a fence holds it), and nothing here updates or deletes.
/// </summary>
internal static class SearchIndexWriter
{
    /// <summary>
    /// Writes <paramref name="post"/>'s document and terms and returns whether the document was written (false only where <paramref name="skipExisting"/> and the post already had one). The terms are one
    /// <c>INSERT ... SELECT unnest(...)</c>, about 160 rows for a 2 KB post.
    /// </summary>
    [SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Every awaited call carries ConfigureAwait(false); the analyzer flags the `await using` disposal.")]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The only interpolated text is the quoted schema name the caller built once; every value is a bound parameter.")]
    public static async Task<bool> WriteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string quotedSchema,
        short version,
        IndexedPost post,
        bool skipExisting,
        CancellationToken cancellationToken)
    {
        var conflict = skipExisting ? " ON CONFLICT DO NOTHING" : string.Empty;

        await using (var document = new NpgsqlCommand(
            $"INSERT INTO {quotedSchema}.search_docs (seq, index_version, post_id, digest, board, kind, author, tags_folded, terms_truncated) " +
            $"VALUES (@seq, @v, @post, @digest, @board, @kind, @author, @tags, @truncated){conflict};", connection, transaction))
        {
            document.Parameters.Add(new NpgsqlParameter("seq", NpgsqlDbType.Bigint) { Value = post.Seq });
            document.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Smallint) { Value = version });
            document.Parameters.Add(new NpgsqlParameter("post", NpgsqlDbType.Text) { Value = post.PostId });
            document.Parameters.Add(new NpgsqlParameter("digest", NpgsqlDbType.Text) { Value = post.Digest });
            document.Parameters.Add(new NpgsqlParameter("board", NpgsqlDbType.Text) { Value = post.Board });
            document.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Text) { Value = post.Kind });
            document.Parameters.Add(new NpgsqlParameter("author", NpgsqlDbType.Text) { Value = post.Author });
            document.Parameters.Add(new NpgsqlParameter("tags", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = post.TagsFolded.ToArray() });
            document.Parameters.Add(new NpgsqlParameter("truncated", NpgsqlDbType.Boolean) { Value = post.TermsTruncated });
            if (await document.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0) return false;
        }

        if (post.Terms.IsEmpty) return true;

        await using var terms = new NpgsqlCommand(
            $"INSERT INTO {quotedSchema}.search_terms (index_version, term, seq, title_tf, tag_tf, body_tf) " +
            $"SELECT @v, t.term, @seq, t.a, t.b, t.c FROM unnest(@terms, @a, @b, @c) AS t(term, a, b, c){conflict};", connection, transaction);
        terms.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Smallint) { Value = version });
        terms.Parameters.Add(new NpgsqlParameter("seq", NpgsqlDbType.Bigint) { Value = post.Seq });
        terms.Parameters.Add(new NpgsqlParameter("terms", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = post.Terms.Select(t => t.Key).ToArray() });
        terms.Parameters.Add(new NpgsqlParameter("a", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = post.Terms.Select(t => t.TitleTf).ToArray() });
        terms.Parameters.Add(new NpgsqlParameter("b", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = post.Terms.Select(t => t.TagTf).ToArray() });
        terms.Parameters.Add(new NpgsqlParameter("c", NpgsqlDbType.Array | NpgsqlDbType.Integer) { Value = post.Terms.Select(t => t.BodyTf).ToArray() });
        await terms.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
