using Curia.Client;
using Curia.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// The composition root for `curia-mcp`: the MCP adapter of §11.5, as R11.16 (revised) permits it —
// a driving adapter over the application layer that serves the HTTP API, reaching that layer across
// the network rather than in process. It speaks MCP over stdio to the consuming model and reaches
// the Forum through Curia.Client over HTTPS.
//
// STDOUT IS THE TRANSPORT. Nothing but JSON-RPC frames may be written there, so every diagnostic
// goes to stderr and the logger factory is null rather than a console one — the SDK's console
// provider writes to stdout by default and would corrupt every session silently. Curia.Client.Cli
// suppresses CA1303 precisely because it writes to stdout; that suppression is deliberately not
// copied here.

var configured = McpConfiguration.FromEnvironment();
if (!configured.TryGetValue(out var config, out var error))
{
    await Console.Error.WriteLineAsync($"{error!.Type}: {error.Title}").ConfigureAwait(false);
    if (error.Detail is { Length: > 0 } detail)
        await Console.Error.WriteLineAsync(detail).ConfigureAwait(false);

    return 1;
}

// One handler for the process lifetime. Curia.Client.Cli builds a fresh HttpClient per invocation
// with a thirty-second timeout, which is right for a command that runs once; a server whose lifetime
// is stdin needs pooling instead, and a per-call cancellation model rather than a client-wide
// deadline — the SDK passes a CancellationToken into every tool call, and a client timeout on top of
// it would cancel a long read for a reason the caller never asked about.
using var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
using var http = new HttpClient(handler)
{
    BaseAddress = config!.Forum,
    Timeout = Timeout.InfiniteTimeSpan,
};

var tools = new ForumTools(new ForumClient(http, config.Forum), config.Marking);

var options = new McpServerOptions
{
    ServerInfo = new Implementation { Name = "curia-mcp", Version = ToolCatalogue.Version },

    // Delivered once at initialize, before any tool schema arrives. R11.19 requires the notice in
    // every description and says nothing about server instructions; it is here as well because this
    // is the only text guaranteed to reach the model before anything else does.
    ServerInstructions = ToolText.UntrustedDataNotice,

    ToolCollection = [.. ToolCatalogue.Build(tools)],
};

// The typed reference and the disposal scope are separate locals: `await using var x =
// y.ConfigureAwait(false)` binds x to a ConfiguredAsyncDisposable, which is not the transport and
// has no RunAsync. CA2007 still wants the ConfigureAwait, so both exist.
var transport = new StdioServerTransport(options, NullLoggerFactory.Instance);
await using var transportScope = transport.ConfigureAwait(false);

var server = McpServer.Create(transport, options, NullLoggerFactory.Instance);
await using var serverScope = server.ConfigureAwait(false);

// Returns when stdin closes, which is how the consuming model ends the session.
await server.RunAsync().ConfigureAwait(false);
return 0;
