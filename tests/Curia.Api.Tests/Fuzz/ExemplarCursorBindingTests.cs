using System.Diagnostics.CodeAnalysis;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Api.Tests.Fuzz;

/// <summary>
/// R9.35 (errata G36) and R14.10: the request fuzzer's exemplar of <c>GET /v1/search</c> presents a cursor the fuzzer's context minted, and a cursor now
/// belongs to the query that minted it. The exemplar must therefore be a valid request, which it can only be if every member the binding follows is the
/// same in the request that minted the cursor and in the exemplar that presents it. Held here from the two definitions themselves, so a change to either
/// that breaks the pairing fails this fact by name, and not as an exemplar the fuzzer reports as refused.
///
/// <para><b>Oracle.</b> The members to compare are the ones the errata binds (<see cref="PublishedSearchPage"/>); the minting request is
/// <see cref="FuzzContext.SearchMintQuery"/> and the exemplar is the row the fuzzer sends, built offline.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ExemplarCursorBindingTests(FuzzForumFixture forum) : IClassFixture<FuzzForumFixture>
{
    private static Dictionary<string, string> Parse(string query) =>
        query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p.Length > 1 ? p[1] : string.Empty), StringComparer.Ordinal);

    [Fact]
    public async Task R9_35_TheSearchExemplarPresentsItsCursorUnderTheQueryThatMintedIt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var context = FuzzContext.Offline(forum);

        var minted = Parse(FuzzContext.SearchMintQuery(context.Seed.Id));
        var row = Exemplars.All.Single(r => r.Route == "GET /v1/search");
        var model = await row.Build(context, ct);
        var sent = model.Query.ToDictionary(q => q.Key, q => q.Value, StringComparer.Ordinal);

        Assert.True(sent.ContainsKey("cursor"), "the exemplar presents no cursor, so the comparison below would hold nothing");

        // The HTTP spellings of the members the errata binds: q, board, author, kind, tags, min_verification, limit.
        var differing = new List<string>();
        foreach (var member in PublishedSearchPage.BoundMembers())
        {
            var mintedValue = minted.TryGetValue(member, out var m) ? m : null;
            var sentValue = sent.TryGetValue(member, out var s) ? s : null;
            if (!string.Equals(mintedValue, sentValue, StringComparison.Ordinal))
                differing.Add($"{member}: minted with {mintedValue ?? "(absent)"}, presented with {sentValue ?? "(absent)"}");
        }

        Assert.True(differing.Count == 0, "the exemplar's valid request would be refused as a cursor from another query (exemplar request was refused: 400 curia/search/cursor-query-mismatch); " + string.Join("; ", differing));
    }
}
