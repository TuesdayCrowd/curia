using System.Globalization;
using System.Text;
using Curia.Domain.Primitives;

namespace Curia.Application.Credentials;

/// <summary>
/// R4.37 for the rotation use case, so a <c>kid</c> that enrollment refuses cannot be bound by a rotation.
/// The same category test as the enrollment route's <c>ControlCharacter</c> (Curia.Api), which cannot be
/// shared: the route asks it of raw strings before any boundary type exists, and a public string-taking
/// method here would be a new row for the R11.34 fence, which this task adds none of beyond its two
/// constructors. Names the field and never echoes the value.
/// </summary>
internal static class IdentifierRules
{
    /// <summary>
    /// The refusal for an identifier holding a character of general category Cc, Cf, Zl or Zp, or null
    /// (R4.37). Walks scalar values, so a format character beyond the Basic Multilingual Plane is found as
    /// itself rather than as two surrogate halves. Asked after the text is well-formed. Only these four
    /// categories are refused (errata G17, "What this costs" 5).
    /// </summary>
    internal static Error? ControlCharacter(string value, string field)
    {
        ArgumentNullException.ThrowIfNull(value);

        foreach (var rune in value.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            var name = category == UnicodeCategory.Control ? "Cc"
                : category == UnicodeCategory.Format ? "Cf"
                : category == UnicodeCategory.LineSeparator ? "Zl"
                : category == UnicodeCategory.ParagraphSeparator ? "Zp"
                : null;

            if (name is not null)
                return EnrollmentErrors.IdentifierControlCharacter(field, rune.Value, name);
        }

        return null;
    }
}
