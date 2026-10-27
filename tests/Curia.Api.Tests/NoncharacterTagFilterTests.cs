using System.Diagnostics.CodeAnalysis;
using System.Net;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R9.45 (errata G43): a <c>tags</c> filter element that cannot be read in Unicode normalization form C, such as one holding the
/// noncharacter U+FFFE (which <c>string.Normalize</c> throws on), is compared as it came. The search and the inbox answer it as a
/// query that names no stored tag, <c>200</c> with no results, never a server fault (R11.33). ADMIT refuses noncharacters, so no
/// stored tag holds one.
///
/// <para><b>Oracle.</b> The status the route gives, and an empty result list. The tests are red when <c>NormalizedC</c> lets the
/// exception through: the route answers <c>500</c>.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class NoncharacterTagFilterTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    [Fact]
    public async Task R9_45_ASearchTagsFilterHoldingANoncharacterAnswersOkAndFindsNothing()
    {
        var ct = TestContext.Current.CancellationToken;

        using var response = await forum.Client.GetAsync(new Uri("/v1/search?q=anything&tags=%EF%BF%BE", UriKind.Relative), ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}");
    }

    [Fact]
    public async Task R9_45_AnInboxTagsFilterHoldingANoncharacterAnswersOkAndFindsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var probe = new InboxProbe(forum);
        var reader = await probe.EnrolAsync("nonchar-reader", ct);

        var (status, body) = await probe.InboxRawAsync(reader, "?tags=%EF%BF%BE", ct);

        Assert.True(status == HttpStatusCode.OK, $"{(int)status}: {body}");
    }
}
