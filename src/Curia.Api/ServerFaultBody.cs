using Curia.Application.Credentials;
using Curia.Domain.Primitives;

namespace Curia.Api;

/// <summary>
/// The members of a 5xx problem document, in the order they are written: <c>type</c>, <c>title</c> and a null
/// <c>detail</c> (R5.12, R11.33; errata G17). Public, pure and a type of its own, so that a fact can drive it
/// without a new <c>InternalsVisibleTo</c> (CS-5): <see cref="ServerFault"/> is internal and composes its body here.
///
/// <para>A fourth member is written for one problem type alone, by a requirement that names it (R11.33 (add.),
/// R4.44); this type is where that rule is held, and nowhere else.</para>
/// </summary>
public static class ServerFaultBody
{
    /// <summary>The members of the body for <paramref name="error"/>, as the writer serializes them.</summary>
    public static IReadOnlyDictionary<string, object?> Of(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var members = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = error.Type,
            ["title"] = error.Title,
            ["detail"] = null,
        };

        // R4.44, R11.33 (add.): the one member, on the one type, and only as a whole number of at least 0.
        if (error.Type == KeyActErrors.NotHonouredType && error.LogIndex is >= 0 and var index)
        {
            members["log_index"] = index;
        }

        return members;
    }
}
