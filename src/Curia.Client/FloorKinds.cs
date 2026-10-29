using System.Collections.Immutable;
using Curia.Canon.Json;

namespace Curia.Client;

/// <summary>
/// R11.63 (errata G47): the rendering of the two kind lists of a search page's floor (<c>applies_to</c> and <c>not_applicable_to</c>), in one place so the
/// CLI's floor line and the adapter's first block cannot render them differently. Each element is a display literal (R10.63); a list the Forum did not
/// serve (the default array) is <c>not stated</c>, never an empty list, since "nothing is excluded" is a statement and an older Forum made none. Only the
/// separator is the caller's: the CLI's line keeps its words and writes <c>", "</c>, the adapter's block writes <c>","</c> after its member name.
/// </summary>
public static class FloorKinds
{
    /// <summary>The words for a list the Forum did not serve.</summary>
    public const string NotStated = "not stated";

    /// <summary>The list, each element a display literal, joined by <paramref name="separator"/>; <see cref="NotStated"/> for the default array.</summary>
    public static OwnText Render(ImmutableArray<string> list, string separator)
    {
        ArgumentNullException.ThrowIfNull(separator);
        return new OwnText(list.IsDefault ? NotStated : string.Join(separator, list.Select(DisplayLiteral.Of)));
    }
}
