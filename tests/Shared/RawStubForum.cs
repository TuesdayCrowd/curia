using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Curia.Tests.Shared;

/// <summary>What the stub does with a submission it has read (errata G55, R11.77: the request was sent and no response is read).</summary>
internal enum UnheardBehaviour
{
    /// <summary>Read the request and never answer: the client's own timeout is what ends it.</summary>
    Stall,

    /// <summary>Read the request and close the connection with a FIN, no response: the transport reports the response as ended.</summary>
    Fin,

    /// <summary>Read the request and reset the connection (RST): the transport reports an error it does not name.</summary>
    Reset,
}

/// <summary>
/// A Forum on a loopback socket that answers the token endpoint like a Forum and does one of three things to a submission after reading it: nothing, a FIN, an RST. It is
/// raw TCP because an HTTP listener cannot leave a request unanswered, close without a response or reset (the three cases of M5's record). It checks nothing of the DPoP
/// protocol; the facts are about what a client does when it hears no answer. Every body it was sent is kept, so a probe recomputes the digest of the act from what was received.
/// </summary>
internal sealed class RawStubForum : IDisposable
{
    private const string Token = """{"access_token":"stub-access-token","token_type":"DPoP","expires_in":300,"scope":"question:create"}""";

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<TcpClient> _open = [];
    private readonly Task _loop;
    private int _boardListReads;
    private int _assertedReads;
    private bool _disposed;

    internal RawStubForum(UnheardBehaviour behaviour)
    {
        Behaviour = behaviour;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Address = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
        _loop = Task.Run(AcceptAsync);
    }

    internal Uri Address { get; }

    internal UnheardBehaviour Behaviour { get; set; }

    /// <summary>True: a request that is neither the token endpoint nor the submission route (a read, an acceptance, a flag) is read and never answered.</summary>
    internal bool StallReads { get; set; }

    /// <summary>True: a read of the list of boards is reset (RST) and never answered, as a connection a proxy drops is (errata G63, LOB52).</summary>
    internal bool ResetBoardListReads { get; set; }

    /// <summary>True: the token endpoint is read and never answered.</summary>
    internal bool StallToken { get; set; }

    /// <summary>Every request read, as "METHOD path".</summary>
    internal ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>The body of every <c>POST /v1/posts</c> read.</summary>
    internal ConcurrentQueue<string> Submissions { get; } = new();

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _open.Add(client);
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A stub connection that fails is the client's business; the stub only records what it read.")]
    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            var (method, path, body) = await ReadRequestAsync(stream).ConfigureAwait(false);
            Requests.Enqueue($"{method} {path}");

            if (method == "POST" && path == "/oauth/token")
            {
                if (StallToken) await Task.Delay(Timeout.Infinite, _stop.Token).ConfigureAwait(false);

                await RespondAsync(stream, 200, "application/json", Token).ConfigureAwait(false);
                client.Close();
                return;
            }

            if (method == "POST" && path == "/v1/posts")
            {
                Submissions.Enqueue(body);
                switch (Behaviour)
                {
                    case UnheardBehaviour.Stall:
                        await Task.Delay(Timeout.Infinite, _stop.Token).ConfigureAwait(false);
                        break;
                    case UnheardBehaviour.Fin:
                        client.Close();
                        break;
                    default:
                        client.LingerState = new LingerOption(true, 0);
                        client.Close();
                        break;
                }

                return;
            }

            if (StallReads) await Task.Delay(Timeout.Infinite, _stop.Token).ConfigureAwait(false);

            // Errata G63 (R11.88): this stub serves no list of boards, so it is an older Forum to the board check, and every client test that drives ask over it takes the
            // NOT STATED branch. The branch must not be passed through unasserted (trap 192): a test that read the list here must say so with ExpectOlderForumLine and end with AssertOlderForumAsserted.
            if (method == "GET" && (path == "/v1/boards" || path.StartsWith("/v1/boards?", StringComparison.Ordinal)))
            {
                // A reset is not an older Forum's answer, so it is not a read the tripwire counts.
                if (ResetBoardListReads)
                {
                    client.LingerState = new LingerOption(true, 0);
                    client.Close();
                    return;
                }

                Interlocked.Increment(ref _boardListReads);
            }

            await RespondAsync(stream, 404, "application/problem+json", """{"type":"curia/request/no-route","title":"No route","detail":null}""").ConfigureAwait(false);
            client.Close();
        }
        catch (Exception)
        {
            client.Close();
        }
    }

    private static async Task<(string Method, string Path, string Body)> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new List<byte>();
        var one = new byte[1];
        var head = string.Empty;
        while (!head.EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one).ConfigureAwait(false) == 0) throw new IOException("closed before the request head ended");
            buffer.Add(one[0]);
            head = Encoding.ASCII.GetString([.. buffer]);
        }

        var lines = head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var first = lines[0].Split(' ');
        var length = lines.Skip(1)
            .Select(l => l.Split(':', 2))
            .Where(p => p.Length == 2 && p[0].Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            .Select(p => int.Parse(p[1].Trim(), System.Globalization.CultureInfo.InvariantCulture))
            .FirstOrDefault();
        var body = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = await stream.ReadAsync(body.AsMemory(read)).ConfigureAwait(false);
            if (n == 0) throw new IOException("closed before the request body ended");
            read += n;
        }

        return (first[0], first[1], Encoding.UTF8.GetString(body));
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {status} X\r\nContent-Type: {contentType}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head)).ConfigureAwait(false);
        await stream.WriteAsync(bytes).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Asserts that <paramref name="output"/> (what a command that read this stub's list of boards printed) holds the line that says the Forum has no list of boards and the board was not checked
    /// (R10.98's third state), and records that the test did, for the reads made so far: a read made after this call is not covered by it (Amendment 2, ruling 31).
    /// </summary>
    internal void ExpectOlderForumLine(string output)
    {
        ArgumentNullException.ThrowIfNull(output);

        var folded = System.Text.RegularExpressions.Regex.Replace(output, @"\s+", " ");
        if (!folded.Contains("NOT STATED (this Forum has no list of boards, so the board was not checked)", StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException("a command ran over the raw stub, which is an older Forum to the board check, and printed no NOT STATED board line: " + folded);

        Volatile.Write(ref _assertedReads, Volatile.Read(ref _boardListReads));
    }

    /// <summary>
    /// The tripwire (trap 192, trap 199): fails when a list read was answered as an older Forum's route and a later read was never asserted with <see cref="ExpectOlderForumLine"/>. Called explicitly, at the end
    /// of the test's body, so that it is reached only when the body did not throw and a test that failed for its own reason reports that reason (<see cref="Dispose"/> throws nothing).
    /// </summary>
    internal void AssertOlderForumAsserted()
    {
        var reads = Volatile.Read(ref _boardListReads);
        var asserted = Volatile.Read(ref _assertedReads);
        if (reads > asserted)
            throw new Xunit.Sdk.XunitException($"the raw stub answered {reads} read(s) of GET /v1/boards as an older Forum's route, and the test asserted the NOT STATED line for {asserted} of them (ExpectOlderForumLine): the rest passed through the older-Forum branch unasserted");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _stop.Cancel();
        _listener.Stop();
        _listener.Dispose();
        foreach (var client in _open) client.Close();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The loop ends by its token or its listener closing; either is how it stops.
        }

        _stop.Dispose();
        GC.SuppressFinalize(this);
    }
}
