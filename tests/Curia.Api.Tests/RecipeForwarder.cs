using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Curia.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Curia.Api.Tests;

/// <summary>
/// Tier A of the deployment pack (errata G83): a small forwarder in front of a Forum on a real socket that <b>behaves as the recipe says</b>. It reads <c>deploy/proxy/curia-http.conf</c> and
/// <c>deploy/proxy/curia-locations.conf</c> and interprets the subset of directives those files use: <c>limit_req_zone</c> and <c>limit_req</c> (a leaky bucket per client address, nginx's own
/// arithmetic: <c>burst</c> excess requests are admitted and the next is refused), <c>limit_req_status</c>, <c>client_max_body_size</c>, <c>error_page</c> to a named location whose <c>return</c>
/// and <c>default_type</c> give the answer, and <c>proxy_set_header</c> with the variables the recipe uses. <b>It is a model of the recipe and not nginx</b>: what it cannot show is that nginx
/// honours a stanza as this reads it, which the live run's real nginx is for. An empty recipe makes the model do what nginx does with no directive: the upstream's own authority as <c>Host</c>
/// (<c>$proxy_host</c>), no limit, no cap.
/// </summary>
internal sealed class RecipeForwarder : IAsyncDisposable
{
    /// <summary>The mutations the falsification rows apply to the model (FPX1 to FPX4): off by default.</summary>
    internal sealed record Options(bool DropBucket = false, bool RewriteHost = false, bool TrustXForwardedHost = false, bool HtmlOn429 = false, string? EnrolmentAllow = null, bool IgnoreDeny = false, bool CaseSensitiveRegex = false, bool DropAnchor = false, bool KeepFragment = false);

    private sealed record Answer(int Status, string ContentType, string Body);

    private sealed class Bucket
    {
        internal double Excess;
        internal long LastTicks;
    }

