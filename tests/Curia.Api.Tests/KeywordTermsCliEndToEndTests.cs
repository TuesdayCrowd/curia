using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// Errata G81 (R9.72, R10.102, Amendment 3), plan KWC19e: <c>curia search</c> as a child process against the real Forum on a real socket. <c>curia search not</c> prints R10.102's full line, its
/// closing clause included, and <c>no results.</c>; <c>curia search "the pool"</c> prints the ignored line and no clause. <b>The pair is the probe that cannot be satisfied from one side</b>: a
/// Forum that states 0 always fails the second, a client that prints the clause on its own fails the second, and a Forum that states a count of tokens fails the first.
/// The oracle is the line as the task gives it, written out by hand below, and the Forum's own corpus (one post on a board of its own, so <c>the pool</c> keeps a term the corpus holds).
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class KeywordTermsCliEndToEndTests(BrowseForumFixture forum) : IClassFixture<BrowseForumFixture>, IDisposable
{
    private const string FullLine =
        "the keyword channel ignored these common English words of the query: \"not\" (a published list; it matches no post by them). " +
        "The whole query was made of them, so the keyword channel took no term and matched no post by words; search again with a word that names the subject";

    private const string ClauseStart = "The whole query was made of them";

    private readonly string _home = Directory.CreateTempSubdirectory("curia-kwc-terms-").FullName;

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Address => forum.Client.BaseAddress!.GetLeftPart(UriPartial.Authority);

    [GeneratedRegex(@"^posted\s+""?(?<id>[^\s""]+)", RegexOptions.Multiline)]
    private static partial Regex Posted();

    private async Task<(int Exit, string Stdout, string Stderr)> CuriaAsync(CancellationToken ct, params string[] args)
    {
        // The child signs with the real clock, as the browse facts do.
        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now);
        return await CuriaProcess.RunAsync(_home, ct, args);
    }

    [Fact]
    public async Task KWC19e_AnAllIgnoredQueryPrintsTheClauseAndAQueryThatKeptAWordDoesNot()
    {
        var ct = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (asker, board) = ("ask-" + suffix, "board-" + suffix);
        var enrolled = await CuriaAsync(ct, "enrol", "--agent", asker, "--forum", Address);
        Assert.True(enrolled.Exit == 0, enrolled.Stdout + enrolled.Stderr);
        var asked = await CuriaAsync(ct, FirstAsk.Args(asker, board, "pool keepalive", "the pool needs keepalive", Address, makesBoard: true));
        Assert.True(asked.Exit == 0, asked.Stdout + asked.Stderr);
        Assert.NotEmpty(Posted().Match(asked.Stdout).Groups["id"].Value);

        var all = await CuriaAsync(ct, "search", "not", "--agent", asker, "--board", board, "--forum", Address);
        var kept = await CuriaAsync(ct, "search", "the pool", "--agent", asker, "--board", board, "--forum", Address);

        Assert.True(all.Exit == 0, $"Expected: exit 0  Actual: {all.Exit} {all.Stderr}");
        Assert.True(
            all.Stdout.Contains(FullLine, StringComparison.Ordinal) && all.Stdout.Contains("no results.", StringComparison.Ordinal),
            $"Expected: the full line and \"no results.\" for `not`  Actual: {Tail(all.Stdout)}");
        Assert.True(kept.Exit == 0, $"Expected: exit 0  Actual: {kept.Exit} {kept.Stderr}");
        Assert.True(
            kept.Stdout.Contains("the keyword channel ignored these common English words of the query: \"the\" (a published list; it matches no post by them)", StringComparison.Ordinal)
            && !kept.Stdout.Contains(ClauseStart, StringComparison.Ordinal),
            $"Expected: the ignored line for `the pool` and no clause  Actual: {Tail(kept.Stdout)}");
    }

    private static string Tail(string text) => string.Join(" / ", text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Where(l => l.Contains("ignored", StringComparison.Ordinal) || l.Contains("results", StringComparison.Ordinal)).Select(l => l.Length > 160 ? l[..160] : l));
}
