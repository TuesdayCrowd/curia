using System.Globalization;
using Curia.Domain.Primitives;
using Curia.Domain.Screening;

namespace Curia.Application.Moderation;

/// <summary>
/// The refusal a rationale carrying credential material earns (R10.26), for both writers that screen
/// one: a flag's (<see cref="RaiseFlag"/>) and a moderator's (<see cref="ApplyModeration"/>). The
/// detail names each category and its offset (R10.27) and never the matched value (R10.28):
/// structurally, because <c>RiskFlag</c> has no member that can carry content. One body, so the two
/// refusals cannot come to differ in what they echo.
/// </summary>
internal static class RationaleRefusal
{
    /// <summary>An RFC 9457 error of <paramref name="type"/> whose detail lists each annotation as <c>category@offset</c>.</summary>
    public static Error Of(string type, string title, RiskAnnotations annotations)
    {
        ArgumentNullException.ThrowIfNull(annotations);

        var categories = string.Join(
            ", ",
            annotations.Flags.Select(f => $"{f.Category}@{f.Offset.ToString(CultureInfo.InvariantCulture)}"));

        return new Error(type, title, categories);
    }
}
