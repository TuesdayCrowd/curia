using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Unicode;          // Utf8.IsValid
using Curia.Domain.Primitives;

namespace Curia.Canon.Json;

/// <summary>Caps frozen by R15.1. See spec §5.1.</summary>
public sealed record AdmitLimits(int MaxBytes, int MaxDepth, int MaxMembersPerObject, int MaxStringBytes)
{
    public static readonly AdmitLimits Default = new(
        MaxBytes: 1_048_576,
        MaxDepth: 32,
        MaxMembersPerObject: 1_024,
        MaxStringBytes: 262_144);
}

/// <summary>
/// ADMIT phase ① (§6.4): reject or pass, never repair. A hand-rolled Utf8JsonReader
/// walk rather than JsonSerializer.Deserialize, because duplicate-key rejection and
/// the size and depth caps must apply before any object exists.
/// </summary>
public static class JsonReader
{
    public static Result<JsonValue> Parse(ReadOnlySpan<byte> utf8, AdmitLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        if (utf8.Length > limits.MaxBytes)
            return Result<JsonValue>.Fail(CanonErrors.SizeExceeded(limits.MaxBytes));

        if (utf8.IndexOf((byte)0) >= 0)
            return Result<JsonValue>.Fail(CanonErrors.NulByte());

        if (Utf8.IsValid(utf8) is false)
            return Result<JsonValue>.Fail(CanonErrors.InvalidUtf8());

        var options = new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            // Deliberately above limits.MaxDepth: our own depth: parameter in ReadValue is
            // the sole authority for curia/admit/depth-exceeded (see the comment there). If
            // this were set to limits.MaxDepth, the reader's own internal counter would trip
            // first on every legitimate depth-cap violation, forcing the catch clause below
            // to recover the specific slug by sniffing the exception message -- and that
            // message-substring approach is unreliable, because Utf8JsonReader reuses the
            // word "depth" for an unrelated end-of-input error ("Expected depth to be zero at
            // the end of the JSON payload"), which a truncated-but-shallow document like `{`
            // or `{"a":` also triggers. The +2 headroom (not just +1) leaves margin for the
            // reader to always finish delivering the (limits.MaxDepth + 1)-th StartObject/
            // StartArray token -- the one our own check rejects on -- confirmed empirically:
            // Utf8JsonReader.Read() tolerates exactly MaxDepth opening brackets before
            // throwing on the next one, so headroom of 1 would already suffice; +2 keeps a
            // one-level buffer against off-by-one drift in that counting across .NET versions.
            MaxDepth = limits.MaxDepth + 2,
        };

