using Curia.Client;
using Curia.Domain.Primitives;

namespace Curia.Mcp;

/// <summary>
/// What <c>curia-mcp</c> writes to stderr when it cannot start: the refusal's slug and title, which
/// are this adapter's and the reference client's own, and its detail as a display literal.
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
