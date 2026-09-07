using Curia.Domain.Authorization;
using ModelContextProtocol.Server;

namespace Curia.Mcp;

/// <summary>
/// The registered tool set, and the single place a description is composed.
///
/// <para><b>One place, so a test can enumerate it.</b> R11.19's conformance check reads
/// <c>tool.ProtocolTool.Description</c> off this collection rather than a list written beside it,
/// so an eighth tool cannot be registered with a description nothing checks. A hand-written array
/// would be a second place to remember, and forgetting is what the check exists for.</para>
///
/// <para><b>Descriptions are composed at run time, and this is deliberate.</b>
/// <c>McpServerToolAttribute</c> carries no description at all — the attribute path takes a
/// <c>[Description]</c> constant, which cannot hold a span derived from the published tables.
/// <c>McpServerToolCreateOptions.Description</c> takes an ordinary string, so R11.27's one
/// substituted span can be composed from Table 10 rather than transcribed beside it. Established by
/// building a server and reading its <c>tools/list</c>, not by reading the SDK.</para>
///
/// <para><b>R11.17's tool table, as far as Stage 2 goes.</b> <c>curia_search</c> and
/// <c>curia_read</c>, both anonymous. <c>curia_verify</c> is Stage 3 — it needs the inclusion-proof
/// check the reference client has never learned (plan defect D9) — and the four write tools are
/// Stage 4, behind R11.20's signer seam, which nothing in this tree can satisfy yet.</para>
/// </summary>
internal static class ToolCatalogue
{
    /// <summary>Reported in the initialize handshake. Bumped when the served tool surface changes.</summary>
    internal const string Version = "0.1.0";

    internal static IEnumerable<McpServerTool> Build(ForumTools tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        yield return McpServerTool.Create(
            tools.SearchAsync,
            new McpServerToolCreateOptions
            {
                Name = "curia_search",
                Description = ToolText.Compose(
                    ToolText.SearchTemplate,
                    TierSpan.For(ResourceKind.Thread, ActionKind.Search)),
                ReadOnly = true,
                OpenWorld = true,
            });

        yield return McpServerTool.Create(
            tools.ReadAsync,
            new McpServerToolCreateOptions
            {
                Name = "curia_read",
                Description = ToolText.Compose(
                    ToolText.ReadTemplate,
                    TierSpan.For(ResourceKind.Thread, ActionKind.Read)),
                ReadOnly = true,
                OpenWorld = true,
            });
    }
}
