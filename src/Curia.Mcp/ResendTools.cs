using Curia.Client;
using ModelContextProtocol.Protocol;

namespace Curia.Mcp;

/// <summary>
/// Errata G58, R11.79's two tools: <c>curia_resend</c> (send exactly an act this host kept) and <c>curia_acts</c> (list what it holds). <b>The seam:</b> both are registered and
/// answer <c>NOT SENT: not built</c> as an <c>isError</c> result with nothing sent; the stage that builds the kept act replaces the bodies. Neither reaches the Forum.
/// </summary>
internal sealed partial class ForumTools
{
    private const string NotBuilt = "NOT SENT: not built";

    /// <summary>R11.79's <c>curia_resend</c>. The schema (a required <c>digest</c>) is checked by R11.37's gate before this runs.</summary>
    internal Task<CallToolResult> ResendAsync(string digest, CancellationToken cancellationToken)
    {
        // R11.43: a blank or null digest is refused by name before anything else, as every identifier is; the guard is the seam's one real behaviour.
        if (ArgumentGuards.Identifier(digest, nameof(digest)) is { } refused) return Task.FromResult(refused);
        _ = Writer();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = NotBuilt }] });
    }

    /// <summary>R11.79's <c>curia_acts</c>: no argument, no Forum call.</summary>
    internal Task<CallToolResult> ActsAsync(CancellationToken cancellationToken)
    {
        _ = Writer();
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = NotBuilt }] });
    }
}
