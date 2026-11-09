using System.Globalization;
using System.Text;
using System.Text.Json;
using Curia.Canon.Json;
using Curia.Tests.Shared;

namespace Curia.Client.Tests;

/// <summary>
/// The harness PR 7's client facts share (errata G63): a Forum that serves a board listing from a table and answers a post with a receipt, the real <c>curia</c> run against it, and what it received. The
/// listing's answer honours <c>board=</c> (the exact filter) and <c>limit=</c>, as R9.58 says the Forum does; every request is recorded with its query, so a fact reads how many list requests a command made.
/// The words an expected line is built from are the errata's (<see cref="PublishedBoardListWords"/>); no expected line is copied from the code.
/// </summary>
internal static class BoardListHarness
{
    internal sealed record Board(string Name, int Posts, int Authors, string? Latest = "2026-11-05T12:00:00.0000000+00:00");

    internal sealed record Outcome(int Exit, string Stdout, string Stderr, string[] Requests, string[] KeptActs)
    {
        internal string All => Stdout + Environment.NewLine + Stderr;

        internal int ListRequests => Requests.Count(r => r.StartsWith("GET /v1/boards", StringComparison.Ordinal) && !r.StartsWith("GET /v1/boards/", StringComparison.Ordinal));

        internal int Posts => Requests.Count(r => r.StartsWith("POST /v1/posts", StringComparison.Ordinal));

        internal int Tokens => Requests.Count(r => r.Contains("/oauth/token", StringComparison.Ordinal));
    }

    /// <summary>One row as the Forum writes it, with the standing envelope members a board row carries.</summary>
    internal static string RowJson(Board b)
    {
        var latest = b.Latest is null ? string.Empty : $",\"latest_post_at\":{JsonSerializer.Serialize(b.Latest)}";
        return $"{{\"board\":{JsonSerializer.Serialize(b.Name)},\"posts\":{b.Posts.ToString(CultureInfo.InvariantCulture)},\"authors\":{b.Authors.ToString(CultureInfo.InvariantCulture)}{latest},"
            + "\"provenance\":{\"content_type\":\"agent-authored/untrusted\",\"warning\":\"standing\",\"reader_contract\":\"/.well-known/reader-contract/v1\"}}";
    }

    internal static string PageJson(IEnumerable<Board> rows, int total, string? next = null) =>
        $"{{\"boards\":[{string.Join(",", rows.Select(RowJson))}],\"total\":{total.ToString(CultureInfo.InvariantCulture)},\"corpus_bound\":9{(next is null ? string.Empty : ",\"next_cursor\":" + JsonSerializer.Serialize(next))}}}";

    /// <summary>
    /// The page a Forum whose world holds <paramref name="boards"/> (one post by one author each) answers a list read with: the row for the exact <c>board=</c> filter in <paramref name="query"/>
    /// or the whole list. The stubs that serve a world for a fact about something else (<c>ScriptedForum</c>, <c>RecordingForum</c>, <c>ListenerForum</c>) answer the list route from it.
    /// </summary>
    internal static string WorldPage(IEnumerable<string> boards, string query)
    {
        var filter = Param(query, "board");
        var listed = boards.Where(b => filter is null || string.Equals(b, filter, StringComparison.Ordinal)).Select(b => new Board(b, 1, 1)).ToArray();
        return PageJson(listed, listed.Length);
    }

    private static string Receipt(string digest) =>
        $$"""{"post_id":"01HXLOB00RECEIPT000000000","digest":"{{digest}}","server_ts":"1970-01-01T00:00:00.0000000+00:00","risk_flags":[],"vector_index":"indexed","board_new":false}""";

    /// <summary>The Forum a fact configures: the boards it lists, or another answer for the list route (<paramref name="listAnswer"/>), and the receipt for a post.</summary>
    internal static RecordingForum Forum(IReadOnlyList<Board>? boards = null, Func<string, (int Status, string Body)>? listAnswer = null)
    {
        var table = boards ?? [];
        return new RecordingForum
        {
            QueryResponder = (method, path, query, body) =>
            {
                if (method == "GET" && path == "/v1/boards")
                {
                    if (listAnswer is not null) return listAnswer(query);

                    var filter = Param(query, "board");
                    var limit = Param(query, "limit") is { } l && int.TryParse(l, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 50;
                    // R9.58: the Forum's order, most posts first and then by name in ordinal order, whatever order the fact wrote its table in.
                    var rows = filter is null
                        ? [.. table.OrderByDescending(b => b.Posts).ThenBy(b => b.Name, StringComparer.Ordinal)]
                        : (IReadOnlyList<Board>)[.. table.Where(b => string.Equals(b.Name, filter, StringComparison.Ordinal))];
                    return (200, PageJson(rows.Take(limit), rows.Count, filter is null && rows.Count > limit ? "bmV4dA==" : null));
                }

                return method == "POST" && path == "/v1/posts" && ReceivedAct.IsSubmission(body) ? (201, Receipt(ReceivedAct.DigestOf(body))) : null;
            },
        };
    }

    private static string? Param(string query, string name)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var at = pair.IndexOf('=', StringComparison.Ordinal);
            if (at > 0 && pair[..at] == name) return Uri.UnescapeDataString(pair[(at + 1)..]);
        }

        return null;
    }

    /// <summary>Runs <c>curia</c> with <paramref name="argv"/> against <paramref name="forum"/> as agent <c>alice</c> (enrolled against it), and returns what it printed and what the Forum received.</summary>
    internal static async Task<Outcome> RunAsync(RecordingForum forum, CancellationToken ct, params string[] argv)
    {
        var home = CuriaCli.NewHome(forum.Address);
        var before = forum.Lines.Length;
        try
        {
            var (exit, stdout, stderr) = await CuriaCli.RunAsync(home, ct, [.. argv, "--forum", forum.Address.ToString()]);
            return new Outcome(exit, stdout, stderr, [.. forum.Lines.Skip(before)], [.. Directory.EnumerateFiles(home, "*.act", SearchOption.AllDirectories)]);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>The arguments of a question on <paramref name="board"/> as agent <c>alice</c>; <paramref name="extra"/> follows.</summary>
    internal static string[] Ask(string board, params string[] extra) =>
        ["ask", "--agent", "alice", "--board", board, "--title", "A title", "--body", "A body", .. extra];

    /// <summary>The line the board line is, for a state's published words, with the counts filled in and the typed name as a display literal.</summary>
    internal static string BoardLine(string state, string board, int posts = 0, int authors = 0) =>
        PublishedBoardListWords.LineLabel() + DisplayLiteral.Of(board) + ": "
        + state.Replace("<posts>", posts.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal).Replace("<authors>", authors.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    internal static string[] Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    internal static string Fold(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

    internal static void Said(IEnumerable<string> expected, IEnumerable<string> actual)
    {
        var (e, a) = (string.Join(" | ", expected), string.Join(" | ", actual));
        if (!string.Equals(e, a, StringComparison.Ordinal)) throw new Xunit.Sdk.XunitException($"Expected: {e}{Environment.NewLine}Actual:   {a}");
    }

    internal static string Escaped(string text) => DisplayLiteral.Of(text);

    internal static readonly Encoding Utf8 = new UTF8Encoding(false);
}
