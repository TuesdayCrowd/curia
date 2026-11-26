using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>The Forum on a real socket, in Development, set explicitly and not left to the factory's default (a row that thinks it is Development must be one).</summary>
public sealed class BodyBoundDevelopmentFixture : ForumFixture
{
    public BodyBoundDevelopmentFixture() => UseReservedKestrelPort();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseEnvironment("Development");
    }
}

/// <summary>The Forum on a real socket, in Production, as a deployed one runs.</summary>
public sealed class BodyBoundProductionFixture : ForumFixture
{
    public BodyBoundProductionFixture() => UseReservedKestrelPort();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseEnvironment("Production");
    }
}

/// <summary>
/// BND6 (errata G84, R6.68 (rev.), R11.33; D125): a request whose body the host's own reader rejects is answered by R11.33's rule and never reaches the host's response writer, which answers a body
/// rejected on the post route with nothing in Production and, in Development, with a page that carries the request's <c>Authorization</c> and <c>DPoP</c> headers and the token's value (Task 0.4).
/// Each row is a raw socket on Kestrel, because <c>TestServer</c> enforces none of the host's limits, with a valid token and DPoP proof, in <b>both</b> environments, the host's own environment asserted
/// before the request is sent. (a) is green with the middleware's catch deleted (a declared length is refused before any handler runs); (b) to (e) are the rows that catch decides.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public abstract class BodyBoundKestrelTests(ForumFixture forum, string environment)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Sent(RawHttp.Reply Reply, string Token, string Proof);

    private async Task<Sent> SendAsync(string route, string framing, Func<NetworkStream, Task> writeBody, int readSeconds)
    {
        Assert.Equal(environment, forum.Services.GetRequiredService<IHostEnvironment>().EnvironmentName);
        using var http = forum.Client;
        var credential = await BodyBoundKit.SignInAsync(forum, http, Ct);
        var origin = new Uri(forum.PublicOrigin);
        string path;
        string target;
        if (route == "keys")
        {
            var segment = Uri.EscapeDataString(credential.AgentId).Replace("%3A", ":", StringComparison.Ordinal);
            path = $"/v1/agents/{segment}/keys";
            target = forum.PublicOrigin + path;
        }
        else
        {
            path = BodyBoundKit.PathOf(route);
            target = forum.PublicOrigin + path;
        }

        var authenticated = route is "posts" or "keys";
        var proof = authenticated ? credential.Dpop.Proof("POST", target, forum.Now, credential.Token, credential.Nonce) : "zz-visible-proof";
        var token = authenticated ? credential.Token : "zz-visible-secret";
        var head = BodyBoundKit.RawHead(path, framing, origin.Authority, BodyBoundKit.ContentTypeOf(route), token, proof);
        return new Sent(await RawHttp.SendAsync(origin.Port, head, writeBody, readSeconds, Ct), token, proof);
    }

    private static async Task WriteChunksAsync(NetworkStream stream, int totalBytes)
    {
        var data = new byte[65_536];
        Array.Fill(data, (byte)'a');
        var header = Encoding.ASCII.GetBytes("10000\r\n");
        var crlf = "\r\n"u8.ToArray();
        for (var sent = 0; sent < totalBytes; sent += data.Length)
        {
            await stream.WriteAsync(header, Ct);
            await stream.WriteAsync(data, Ct);
            await stream.WriteAsync(crlf, Ct);
        }

        await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), Ct);
    }

    private static Task WriteBytesAsync(NetworkStream stream, byte[] bytes) => stream.WriteAsync(bytes, Ct).AsTask();

    /// <summary>The row's check: the status, a non-empty problem document of the type (a document the Forum composed), and in Development none of what the host's page carries.</summary>
    private string? Problem(Sent sent, int status, string type)
    {
        var reply = sent.Reply;
        var found = new List<string>();
        if (reply.Status != status) found.Add($"expected {status}, got {reply.Status}");
        if (reply.Body.Length == 0) found.Add("the body is empty");
        else if (BodyBoundKit.MemberText(reply.Body, "type") != type) found.Add($"expected a problem document of type {type}, got {BodyBoundKit.Describe(reply.Status, reply.Body)}");
        foreach (var leak in new[] { "Authorization", "DPoP", sent.Token, sent.Proof })
            if (reply.Body.Contains(leak, StringComparison.OrdinalIgnoreCase)) found.Add($"the body contains \"{(leak == sent.Token ? "the token's value" : leak == sent.Proof ? "the proof's value" : leak)}\"");

        return found.Count == 0 ? null : $"[{environment}] {string.Join("; ", found)}";
    }

    private static void Said(string? found) => Assert.True(found is null, found);

    private async Task<string?> RowAsync(string route, char row) => row switch
    {
        'a' => Problem(await SendAsync(route, "Content-Length: 40000000", s => WriteBytesAsync(s, new byte[65_536]), 15), 413, BodyBoundKit.TooLargeType),
        'b' => Problem(await SendAsync(route, "Transfer-Encoding: chunked", s => WriteChunksAsync(s, 1_100_000), 20), 413, BodyBoundKit.TooLargeType),
        'c' => Problem(await SendAsync(route, "Content-Length: 1000", s => WriteBytesAsync(s, new byte[10]), 40), 408, "curia/request/refused"),
        'd' => Problem(await SendAsync(route, "Transfer-Encoding: chunked", s => WriteBytesAsync(s, "zz\r\nabc\r\n0\r\n\r\n"u8.ToArray()), 15), 400, "curia/request/unreadable"),
        'e' => Problem(await SendAsync(route, "Transfer-Encoding: chunked", s => WriteBytesAsync(s, "3\r\nabcXX0\r\n\r\n"u8.ToArray()), 15), 400, "curia/request/unreadable"),
        _ => throw new ArgumentOutOfRangeException(nameof(row), row, "BND6 has rows a to e"),
    };

    /// <summary>BND6 (a): a declared 40,000,000 bytes, 64 KiB sent, on the post route.</summary>
    [Fact]
    public async Task R6_68_BND6a_ADeclaredLengthPastTheBoundIsAProblemDocument() => Said(await RowAsync("posts", 'a'));

    /// <summary>BND6 (b): a chunked body past the bound.</summary>
    [Fact]
    public async Task R6_68_BND6b_AChunkedBodyPastTheBoundIsAProblemDocument() => Said(await RowAsync("posts", 'b'));

    /// <summary>BND6 (c): a stalled body (Content-Length 1,000, ten bytes, silence): the host's minimum data rate answers 408, about six seconds on.</summary>
    [Fact]
    public async Task R6_68_BND6c_AStalledBodyIsAProblemDocument() => Said(await RowAsync("posts", 'c'));

    /// <summary>BND6 (d): a malformed chunk size line.</summary>
    [Fact]
    public async Task R6_68_BND6d_AMalformedChunkSizeIsAProblemDocument() => Said(await RowAsync("posts", 'd'));

    /// <summary>BND6 (e): chunk data not followed by CRLF.</summary>
    [Fact]
    public async Task R6_68_BND6e_ChunkDataWithoutItsCrlfIsAProblemDocument() => Said(await RowAsync("posts", 'e'));

    /// <summary>
    /// BND6 (pin, binder route): the same stall and malformed frames at <c>POST /v1/agents</c>, whose binder answers them 400 <c>curia/request/unreadable</c> already. <b>Green before and after</b>: a pin and not a discharge.
    /// </summary>
    [Fact]
    public async Task R6_68_BND6pin_TheBinderRoutesAnsweredAProblemDocumentAlready()
    {
        var problems = new List<string?>
        {
            Problem(await SendAsync("agents", "Content-Length: 1000", s => WriteBytesAsync(s, new byte[10]), 40), 400, "curia/request/unreadable"),
            Problem(await SendAsync("agents", "Transfer-Encoding: chunked", s => WriteBytesAsync(s, "zz\r\nabc\r\n0\r\n\r\n"u8.ToArray()), 15), 400, "curia/request/unreadable"),
        };
        Assert.True(problems.All(p => p is null), string.Join("; ", problems.Where(p => p is not null)));
    }

    /// <summary>BND6g: the five requests at a key act's route (it reads <c>http.Body</c> as the post route does). Recorded by Task 2; labelled in its report as red or as a pin.</summary>
    [Fact]
    public async Task R6_68_BND6g_TheKeyActRouteAnswersTheFiveRequestsAsProblemDocuments()
    {
        var problems = new List<string>();
        foreach (var row in "abcde")
            if (await RowAsync("keys", row) is { } found) problems.Add($"row {row}: {found}");

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}

/// <summary>BND6 in Development, where the host's page carries the credential.</summary>
[Collection("forum")]
public sealed class BodyBoundKestrelTestsInDevelopment(BodyBoundDevelopmentFixture forum) : BodyBoundKestrelTests(forum, "Development"), IClassFixture<BodyBoundDevelopmentFixture>;

/// <summary>BND6 in Production, where the host's answer is empty.</summary>
[Collection("forum")]
public sealed class BodyBoundKestrelTestsInProduction(BodyBoundProductionFixture forum) : BodyBoundKestrelTests(forum, "Production"), IClassFixture<BodyBoundProductionFixture>;
