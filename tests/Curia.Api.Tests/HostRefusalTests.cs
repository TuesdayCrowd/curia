using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using Curia.Api.Tests.Fuzz;
using Xunit;
using static Curia.Api.Tests.OriginProbe;

namespace Curia.Api.Tests;

/// <summary>
/// R5.27 over the real routes (the plan's ORG10, ORG11 and ORG12): a request whose <c>Host</c> is not the origin's authority is refused by the Forum on every route without exception, as a problem
/// document of its own that carries nothing of the request. <b>Red on the tree</b>: the unmodified Forum serves every name it is addressed by. The rows the origin's own spellings add are the controls (green
/// on the tree, said so).
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed partial class HostRefusalTests(ForumFixture forum, KestrelForumFixture kestrel) : IClassFixture<ForumFixture>, IClassFixture<KestrelForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex Parameter();

    /// <summary>Every route the host registers, as a method and a path a request can be sent to, plus the routes the inventory leaves out (<c>/health</c>, the token endpoint, the well-known paths) and one that is not mapped.</summary>
    private (string Method, string Path)[] Surface() =>
    [
        .. SurfaceInventory.Routes(forum).Select(r => (r.Method, Parameter().Replace(r.Pattern, "x"))),
        ("GET", "/health"),
        ("POST", "/oauth/token"),
        ("GET", "/oauth/jwks"),
        ("GET", "/.well-known/oauth-authorization-server"),
        ("GET", "/.well-known/reader-contract/v1"),
        ("GET", "/no-such-path-org10"),
    ];

    private static async Task<OriginReply> SendAsync(HttpClient client, (string Method, string Path) route, string? host = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(route.Method), route.Path);
        if (host is not null) request.Headers.TryAddWithoutValidation("Host", host); // a value the header's own validation would refuse ("a,b") still reaches the host
        if (route.Method == "POST") request.Content = new FormUrlEncodedContent(new Dictionary<string, string>());
        return await ReadAsync(await client.SendAsync(request, Ct), Ct);
    }

    /// <summary>
    /// ORG10. Under a foreign <c>Host</c> every route of the surface answers <c>400</c> <c>curia/request/host-not-served</c> (RFC 6749's object at <c>/oauth/token</c>): a problem document with <b>no <c>detail</c>,
    /// no member holding any part of the sent <c>Host</c>, no <c>Location</c>, no <c>Set-Cookie</c></b>, in the media type the Forum's other problem documents use (read from its own unmapped-route answer under
    /// the honest <c>Host</c>), with no exemption for <c>/health</c> or the Reader Contract. Red on the tree.
    /// </summary>
    [Fact]
    public async Task R5_27_ORG10_EveryRouteRefusesAForeignHostInTheForumsOwnWords()
    {
        using var honest = ClientWith(forum, "localhost");
        var oracle = await SendAsync(honest, ("GET", "/no-such-path-org10"));
        Assert.True(oracle.Type == "curia/request/no-route" && oracle.ContentType is not null, $"the oracle (an unmapped route under Host localhost) was not the Forum's problem document: {oracle.Says}");

        var failures = new List<string>();
        var surface = Surface();
        foreach (var (host, sent) in new[] { ("foreign.example", "foreign.example"), ("localhost:8080", "8080") })
        {
            using var client = ClientWith(forum, host);
            foreach (var route in surface)
            {
                var reply = await SendAsync(client, route);
                var row = $"{route.Method} {route.Path} under Host {host}";
                if (!reply.IsHostRefusal) { failures.Add($"{row}: expected 400 {HostRefusalType}, the Forum answered {reply.Says}"); continue; }
                var expectedType = route.Path == "/oauth/token" ? "application/json" : oracle.ContentType;
                Expect(failures, reply.ContentType == expectedType, $"{row}: expected {expectedType}, the answer is {reply.ContentType}");
                Expect(failures, reply.Detail is null, $"{row}: the refusal carries a detail: {reply.Detail}");
                Expect(failures, !reply.Body.Contains(sent, StringComparison.OrdinalIgnoreCase), $"{row}: the body holds the sent Host: {reply.Body}");
                Expect(failures, !reply.HeaderNames.Contains("Location", StringComparer.OrdinalIgnoreCase) && !reply.HeaderNames.Contains("Set-Cookie", StringComparer.OrdinalIgnoreCase), $"{row}: the answer carries Location or Set-Cookie");
            }
        }

        // The names the origin does spell, and the one it does not, answer as before: the control is that none of the origin's own spellings is refused.
        foreach (var host in new[] { "localhost", "LOCALHOST", "localhost:80" })
        {
            using var client = ClientWith(forum, host);
            foreach (var route in surface)
            {
                var reply = await SendAsync(client, route);
                Expect(failures, !reply.IsHostRefusal && reply.Status < 500, $"{route.Method} {route.Path} under Host {host}: expected the route's own answer, the Forum answered {reply.Says}");
            }
        }

        // A trailing dot is another name for a host, and is refused, not mapped (non-property (i); FORG25).
        using (var dotted = ClientWith(forum, "localhost."))
        {
            var reply = await SendAsync(dotted, ("GET", "/health"));
            Expect(failures, reply.IsHostRefusal, $"GET /health under Host localhost.: expected 400 {HostRefusalType}, the Forum answered {reply.Says}");
        }

        NoFailures(failures, $"R5.27: {surface.Length} routes under a foreign Host");
    }

    /// <summary>
    /// ORG10, over Kestrel's own parser: an empty <c>Host</c> value, a request with no <c>Host</c> at all (HTTP/1.0) and a 300-byte label are handed to the Forum (T0-2) and refused <c>host-not-served</c>, never a
    /// 5xx; the origin's own spelling is the control. Red on the tree for the three refusals.
    /// </summary>
    [Fact]
    public async Task R5_27_ORG10_AnEmptyHostAndNoHostAndALongLabelAreRefusedByTheForum()
    {
        var address = kestrel.Client.BaseAddress!;
        var own = address.Authority;
        var failures = new List<string>();
        var rows = new (string Name, string Request, bool Refused)[]
        {
            ("the origin's own spelling", $"GET /health HTTP/1.1\r\nHost: {own}\r\nConnection: close\r\n\r\n", false),
            ("an empty Host", "GET /health HTTP/1.1\r\nHost:\r\nConnection: close\r\n\r\n", true),
            ("HTTP/1.0 with no Host", "GET /health HTTP/1.0\r\n\r\n", true),
            ("a 300-byte label", $"GET /health HTTP/1.1\r\nHost: {new string('a', 300)}.example\r\nConnection: close\r\n\r\n", true),
            ("a foreign name", "GET /health HTTP/1.1\r\nHost: foreign.example\r\nConnection: close\r\n\r\n", true),
        };
        foreach (var (name, request, refused) in rows)
        {
            var reply = await RawAsync(address, Encoding.ASCII.GetBytes(request), Ct);
            if (refused) Expect(failures, reply.IsHostRefusal, $"{name}: expected 400 {HostRefusalType}, the answer was {reply.Says}");
            else Expect(failures, reply.Status == 200, $"{name}: expected 200, the answer was {reply.Says}");
            Expect(failures, reply.Status < 500, $"{name}: a server fault: {reply.Says}");
        }

        NoFailures(failures, "R5.27: the Forum refuses a Host that is absent, empty or not its name, over a real socket");
    }

    /// <summary>
    /// ORG11, item 374 as a client meets it. The real <c>curia</c>, dialled under a name that is not the origin (the Forum's own port under <c>localhost</c>, where its origin is the loopback address), prints the
    /// Forum's own problem document and exits 3, and <b>not</b> <c>curia/client/unreadable-problem</c>. The exit and the words are the page CRO-a is measured against. Red on the tree: the foreign name is served.
    /// </summary>
    [Fact]
    public async Task R5_27_ORG11_CuriaDialledUnderAnotherNameReadsTheForumsRefusal()
    {
        var dialled = $"http://localhost:{kestrel.Client.BaseAddress!.Port}";
        var home = Directory.CreateTempSubdirectory("curia-org11-").FullName;
        try
        {
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "curia.dll"));
            foreach (var arg in new[] { "search", "zebra", "--board", "org11", "--forum", dialled }) start.ArgumentList.Add(arg);
            start.Environment["CURIA_CLIENT_HOME"] = home;
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(Ct);
            var stderr = process.StandardError.ReadToEndAsync(Ct);
            await process.WaitForExitAsync(Ct);
            var (output, error) = (await stdout, await stderr);

            var failures = new List<string>();
            Expect(failures, process.ExitCode == 3, $"expected exit 3, got {process.ExitCode}");
            Expect(failures, error.Contains(HostRefusalType, StringComparison.Ordinal), $"expected stderr to carry {HostRefusalType}, stderr was: {error.Trim()}");
            Expect(failures, !error.Contains("unreadable-problem", StringComparison.Ordinal), $"stderr carries curia/client/unreadable-problem: {error.Trim()}");
            if (failures.Count > 0)
            {
                var said = output.Trim().ReplaceLineEndings(" | ");
                failures.Add("stderr: " + error.Trim().ReplaceLineEndings(" | ") + " || stdout begins: " + said[..Math.Min(160, said.Length)]);
            }

            NoFailures(failures, "item 374: a Forum addressed by a name it does not serve answers in its own contract");
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>
    /// ORG12a (amendment 1, #9), the closed set of <c>Host</c> values with each one's verdict: the origin's spelling, its case variant and its explicit default port answer as without the variation; a wrong port, a
    /// foreign name, a trailing dot, a 300-byte label and <c>a,b</c> are the Forum's <c>host-not-served</c>; and <c>a@b</c>, a space and two <c>Host</c> headers are <b>Kestrel's</b> own <c>400</c> over a real
    /// socket (T0-2), asserted as not the Forum's so that Kestrel's refusal is never counted as the Forum's. The test counts, with its own list, that every value of the set was sent. Red on the tree by its own
    /// assertion (the foreign values are served). Wiring the set into the three passes of the request fuzzer is Task 4's; this is the verdict table the exemplar will be held to.
    /// </summary>
    [Fact]
    public async Task R14_10_ORG12a_EveryHostOfTheClosedSetGetsItsVerdict()
    {
        var sent = new List<string>();
        var failures = new List<string>();
        var long300 = new string('a', 300) + ".example";

        foreach (var (host, refused) in new (string, bool)[]
        {
            ("localhost", false), ("LOCALHOST", false), ("localhost:80", false),
            ("localhost:8080", true), ("foreign.example", true), ("localhost.", true), (long300, true), ("a,b", true),
        })
        {
            using var client = forum.CreateClient();
            var reply = await SendAsync(client, ("GET", "/health"), host);
            sent.Add(host);
            if (refused) Expect(failures, reply.IsHostRefusal, $"Host {(host.Length > 40 ? host[..40] + "..." : host)} (TestServer): expected 400 {HostRefusalType}, the Forum answered {reply.Says}");
            else Expect(failures, reply.Status == 200, $"Host {host} (TestServer): expected 200, the Forum answered {reply.Says}");
        }

        var address = kestrel.Client.BaseAddress!;
        foreach (var (name, request) in new (string, string)[]
        {
            ("a@b", "GET /health HTTP/1.1\r\nHost: a@b\r\nConnection: close\r\n\r\n"),
            ("a space", "GET /health HTTP/1.1\r\nHost: a b\r\nConnection: close\r\n\r\n"),
            ("two Host headers", $"GET /health HTTP/1.1\r\nHost: {address.Authority}\r\nHost: {address.Authority}\r\nConnection: close\r\n\r\n"),
            ("a,b", "GET /health HTTP/1.1\r\nHost: a,b\r\nConnection: close\r\n\r\n"),
        })
        {
            var reply = await RawAsync(address, Encoding.ASCII.GetBytes(request), Ct);
            sent.Add(name);
            Expect(failures, reply.Status == 400 && !reply.IsHostRefusal, $"Host {name} (Kestrel): expected Kestrel's own 400, not the Forum's refusal, the answer was {reply.Says}");
        }

        Expect(failures, sent.Count == 12, $"the pass sent {sent.Count} values, the closed set has 12");
        NoFailures(failures, "R14.10: the Host header is a varied input with a verdict for each value");
    }

    /// <summary>
    /// ORG12b (amendment 1, #9). <c>Host</c> is in no row of <c>SurfaceInventory.TransportHeaders</c>: the row's reason ("framing, computed by the client; Kestrel's parser") is false once Forum code reads the
    /// header, and a header listed as framing can never be reported as unexercised, whatever reads it (the coverage rule is satisfied by a framework read, T0-9). Red on the tree because <c>Host</c> is listed;
    /// FORG22 is killed by this probe alone.
    /// </summary>
    [Fact]
    public async Task R14_10_ORG12b_HostIsNotListedAsFraming()
    {
        var listed = SurfaceInventory.TransportHeaders.Select(h => h.Name).ToArray();
        Assert.DoesNotContain("Host", listed);

        // Not listed is not enough (the coverage rule is satisfied by a framework read): an exemplar sends it, and the closed set's every value is a variation of it.
        var health = await Exemplars.All.Single(row => row.Route == "GET /health").Build(null!, Ct); // an anonymous row reads nothing of the context
        Assert.Contains(health.Headers, h => h.Name == "Host");
        var ids = Variations.HostValues.Select(v => "host-" + v.Id).ToArray();
        Assert.Equal(9, ids.Length);
        foreach (var id in ids) Assert.Contains(Variations.Closed, v => v.Id == id);
    }
}
