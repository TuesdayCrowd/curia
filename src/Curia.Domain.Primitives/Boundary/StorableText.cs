using System.Text;

namespace Curia.Domain.Primitives.Boundary;

public static class StorableText
{
    public const string AbsentType = "curia/input/absent", BlankType = "curia/input/blank", NulType = "curia/input/nul",
        UnpairedSurrogateType = "curia/input/unpaired-surrogate", TooLongType = "curia/input/too-long";

    /// R11.34 (errata G18): the one rule. Order: absent, blank (when names), nul, unpaired surrogate, too long.
    public static Result<string> Check(string? value, string field, int maxUtf8Bytes, bool names)
    {
        if (value is null) return Fail(AbsentType, field);
        if (names && string.IsNullOrWhiteSpace(value)) return Fail(BlankType, field);
        if (value.Contains('\0', StringComparison.Ordinal)) return Fail(NulType, field);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c)) { if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) { i++; continue; } return Fail(UnpairedSurrogateType, field); }
            if (char.IsLowSurrogate(c)) return Fail(UnpairedSurrogateType, field);
        }
        var bytes = Encoding.UTF8.GetByteCount(value);
        return bytes > maxUtf8Bytes
            ? Result<string>.Fail(new Error(TooLongType, "The value is longer than its store holds", $"field={field} bytes={bytes}: at most {maxUtf8Bytes} UTF-8 bytes"))
            : Result<string>.Ok(value);
    }

    private static Result<string> Fail(string type, string field) =>
        Result<string>.Fail(new Error(type, "The value cannot be stored", "field=" + field));
}
