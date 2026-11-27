using System.Net.Sockets;
using System.Text;

namespace Curia.Api.Tests;

/// <summary>A request written to a socket by hand: for the probes whose request no <c>HttpClient</c> will send (a declared length the body does not reach), and whose answer is read before the body is finished.</summary>
internal static class RawHttp
{
    internal sealed record Reply(int Status, Dictionary<string, string> Headers, string Body);

    /// <summary>
    /// POST with <c>Content-Length: <paramref name="declaredLength"/></c> and only <paramref name="sent"/> bytes of body, then reads the answer to the end of the connection (or to the declared length of the answer).
    /// </summary>
    internal static async Task<Reply> PostAsync(int port, string host, string path, long declaredLength, int sent, string? contentType, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(System.Net.IPAddress.Loopback, port, ct).ConfigureAwait(false);
        var stream = tcp.GetStream();
        var head = new StringBuilder()
            .Append("POST ").Append(path).Append(" HTTP/1.1\r\nHost: ").Append(host).Append("\r\nContent-Length: ").Append(declaredLength).Append("\r\n");
        if (contentType is not null) head.Append("Content-Type: ").Append(contentType).Append("\r\n");
        head.Append("Connection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct).ConfigureAwait(false);
        if (sent > 0)
        {
            var body = new byte[sent];
            Array.Fill(body, (byte)'a');
            try
            {
                await stream.WriteAsync(body, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The host refused before reading the body and closed: the answer is still on the socket.
            }
        }

        return await ReadReplyAsync(stream, 10, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A request written to a socket by hand from its head (the request line and every header, ending in the blank line) and a body writer, so that a probe states its own framing: a declared
    /// length the body does not reach, a chunked body, a stall (a writer that returns and sends nothing more) or a malformed chunk frame. The answer is read to the end of the connection, or to
    /// <paramref name="readSeconds"/> of silence; the head carries <c>Connection: close</c> so that the host ends the connection after its answer.
    /// </summary>
    internal static async Task<Reply> SendAsync(int port, string head, Func<NetworkStream, Task> writeBody, int readSeconds, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(System.Net.IPAddress.Loopback, port, ct).ConfigureAwait(false);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
        try
        {
            await writeBody(stream).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The host answered before the body was finished and closed: the answer is still on the socket.
        }

        return await ReadReplyAsync(stream, readSeconds, ct).ConfigureAwait(false);
    }

    private static async Task<Reply> ReadReplyAsync(NetworkStream stream, int readSeconds, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(readSeconds));
        try
        {
            int n;
            while ((n = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
                await buffer.WriteAsync(chunk.AsMemory(0, n), ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // A reset after the answer: what was read is the answer.
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Silence for the whole of the read window: what was read is what the host said.
        }

        var text = Encoding.UTF8.GetString(buffer.ToArray());
        var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var headerText = split < 0 ? text : text[..split];
        var body2 = split < 0 ? string.Empty : text[(split + 4)..];
        var lines = headerText.Split("\r\n");
        var status = int.Parse(lines[0].Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0) headers[line[..colon]] = line[(colon + 1)..].Trim();
        }

        if (headers.TryGetValue("Transfer-Encoding", out var te) && te.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            body2 = DeChunk(body2);
        return new Reply(status, headers, body2);
    }

    private static string DeChunk(string body)
    {
        var sb = new StringBuilder();
        var at = 0;
        while (at < body.Length)
        {
            var eol = body.IndexOf("\r\n", at, StringComparison.Ordinal);
            if (eol < 0) break;
            var size = int.Parse(body[at..eol], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
            if (size == 0) break;
            sb.Append(body, eol + 2, Math.Min(size, body.Length - eol - 2));
            at = eol + 2 + size + 2;
        }

        return sb.ToString();
    }
}
