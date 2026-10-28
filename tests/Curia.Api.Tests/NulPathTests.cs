using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Text;
using Curia.Api.Tests.Fuzz;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R11.33 and decision 6 of errata G43, a recorded transport non-property: a path that carries <c>%00</c> is refused by Kestrel's own request-line
/// parser, below every middleware, with <c>400</c>, no content type and no body, so R11.33's no-5xx half holds and its problem-document half cannot
/// be met for a request the server never parsed. Pinned so that deciding it flips a fact on purpose. A query value is another door: it reaches the
/// Forum, and a contrast cell records what it answers (a lead, register D61 item 29: <c>/v1/jwks?agent=a%00b</c> echoes the value as an escape).
///
/// <para><b>Oracles.</b> A raw request line over a socket (<c>curl --path-as-is</c>'s equivalent), because an <see cref="HttpClient"/> normalizes the
/// path. The status line, headers and body are read as bytes.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class NulPathTests(KestrelForumFixture forum) : IClassFixture<KestrelForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Uri Address()
    {
        forum.StartServer();
        return forum.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .Select(a => new Uri(a))
            .First(u => u.Scheme == Uri.UriSchemeHttp);
    }

    private async Task<(int Status, string[] Headers, string Body)> RawGetAsync(string target)
    {
        var address = Address();
        using var client = new TcpClient();
        await client.ConnectAsync(address.Host, address.Port, Ct);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET {target} HTTP/1.1\r\nHost: {address.Authority}\r\nConnection: close\r\n\r\n"), Ct);

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, Ct);
        var text = Encoding.UTF8.GetString(buffer.ToArray());
        var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        Assert.True(split > 0, "no response head: " + text);
        var head = text[..split].Split("\r\n");
        var status = int.Parse(head[0].Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
        return (status, head[1..], text[(split + 4)..]);
    }

    /// <summary>PJ12: three path doors answer <c>400</c> with no content type and no body, and none a 5xx.</summary>
    [Theory]
    [InlineData("/v1/posts/AB%00CD")]
    [InlineData("/v1/threads/AB%00CD")]
    [InlineData("/v1/boards/a%00b/posts")]
    public async Task R11_33_TheServersParserAnswersAPercentNulPathBeforeAnyForumCode(string target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var (status, headers, body) = await RawGetAsync(target);

        Assert.Equal(400, status);
        Assert.DoesNotContain(headers, h => h.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(headers, h => h.Equals("Content-Length: 0", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(body);
    }

    /// <summary>PJ12's control: the same doors with a good id answer the Forum's own problem document.</summary>
    [Fact]
    public async Task R11_33_AGoodUnknownIdAnswersTheForumsOwnDocument()
    {
        var (status, headers, body) = await RawGetAsync("/v1/posts/ZZZ");

        Assert.Equal(404, status);
        Assert.Contains(headers, h => h.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase) && h.Contains("json", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("curia/posts/not-found", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// PJ12's contrast cell, a recorded lead and not a rule: a <c>%00</c> in the <c>agent</c> query of <c>/v1/jwks</c> reaches the Forum, which answers
    /// <c>404 curia/keys/unknown-agent</c> and echoes the value as <c>\u0000</c> in <c>detail</c> (register D61 item 29). Flips on purpose if decided.
    /// </summary>
    [Fact]
    public async Task R11_33_ContrastAQueryNulReachesTheForumAndIsEchoedAsAnEscape()
    {
        var (status, _, body) = await RawGetAsync("/v1/jwks?agent=a%00b");

        Assert.Equal(404, status);
        Assert.Contains("curia/keys/unknown-agent", body, StringComparison.Ordinal);
        Assert.Contains("a\\u0000b", body, StringComparison.Ordinal);
    }
}
