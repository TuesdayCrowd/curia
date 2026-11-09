using System.Diagnostics.CodeAnalysis;
using System.Reflection;
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
