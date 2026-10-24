using Curia.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Curia.Mcp;

/// <summary>
/// The one place the MCP server is composed (errata G27, R14.11): <c>Program.cs</c> and every test
/// harness take their <see cref="McpServerOptions"/> from <see cref="Options"/>, so a test that
/// drives the server drives what a host runs.
///
/// <para>It registers the one request filter (R11.37): before any tool runs, the call is checked
/// against the input schema that tool published, and a call that does not match is answered here
/// with a refusal naming each fault and saying nothing was sent. One rule closes every tool at every
/// depth, which a binder setting or an extension-data bag on each parameter type would not, and the
/// binder's own exception reaches the model only as the SDK's generic text.</para>
/// </summary>
internal static class ServerComposition
{
    /// <summary>The fixed opening of a contract refusal (R11.37). A tool's own <c>NOT SENT:</c> refusals do not begin with it.</summary>
    internal const string ContractRefusal = "NOT SENT: this call does not match";

    private const string SearchTool = "curia_search";

    /// <summary>
    /// The server a host runs over <paramref name="tools"/>: its identity, the instructions naming the
    /// agent it writes as, the tool collection, and the schema filter. The only method that builds
    /// <see cref="McpServerOptions"/> (a fact in <c>Curia.Architecture.Tests</c> holds it).
    /// </summary>
    internal static McpServerOptions Options(ForumTools tools, string? agentId)
    {
        var collection = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var tool in ToolCatalogue.Build(tools)) collection.Add(tool);

        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = "curia-mcp", Version = ToolCatalogue.Version },

            // Delivered once at initialize, before any tool schema arrives. R11.19 requires the notice in
            // every description and says nothing about server instructions; it is here as well because this
            // is the only text guaranteed to reach the model before anything else does -- and so is whose
            // name the write tools act in, or why there are none.
            ServerInstructions = ToolText.ServerInstructions(agentId, ToolCatalogue.WriteToolNames(tools)),

            ToolCollection = collection,
        };

        options.Filters.Request.CallToolFilters.Add(next => (context, cancellationToken) =>
        {
            var refusal = Refusal(collection, context.Params);
            return refusal is null ? next(context, cancellationToken) : ValueTask.FromResult(refusal);
        });

        return options;
    }

    /// <summary>
    /// The refusal for a call that does not match the input schema of the registered tool it names, or
    /// null when it does or names no tool of ours (the SDK answers that one).
    /// </summary>
    private static CallToolResult? Refusal(McpServerPrimitiveCollection<McpServerTool> collection, CallToolRequestParams? call)
    {
        if (call is null || !collection.TryGetPrimitive(call.Name, out var tool) || tool is null) return null;

        // Only curia_search takes the criteria record R9.26's list is about; a member of that name on
        // another tool is a plain unknown one.
        Func<string, string?>? reasonFor = string.Equals(call.Name, SearchTool, StringComparison.Ordinal)
            ? member => SearchCriteria.Refusable.TryGetValue(member, out var waits) ? waits : null
            : null;

        var faults = ArgumentContract.Check(tool.ProtocolTool.InputSchema, call.Arguments, reasonFor);
        if (faults.IsEmpty) return null;

        // A path is the caller's text, so it is a display literal like any value this adapter did not
        // write (R10.63); the schema's own words (a tool name, its members and types) are ours.
        var text = new FrameBuilder()
            .Line($"{new OwnText(ContractRefusal)} {new OwnText(call.Name)}'s input schema")
            .Line("Nothing was sent to the Forum. Send the call again as the schema describes, naming each problem below.");

        foreach (var fault in faults)
        {
            switch (fault.Kind)
            {
                case ArgumentFaultKind.Unknown:
                    text.Line($"  {fault.Path}: not a member of the schema here; it defines {new OwnText(fault.Expected.Length == 0 ? "no members" : fault.Expected)}");
                    break;
                case ArgumentFaultKind.Missing:
                    text.Line($"  {fault.Path}: required by the schema and absent");
                    break;
                case ArgumentFaultKind.WrongType:
                    text.Line($"  {fault.Path}: not a type the schema admits here; it admits {new OwnText(fault.Expected)}");
                    break;
                default:
                    throw new InvalidOperationException($"Unhandled fault kind {fault.Kind}.");
            }

            if (fault.Reason is { Length: > 0 } reason)
                text.Line($"    {fault.Path}: {new OwnText(reason)}");
        }

        return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = text.ToString() }] };
    }
}
