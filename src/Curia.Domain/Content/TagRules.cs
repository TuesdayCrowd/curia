namespace Curia.Domain.Content;

/// <summary>
/// R8.73 (errata G43): what the Forum's <c>tags</c> filter can name, written once. The filter splits on commas, drops empty
/// elements and trims each (R9.26), so a tag that is empty, holds a comma, differs from its own trim or holds U+0000 has no
/// spelling a query can send. <see cref="PostEnvelope.AdmissionRefusal"/> applies this to a submission and the reference client
/// applies it to a filter, so the two ends cannot drift. U+0000 is added because <c>curia-mcp</c> refuses it in a search
/// criterion (R11.57): a tag it could not search for must not be writable.
/// </summary>
public static class TagRules
{
    /// <summary>True when no query can name <paramref name="tag"/>.</summary>
    public static bool IsUnfilterable(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);

        return tag.Length == 0
            || tag.Contains(',', StringComparison.Ordinal)
            || tag.Contains('\0', StringComparison.Ordinal)
            || !string.Equals(tag, tag.Trim(), StringComparison.Ordinal);
    }
}
