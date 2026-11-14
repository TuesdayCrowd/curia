using System.Globalization;
using Curia.Domain.Primitives;

namespace Curia.Application.Credentials;

/// <summary>
/// The refusals of an operator act (errata G70, R11.97), each by name. Not <see cref="KeyActErrors"/>: those are key acts', and a reflecting test reads that type's constants alone.
/// None names a statement, a signature or a payload member.
/// </summary>
public static class ActErrors
{
    /// <summary>
    /// R11.97: the act row was appended and the Forum skips it. The writer says so and never reports success for it. A fault of the Forum (its writer and its fold disagree, or a row raced it), so
    /// no route answers it and it is deliberately not in <c>StatusForKeyAct</c>: an unlisted type answers 500.
    /// </summary>
    public const string NotHonouredType = "curia/acts/not-honoured";

    /// <summary>
    /// The row was appended at <paramref name="index"/> and the Forum skips it, by <paramref name="predicate"/> (R6.67). It is in the log, and acts on nothing. The detail names the index and the
    /// predicate and never a payload member.
    /// </summary>
    public static Error NotHonoured(long index, string predicate) => new(
        NotHonouredType,
        "The Forum does not honour the operator-act row it recorded",
        $"the row was appended at log index {index.ToString(CultureInfo.InvariantCulture)} and the Forum skips it ({predicate}); it acts on nothing");
}
