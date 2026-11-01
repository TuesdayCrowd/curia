using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// Errata G54 (R11.75) at the adapter: CPF22 the first block of <c>curia_search</c>, every case; CPF24 the description; CPF25 the version; CPF27 an older Forum. The words,
/// the paragraph, the members' names and the version are the entry's (<see cref="PublishedCheckWords"/>, and for the version <c>PublishedReadersLastWords</c>, the one reader of
/// one); a printed line is compared whitespace-folded; the documents are served by the stub's knobs. The CLI's half is <c>VectorCheckWordsTests</c> in <c>Curia.Client.Tests</c>.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class VectorCheckWordsToolTests : IDisposable
{
    private const string CheckedAt = "2026-11-01T10:00:00.0000000+00:00";
    private const string DueAt = "2026-11-01T10:05:00.0000000+00:00";

    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-g54-").FullName;

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void Said(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    private static string Fold(string s) => PublishedCheckWords.Fold(s);

    private static string Text(CallToolResult result) => string.Join('\n', result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private async Task<string> BlockAsync(string? pendingJson, string? checkedJson, string? dueJson)
    {
        using var log = new StubLog { SearchVectorPendingJson = pendingJson, SearchVectorCheckedAtJson = checkedJson, SearchVectorCheckDueAtJson = dueJson };
        var tools = new ForumTools(log.Client(), MarkingMode.None, new HeadStore(_home), null);
        return Fold(Text(await tools.SearchAsync(new SearchCriteria { Query = "x" }, Ct)));
    }

    /// <summary>The members the first block states for a row, and whether it adds the owed sentence: R11.75's words, composed.</summary>
    private static (string[] Members, bool Sentence) Expected(string? count, string? checkedJson, string? dueJson) =>
    (
        [
            count is null ? "vector_pending=not stated" : $"vector_pending={count}",
            dueJson is null ? "vector_check_due_at=not stated" : dueJson == "null" ? "vector_check_due_at=none" : $"vector_check_due_at={DueAt}",
            checkedJson is null ? "vector_checked_at=not stated" : $"vector_checked_at={CheckedAt}",
        ],
        dueJson is not null && dueJson != "null"
    );

    /// <summary>
    /// CPF22, the tool's half. For the matrix of the due member (null, an instant, absent) by the checked member (an instant, absent) by the count (0, 7, absent), the first
    /// block states the three members as R11.75's words give them, and adds the owed sentence exactly when the due member is an instant.
    /// </summary>
    [Fact]
    public async Task R11_75_CPF22_TheFirstBlockStatesTheThreeMembersAndTheOwedSentenceExactlyWhenACheckIsOwed()
    {
        var sentence = Fold(PublishedCheckWords.OwedSentence());
        var expected = new List<string>();
        var actual = new List<string>();
        foreach (var count in new string?[] { "0", "7", null })
        foreach (var due in new[] { "null", $"\"{DueAt}\"", null })
        foreach (var checkedAt in new[] { $"\"{CheckedAt}\"", null })
        {
            var (members, owed) = Expected(count, checkedAt, due is null or "null" ? due : DueAt);
            var block = await BlockAsync(count, checkedAt, due);
            var name = $"count {count ?? "absent"}, due {due ?? "absent"}, checked {(checkedAt is null ? "absent" : "an instant")}";
            expected.Add($"{name}: {string.Join(" ", members)}{(owed ? " + sentence" : string.Empty)}");
            var missing = members.Where(m => !block.Contains(m, StringComparison.Ordinal)).ToArray();
            actual.Add($"{name}: {(missing.Length == 0 ? string.Join(" ", members) : "block lacks " + string.Join(" and ", missing))}{(block.Contains(sentence, StringComparison.Ordinal) ? (owed ? " + sentence" : " + an owed sentence that is not owed") : (owed ? " (no owed sentence)" : string.Empty))}");
        }

        Said(string.Join(" | ", expected), string.Join(" | ", actual));
    }

    private string DescriptionOf(string tool)
    {
        using var http = new HttpClient();
        var client = new ForumClient(http, new Uri("https://forum.invalid/", UriKind.Absolute));
        return ToolCatalogue.Build(new ForumTools(client, MarkingMode.Datamark, new HeadStore(_home), null))
            .Single(t => t.ProtocolTool.Name == tool).ProtocolTool.Description ?? string.Empty;
    }

    /// <summary>
    /// CPF24, the tool's half. <c>curia_search</c>'s description carries R11.75's paragraph and names the two new members, derived from the catalogue; nothing in it says
    /// <c>none pending</c> as a bare phrase outside the paragraph that qualifies it.
    /// </summary>
    [Fact]
    public void R11_75_CPF24_TheDescriptionOfSearchCarriesThePublishedParagraphAndNamesTheMembers()
    {
        var description = Fold(DescriptionOf("curia_search"));
        var members = PublishedCheckWords.MemberNames();
        var missing = new List<string>();
        if (!description.Contains(Fold(PublishedCheckWords.Paragraph()), StringComparison.Ordinal)) missing.Add("the paragraph \"" + Fold(PublishedCheckWords.Paragraph()) + "\"");
        foreach (var member in new[] { members.CheckedAt, members.CheckDueAt })
            if (!description.Contains(member, StringComparison.Ordinal)) missing.Add(member);

        Said("the description of curia_search carries R11.75's paragraph and names vector_checked_at and vector_check_due_at", missing.Count == 0
            ? "the description of curia_search carries R11.75's paragraph and names vector_checked_at and vector_check_due_at"
            : $"the description of curia_search lacks {string.Join(" and ", missing)}");
    }

    /// <summary>CPF25. <c>curia-mcp</c> is the version G54's R11.75 names, read through the one reader of a version out of an errata paragraph (RLW40; which reads R11.75 since the committed-post PR).</summary>
    [Fact]
    public void R11_75_CPF25_TheAdapterIsTheVersionTheEntryNames()
    {
        var named = PublishedReadersLastWords.Version();
        Said($"expected {named}, actual {named}", $"expected {named}, actual {ToolCatalogue.Version}");
    }

    /// <summary>
    /// CPF27, the tool's half. An older Forum: a page without the two members states <c>vector_check_due_at=not stated</c> and <c>vector_checked_at=not stated</c>, never
    /// <c>none</c>, and no owed sentence; with only one member absent only that one is not stated.
    /// </summary>
    [Fact]
    public async Task R11_75_CPF27_AnOlderForumsPageIsNotStatedInTheBlockAndNeverNone()
    {
        var rows = new (string Name, string? Checked, string? Due)[]
        {
            ("both absent", null, null),
            ("only the due member absent", $"\"{CheckedAt}\"", null),
            ("only the checked member absent", null, "null"),
        };

        var expected = new List<string>();
        var actual = new List<string>();
        foreach (var (name, checkedAt, due) in rows)
        {
            var (members, _) = Expected("0", checkedAt, due);
            var block = await BlockAsync("0", checkedAt, due);
            var missing = members.Where(m => !block.Contains(m, StringComparison.Ordinal)).ToArray();
            expected.Add($"{name}: {string.Join(" ", members)}");
            actual.Add($"{name}: {(missing.Length == 0 ? string.Join(" ", members) : "block lacks " + string.Join(" and ", missing))}");
        }

        Said(string.Join(" | ", expected), string.Join(" | ", actual));
        _ = CultureInfo.InvariantCulture;
    }
}
