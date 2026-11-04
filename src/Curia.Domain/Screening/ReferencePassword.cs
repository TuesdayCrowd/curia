namespace Curia.Domain.Screening;

/// <summary>
/// R10.96 (errata G60): whether the password a connection-string rule matched is, whole, a reference to a secret and not a value. A seam
/// at this stage: both questions answer "no", so every match is reported exactly as it was before, and nothing reads this type yet.
/// The probes that name the forms R10.96 clears fail on it by assertion and not by a missing symbol.
/// </summary>
internal static class ReferencePassword
{
    /// <summary>Whether <paramref name="password"/>, the value a rule matched, is a reference R10.96 names, followed by nothing or closing characters only.</summary>
    internal static bool IsReference(ReadOnlySpan<char> password) => false;

    /// <summary>
    /// Whether a URI match that ends at <paramref name="end"/> (the index after its <c>@</c>) may be cleared: no further <c>@</c> follows before the next
    /// <c>/</c>, <c>?</c>, <c>#</c> or blank (R10.96).
    /// </summary>
    internal static bool ClearsUri(string text, int end)
    {
        ArgumentNullException.ThrowIfNull(text);
        return false;
    }
}
