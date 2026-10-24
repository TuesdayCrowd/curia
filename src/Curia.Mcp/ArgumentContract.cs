using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace Curia.Mcp;

/// <summary>What kind of mismatch between a call and the input schema the model was shown (R11.37).</summary>
internal enum ArgumentFaultKind { Unknown, Missing, WrongType }

/// <summary>
/// One place a call does not match its schema. It never carries the received value (R9.25: the value, where
/// naming it echoes content).
/// </summary>
/// <param name="Path">Where in the arguments: <c>board</c>, <c>criteria.kinds</c>, <c>criteria.kinds[0]</c>.</param>
/// <param name="Kind">The mismatch.</param>
/// <param name="Expected">What the schema has there: for an unknown member the members it defines, for a wrong type the types it admits.</param>
/// <param name="Reason">For an unknown member the specification names, why it is not honoured (R9.26).</param>
internal readonly record struct ArgumentFault(string Path, ArgumentFaultKind Kind, string Expected, string? Reason = null);

/// <summary>
/// R11.37 (errata G27, ruling 3): a call checked against the tool's own published input schema, the one
/// the model was shown. It decides nothing about the Forum (R11.16 (rev.)): its only subject is what this
/// adapter published, so a schema the SDK derives from a signature and one written by hand are checked by
/// the same rule at every depth. It reads <c>properties</c>, <c>required</c>, <c>type</c> (a string or an
/// array of them) and <c>items</c>, and nothing else the SDK does not emit.
/// <para>It admits what the binder admits and no less: a numeric string for <c>integer</c> or
/// <c>number</c> (observed: <c>limit:"3"</c> answers a page), so it is not a stricter rule than the
/// transport's. A fault never carries the received value (R9.25).</para>
/// </summary>
internal static class ArgumentContract
{
    /// <summary>Every fault, in argument order and depth first; the required members left out follow the ones given. Empty when the call matches.</summary>
    internal static ImmutableArray<ArgumentFault> Check(
        JsonElement schema, IEnumerable<KeyValuePair<string, JsonElement>>? arguments, Func<string, string?>? reasonFor = null)
    {
        var faults = ImmutableArray.CreateBuilder<ArgumentFault>();
        CheckObject(schema, [.. arguments ?? []], string.Empty, faults, reasonFor);
        return faults.ToImmutable();
    }

    private static void CheckObject(
        JsonElement schema,
        IReadOnlyList<KeyValuePair<string, JsonElement>> given,
        string path,
        ImmutableArray<ArgumentFault>.Builder faults,
        Func<string, string?>? reasonFor)
    {
        var properties = schema.TryGetProperty("properties", out var found) && found.ValueKind is JsonValueKind.Object
            ? found
            : default;
        var defined = properties.ValueKind is JsonValueKind.Object
            ? string.Join(", ", properties.EnumerateObject().Select(member => member.Name))
            : string.Empty;

        foreach (var (name, value) in given)
        {
            var at = At(path, name);
            if (properties.ValueKind is JsonValueKind.Object && properties.TryGetProperty(name, out var memberSchema))
                CheckValue(memberSchema, value, at, faults, reasonFor);
            else
                faults.Add(new ArgumentFault(at, ArgumentFaultKind.Unknown, defined, reasonFor?.Invoke(name)));
        }

        if (!schema.TryGetProperty("required", out var required) || required.ValueKind is not JsonValueKind.Array) return;

        foreach (var entry in required.EnumerateArray())
        {
            var name = entry.GetString();
            if (name is not null && !given.Any(pair => string.Equals(pair.Key, name, StringComparison.Ordinal)))
                faults.Add(new ArgumentFault(At(path, name), ArgumentFaultKind.Missing, string.Empty));
        }
    }

    private static void CheckValue(
        JsonElement schema, JsonElement value, string path, ImmutableArray<ArgumentFault>.Builder faults, Func<string, string?>? reasonFor)
    {
        var admitted = Types(schema);
        if (admitted.Count > 0 && !Admits(admitted, value))
        {
            faults.Add(new ArgumentFault(path, ArgumentFaultKind.WrongType, string.Join(" or ", admitted)));
            return;
        }

        if (value.ValueKind is JsonValueKind.Object)
        {
            CheckObject(schema, [.. value.EnumerateObject().Select(member => new KeyValuePair<string, JsonElement>(member.Name, member.Value))], path, faults, reasonFor);
        }
        else if (value.ValueKind is JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            var index = 0;
            foreach (var element in value.EnumerateArray())
            {
                CheckValue(items, element, path + "[" + index.ToString(CultureInfo.InvariantCulture) + "]", faults, reasonFor);
                index++;
            }
        }
    }

    private static string At(string path, string name) => path.Length == 0 ? name : path + "." + name;

    /// <summary>The types a schema admits; empty when it states none, which admits any value.</summary>
    private static List<string> Types(JsonElement schema)
    {
        if (!schema.TryGetProperty("type", out var type)) return [];

        return type.ValueKind switch
        {
            JsonValueKind.String => [type.GetString()!],
            JsonValueKind.Array => [.. type.EnumerateArray().Where(t => t.ValueKind is JsonValueKind.String).Select(t => t.GetString()!)],
            JsonValueKind.Undefined or JsonValueKind.Object or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => [],
            _ => [],
        };
    }

    private static bool Admits(List<string> types, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => types.Contains("null"),
        JsonValueKind.True or JsonValueKind.False => types.Contains("boolean"),
        JsonValueKind.Array => types.Contains("array"),
        JsonValueKind.Object => types.Contains("object"),
        JsonValueKind.Number => types.Contains("number") || (types.Contains("integer") && value.TryGetInt32(out _)),
        JsonValueKind.String => types.Contains("string") || AdmitsNumericText(types, value.GetString()!),
        JsonValueKind.Undefined => false,
        _ => false,
    };

    /// <summary>The binder reads a numeric string as a number, and binds every integer parameter as an <c>int</c> (a 32-bit value), so that is the range admitted; the contract is not stricter than the transport (ruling 3).</summary>
    private static bool AdmitsNumericText(List<string> types, string text)
    {
        if (types.Contains("number"))
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d);

        return types.Contains("integer") && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
    }
}
