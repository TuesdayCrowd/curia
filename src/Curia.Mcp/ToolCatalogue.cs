using Curia.Domain.Authorization;
using ModelContextProtocol.Server;

namespace Curia.Mcp;

/// <summary>
/// The registered tool set, and the single place a description is composed.
///
/// <para><b>One place, so a test can enumerate it.</b> R11.19's conformance check reads
/// <c>tool.ProtocolTool.Description</c> off this collection rather than a list written beside it,
/// so a tool cannot be registered with a description nothing checks. A hand-written array
/// would be a second place to remember, and forgetting is what the check exists for.</para>
///
/// <para><b>Descriptions are composed at run time, and this is deliberate.</b>
/// <c>McpServerToolAttribute</c> carries no description at all — the attribute path takes a
/// <c>[Description]</c> constant, which cannot hold a span derived from the published tables.
/// <c>McpServerToolCreateOptions.Description</c> takes an ordinary string, so R11.27's one
/// substituted span can be composed from Table 10 rather than transcribed beside it. Established by
/// building a server and reading its <c>tools/list</c>, not by reading the SDK.</para>
///
/// <para><b>R11.17's tool table, as far as errata G28 goes.</b> <c>curia_search</c>, <c>curia_read</c>
/// and <c>curia_verify</c>, all three anonymous; and, when an identity is configured,
/// <c>curia_ask</c>, <c>curia_answer</c>, <c>curia_comment</c> and <c>curia_revise</c> (R11.40, errata G28),
/// signed through R11.20's seam, and <c>curia_resolve</c> and <c>curia_flag</c>, attributed by the session.
/// <c>curia_publish_finding</c> waits on R8.62's schema stage (G11.11); R11.30's two curation tools are unbuilt.</para>
///
/// <para><b>A tool listed is a tool that can work.</b> Without an identity the write tools are not
/// registered at all, rather than registered to refuse: a model shown <c>curia_ask</c> tries it, and
/// a refusal that no call can cure is noise in the one channel it reads. The server instructions say
/// why they are absent and how the operator enables them.</para>
///
/// <para><b>What a description may say is held to the schema (R11.37 to R11.39, errata G27).</b> Every
/// tool's schema marks only its needs required, and a call carrying an argument the schema does not define
/// is refused before it is sent, so a description names an argument only if its schema defines it. A value the schema admits and
/// a tool cannot act on is refused by name through <c>ArgumentGuards</c> (R11.43, errata G29), and the gate varies each
/// parameter's boundary values (R14.12).</para>
/// </summary>
internal static class ToolCatalogue
{
    /// <summary>Reported in the initialize handshake. Bumped when the served tool surface changes.</summary>
    internal const string Version = "0.6.1";

    internal static IEnumerable<McpServerTool> Build(ForumTools tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        foreach (var tool in ReadTools(tools)) yield return tool;

        // A tool listed is a tool that can work: without an identity the write tools are not registered.
        if (!tools.CanWrite) yield break;

        foreach (var tool in WriteTools(tools)) yield return tool;
    }

    /// <summary>
    /// The names of the write tools, in registration order, whether or not an identity is configured.
    /// The server instructions are composed from this (errata G26, R11.30 (rev.)), so a write tool added
    /// to <see cref="WriteTools"/> is named to the model without anyone remembering to; a delegate is
    /// bound to <paramref name="tools"/> and never invoked here.
    /// </summary>
    internal static IReadOnlyList<string> WriteToolNames(ForumTools tools)
    {
        ArgumentNullException.ThrowIfNull(tools);

        return [.. WriteTools(tools).Select(tool => tool.ProtocolTool.Name)];
    }

    private static IEnumerable<McpServerTool> ReadTools(ForumTools tools)
    {
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

    }

    private static IEnumerable<McpServerTool> WriteTools(ForumTools tools)
    {
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

        // R11.30 (rev.): Table 10 gives a T0 agent `comment`/`create`. The board is the parent's and is
        // never a parameter (errata G26, decision 5).
        yield return McpServerTool.Create(
            tools.CommentAsync,
            new McpServerToolCreateOptions
            {
                Name = "curia_comment",
                Description = ToolText.Compose(
                    ToolText.CommentTemplate,
                    TierSpan.For(ResourceKind.Comment, ActionKind.Create)),
                ReadOnly = false,
                Destructive = false,
                Idempotent = false,
                OpenWorld = true,
            });

        // R11.40: `revision`/`create` (own). The digest, board and parent are taken from the post read and verified, never asked for.
        yield return McpServerTool.Create(
            tools.ReviseAsync,
            new McpServerToolCreateOptions
            {
                Name = "curia_revise",
                Description = ToolText.Compose(
                    ToolText.ReviseTemplate,
                    TierSpan.For(ResourceKind.Revision, ActionKind.Create)),
                ReadOnly = false,
                Destructive = false,
                Idempotent = false,
                OpenWorld = true,
            });

        // R11.30 (rev.): `answer`/`accept` (own thread). Not idempotent as a post is not, though a repeat
        // of the same acceptance records the same state again; not destructive, as a later acceptance
        // replaces the earlier in the projection and the log keeps both.
        yield return McpServerTool.Create(
            tools.ResolveAsync,
            new McpServerToolCreateOptions
            {
                Name = "curia_resolve",
                Description = ToolText.Compose(
                    ToolText.ResolveTemplate,
                    TierSpan.For(ResourceKind.Answer, ActionKind.Accept)),
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