        var reader = new Utf8JsonReader(utf8, options);
        try
        {
            if (!reader.Read())
                return Result<JsonValue>.Fail(CanonErrors.Malformed("empty input"));

            var result = ReadValue(ref reader, limits, depth: 1);
            if (!result.IsOk)
                return result;

            return reader.Read()
                ? Result<JsonValue>.Fail(CanonErrors.Malformed("trailing content after top-level value"))
                : result;
        }
        catch (JsonException ex)
        {
            // With the headroom above, our own depth check always wins the race for a real
            // depth-cap violation, so anything that reaches here is a genuine structural
            // failure (truncated input, invalid syntax) rather than the depth cap -- no
            // substring inspection needed or wanted.
            return Result<JsonValue>.Fail(CanonErrors.Malformed(ex.Message));
        }
    }

    private static Result<JsonValue> ReadValue(ref Utf8JsonReader reader, AdmitLimits limits, int depth)
    {
        // The sole authority for curia/admit/depth-exceeded -- see the MaxDepth headroom
        // comment in Parse for why the reader's own JsonReaderOptions.MaxDepth is set above
        // limits.MaxDepth rather than equal to it.
        //
        // The check applies only to containers (StartObject/StartArray), never to leaf
        // values: depth counts levels of nesting, and a leaf sitting inside the
        // limits.MaxDepth-th container is not itself an additional level of nesting. Applying
        // the check uniformly to every token (the earlier, buggy shape of this method) rejected
        // a scalar nested inside exactly limits.MaxDepth containers -- checked at depth
        // limits.MaxDepth + 1 -- making the effective accepted maximum limits.MaxDepth - 1
        // levels of content instead of limits.MaxDepth. See
        // conformance/admit-reject/over-nested/meta.json: "33 levels exceeds the depth cap of
        // 32" -- 32 must be accepted, 33 rejected.
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
                return depth > limits.MaxDepth
                    ? Result<JsonValue>.Fail(CanonErrors.DepthExceeded(limits.MaxDepth))
                    : ReadObject(ref reader, limits, depth);
            case JsonTokenType.StartArray:
                return depth > limits.MaxDepth
                    ? Result<JsonValue>.Fail(CanonErrors.DepthExceeded(limits.MaxDepth))
                    : ReadArray(ref reader, limits, depth);
            case JsonTokenType.String: return ReadString(ref reader, limits);
            case JsonTokenType.Number: return Result<JsonValue>.Ok(new JsonValue.Number(reader.GetDouble()));
            case JsonTokenType.True: return Result<JsonValue>.Ok(new JsonValue.Bool(true));
            case JsonTokenType.False: return Result<JsonValue>.Ok(new JsonValue.Bool(false));
            case JsonTokenType.Null: return Result<JsonValue>.Ok(JsonValue.Null.Instance);
            default: return Result<JsonValue>.Fail(CanonErrors.Malformed($"unexpected token {reader.TokenType}"));
        }
    }

    private static Result<JsonValue> ReadString(ref Utf8JsonReader reader, AdmitLimits limits)
    {
        if (reader.ValueSpan.Length > limits.MaxStringBytes)
            return Result<JsonValue>.Fail(CanonErrors.StringTooLong(limits.MaxStringBytes));

        var value = ReadStringValue(ref reader);
        return value.IsOk
            ? Result<JsonValue>.Ok(new JsonValue.String(value.Match(v => v, _ => "")))
            : value.ToFailure<JsonValue>();
    }

    /// <summary>
    /// Wraps Utf8JsonReader.GetString(); callers must only invoke this when TokenType is
    /// String or PropertyName. Contrary to a common assumption, GetString() does not
    /// substitute U+FFFD for a \uXXXX escape that decodes to an unpaired surrogate — it
    /// throws InvalidOperationException instead, before this code ever sees a string to
    /// inspect. That failure is mapped explicitly to the specific slug (R6.15) rather than
    /// left to surface as an unhandled exception or collapse into a generic "malformed".
    /// </summary>
    private static Result<string> ReadStringValue(ref Utf8JsonReader reader)
    {
        try
        {
            return Result<string>.Ok(reader.GetString()!);
        }
        catch (InvalidOperationException)
        {
            return Result<string>.Fail(CanonErrors.UnpairedSurrogate());
        }
    }

    private static Result<JsonValue> ReadObject(ref Utf8JsonReader reader, AdmitLimits limits, int depth)
    {
        var members = ImmutableArray.CreateBuilder<KeyValuePair<string, JsonValue>>();
        var keys = new HashSet<string>(StringComparer.Ordinal);

        while (true)
        {
            if (!reader.Read())
                return Result<JsonValue>.Fail(CanonErrors.Malformed("truncated object"));

            if (reader.TokenType == JsonTokenType.EndObject)
                return Result<JsonValue>.Ok(new JsonValue.Object(members.ToImmutable()));

            if (reader.TokenType != JsonTokenType.PropertyName)
                return Result<JsonValue>.Fail(CanonErrors.Malformed($"expected property name, saw {reader.TokenType}"));

            var keyResult = ReadStringValue(ref reader);
            if (!keyResult.IsOk)
                return keyResult.ToFailure<JsonValue>();

            var key = keyResult.Match(v => v, _ => "");
            if (!keys.Add(key))
                return Result<JsonValue>.Fail(CanonErrors.DuplicateKey(key));

            if (members.Count + 1 > limits.MaxMembersPerObject)
                return Result<JsonValue>.Fail(CanonErrors.MembersExceeded(limits.MaxMembersPerObject));

            if (!reader.Read())
                return Result<JsonValue>.Fail(CanonErrors.Malformed("truncated member value"));

            var value = ReadValue(ref reader, limits, depth + 1);
            if (!value.IsOk)
                return value;

            members.Add(new KeyValuePair<string, JsonValue>(key, value.Match(v => v, _ => JsonValue.Null.Instance)));
        }
    }

    private static Result<JsonValue> ReadArray(ref Utf8JsonReader reader, AdmitLimits limits, int depth)
    {
        var items = ImmutableArray.CreateBuilder<JsonValue>();
        while (true)
        {
            if (!reader.Read())
                return Result<JsonValue>.Fail(CanonErrors.Malformed("truncated array"));

            if (reader.TokenType == JsonTokenType.EndArray)
                return Result<JsonValue>.Ok(new JsonValue.Array(items.ToImmutable()));

            var value = ReadValue(ref reader, limits, depth + 1);
            if (!value.IsOk)
                return value;

            items.Add(value.Match(v => v, _ => JsonValue.Null.Instance));
        }
    }
}
