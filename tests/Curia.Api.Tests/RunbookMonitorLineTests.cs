using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// A Forum named by the origin <c>docs/runbooks/reverse-proxy.md</c>'s monitor line names (read from the runbook; <c>https://forum.example</c> when the runbook has no line, so that the probe's first assertion,
/// and not a failed start, is what goes red). Its clients send that origin's authority as <c>Host</c>, as a monitor going through the edge does.
/// </summary>
public sealed partial class MonitorOriginFixture : ForumFixture
{
    /// <summary>The monitor line: <c>curl -fsS --resolve &lt;name&gt;:&lt;port&gt;:127.0.0.1 &lt;scheme&gt;://&lt;authority&gt;/health</c>.</summary>
    internal static readonly Regex Monitor = MonitorLine();

    internal static string Authority { get; } = Monitor.Match(Read()) is { Success: true } m ? m.Groups[4].Value : "forum.example";

    internal static string Scheme { get; } = Monitor.Match(Read()) is { Success: true } m ? m.Groups[3].Value : "https";

    internal override string PublicOrigin => $"{Scheme}://{Authority}";

    /// <summary>R11.112 (errata G85): a Forum that holds a public origin runs Production, the shape the hosted Forum runs; Development with it is refused at start.</summary>
    protected override string? HostingEnvironment => "Production";

    protected override void ConfigureClient(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.Host = Authority;
    }

    internal static string Read() => File.ReadAllText(Path.Combine(PublishedEdge.RepoRoot(), "docs", "runbooks", "reverse-proxy.md"));

    [GeneratedRegex(@"--resolve (\S+?):(\d+):127\.0\.0\.1 (https?)://(\S+?)/health")]
    private static partial Regex MonitorLine();
}

/// <summary>
/// DPK8 (errata G83; amendment P8): the runbook's monitor line is read for the <c>Host</c> it sends and held against a real Forum configured with that name: <c>/health</c> under it is 200, under
/// <c>127.0.0.1</c> (a probe by address, a cloud load balancer's default) and under the same name altered by one character it is <c>400 curia/request/host-not-served</c>, and the runbook states that claim in a sentence this probe reads.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class RunbookMonitorLineTests(MonitorOriginFixture forum) : IClassFixture<MonitorOriginFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(HttpStatusCode Status, string? Type)> HealthAsync(string host)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Host = host;
        using var response = await forum.Client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        try
        {
            return (response.StatusCode, JsonDocument.Parse(text).RootElement.TryGetProperty("type", out var t) ? t.GetString() : null);
        }
        catch (JsonException)
        {
            return (response.StatusCode, null);
        }
    }

    [Fact]
    public async Task G83_DPK8_TheMonitorLineSendsTheOriginsHostAndAProbeByAddressIsRefused()
    {
        var problems = new List<string>();
        var runbook = MonitorOriginFixture.Read();
        var m = MonitorOriginFixture.Monitor.Match(runbook);
        if (!m.Success) problems.Add("docs/runbooks/reverse-proxy.md has no monitor line 'curl -fsS --resolve <name>:<port>:127.0.0.1 https://<name>/health'");
        else
        {
            var authority = m.Groups[4].Value;
            var defaultPort = m.Groups[3].Value == "https" ? "443" : "80";
            var expectedResolve = authority.Contains(':', StringComparison.Ordinal) ? authority : $"{authority}:{defaultPort}";
            if (m.Groups[1].Value + ":" + m.Groups[2].Value != expectedResolve) problems.Add($"--resolve names {m.Groups[1].Value}:{m.Groups[2].Value} but the URL's authority is {authority}");

            // Closing FDPK8a's survivor: a monitor line renamed consistently (--resolve and URL together) passes every check below, because the fixture is configured from the line itself. The name
            // must also be the one the shipped recipe serves: the literal nginx.conf redirects port 80 to.
            var nginx = await File.ReadAllTextAsync(Path.Combine(PublishedEdge.RepoRoot(), "deploy", "proxy", "nginx.conf"), Ct);
            var recipeName = RecipeRedirectName().Match(nginx);
            var monitorName = authority.Split(':')[0];
            if (!recipeName.Success) problems.Add("deploy/proxy/nginx.conf has no 'return 301 https://<name>...' redirect to compare the monitor line's name with");
            else if (!string.Equals(recipeName.Groups[1].Value, monitorName, StringComparison.Ordinal)) problems.Add($"the monitor line names {monitorName} but deploy/proxy/nginx.conf serves {recipeName.Groups[1].Value}");

            var named = await HealthAsync(authority);
            if (named.Status != HttpStatusCode.OK) problems.Add($"/health under the runbook's Host {authority} answered {(int)named.Status}, not 200");
            foreach (var other in new[] { "127.0.0.1", authority[..^1] + (authority[^1] == 'x' ? 'y' : 'x') })
            {
                var r = await HealthAsync(other);
                if (r.Status != HttpStatusCode.BadRequest || r.Type != "curia/request/host-not-served") problems.Add($"/health under Host {other} answered {(int)r.Status} {r.Type}, not 400 curia/request/host-not-served");
            }
        }

        var flat = Whitespace().Replace(runbook, " ");
        if (!ByAddress().IsMatch(flat)) problems.Add("the runbook does not say that a probe by address is answered 400 host-not-served");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    [GeneratedRegex(@"return\s+301\s+https?://([^/$:\s;]+)")]
    private static partial Regex RecipeRedirectName();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"probe by address.{0,300}host-not-served")]
    private static partial Regex ByAddress();
}
