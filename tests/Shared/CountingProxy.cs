using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace Curia.Tests.Shared;

/// <summary>
/// A loopback forwarder in front of a Forum that counts what it relays, by path: the oracle for "this many requests were sent" where the Api fixture has no
/// counter and a client binds its Forum URL (PR 3, ruling 23). Promoted from <c>CliGuardEndToEndTests</c> with the same behaviour and the counters the readers'
/// last words need. <b>Only <c>GET /v1/posts/{id}</c> counts as a post read</b>, whatever its query (the question's read carries <c>?marking=datamark</c>);
/// <c>/v1/jwks</c>, <c>/oauth/token</c> and <c>/v1/threads/*</c> are counted apart, so a probe that counted every <c>GET</c> would move with the token cache.
/// </summary>
internal sealed class CountingProxy : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly HttpClient _upstream = new();
    private readonly Uri _target;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<string> _gets = new();
    private readonly ConcurrentQueue<string> _posts = new();
    private readonly ConcurrentDictionary<string, int> _failGets = new(StringComparer.Ordinal);
    private int _batchRequests;

    private CountingProxy(Uri target, int port)
    {
        _target = target;
        Address = $"http://{IPAddress.Loopback}:{port}";
        _listener.Prefixes.Add(Address + "/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    internal string Address { get; }

    internal int BatchRequests => Volatile.Read(ref _batchRequests);

    /// <summary>How many <c>GET /v1/posts/{id}</c> requests for <paramref name="postId"/> the proxy relayed.</summary>
    internal int ReadsOf(string postId) => _gets.Count(path => string.Equals(path, "/v1/posts/" + Uri.EscapeDataString(postId), StringComparison.Ordinal));

    /// <summary>How many <c>GET /v1/posts/{id}</c> requests the proxy relayed in all: one path segment after <c>/v1/posts/</c>, whatever its query.</summary>
    internal int PostReads => _gets.Count(IsPostRead);

    /// <summary>How many <c>GET /v1/threads/{root}</c> requests the proxy relayed.</summary>
    internal int ThreadReads => _gets.Count(path => path.StartsWith("/v1/threads/", StringComparison.Ordinal));

    /// <summary>How many requests for a key set (<c>/v1/jwks</c>) the proxy relayed.</summary>
    internal int JwksReads => _gets.Count(path => path.StartsWith("/v1/jwks", StringComparison.Ordinal));

    /// <summary>How many <c>POST /v1/posts</c> submissions the Forum took up through the proxy (a <c>401</c> that only issues a nonce is not one; the batch route and an acceptance are not submissions).</summary>
    internal int Submissions => _posts.Count(path => string.Equals(path, "/v1/posts", StringComparison.Ordinal));

    /// <summary>The post ids of the <c>GET /v1/posts/{id}</c> requests relayed so far, in order.</summary>
    internal string[] PostReadIds => [.. _gets.Where(IsPostRead).Select(path => Uri.UnescapeDataString(path["/v1/posts/".Length..]))];

    /// <summary>Makes the next <c>GET /v1/posts/{postId}</c> answer <c>503</c> without reaching the Forum (a transient fault); every other request is forwarded.</summary>
    internal void FailNextReadOf(string postId) => _failGets[Uri.EscapeDataString(postId)] = 1;

    internal static CountingProxy In(Uri target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new CountingProxy(target, FreePort());
    }

    private static bool IsPostRead(string path) =>
        path.StartsWith("/v1/posts/", StringComparison.Ordinal) && path.Length > "/v1/posts/".Length && !path["/v1/posts/".Length..].Contains('/', StringComparison.Ordinal);

    private static int FreePort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A relay failure ends that exchange only; the test's own assertions then see the missing answer.")]
    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            try
            {
                var request = context.Request;
                var path = request.Url!.AbsolutePath;
                if (string.Equals(request.HttpMethod, "GET", StringComparison.Ordinal)) _gets.Enqueue(path);
                if (string.Equals(request.HttpMethod, "POST", StringComparison.Ordinal)
                    && string.Equals(path, "/v1/posts/batch", StringComparison.Ordinal))
                    Interlocked.Increment(ref _batchRequests);

                if (string.Equals(request.HttpMethod, "GET", StringComparison.Ordinal)
                    && IsPostRead(path)
                    && _failGets.TryRemove(path["/v1/posts/".Length..], out _))
                {
                    context.Response.StatusCode = 503;
                    continue;
                }

                using var forwarded = new HttpRequestMessage(new HttpMethod(request.HttpMethod), new Uri(_target, request.Url!.PathAndQuery));
                foreach (var name in new[] { "Authorization", "DPoP", "If-None-Match" })
                    if (request.Headers[name] is { } value) forwarded.Headers.TryAddWithoutValidation(name, value);
                forwarded.Headers.Host = request.UserHostName; // a DPoP proof is signed over the URL the client used, so the Forum must rebuild that one
                using var body = new MemoryStream();
                await request.InputStream.CopyToAsync(body);
                if (body.Length > 0)
                {
                    forwarded.Content = new ByteArrayContent(body.ToArray());
                    if (request.ContentType is { } type) forwarded.Content.Headers.TryAddWithoutValidation("Content-Type", type);
                }

                using var answer = await _upstream.SendAsync(forwarded);

                // A DPoP client's first POST is often answered 401 with the nonce to sign next and is then repeated; only the request the Forum took up counts as a submission.
                if (string.Equals(request.HttpMethod, "POST", StringComparison.Ordinal) && answer.StatusCode != HttpStatusCode.Unauthorized) _posts.Enqueue(path);
                var bytes = await answer.Content.ReadAsByteArrayAsync();
                context.Response.StatusCode = (int)answer.StatusCode;
                context.Response.ContentType = answer.Content.Headers.ContentType?.ToString();

                // The Forum's answer carries what a DPoP client needs beside the body (the nonce it must sign next, a challenge), so every end-to-end header is relayed.
                foreach (var header in answer.Headers.Concat(answer.Content.Headers))
                {
                    if (header.Key is "Transfer-Encoding" or "Content-Length" or "Content-Type" or "Connection" or "Keep-Alive") continue;
                    context.Response.Headers[header.Key] = string.Join(", ", header.Value);
                }

                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
            }
            catch (Exception e) when (e is HttpRequestException or HttpListenerException or IOException or InvalidOperationException)
            {
                context.Response.StatusCode = 502;
            }
            finally
            {
                context.Response.Close();
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
        _upstream.Dispose();
        _stop.Dispose();
    }
}
