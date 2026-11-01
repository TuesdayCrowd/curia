using System.Net;
using System.Net.Sockets;
using Curia.Tests.Shared;

namespace Curia.Client.Tests;

/// <summary>
/// A <see cref="StubLog"/> on a real loopback socket (errata G52, ULG55 and ULG57): every request the real <c>curia</c> command makes in a child process is handed
/// to the stub's own handler and its answer is sent back as it came, so a verb that reads a post, verifies it, signs and posts runs against the same Forum the
/// tool tests use. The stub holds the record of what it was sent (<see cref="StubLog.WrittenBodies"/>).
/// </summary>
internal sealed class StubLogListener : IDisposable
{
    private static readonly HashSet<string> NotForwarded = new(StringComparer.OrdinalIgnoreCase) { "Host", "Content-Length", "Connection", "Transfer-Encoding", "Expect", "Keep-Alive" };

    private readonly HttpListener _listener = new();
    private readonly HttpClient _upstream;
    private readonly Task _serving;

    internal StubLogListener(StubLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _upstream = log.RawClient();

        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        Address = new Uri($"http://127.0.0.1:{port}");
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _serving = Task.Run(ServeAsync);
    }

    internal Uri Address { get; }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            using var forwarded = new HttpRequestMessage(new HttpMethod(context.Request.HttpMethod), context.Request.Url!.PathAndQuery);
            using var buffer = new MemoryStream();
            await context.Request.InputStream.CopyToAsync(buffer);
            if (context.Request.HttpMethod != "GET" && context.Request.HttpMethod != "HEAD") forwarded.Content = new ByteArrayContent(buffer.ToArray());
            foreach (var name in context.Request.Headers.AllKeys.OfType<string>().Where(n => !NotForwarded.Contains(n)))
            {
                var value = context.Request.Headers[name]!;
                if (!forwarded.Headers.TryAddWithoutValidation(name, value)) forwarded.Content?.Headers.TryAddWithoutValidation(name, value);
            }

            using var answer = await _upstream.SendAsync(forwarded);
            var bytes = await answer.Content.ReadAsByteArrayAsync();
            context.Response.StatusCode = (int)answer.StatusCode;
            foreach (var header in answer.Headers.Concat(answer.Content.Headers).Where(h => !NotForwarded.Contains(h.Key)))
            {
                if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) context.Response.ContentType = string.Join(", ", header.Value);
                else context.Response.AddHeader(header.Key, string.Join(", ", header.Value));
            }

            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    public void Dispose()
    {
        _listener.Close();
        _serving.Wait(TimeSpan.FromSeconds(5));
        _upstream.Dispose();
        GC.SuppressFinalize(this);
    }
}
