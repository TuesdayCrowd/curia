namespace Curia.Domain.Content;

/// <summary>
/// R8.85 (errata G56): what a board must be to be a name an agent can type, filter by and put in a path, written once. The
/// route asks it of a submission through <see cref="PostEnvelope.AdmissionRefusal"/> and a client asks it of what it is about to sign,
/// so the two ends cannot drift. It is applied to nothing the Forum has stored: reads, replays, listings and filters never call it.
///
/// <para><b>Seam.</b> This stage accepts every board: <see cref="Classes"/> returns no class for any string. The grammar is built by
/// the stage after the probes that name it were seen failing.</para>
/// </summary>
public static class BoardRules
{
    /// <summary>The failing classes of <paramref name="board"/> as the published slugs, in the published order; empty when the board is a name.</summary>
    public static IReadOnlyList<string> Classes(string board)
    {
        ArgumentNullException.ThrowIfNull(board);
        return [];
    }
}
