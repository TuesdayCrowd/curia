using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R10.84 (errata G43) at the flag route: a rationale that holds U+0000 or an unpaired surrogate is refused in words that name the rationale (the surrogate cell is measured below: the reader refuses the body first), under the
/// type it has today (<c>curia/flag/detail-unstorable</c>, <c>400</c>). The probe posts the raw JSON, because no client and no tool can deliver either
/// character (the SDK fails the parse, the command line has no such value): <c>\u0000</c> and <c>\ud800</c> are written as the escapes a hand-built
/// client may send. The control is a rationale with neither, which is flagged.
///
/// <para><b>Oracles.</b> The route's own problem document; the type from the register of the unmodified tree (<c>detail-unstorable</c>), unchanged.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class FlagRationaleWordsEndToEndTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(HttpStatusCode Status, string Body)> FlagRawAsync(string rationaleJson)
    {
        var probe = new InboxProbe(forum);
        var author = await probe.EnrolAsync("flag-author", Ct);
        var asked = await probe.AskAsync(author, "board-" + InboxProbe.Suffix(), "Flagged " + InboxProbe.Suffix(), Ct);
        var reporter = await probe.EnrolAsync("flag-reporter", Ct);
        var client = forum.Client;
        var token = await reporter.Dpop.GetTokenAsync(client, TokenEndpoint, forum.Now, Ct);

        using var response = await reporter.Dpop.PostAsync(
            client,
            $"http://localhost/v1/posts/{asked.Id}/flags",
            token,
            Encoding.UTF8.GetBytes("{\"kind\":\"spam\",\"rationale\":" + rationaleJson + "}"),
            forum.Now,
            Ct,
            contentType: "application/json");
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>PJ36's raw cells: the words name the rationale; the type and the status are unchanged.</summary>
    [Fact]
    public async Task R10_84_ARawRationaleWithNulIsNamedAsTheRationale()
    {
        var (status, body) = await FlagRawAsync("\"looks \\u0000 odd\"");

        Assert.True(status == HttpStatusCode.BadRequest, $"{(int)status}: {body}");
        using var json = JsonDocument.Parse(body);
        Assert.Equal("curia/flag/detail-unstorable", json.RootElement.GetProperty("type").GetString());
        var words = json.RootElement.GetProperty("title").GetString() + "\n" + json.RootElement.GetProperty("detail").GetString();
        Assert.Contains("rationale", words, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// PJ36's surrogate cell, measured on the day (Task 0): an unpaired surrogate in the raw body never reaches the flag path. The guarded JSON reader
    /// (R11.34) refuses the body as a whole, <c>400 curia/request/unreadable</c>, whose words name no member. Recorded as the reader's non-property; a
    /// rationale's surrogate is therefore not R10.84's to word, and R10.84 names only what the flag path can receive. Green first.
    /// </summary>
    [Fact]
    public async Task R10_84_ARawRationaleWithALoneSurrogateIsTheReadersRefusalOfTheBody()
    {
        var (status, body) = await FlagRawAsync("\"looks \\ud800 odd\"");

        Assert.True(status == HttpStatusCode.BadRequest, $"{(int)status}: {body}");
        using var json = JsonDocument.Parse(body);
        Assert.Equal("curia/request/unreadable", json.RootElement.GetProperty("type").GetString());
        Assert.DoesNotContain("looks", body, StringComparison.Ordinal);
    }

    /// <summary>PJ36's control: a rationale with neither character is flagged.</summary>
    [Fact]
    public async Task R10_84_ARationaleWithNeitherIsFlagged()
    {
        var (status, body) = await FlagRawAsync("\"looks like spam\"");

        Assert.True(status == HttpStatusCode.Created, $"{(int)status}: {body}");
    }
}
