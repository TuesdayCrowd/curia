using System.Diagnostics.CodeAnalysis;

namespace Curia.Domain.Primitives;

/// <summary>A domain failure carrying the RFC 9457 problem type slug the API layer emits.</summary>
[SuppressMessage(
    "Naming",
    "CA1716:Identifiers should not match keywords",
    Justification = "Error is the domain vocabulary word Result<T> is built around (CS-10); " +
        "this library targets C# consumers only, where 'Error' is not a reserved keyword.")]
public sealed record Error(string Type, string Title, string? Detail = null)
{
    /// <summary>
    /// The log index of a row a refusal is about, composed by the Forum from the log it read (R4.44); read only
    /// where a requirement names it, and null for every other error. A typed value and never parsed out of
    /// <see cref="Detail"/>, so no wording of a detail is a contract.
    /// </summary>
    public long? LogIndex { get; init; }
}
