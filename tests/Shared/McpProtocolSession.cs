using System.IO.Pipelines;
using System.Text;
using Curia.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Curia.Tests.Shared;

/// <summary>
/// What a tool answered: the text a model reads, the same text block by block, whether the protocol marked it an
/// error, and per block the URI of an embedded resource (null for a plain text block, so the adapter's own words
/// are told from a passage).
/// </summary>
internal readonly record struct McpAnswer(bool IsError, string Text, IReadOnlyList<string> Blocks, IReadOnlyList<string?> Uris);

/// <summary>
/// R14.11 (errata G27): a real MCP client and a real server over two pipes, the server built by
/// <see cref="ServerComposition.Options"/> so what a fact drives is what the host runs (ruling 1). Shared by
/// <c>Curia.Mcp.Tests</c> (a stub Forum) and <c>Curia.Api.Tests</c> (the real one); the caller builds the tools.
/// </summary>
internal sealed class McpProtocolSession : IAsyncDisposable
{
    private readonly McpClient _client;
    private readonly McpServer _server;
    private readonly StreamServerTransport _transport;
    private readonly IDisposable? _owned;
    private readonly Task _running;

    private McpProtocolSession(McpClient client, McpServer server, StreamServerTransport transport, IDisposable? owned, Task running)
    {
        _client = client;
        _server = server;
        _transport = transport;
        _owned = owned;
        _running = running;
    }

    /// <param name="tools">The tools the server serves.</param>
    /// <param name="agentId">The agent the server instructions name, or null for a read-only session.</param>
    /// <param name="owned">Disposed with the session (the enrolled agent, where the caller has no better owner).</param>
    internal static async Task<McpProtocolSession> StartAsync(ForumTools tools, string? agentId, IDisposable? owned = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var options = ServerComposition.Options(tools, agentId);

        var toServer = new Pipe();
        var toClient = new Pipe();
        var transport = new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream(), "curia-mcp-protocol-test", NullLoggerFactory.Instance);
        var server = McpServer.Create(transport, options, NullLoggerFactory.Instance);
        var running = server.RunAsync(ct);

        var client = await McpClient.CreateAsync(
            new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream(), NullLoggerFactory.Instance),
            new McpClientOptions { ClientInfo = new Implementation { Name = "curia-protocol-gate", Version = "0" } },
            NullLoggerFactory.Instance,
            ct);

        return new McpProtocolSession(client, server, transport, owned, running);
    }

    internal async Task<IReadOnlyList<Tool>> ListAsync() =>
        [.. (await _client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken)).Select(t => t.ProtocolTool)];

    internal async Task<CallToolResult> CallRawAsync(string tool, Dictionary<string, object?> arguments) =>
        await _client.CallToolAsync(tool, arguments, cancellationToken: TestContext.Current.CancellationToken);

    internal async Task<McpAnswer> CallAsync(string tool, Dictionary<string, object?> arguments)
    {
        var result = await _client.CallToolAsync(tool, arguments, cancellationToken: TestContext.Current.CancellationToken);
        var blocks = new List<string>();
        var uris = new List<string?>();
        foreach (var block in result.Content)
        {
            switch (block)
            {
                case TextContentBlock t:
                    blocks.Add(t.Text);
                    uris.Add(null);
                    break;
                case EmbeddedResourceBlock { Resource: TextResourceContents resource }:
                    blocks.Add(resource.Text);
                    uris.Add(resource.Uri);
                    break;
                default:
                    break;
            }
        }

        var text = new StringBuilder();
        foreach (var block in blocks) text.AppendLine(block);
        return new McpAnswer(result.IsError == true, text.ToString().TrimEnd(), blocks, uris);
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
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

        _owned?.Dispose();
    }
}
