using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using Curia.Domain.Content;
using Curia.Domain.Serving;
using Xunit;
using static Curia.Api.Tests.BoardListProbe;

namespace Curia.Api.Tests;

/// <summary>
/// R9.58, R10.17 (errata G63), LOB13: the provenance envelope's gate reads a board row for what is true of a name. A row carries <c>content_type</c>, the standing <c>warning</c> and <c>reader_contract</c>, and no
/// author, no verification level and no marking (the Forum does not know one author or a level for a name); and <c>PropertyP22GateTests</c> drives the route with a per-route form for both of its walls (the
/// post-id containment check and the required members), read from one table, so that the four post routes keep exactly the assertions they have and no route is without a row.
///
/// <para><b>Oracle.</b> The envelope is read from the route and compared with the constants of <c>Curia.Domain.Serving</c> and <c>PostEnvelope</c>; the gate's table is read by reflection for the member the gate is
/// to gain (<c>RouteForms</c>), named here as the contract Task 2 builds to. Predicted red on the seam: the route lists no row, and the gate has no such table.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class BoardListGateTests(PassThroughGateForumFixture forum) : IClassFixture<PassThroughGateForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task R10_17_LOB13_ABoardRowCarriesTheStandingEnvelopeAndNoAuthorOrLevel()
    {
        var board = Name("lob13");
        await StoreAsync(forum, board, 1, 1, Ct);

        var row = (Rows(await ListAsync(forum.Client, "board=" + board, Ct)) ?? []).FirstOrDefault();
        var members = row is null ? [] : row.Provenance.EnumerateObject().Select(m => m.Name).Order(StringComparer.Ordinal).ToArray();

        var standing = row is not null && row.Provenance.TryGetProperty("warning", out var w) && w.GetString() == Provenance.StandardWarning
            && row.Provenance.GetProperty("content_type").GetString() == PostEnvelope.RequiredContentType;
        NameProbe.Said(
            $"the row for {board} holds content_type, reader_contract, warning; the warning is the standing one",
            $"the row for {board} {(row is null ? "is not listed" : "holds " + string.Join(", ", members) + "; the warning is " + (standing ? "the standing one" : "not the standing one"))}");

        // The content of the row, against another route's artifact: a blank or a different reader contract, a different content type or a name that was changed on the way out is a row the gate must not pass.
        var perBoard = await NameProbe.GetAsync(forum.Client, $"/v1/boards/{board}/posts", ct: Ct);
        var postContract = perBoard.Json.ValueKind == JsonValueKind.Array && perBoard.Json.GetArrayLength() > 0
            ? perBoard.Json[0].GetProperty("provenance").GetProperty("reader_contract").GetString()
            : null;
        var rowContract = row is not null && row.Provenance.TryGetProperty("reader_contract", out var rc) ? rc.GetString() : null;
        var rowType = row is not null && row.Provenance.TryGetProperty("content_type", out var ctype) ? ctype.GetString() : null;
        NameProbe.Said(
            $"the row names {board}; its content type is {PostEnvelope.RequiredContentType}; its reader contract is stated and is the contract the per-board listing serves",
            $"the row names {row?.Board ?? "no board"}; its content type is {rowType ?? "not stated"}; its reader contract is "
            + (string.IsNullOrWhiteSpace(rowContract) ? "not stated" : rowContract == postContract ? "stated and is the contract the per-board listing serves" : "stated and is not the contract the per-board listing serves"));
    }

    /// <summary>
    /// LOB13, the table's content (Amendment 3, ruling 48), written by hand from G63's tenth finding and not read back from the table: the four post routes expect the post (members <c>author</c> and
    /// <c>verification_level</c>, and a containment that returns the post id it is given) and <c>/v1/boards</c> expects the board (members <c>content_type</c> and <c>reader_contract</c>, and a containment that
    /// returns the board). The live row's provenance names, less <c>warning</c>, are the board form's members as sets, so a table that lost a member, or a row that gained one the table does not know, is red.
    /// </summary>
    [Fact]
    public async Task R10_17_LOB13_TheTableHoldsWhatEachRouteIsToBeAskedAndTheLiveRowHoldsExactlyTheBoardMembers()
    {
        var board = Name("lob13t");
        await StoreAsync(forum, board, 1, 1, Ct);
        var row = (Rows(await ListAsync(forum.Client, "board=" + board, Ct)) ?? []).FirstOrDefault();
        var live = row is null ? [] : row.Provenance.EnumerateObject().Select(m => m.Name).Where(n => n != "warning").Order(StringComparer.Ordinal).ToArray();

        var expected = new List<string>
        {
            "/v1/posts/{postId}: the post, author + verification_level, returns the post id",
            "/v1/threads/{rootPostId}: the post, author + verification_level, returns the post id",
            "/v1/boards/{board}/posts: the post, author + verification_level, returns the post id",
            "/v1/search: the post, author + verification_level, returns the post id",
            "/v1/boards: the board, content_type + reader_contract, returns the board",
            "the live row holds content_type, reader_contract besides the warning",
        };
        var actual = PropertyP22GateTests.RouteForms.Select(f =>
            $"{f.Route}: {f.ContainsWhat}, {string.Join(" + ", f.Members.Order(StringComparer.Ordinal))}, returns "
            + (f.Contains("B-sentinel", "I-sentinel") switch { "I-sentinel" => "the post id", "B-sentinel" => "the board", var other => "'" + other + "'" })).ToList();
        actual.Add("the live row holds " + (live.Length == 0 ? "no member" : string.Join(", ", live)) + " besides the warning");

        // The form's members, set against the live row: the table and the row agree as sets.
        var form = PropertyP22GateTests.RouteForms.FirstOrDefault(f => f.Route == "/v1/boards");
        var formMembers = form is null ? [] : form.Members.Order(StringComparer.Ordinal).ToArray();
        expected.Add("the board form's members are the live row's");
        actual.Add(formMembers.SequenceEqual(live, StringComparer.Ordinal) ? "the board form's members are the live row's" : $"the board form's members are [{string.Join(", ", formMembers)}] and the live row's are [{string.Join(", ", live)}]");

        // Order of rows is the table's; compare as the table orders them.
        NameProbe.Said(expected.OrderBy(x => x, StringComparer.Ordinal), actual.OrderBy(x => x, StringComparer.Ordinal));
    }

    /// <summary>
    /// LOB13, the gate's own shape: <see cref="PropertyP22GateTests"/> declares a static <c>RouteForms</c> table (one row per content route, the board row's containment form and member list beside the four post routes'
    /// existing assertions), which is how it drives <c>/v1/boards</c> without loosening anything it holds for a post. Predicted red on the seam: no such member.
    /// </summary>
    [Fact]
    public void R10_17_LOB13_TheGateTakesItsTwoPerRouteFormsFromOneTable()
    {
        var table = typeof(PropertyP22GateTests).GetMember("RouteForms", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault();

        Assert.True(table is not null, "PropertyP22GateTests declares no static RouteForms: the gate still asserts a post's shape (a post id in the body, an author and a level in the envelope) of every route it drives, so a board row cannot be driven without loosening it");
    }
}
