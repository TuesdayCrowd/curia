using Curia.Domain.Primitives;

namespace Curia.Client;

/// <summary>
/// R11.84 (errata G60): the one definition of the sentences a client adds about a reference to a secret. A seam at this stage: it returns nothing,
/// so no refusal and no description changes, and the probes that expect the sentences fail by assertion.
/// </summary>
public static class CredentialWording
{
    /// <summary>The sentence a credential refusal carries after the instruction to rotate, or <see langword="null"/> where the refusal carries none.</summary>
    public static string? SentenceFor(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return null;
    }
}
