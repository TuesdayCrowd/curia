using System.Diagnostics.CodeAnalysis;
using System.Text;
using Curia.Canon.Json;
using Curia.Client.Cli;
using Curia.Domain.Primitives;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;
using static Curia.Client.Tests.ConsoleCapture;

namespace Curia.Client.Tests;

/// <summary>
/// Errata G51 (R11.70) at the reference client (the D92 PR): PIL26 the strict reader of <c>vector_index</c> and <c>vector_pending</c>, PIL27 the
/// receipt's four lines, PIL28 a pending post is exit 0 and sent once, PIL29 the search lines and PIL30 the sentence in the helps. The values, the lines and
/// the sentence are the entry's (<see cref="PublishedIndexLag"/>); a printed line is compared whitespace-folded, as the entry says. The documents are served
/// by the stub's <c>ReceiptVectorIndexJson</c> and <c>SearchVectorPendingJson</c> knobs, so the oracle is the JSON each row wrote.
/// </summary>
[Collection("console")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class IndexWordsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void Said(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    private static JsonValue.Object Parse(string json)
    {
        var parsed = JsonReader.Parse(Encoding.UTF8.GetBytes(json), ClientJson.Limits);
        Assert.True(parsed.TryGetValue(out var value, out var error), error?.Type);
        return Assert.IsType<JsonValue.Object>(value);
    }

    private static string ReceiptWith(string? vectorIndexJson) =>
        StubLog.ReceiptJson("sha256:" + new string('a', 64))[..^1] + (vectorIndexJson is null ? string.Empty : ",\"vector_index\":" + vectorIndexJson) + "}";

    private static string Described(Result<PostReceipt> read) =>
        read.TryGetValue(out var receipt, out var error) ? $"read as {receipt!.VectorIndex}" : $"malformed ({error!.Type})";

    /// <summary>
    /// PIL26, the receipt. The three values are read; an absent member is not stated; the value in the wrong case, the empty string, a boolean, a number,
    /// null and an object are each a malformed receipt, never <c>indexed</c> and never <c>not stated</c>.
    /// </summary>
    [Fact]
    public void R11_70_PIL26_TheReceiptReaderIsStrictAboutVectorIndex()
    {
        var (indexed, pending, notApplicable) = PublishedIndexLag.Values();
        var rows = new (string Json, string Expected)[]
        {
            ($"\"{indexed}\"", "read as Indexed"),
            ($"\"{pending}\"", "read as Pending"),
            ($"\"{notApplicable}\"", "read as NotApplicable"),
            ($"\"{char.ToUpperInvariant(indexed[0])}{indexed[1..]}\"", "malformed"),
            ("\"\"", "malformed"),
            ("true", "malformed"),
            ("1", "malformed"),
            ("null", "malformed"),
            ("{}", "malformed"),
        };

        var expected = new List<string> { "absent: read as NotStated" };
        var actual = new List<string> { "absent: " + Described(ForumDocuments.ReadReceipt(Parse(ReceiptWith(null)))) };
        foreach (var (json, wanted) in rows)
        {
            var read = Described(ForumDocuments.ReadReceipt(Parse(ReceiptWith(json))));
            expected.Add($"{json}: {wanted}");
            actual.Add($"{json}: {(wanted == "malformed" && read.StartsWith("malformed", StringComparison.Ordinal) ? "malformed" : read)}");
        }

        Said(string.Join("; ", expected), string.Join("; ", actual));
    }

    /// <summary>PIL26, the search page. A count of 0 or 7 is read; an absent member is not stated; -1, 1.0, a string and null are a malformed search page.</summary>
    [Fact]
    public async Task R11_70_PIL26_TheSearchReaderIsStrictAboutVectorPending()
    {
        var rows = new (string? Json, string Expected)[]
        {
            (null, "read as null"),
            ("0", "read as 0"),
            ("7", "read as 7"),
            ("-1", "malformed"),
            ("1.0", "malformed"),
            ("\"3\"", "malformed"),
            ("null", "malformed"),
        };

        var expected = new List<string>();
        var actual = new List<string>();
        foreach (var (json, wanted) in rows)
        {
            using var log = new StubLog { SearchVectorPendingJson = json };
            var found = await log.Client().SearchAsync(new SearchRequest("x"), MarkingMode.None, Ct);
            var read = found.TryGetValue(out var page, out _) ? $"read as {(page!.VectorPending is { } n ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null")}" : "malformed";
            expected.Add($"{json ?? "absent"}: {wanted}");
            actual.Add($"{json ?? "absent"}: {read}");
        }

        Said(string.Join("; ", expected), string.Join("; ", actual));
    }

    private static string? LineLabelled(string stdout, string label) =>
        stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Where(l => l.StartsWith(label + "  ", StringComparison.Ordinal))
            .Select(PublishedIndexLag.Fold)
            .FirstOrDefault();

    private static readonly string[] SendAsk = ["ask", "--agent", "alice", "--board", "b", "--title", "A title", "--body", "A body."];

    /// <summary>
    /// PIL27, the CLI's half. For each of the four receipt states (the three values and a receipt with no member) <c>curia ask</c> prints the receipt's
    /// <c>search</c> line, and it is the entry's, with <c>NOT STATED</c> for a receipt that said nothing and never <c>by words and by meaning</c>. The command is
    /// the real <c>curia</c> in a child process against a Forum on a loopback socket.
    /// </summary>
    [Fact]
    public async Task R11_70_PIL27_TheCliPrintsTheReceiptsLineInThePublishedWords()
    {
        var (indexed, pending, notApplicable) = PublishedIndexLag.Values();
        var rows = new (string? Json, string Words)[]
        {
            ($"\"{indexed}\"", PublishedIndexLag.ReceiptLine(indexed)),
            ($"\"{pending}\"", PublishedIndexLag.ReceiptLine(pending)),
            ($"\"{notApplicable}\"", PublishedIndexLag.ReceiptLine(notApplicable)),
            (null, PublishedIndexLag.NotStatedLine()),
        };

        using var home = new CliHome();
        var expected = new List<string>();
        var actual = new List<string>();
        foreach (var (json, words) in rows)
        {
            var run = await RunAskAsync(home, json);
            expected.Add($"{json ?? "absent"}: exit 0, \"{words}\"");
            actual.Add($"{json ?? "absent"}: exit {run.Exit}, \"{LineLabelled(run.Stdout, "search") ?? "no search line"}\"");
        }

        Said(string.Join("; ", expected.Select(Fold)), string.Join("; ", actual.Select(Fold)));
    }

    private static string Fold(string s) => PublishedIndexLag.Fold(s);

    /// <summary><c>curia ask</c> against a Forum that answers <c>201</c> with the receipt <see cref="ReceiptWith"/> builds, and the POSTs it saw.</summary>
    private static async Task<(int Exit, string Stdout, string Stderr, int Posts)> RunAskCountingAsync(CliHome home, string? vectorIndexJson)
    {
        using var forum = new ListenerForum();
        forum.Answer = (_, _) => (201, ReceiptWith(vectorIndexJson));
        var run = await home.RunAsync([.. SendAsk, "--forum", forum.Address.ToString()]);
        return (run.Exit, run.Stdout, run.Stderr, forum.Calls("POST /v1/posts").Count);
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAskAsync(CliHome home, string? vectorIndexJson)
    {
        var run = await RunAskCountingAsync(home, vectorIndexJson);
        return (run.Exit, run.Stdout, run.Stderr);
    }

    /// <summary>
    /// PIL28, the CLI's half. A Forum that answers <c>201</c> with <c>pending</c>: <c>curia ask</c> exits 0 (the post is stored, so it is not an error),
    /// sends exactly one POST of the post and prints the line that says not to send it again. The exit code and the single POST are controls; the line is
    /// the red.
    /// </summary>
    [Fact]
    public async Task R11_70_PIL28_APendingReceiptIsExitZeroAndTheCommandSendsOnce()
    {
        var pending = PublishedIndexLag.Values().Pending;
        using var home = new CliHome();
        var run = await RunAskCountingAsync(home, $"\"{pending}\"");
        var posts = run.Posts;
        var line = LineLabelled(run.Stdout, "search");

        Said(
            Fold($"exit 0; 1 POST; \"{PublishedIndexLag.ReceiptLine(pending)}\""),
            Fold($"exit {run.Exit}; {posts} POST; \"{line ?? "no search line"}\""));
    }

    private static async Task<string> SearchOutputAsync(string? pendingJson)
    {
        using var log = new StubLog { SearchVectorPendingJson = pendingJson };
        var args = Args.Parse(["search", "x"], 1);
        var (exit, stdout, stderr) = await CaptureAsync(
            () => Program.SearchPageAsync(args, "x", null, log.Client(), MarkingMode.None, null, TestContext.Current.CancellationToken));
        return exit == 0 ? LineLabelled(stdout, "vector") ?? "no vector line" : $"exit {exit}: {stderr.Trim()}";
    }

    /// <summary>
    /// PIL29, the CLI's half. <c>curia search</c> prints <c>vector  none pending</c> for 0, the count line for a positive number and <c>vector  NOT STATED</c>
    /// where the Forum served none, each as the entry gives it.
    /// </summary>
    [Fact]
    public async Task R11_70_PIL29_TheSearchLinesStateThePendingCount()
    {
        var expected = new[] { PublishedIndexLag.NonePendingLine(), PublishedIndexLag.CountLine(7), PublishedIndexLag.SearchNotStatedLine() };
        var actual = new[] { await SearchOutputAsync("0"), await SearchOutputAsync("7"), await SearchOutputAsync(null) };

        Said(string.Join(" | ", expected.Select(Fold)), string.Join(" | ", actual.Select(Fold)));
    }

    /// <summary>PIL30, the CLI's half. The help of each verb that sends a post (<c>ask</c>, <c>comment</c>, <c>answer</c>, <c>revision</c>) and of <c>search</c> carries the entry's sentence.</summary>
    [Fact]
    public void R11_70_PIL30_TheHelpOfTheVerbsThatSendAPostAndOfSearchCarriesTheSentence()
    {
        var sentence = PublishedIndexLag.Sentence();
        var verbs = new[] { "ask", "comment", "answer", "revision", "search" };
        var missing = verbs.Where(v => !Fold(Help.For(v) ?? string.Empty).Contains(sentence, StringComparison.Ordinal)).ToArray();

        Said("the sentence is in the help of 5 verbs (ask comment answer revision search)", missing.Length == 0
            ? "the sentence is in the help of 5 verbs (ask comment answer revision search)"
            : $"the sentence is absent from the help of {missing.Length} verbs ({string.Join(' ', missing)})");
    }

    /// <summary>PIL27, the source half. No source file outside <c>IndexWording.cs</c> writes the receipt's words: the phrase <c>by meaning</c> is written once, in the definition both clients call.</summary>
    [Fact]
    public void R11_70_PIL27_OnlyTheDefinitionWritesTheReceiptsWords()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curia.sln"))) dir = dir.Parent;
        var src = Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Curia.sln not found"), "src");

        var writers = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("by meaning", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Said("by meaning is written in IndexWording.cs and nowhere else", $"by meaning is written in {string.Join(", ", writers.DefaultIfEmpty("no file"))}{(writers.SequenceEqual(["IndexWording.cs"]) ? " and nowhere else" : string.Empty)}");
    }
}
