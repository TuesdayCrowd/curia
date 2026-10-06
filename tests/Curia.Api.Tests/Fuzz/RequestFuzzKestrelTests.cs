using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Curia.Api.Tests.Fuzz;

/// <summary>
/// The real Forum on a real socket: <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}.UseKestrel(int)"/>
/// on a port the system picks, so a request line reaches Kestrel's own parser before anything of the
/// Forum's, as no in-process test server lets it (spec §4.10).
/// </summary>
public sealed class KestrelForumFixture : ForumFixture
{
    public KestrelForumFixture() => UseKestrel(0);
}

/// <summary>
/// R14.10's Kestrel pass (spec §4.10): raw path bytes, percent-encoded and not, written into the
/// request line of every route with a path parameter, over a <see cref="TcpClient"/>. It asserts that a
/// row of every route gets past Kestrel's parser. Whether the route's own path parser is reached is the
/// closed pass's clause 5, which sends authenticated, percent-encoded path variations.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("fuzz")]
public sealed class RequestFuzzKestrelTests(KestrelForumFixture forum) : IClassFixture<KestrelForumFixture>
{
    /// <summary>What is written in place of the path parameter: percent-encoded text, and two raw bytes.</summary>
    private static readonly (string Name, byte[] Bytes)[] Values =
    [
        ("%00", Encoding.ASCII.GetBytes("%00")),
        ("a%00b", Encoding.ASCII.GetBytes("a%00b")),
        ("%FF", Encoding.ASCII.GetBytes("%FF")),
        ("%C0%80", Encoding.ASCII.GetBytes("%C0%80")),
        ("%ED%A0%80", Encoding.ASCII.GetBytes("%ED%A0%80")),
        ("raw 0xFF", [0xFF]),
        ("raw 0x00", [0x00]),
    ];

    [Fact]
    public async Task R14_10_NoRawPathByteIsAServerFault()
    {
        var ct = TestContext.Current.CancellationToken;
        forum.StartServer();
        var address = forum.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .Select(a => new Uri(a))
            .First(u => u.Scheme == Uri.UriSchemeHttp);

        var routes = SurfaceInventory.Routes(forum).Where(r => r.Parameters.Length > 0).ToList();
        Assert.True(routes.Count > 0, "no route has a path parameter; the derivation is wrong");

        var failures = new List<string>();
        var slowest = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var (method, pattern, parameters) in routes)
        {
            var route = $"{method} {pattern}";
            var passedKestrel = false;
            var answers = new List<string>();
            foreach (var parameter in parameters)
            {
                foreach (var (name, bytes) in Values)
                {
                    var stopwatch = Stopwatch.StartNew();
                    var (status, body) = await SendAsync(address, method, PathWith(pattern, parameter, bytes), ct);
                    stopwatch.Stop();
                    var ms = (long)Math.Round(stopwatch.Elapsed.TotalMilliseconds);
                    slowest[route] = Math.Max(slowest.GetValueOrDefault(route), ms);
                    answers.Add($"{name} {status.ToString(CultureInfo.InvariantCulture)}");
                    if (status is < 100 or >= 500)
                    {
                        failures.Add($"{route} path:{parameter} {name}: {status.ToString(CultureInfo.InvariantCulture)}, a server fault or no status line");

                        // No problem type: the body may be chunk-framed, and this pass does not de-chunk it.
                        await FuzzRun.AppendFailureAsync(route, "kestrel", $"path:{parameter}", name, "plain", status, string.Empty, ms, ledgered: false, ct);
                    }

                    // Kestrel's own refusal is a 400 with an empty body. Any other answer, a 5xx included, means the request got past Kestrel's parser into the application, though not necessarily to the route's path parser: authentication, the body binder or routing may answer first.
                    if (status >= 100 && !(status == 400 && body.Length == 0)) passedKestrel = true;
                }
            }

            if (!passedKestrel)
                failures.Add($"{route}: Kestrel refused every row, so no path byte got past its parser ({string.Join(", ", answers)})");
            TestContext.Current.TestOutputHelper?.WriteLine($"{route}: {string.Join(", ", answers)}");
        }

        if (Environment.GetEnvironmentVariable("CURIA_FUZZ_TIMINGS") is { } timings)
        {
            var lines = new StringBuilder();
            foreach (var (route, ms) in slowest)
                lines.Append(CultureInfo.InvariantCulture, $"slowest-kestrel\t{route}\t-\t{ms}\n");
            await File.AppendAllTextAsync(timings, lines.ToString(), ct);
        }

        Assert.True(failures.Count == 0, $"{failures.Count} failures:\n" + string.Join('\n', failures));
    }

    /// <summary>The pattern's path with <paramref name="bytes"/> at <paramref name="parameter"/>, and <c>x</c> at any other parameter.</summary>
    private static byte[] PathWith(string pattern, string parameter, byte[] bytes)
    {
        using var path = new MemoryStream();
        var i = 0;
        while (i < pattern.Length)
        {
            if (pattern[i] != '{')
            {
                path.WriteByte((byte)pattern[i++]);
                continue;
            }

            var end = pattern.IndexOf('}', i);
            var name = pattern[(i + 1)..end].Split(':')[0].TrimStart('*');
            if (string.Equals(name, parameter, StringComparison.Ordinal)) path.Write(bytes);
            else path.WriteByte((byte)'x');
            i = end + 1;
        }

        return path.ToArray();
    }

    /// <summary>One request line and its framing, written raw; the status and the body as text.</summary>
    private static async Task<(int Status, string Body)> SendAsync(Uri address, string method, byte[] path, CancellationToken ct)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(address.Host, address.Port, ct);
        await using var stream = client.GetStream();
        byte[] request =
        [
            .. Encoding.ASCII.GetBytes(method + " "),
            .. path,
            .. Encoding.ASCII.GetBytes(" HTTP/1.1\r\nHost: localhost\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"),
        ];
        await stream.WriteAsync(request, ct);

        using var answer = new MemoryStream();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await stream.CopyToAsync(answer, timeout.Token);

        var text = Encoding.Latin1.GetString(answer.ToArray());
        var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var head = split < 0 ? text : text[..split];
        var body = split < 0 ? string.Empty : text[(split + 4)..];
        var line = head.Split("\r\n")[0].Split(' ');
        var status = line.Length > 1 && int.TryParse(line[1], NumberStyles.None, CultureInfo.InvariantCulture, out var code) ? code : 0;
        return (status, body);
    }
}
