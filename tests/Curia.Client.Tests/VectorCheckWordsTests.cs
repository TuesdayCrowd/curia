using System.Diagnostics.CodeAnalysis;
using Curia.Client.Cli;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;
using static Curia.Client.Tests.ConsoleCapture;

namespace Curia.Client.Tests;

/// <summary>
/// Errata G54 (R11.75) at the reference client: CPF22 the line, every case, composed from the entry's pieces; CPF24 the help of <c>search</c>; CPF27 an older Forum. The
/// pieces, the paragraph and the members' names are the entry's (<see cref="PublishedCheckWords"/>), a printed line is compared whitespace-folded as the entry says, and the
/// documents are served by the stub's <c>SearchVectorPendingJson</c>, <c>SearchVectorCheckedAtJson</c> and <c>SearchVectorCheckDueAtJson</c> knobs, so the oracle is the JSON
/// each row wrote. The tool's half is <c>VectorCheckWordsToolTests</c> in <c>Curia.Mcp.Tests</c>.
/// </summary>
[Collection("console")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class VectorCheckWordsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string CheckedAt = "2026-11-01T10:00:00.0000000+00:00";
    private const string DueAt = "2026-11-01T10:05:00.0000000+00:00";

    private static void Said(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    private static string Fold(string s) => PublishedCheckWords.Fold(s);

    private static string? LineLabelled(string stdout, string label) =>
        stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Where(l => l.StartsWith(label + "  ", StringComparison.Ordinal))
            .Select(Fold)
            .FirstOrDefault();

    private static async Task<string> SearchLineAsync(string? pendingJson, string? checkedJson, string? dueJson)
    {
        using var log = new StubLog { SearchVectorPendingJson = pendingJson, SearchVectorCheckedAtJson = checkedJson, SearchVectorCheckDueAtJson = dueJson };
        var args = Args.Parse(["search", "x"], 1);
        var (exit, stdout, stderr) = await CaptureAsync(
            () => Program.SearchPageAsync(args, "x", null, log.Client(), MarkingMode.None, null, Ct));
        return exit == 0 ? LineLabelled(stdout, "vector") ?? "no vector line" : $"exit {exit}: {stderr.Trim()}";
    }

    /// <summary>
    /// CPF22, the CLI's half. The matrix <c>vector_check_due_at</c> in {null, an instant, absent} by <c>vector_checked_at</c> in {an instant, absent} by the count in {0, 7,
    /// absent}: each line equals R11.75's pieces composed, with <c>none pending</c> only for a stated instant, a null due member and a count of 0, an absent member
    /// <c>NOT STATED</c> and never none.
    /// </summary>
    [Fact]
    public async Task R11_75_CPF22_TheSearchLineIsComposedFromThePublishedPiecesForEveryCase()
    {
        var expected = new List<string>();
        var actual = new List<string>();
        foreach (var count in new int?[] { 0, 7, null })
        foreach (var due in new[] { "null", $"\"{DueAt}\"", null })
        foreach (var checkedAt in new[] { $"\"{CheckedAt}\"", null })
        {
            var name = $"count {count?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "absent"}, due {due ?? "absent"}, checked {(checkedAt is null ? "absent" : "an instant")}";
            expected.Add($"{name}: {Fold(PublishedCheckWords.Line(count, due is null or "null" ? null : DueAt, due == "null", checkedAt is null ? null : CheckedAt))}");
            actual.Add($"{name}: {await SearchLineAsync(count?.ToString(System.Globalization.CultureInfo.InvariantCulture), checkedAt, due)}");
        }

        Said(string.Join(" | ", expected), string.Join(" | ", actual));
    }

    /// <summary>
    /// CPF22, the reader. A member that is neither an instant nor (for the due member) JSON null is a malformed search page: a number, a boolean and a string that is no
    /// instant, for the due member, and null, a number, a boolean and a string that is no instant, for the checked member.
    /// </summary>
    [Fact]
    public async Task R11_75_CPF22_AMemberThatIsNoInstantIsAMalformedSearchPage()
    {
        var rows = new List<(string Name, string? Checked, string? Due)>
        {
            ("due 3", $"\"{CheckedAt}\"", "3"),
            ("due true", $"\"{CheckedAt}\"", "true"),
            ("due \"soon\"", $"\"{CheckedAt}\"", "\"soon\""),
            ("checked null", "null", "null"),
            ("checked 3", "3", "null"),
            ("checked true", "true", "null"),
            ("checked \"yesterday\"", "\"yesterday\"", "null"),
        };

        var expected = new List<string>();
        var actual = new List<string>();
        foreach (var (name, checkedAt, due) in rows)
        {
            using var log = new StubLog { SearchVectorPendingJson = "0", SearchVectorCheckedAtJson = checkedAt, SearchVectorCheckDueAtJson = due };
            var found = await log.Client().SearchAsync(new SearchRequest("x"), MarkingMode.None, Ct);
            expected.Add($"{name}: malformed");
            actual.Add($"{name}: {(found.TryGetValue(out _, out _) ? "read" : "malformed")}");
        }

        Said(string.Join("; ", expected), string.Join("; ", actual));
    }

    /// <summary>
    /// CPF24, the CLI's half. The help of <c>search</c> carries R11.75's paragraph and names the two new members. Derived from the dispatch's help, not listed.
    /// </summary>
    [Fact]
    public void R11_75_CPF24_TheHelpOfSearchCarriesThePublishedParagraphAndNamesTheMembers()
    {
        var help = Fold(Help.For("search") ?? string.Empty);
        var members = PublishedCheckWords.MemberNames();
        var missing = new List<string>();
        if (!help.Contains(Fold(PublishedCheckWords.Paragraph()), StringComparison.Ordinal)) missing.Add("the paragraph \"" + Fold(PublishedCheckWords.Paragraph()) + "\"");
        foreach (var member in new[] { members.CheckedAt, members.CheckDueAt })
            if (!help.Contains(member, StringComparison.Ordinal)) missing.Add(member);

        Said("the help of search carries R11.75's paragraph and names vector_checked_at and vector_check_due_at", missing.Count == 0
            ? "the help of search carries R11.75's paragraph and names vector_checked_at and vector_check_due_at"
            : $"the help of search lacks {string.Join(" and ", missing)}");
    }

    /// <summary>
    /// CPF27, the CLI's half. An older Forum: a search page without the two members prints the <c>NOT STATED</c> pieces and never <c>none pending</c>; with only one member
    /// absent only that piece is <c>NOT STATED</c>.
    /// </summary>
    [Fact]
    public async Task R11_75_CPF27_AnOlderForumsPageIsNotStatedAndNeverNonePending()
    {
        var rows = new (string Name, string? Checked, string? Due, string? DueInstant, bool DueNull, string? CheckedInstant)[]
        {
            ("both absent", null, null, null, false, null),
            ("only the due member absent", $"\"{CheckedAt}\"", null, null, false, CheckedAt),
            ("only the checked member absent", null, "null", null, true, null),
        };

        var expected = new List<string>();
        var actual = new List<string>();
        foreach (var (name, checkedAt, due, dueInstant, dueNull, checkedInstant) in rows)
        {
            expected.Add($"{name}: {Fold(PublishedCheckWords.Line(0, dueInstant, dueNull, checkedInstant))}");
            actual.Add($"{name}: {await SearchLineAsync("0", checkedAt, due)}");
        }

        Said(string.Join(" | ", expected), string.Join(" | ", actual));
    }
}
