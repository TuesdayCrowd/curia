using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Curia.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// A server built by <see cref="ServerComposition.Options"/> over two pipes, spoken to as stdio is: one JSON-RPC message per
/// line, written here as text and never produced by a client library. A fact that needs a frame no client would serialize
/// (a lone surrogate written as its <c>\ud800</c> escape) sends the line it means to send, and reads the line the server
/// answered, which is what a model host would be handed.
/// </summary>
internal sealed class RawFrameSession : IAsyncDisposable
{
    private readonly McpServer _server;
    private readonly StreamServerTransport _transport;
    private readonly Task _running;
    private readonly Stream _toServer;
    private readonly StreamReader _fromServer;
    private int _id;

    private RawFrameSession(McpServer server, StreamServerTransport transport, Task running, Stream toServer, StreamReader fromServer)
    {
        _server = server;
        _transport = transport;
        _running = running;
        _toServer = toServer;
        _fromServer = fromServer;
    }

    internal static async Task<RawFrameSession> StartAsync(ForumTools tools)
    {
        var ct = TestContext.Current.CancellationToken;
        var toServer = new Pipe();
        var toClient = new Pipe();
        var transport = new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream(), "curia-mcp-raw-frame-test", NullLoggerFactory.Instance);
        var server = McpServer.Create(transport, ServerComposition.Options(tools, null), NullLoggerFactory.Instance);
        var session = new RawFrameSession(server, transport, server.RunAsync(ct), toServer.Writer.AsStream(), new StreamReader(toClient.Reader.AsStream(), Encoding.UTF8));

        await session.SendAsync("""{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"raw","version":"0"}}}""");
        await session.ReadAsync();
        await session.SendAsync("""{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}""");
        return session;
    }

    private async Task SendAsync(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await _toServer.WriteAsync(bytes, TestContext.Current.CancellationToken);
        await _toServer.FlushAsync(TestContext.Current.CancellationToken);
    }

    private async Task<JsonDocument> ReadAsync()
    {
        var line = await _fromServer.ReadLineAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(line);
        return JsonDocument.Parse(line);
    }

    /// <summary>
    /// Sends <c>tools/call</c> with <paramref name="argumentsJson"/> as the text of the <c>arguments</c> member, verbatim, and
    /// returns what the model would read: the JSON-RPC error's message, or the result's text blocks, and whether it was an error.
    /// </summary>
    internal async Task<(bool IsError, string Text, string Frame)> CallAsync(string tool, string argumentsJson)
    {
        var id = ++_id;
        await SendAsync("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/call\",\"params\":{\"name\":\"" + tool + "\",\"arguments\":" + argumentsJson + "}}");
        using var answer = await ReadAsync();
        var root = answer.RootElement;
        var frame = root.GetRawText();

        if (root.TryGetProperty("error", out var error))
            return (true, error.GetProperty("message").GetString() ?? string.Empty, frame);

        var result = root.GetProperty("result");
        var text = string.Join(
            "\n",
            result.GetProperty("content").EnumerateArray().Where(b => b.TryGetProperty("text", out _)).Select(b => b.GetProperty("text").GetString()));
        return (result.TryGetProperty("isError", out var isError) && isError.GetBoolean(), text, frame);
    }

    public async ValueTask DisposeAsync()
    {
        _toServer.Close();
        await _server.DisposeAsync();
        await _transport.DisposeAsync();
        try
        {
            await _running;
        }
        catch (OperationCanceledException)
        {
            // the session ended with its client
        }

        _fromServer.Dispose();
    }
}
