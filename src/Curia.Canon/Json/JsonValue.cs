using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Curia.Canon.Json;

/// <summary>
/// An immutable JSON value tree. Closed to this assembly by construction (CS-11):
/// a new case breaks every exhaustive switch at compile time.
/// </summary>
public abstract record JsonValue
{
    private protected JsonValue() { }

    /// <summary>Members are held in source order; canonicalization sorts them (R6.8).</summary>
    [SuppressMessage(
        "Design",
        "CA1034:Nested types should not be visible",
        Justification = "The cases of a closed hierarchy (CS-11) belong nested under JsonValue: they " +
            "have no meaning standing alone, and private protected construction already confines the " +
            "hierarchy to this assembly's exhaustive switches.")]
    [SuppressMessage(
        "Naming",
        "CA1716:Identifiers should not match keywords",
        Justification = "Object is the ECMA-404/JSON term this case represents, matching the case name " +
            "Task 4's interface spec requires; this library targets C# consumers only, where 'object' " +
            "used unqualified as JsonValue.Object is not ambiguous with the keyword.")]
    [SuppressMessage(
        "Naming",
        "CA1720:Identifier contains type name",
        Justification = "See JsonValue.Object's CA1716 justification: the name is JSON vocabulary, not a " +
            "reference to System.Object, and is fixed by Task 4's interface spec.")]
    public sealed record Object(ImmutableArray<KeyValuePair<string, JsonValue>> Members) : JsonValue;

    [SuppressMessage(
        "Design",
        "CA1034:Nested types should not be visible",
        Justification = "See JsonValue.Object's CA1034 justification.")]
    public sealed record Array(ImmutableArray<JsonValue> Items) : JsonValue;

    [SuppressMessage(
        "Design",
        "CA1034:Nested types should not be visible",
        Justification = "See JsonValue.Object's CA1034 justification.")]
    [SuppressMessage(
        "Naming",
        "CA1716:Identifiers should not match keywords",
        Justification = "See JsonValue.Object's CA1716 justification.")]
    [SuppressMessage(
        "Naming",
        "CA1720:Identifier contains type name",
        Justification = "See JsonValue.Object's CA1720 justification.")]
    public sealed record String(string Value) : JsonValue;

    [SuppressMessage(
        "Design",
        "CA1034:Nested types should not be visible",
        Justification = "See JsonValue.Object's CA1034 justification.")]
    public sealed record Number(double Value) : JsonValue;

    [SuppressMessage(
        "Design",
        "CA1034:Nested types should not be visible",
        Justification = "See JsonValue.Object's CA1034 justification.")]
    public sealed record Bool(bool Value) : JsonValue;

    [SuppressMessage(
        "Design",
        "CA1034:Nested types should not be visible",
        Justification = "See JsonValue.Object's CA1034 justification.")]
    public sealed record Null : JsonValue
    {
        public static readonly Null Instance = new();
    }
}