    private readonly WebApplication _app;
    private readonly HttpClient _upstream = new(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None });
    private readonly int _forumPort;
    private readonly Options _options;
    private readonly Dictionary<string, double> _zoneRates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _setHeaders = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<NgxDirective> _locations;
    private readonly Dictionary<string, Answer> _named = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _serverErrorPages = [];
    private readonly ConcurrentDictionary<(string Zone, string Address), Bucket> _buckets = new();
    private readonly long _bodyCap;
    private readonly int _limitStatus;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>Requests the forwarder passed to the Forum, by path: the forwarder's own count of what it asked of the Forum.</summary>
    internal ConcurrentDictionary<string, int> Passed { get; } = new(StringComparer.Ordinal);

    private RecipeForwarder(WebApplication app, int forumPort, Options options, List<NgxDirective> http, List<NgxDirective> locations)
    {
        _app = app;
        _forumPort = forumPort;
        _options = options;
        _locations = locations;
        foreach (var zone in NginxConf.Find(http, "limit_req_zone"))
        {
            var name = zone.Args.First(a => a.StartsWith("zone=", StringComparison.Ordinal))["zone=".Length..].Split(':')[0];
            var rate = zone.Args.FirstOrDefault(a => a.StartsWith("rate=", StringComparison.Ordinal));
            if (rate is not null) _zoneRates[name] = NginxConf.RatePerSecond(rate["rate=".Length..]);
        }

        _limitStatus = NginxConf.Find(http, "limit_req_status").Select(d => int.Parse(d.Args[0], CultureInfo.InvariantCulture)).DefaultIfEmpty(503).First();
        foreach (var header in NginxConf.Find(locations, "proxy_set_header").Where(d => d.Args.Count == 2))
            _setHeaders[header.Args[0]] = header.Args[1];
        _bodyCap = NginxConf.Find(locations, "client_max_body_size").Select(d => NginxConf.SizeBytes(d.Args[0])).DefaultIfEmpty(1_048_576).First();
        foreach (var loc in NginxConf.Find(locations, "location").Where(l => l.Args.Count == 1 && l.Args[0].StartsWith('@')))
        {
            var ret = loc.Children("return").FirstOrDefault();
            if (ret is null || ret.Args.Count < 2) continue;
            var type = loc.Children("default_type").FirstOrDefault() is { Args.Count: > 0 } dt ? dt.Args[0] : "text/plain";
            _named[loc.Args[0]] = new Answer(int.Parse(ret.Args[0], CultureInfo.InvariantCulture), type, ret.Args[1]);
        }

        foreach (var page in locations.Where(d => d.Name == "error_page"))
            foreach (var code in page.Args.Where(a => int.TryParse(a, CultureInfo.InvariantCulture, out _)))
                _serverErrorPages[int.Parse(code, CultureInfo.InvariantCulture)] = page.Args[^1];
    }

    /// <summary>Starts a forwarder on <paramref name="listenPort"/> in front of the Forum on <paramref name="forumPort"/>, configured from the recipe files as they stand.</summary>
    internal static async Task<RecipeForwarder> StartAsync(int listenPort, int forumPort, Options? options = null)
    {
        var root = PublishedEdge.RepoRoot();
        var http = NginxConf.Parse(await File.ReadAllTextAsync(Path.Combine(root, "deploy", "proxy", "curia-http.conf")).ConfigureAwait(false));
        var locations = NginxConf.Parse(await File.ReadAllTextAsync(Path.Combine(root, "deploy", "proxy", "curia-locations.conf")).ConfigureAwait(false));
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPAddress.Loopback, listenPort);
            k.Limits.MaxRequestBodySize = null;
        });
        var app = builder.Build();
        var forwarder = new RecipeForwarder(app, forumPort, options ?? new Options(), http, locations);
        app.Run(forwarder.GuardedAsync);
        await app.StartAsync().ConfigureAwait(false);
        return forwarder;
    }

    /// <summary>
    /// The part of a request target nginx matches a <c>location</c> against: the URI ends at the first <c>#</c> (a fragment is not part of it; measured against nginx 1.28.0 by a raw socket, which sends what curl strips), and the query is
    /// not part of it either. The Forum's own host differs by platform (Kestrel answers a bare <c>400</c> to a <c>#</c> in the target on Linux and routes the path with it on macOS), so a probe reads this function, which is the same on every OS.
    /// </summary>
    internal static string MatchedPath(string rawTarget)
    {
        var end = rawTarget.IndexOfAny(['#', '?']);
        return end < 0 ? rawTarget : rawTarget[..end];
    }

    /// <summary>The <c>location</c> (its arguments, as the recipe writes them) the model selects for a request target as sent on the wire, fragment and query included.</summary>
    internal string? SelectedLocation(string rawTarget)
    {
        var (loc, _, _) = Match(_options.KeepFragment ? rawTarget.Split('?')[0] : MatchedPath(rawTarget));
        return loc is null ? null : string.Join(' ', loc.Args);
    }

    private (NgxDirective? Location, string? ErrorPage429, string? ErrorPage413) Match(string path)
    {
        var loc = NginxConf.Location(_locations, "= " + path) ?? RegexLocation(path) ?? NginxConf.Location(_locations, "/");
        return (loc, ErrorPageFor(loc, _limitStatus), ErrorPageFor(loc, 413));
    }

    /// <summary>The first regex location, in file order, whose pattern matches the path (nginx's order: exact, then regex, then the longest prefix; the recipe has one prefix, <c>/</c>).</summary>
    private NgxDirective? RegexLocation(string path)
    {
        foreach (var loc in NginxConf.Find(_locations, "location").Where(l => l.Args.Count == 2 && l.Args[0] is "~" or "~*"))
        {
            var pattern = loc.Args[1];
            if (_options.DropAnchor && pattern.EndsWith("/?$", StringComparison.Ordinal)) pattern = pattern[..^3];
            var ignoreCase = loc.Args[0] == "~*" && !_options.CaseSensitiveRegex;
            if (Regex.IsMatch(path, pattern, ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None, TimeSpan.FromSeconds(1))) return loc;
        }

        return null;
    }

    /// <summary>nginx's access module over the location's <c>include</c>, <c>allow</c> and <c>deny</c>, in order: the first rule that names the address decides, and an address no rule names is admitted.</summary>
    private bool Admitted(NgxDirective? loc, IPAddress? address)
    {
        if (loc is null) return true;
        var rules = new List<NgxDirective>();
        foreach (var d in loc.Block ?? [])
        {
            if (d.Name == "include" && d.Args.Count == 1) rules.AddRange(IncludedRules(d.Args[0]));
            else if (d.Name is "allow" or "deny") rules.Add(d);
        }

        var peer = address?.IsIPv4MappedToIPv6 == true ? address.MapToIPv4() : address;
        foreach (var rule in rules)
        {
            if (rule.Name == "deny" && _options.IgnoreDeny) continue;
            if (rule.Args.Count != 1 || !Names(rule.Args[0], peer)) continue;
            return rule.Name == "allow";
        }

        return true;
    }

    private IEnumerable<NgxDirective> IncludedRules(string file)
    {
        if (file != "curia-enrolment-allow.conf") return [];
        var text = _options.EnrolmentAllow ?? File.ReadAllText(Path.Combine(PublishedEdge.RepoRoot(), "deploy", "proxy", file));
        return NginxConf.Parse(text).Where(d => d.Name is "allow" or "deny");
    }

    private static bool Names(string rule, IPAddress? peer)
    {
        if (rule == "all") return true;
        if (peer is null) return false;
        return rule.Contains('/', StringComparison.Ordinal) ? IPNetwork.TryParse(rule, out var network) && network.Contains(peer) : IPAddress.TryParse(rule, out var one) && one.Equals(peer);
    }

    private string? ErrorPageFor(NgxDirective? loc, int code)
    {
        var own = loc?.Children("error_page").ToList() ?? [];
        var pages = own.Count > 0 ? own : _locations.Where(d => d.Name == "error_page").ToList();
        return pages.Where(p => p.Args.Contains(code.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal)).Select(p => p.Args[^1]).FirstOrDefault(a => a.StartsWith('@'));
    }

    private async Task RefuseAsync(HttpContext ctx, int code, string? page)
    {
        ctx.Response.StatusCode = code;
        if (code == _limitStatus && _options.HtmlOn429)
        {
            ctx.Response.ContentType = "text/html";
            await ctx.Response.WriteAsync("<html><body><h1>429 Too Many Requests</h1></body></html>").ConfigureAwait(false);
            return;
        }

        if (page is not null && _named.TryGetValue(page, out var answer))
        {
            ctx.Response.StatusCode = answer.Status;
            ctx.Response.ContentType = answer.ContentType;
            await ctx.Response.WriteAsync(answer.Body).ConfigureAwait(false);
            return;
        }

        ctx.Response.ContentType = "text/html";
        await ctx.Response.WriteAsync($"<html><body><h1>{code}</h1><hr>nginx</body></html>").ConfigureAwait(false);
    }

    /// <summary>nginx's leaky bucket as <c>limit_req</c> keeps it: the excess decays at the rate; a request is admitted while the excess it would leave is at most <c>burst</c>.</summary>
    private bool Admit(string zone, string address, int burst)
    {
        if (!_zoneRates.TryGetValue(zone, out var rate)) return true;
        var bucket = _buckets.GetOrAdd((zone, address), _ => new Bucket { LastTicks = _clock.ElapsedTicks });
        lock (bucket)
        {
            var now = _clock.ElapsedTicks;
            var elapsed = (now - bucket.LastTicks) / (double)Stopwatch.Frequency;
            bucket.LastTicks = now;
            bucket.Excess = Math.Max(0, bucket.Excess - (rate * elapsed));
            if (bucket.Excess > burst) return false;
            bucket.Excess += 1;
            return true;
        }
    }

    private string Evaluate(string expression, HttpContext ctx)
    {
        var host = ctx.Request.Headers.Host.ToString();
        return expression switch
        {
            "$http_host" => host,
            "$host" => host.Split(':')[0],
            "$proxy_host" => $"127.0.0.1:{_forumPort}",
            "$http_x_forwarded_host" => ctx.Request.Headers["X-Forwarded-Host"].ToString(),
            "$remote_addr" => ctx.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
            "$scheme" => "https",
            "$proxy_add_x_forwarded_for" => (ctx.Request.Headers["X-Forwarded-For"].ToString() + "," + ctx.Connection.RemoteIpAddress).TrimStart(','),
            _ => expression,
        };
    }

    /// <summary>A fault of the model itself is a 502 that says what it was, so that a probe's red names the forwarder and not a bare 500.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Any fault of the test double is reported to the probe as a 502 naming it.")]
    private async Task GuardedAsync(HttpContext ctx)
    {
        try
        {
            await HandleAsync(ctx).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException && !ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = 502;
            await ctx.Response.WriteAsync($"forwarder fault: {e.GetType().Name}: {e.Message}").ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "/";
        var (loc, page429, page413) = Match(_options.KeepFragment ? path : MatchedPath(path));

        // limit_req, before the Forum reads the request
        var limit = loc?.Children("limit_req").Select(l => (Zone: l.Args.First(a => a.StartsWith("zone=", StringComparison.Ordinal))["zone=".Length..], Burst: l.Args.FirstOrDefault(a => a.StartsWith("burst=", StringComparison.Ordinal)))).FirstOrDefault();
        if (limit is { Zone: not null } && !_options.DropBucket)
        {
            var burst = limit.Value.Burst is null ? 0 : int.Parse(limit.Value.Burst["burst=".Length..], CultureInfo.InvariantCulture);
            if (!Admit(limit.Value.Zone, ctx.Connection.RemoteIpAddress?.ToString() ?? string.Empty, burst))
            {
                await RefuseAsync(ctx, _limitStatus, page429).ConfigureAwait(false);
                return;
            }
        }

        // client_max_body_size, by the declared length
        if (ctx.Request.ContentLength is { } declared && declared > _bodyCap)
        {
            await RefuseAsync(ctx, 413, page413).ConfigureAwait(false);
            return;
        }

        // allow and deny, after the limit and the body bound (nginx's access phase)
        if (!Admitted(loc, ctx.Connection.RemoteIpAddress))
        {
            await RefuseAsync(ctx, 403, ErrorPageFor(loc, 403)).ConfigureAwait(false);
            return;
        }

        using var request = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), new Uri($"http://127.0.0.1:{_forumPort}{ctx.Request.Path}{ctx.Request.QueryString}"));
        if (ctx.Request.ContentLength is > 0 || ctx.Request.Headers.ContainsKey("Transfer-Encoding"))
            request.Content = new StreamContent(ctx.Request.Body);
        foreach (var header in ctx.Request.Headers)
        {
            if (header.Key is "Host" or "Connection" or "Keep-Alive" or "Transfer-Encoding" or "Expect") continue;
            if (_setHeaders.ContainsKey(header.Key)) continue;
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }

        foreach (var (name, expression) in _setHeaders)
        {
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
            request.Headers.TryAddWithoutValidation(name, Evaluate(expression, ctx));
        }

        var hostExpression = _setHeaders.GetValueOrDefault("Host", "$proxy_host");
        if (_options.RewriteHost) hostExpression = "$proxy_host";
        if (_options.TrustXForwardedHost && ctx.Request.Headers.ContainsKey("X-Forwarded-Host")) hostExpression = "$http_x_forwarded_host";
        request.Headers.Host = Evaluate(hostExpression, ctx);

        Passed.AddOrUpdate(path, 1, (_, n) => n + 1);
        using var response = await _upstream.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted).ConfigureAwait(false);
        ctx.Response.StatusCode = (int)response.StatusCode;
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            if (header.Key is "Transfer-Encoding" or "Connection" or "Keep-Alive") continue;
            ctx.Response.Headers[header.Key] = header.Value.ToArray();
        }

        await response.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
        _upstream.Dispose();
    }
}
