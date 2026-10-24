using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Curia.Mcp;

/// <summary>
/// The one place the MCP server is composed (errata G27, R14.11): <c>Program.cs</c> and every test
/// harness take their <see cref="McpServerOptions"/> from <see cref="Options"/>, so a test that
/// drives the server drives what a host runs.
/// </summary>
internal static class ServerComposition
{
    internal static McpServerOptions Options(ForumTools tools, string? agentId) => new()
    {
        ServerInfo = new Implementation { Name = "curia-mcp", Version = ToolCatalogue.Version },

        // Delivered once at initialize, before any tool schema arrives. R11.19 requires the notice in
        // every description and says nothing about server instructions; it is here as well because this
        // is the only text guaranteed to reach the model before anything else does -- and so is whose
        // name the write tools act in, or why there are none.
        ServerInstructions = ToolText.ServerInstructions(agentId, ToolCatalogue.WriteToolNames(tools)),

        ToolCollection = [.. ToolCatalogue.Build(tools)],
    };
}
