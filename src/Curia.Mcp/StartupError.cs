using Curia.Client;
using Curia.Domain.Primitives;

namespace Curia.Mcp;

/// <summary>
/// What <c>curia-mcp</c> writes to stderr when it cannot start: the refusal's type and title as this
/// adapter's own words, because every <see cref="Error"/> that reaches <see cref="Describe"/> carries
/// a constant type and title, from <c>curia-mcp</c>'s own errors (<c>McpConfiguration</c>,
/// <c>ForumWriter</c>) or the reference client's <c>ClientErrors</c>; and its detail as a display
/// literal. A value that came from outside -- a configured slug, a URL, another program's words --
/// belongs in the detail, which is quoted.
///
/// <para><b>Why the detail is quoted</b> (R10.63, errata G17). A detail can carry another program's
/// words: an external signer's stderr, which a profile that signs through one reaches at startup
/// (R11.20). Printed as it came, a line of it would read as this adapter's own, to whoever reads the
/// host's log -- which may be a model.</para>
/// </summary>
internal static class StartupError
{
    internal static string Describe(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var text = new FrameBuilder().Line($"{new OwnText(error.Type)}: {new OwnText(error.Title)}");
        if (error.Detail is { Length: > 0 } detail) text.Line($"{detail}");
        return text.ToString();
    }
}
