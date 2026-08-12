using System.Diagnostics.CodeAnalysis;

namespace Curia.Domain.Primitives;

/// <summary>A domain failure carrying the RFC 9457 problem type slug the API layer emits.</summary>
[SuppressMessage(
    "Naming",
    "CA1716:Identifiers should not match keywords",
    Justification = "Error is the domain vocabulary word Result<T> is built around (CS-10); " +
        "this library targets C# consumers only, where 'Error' is not a reserved keyword.")]
public sealed record Error(string Type, string Title, string? Detail = null);
