using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R9.44 (errata G43): <c>GET /v1/inbox</c> answers a page size it refuses <c>400</c> <c>curia/inbox/invalid-limit</c>, and <c>GET /v1/search</c> keeps
/// <c>curia/search/invalid-limit</c>; the title and the detail are the same for both. R9.37 gave the inbox its own type for a name it does not read;
/// a limit type that names the other route reads as a fault in the caller's choice of route. (The command line's print of the inbox type is
/// <c>SmallOnesEndToEndTests</c>'s, which has a real socket.)
///
/// <para><b>Oracles.</b> Both types and the status are parsed from the errata; the two routes are asked the same three values, and their documents
/// are compared member by member.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class InboxLimitTypeTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(string Type, string Title, string Detail)> RefusalAsync(bool inbox, string limit)
    {
        HttpStatusCode status;
        string body;
        if (inbox)
        {
            var probe = new InboxProbe(forum);
            var reader = await probe.EnrolAsync("limit-reader", Ct);
            (status, body) = await probe.InboxRawAsync(reader, "?limit=" + Uri.EscapeDataString(limit), Ct);
        }
        else
        {
            using var response = await forum.Client.GetAsync(new Uri("/v1/search?limit=" + Uri.EscapeDataString(limit), UriKind.Relative), Ct);
            (status, body) = (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        }

        Assert.Equal((HttpStatusCode)PublishedSmallWords.LimitStatus(), status);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        return (root.GetProperty("type").GetString()!, root.GetProperty("title").GetString()!, root.GetProperty("detail").GetString()!);
    }

    /// <summary>PJ33: the inbox answers its own type, the search route keeps its own, and the title and detail are the same for both.</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("x")]
    public async Task R9_44_TheInboxHasItsOwnLimitType(string limit)
    {
        ArgumentNullException.ThrowIfNull(limit);

        var search = await RefusalAsync(inbox: false, limit);
        var inbox = await RefusalAsync(inbox: true, limit);

        Assert.Equal(PublishedSmallWords.SearchLimitType(), search.Type);
        Assert.Equal(PublishedSmallWords.InboxLimitType(), inbox.Type);
        Assert.Equal(search.Title, inbox.Title);
        Assert.Equal(search.Detail, inbox.Detail);
    }
}
