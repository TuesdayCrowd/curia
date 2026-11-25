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
    private Uri? _target;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<string> _gets = new();
    private readonly ConcurrentQueue<string> _posts = new();
    private readonly ConcurrentDictionary<string, int> _failGets = new(StringComparer.Ordinal);
    private int _batchRequests;

    private CountingProxy(int port)
    {
        Address = $"http://{IPAddress.Loopback}:{port}";
        _listener.Prefixes.Add(Address + "/");
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    internal string Address { get; }

    /// <summary>
    /// D41 (errata G82): the Forum names itself by one configured origin, and a client signs over the address it dials, so the proxy a test counts through must <b>be</b> the Forum's name. The fixture
    /// starts the proxy first (its port is the origin), and gives it the Forum's own address once the host is up (<see cref="SetTarget"/>). The port is taken from the system and the listener started on
    /// it at once; a port another process took in between is another try, bounded, so the proxy's port is never a port that was free and is not.
    /// </summary>
    internal static CountingProxy Start()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                return new CountingProxy(FreePort());
            }
            catch (HttpListenerException)
            {
            }
        }

        throw new InvalidOperationException("the counting proxy could not take a loopback port in twenty tries");
    }

    /// <summary>Gives the proxy the address it forwards to, once, before any request reaches it.</summary>
    internal void SetTarget(Uri target)
    {
        ArgumentNullException.ThrowIfNull(target);
        _target = target;
    }

    /// <summary>
    /// A window over the counters from now on: a fixture is shared by a class's tests, which run one after another, so a test's counts are what the proxy relayed since it marked. The proxy's own
    /// counters are cumulative and are read by nothing else.
    /// </summary>
    internal Window Mark() => new(this, _gets.Count, _posts.Count, Volatile.Read(ref _batchRequests));

    /// <summary>The counters of <see cref="CountingProxy"/> since a <see cref="Mark"/>.</summary>
    internal sealed class Window
    {
        private readonly CountingProxy _proxy;
        private readonly int _getsFrom;
        private readonly int _postsFrom;
        private readonly int _batchFrom;

        internal Window(CountingProxy proxy, int getsFrom, int postsFrom, int batchFrom)
        {
            (_proxy, _getsFrom, _postsFrom, _batchFrom) = (proxy, getsFrom, postsFrom, batchFrom);
        }

        private IEnumerable<string> Gets => _proxy._gets.Skip(_getsFrom);

        internal string Address => _proxy.Address;

        internal int BatchRequests => Volatile.Read(ref _proxy._batchRequests) - _batchFrom;

        /// <summary>How many <c>GET /v1/posts/{id}</c> requests for <paramref name="postId"/> the proxy relayed.</summary>
        internal int ReadsOf(string postId) => Gets.Count(path => string.Equals(path, "/v1/posts/" + Uri.EscapeDataString(postId), StringComparison.Ordinal));

        /// <summary>How many <c>GET /v1/posts/{id}</c> requests the proxy relayed in all: one path segment after <c>/v1/posts/</c>, whatever its query.</summary>
        internal int PostReads => Gets.Count(IsPostRead);

        /// <summary>How many <c>GET /v1/threads/{root}</c> requests the proxy relayed.</summary>
        internal int ThreadReads => Gets.Count(path => path.StartsWith("/v1/threads/", StringComparison.Ordinal));

        /// <summary>How many requests for a key set (<c>/v1/jwks</c>) the proxy relayed.</summary>
        internal int JwksReads => Gets.Count(path => path.StartsWith("/v1/jwks", StringComparison.Ordinal));

        /// <summary>How many <c>POST /v1/posts</c> submissions the Forum took up through the proxy (a <c>401</c> that only issues a nonce is not one; the batch route and an acceptance are not submissions).</summary>
        internal int Submissions => _proxy._posts.Skip(_postsFrom).Count(path => string.Equals(path, "/v1/posts", StringComparison.Ordinal));

        /// <summary>The post ids of the <c>GET /v1/posts/{id}</c> requests relayed since the mark, in order.</summary>
        internal string[] PostReadIds => [.. Gets.Where(IsPostRead).Select(path => Uri.UnescapeDataString(path["/v1/posts/".Length..]))];

        /// <summary>Makes the next <c>GET /v1/posts/{postId}</c> answer <c>503</c> without reaching the Forum (a transient fault); every other request is forwarded.</summary>
        internal void FailNextReadOf(string postId) => _proxy._failGets[Uri.EscapeDataString(postId)] = 1;
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

                using var forwarded = new HttpRequestMessage(new HttpMethod(request.HttpMethod), new Uri(_target ?? throw new InvalidOperationException("the proxy has no target yet"), request.Url!.PathAndQuery));
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
