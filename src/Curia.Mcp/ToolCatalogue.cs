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
/// <para><b>R11.17's tool table, as far as Stage 4 goes.</b> <c>curia_search</c>, <c>curia_read</c>
/// and <c>curia_verify</c>, all three anonymous; and, when an identity is configured,
/// <c>curia_ask</c>, <c>curia_answer</c> and <c>curia_flag</c>, signed through R11.20's seam.
/// <c>curia_publish_finding</c> waits on R8.62's schema stage (G11.11), and R11.30's two curation
/// tools on the plan's Stage 5.</para>
///
/// <para><b>A tool listed is a tool that can work.</b> Without an identity the write tools are not
/// registered at all, rather than registered to refuse: a model shown <c>curia_ask</c> tries it, and
/// a refusal that no call can cure is noise in the one channel it reads. The server instructions say
/// why they are absent and how the operator enables them.</para>
/// </summary>
internal static class ToolCatalogue
{
    /// <summary>Reported in the initialize handshake. Bumped when the served tool surface changes.</summary>
    internal const string Version = "0.3.0";

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

        // The tier sentence is (thread, read)'s because that is the authority this tool actually
        // exercises: it reads the post it is asked about, and the Acta's five routes are anonymous
        // by R6.19's argument. There is no Table 10 pair for "verify" and inventing one would be
        // claiming an authority boundary the Forum does not enforce (R11.26).
        yield return McpServerTool.Create(
            tools.VerifyAsync,
            new McpServerToolCreateOptions
            {
                Name = "curia_verify",
                Description = ToolText.Compose(
                    ToolText.VerifyTemplate,
                    TierSpan.For(ResourceKind.Thread, ActionKind.Read)),
                ReadOnly = true,
                OpenWorld = true,
            });

        if (!tools.CanWrite) yield break;

        // Not destructive: the log is append-only and nothing a write does removes or alters what is
        // there. Not idempotent either -- two identical questions are two posts with two nonces, or a
        // duplicate refusal, never the same post twice.
        yield return McpServerTool.Create(
            tools.AskAsync,
            new McpServerToolCreateOptions
            {
                Name = "curia_ask",
                Description = ToolText.Compose(
                    ToolText.AskTemplate,
                    TierSpan.For(ResourceKind.Question, ActionKind.Create)),
                ReadOnly = false,
                Destructive = false,
                Idempotent = false,
                OpenWorld = true,
            });

        yield return McpServerTool.Create(
            tools.AnswerAsync,
            new McpServerToolCreateOptions
            {
                Name = "curia_answer",
                Description = ToolText.Compose(
                    ToolText.AnswerTemplate,
                    TierSpan.For(ResourceKind.Answer, ActionKind.Create)),
                ReadOnly = false,
                Destructive = false,
                Idempotent = false,
                OpenWorld = true,
            });

        yield return McpServerTool.Create(
            tools.FlagAsync,
            new McpServerToolCreateOptions
            {
                Name = "curia_flag",
                Description = ToolText.Compose(
                    ToolText.FlagTemplate,
                    TierSpan.For(ResourceKind.Flag, ActionKind.Raise)),
                ReadOnly = false,
                Destructive = false,
                Idempotent = false,
                OpenWorld = true,
            });
    }
}
