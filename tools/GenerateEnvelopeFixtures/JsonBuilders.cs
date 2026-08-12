using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Curia.Canon.Json;

namespace GenerateEnvelopeFixtures;

/// <summary>
/// Tiny builder helpers for constructing <see cref="JsonValue"/> trees by hand, plus a
/// non-canonical pretty printer for writing the "wire" submission.json files. This is
/// deliberately separate from <c>Curia.Canon.Canonical.CanonicalJson</c>: the wire file
/// is not required to be in canonical form (only <c>expected.canonical</c> is), and
/// pretty-printing with insertion order preserved is what a real client would produce.
/// </summary>
internal static class JsonBuilders
{
    public static readonly JsonValue Null = JsonValue.Null.Instance;

    public static JsonValue Str(string value) => new JsonValue.String(value);

    public static JsonValue Num(double value) => new JsonValue.Number(value);

    public static JsonValue Bool(bool value) => new JsonValue.Bool(value);

    public static JsonValue Arr(params ReadOnlySpan<JsonValue> items) =>
        new JsonValue.Array([.. items]);

    public static JsonValue Obj(params ReadOnlySpan<(string Key, JsonValue Value)> members)
    {
        var builder = ImmutableArray.CreateBuilder<KeyValuePair<string, JsonValue>>(members.Length);
        foreach (var (key, value) in members)
            builder.Add(new KeyValuePair<string, JsonValue>(key, value));
        return new JsonValue.Object(builder.ToImmutable());
    }

    /// <summary>
    /// Returns a new object equal to <paramref name="obj"/> except that <paramref name="key"/>
    /// (which must already be present exactly once) maps to <paramref name="newValue"/>. Used
    /// to build the <c>tampered-body</c> fixture: sign the original, then derive the tampered
    /// envelope from it by construction rather than by hand-editing JSON text, so the only
    /// difference between signed and published content is the one field under test.
    /// </summary>
    public static JsonValue.Object WithField(JsonValue.Object obj, string key, JsonValue newValue)
    {
        ArgumentNullException.ThrowIfNull(obj);
        var found = false;
        var builder = ImmutableArray.CreateBuilder<KeyValuePair<string, JsonValue>>(obj.Members.Length);
        foreach (var member in obj.Members)
        {
            if (member.Key == key)
            {
                builder.Add(new KeyValuePair<string, JsonValue>(key, newValue));
                found = true;
            }
            else
            {
                builder.Add(member);
            }
        }

        if (!found)
            throw new ArgumentException($"key '{key}' not present", nameof(key));

        return new JsonValue.Object(builder.ToImmutable());
    }

    /// <summary>Pretty-prints (2-space indent, literal UTF-8, insertion order) for a wire file.</summary>
    public static string PrettyPrint(JsonValue value)
    {
        var sb = new StringBuilder();
        Write(value, sb, 0);
        return sb.ToString();
    }

    private static void Write(JsonValue value, StringBuilder sb, int indent)
    {
        switch (value)
        {
            case JsonValue.Object o:
                if (o.Members.Length == 0) { sb.Append("{}"); break; }
                sb.Append("{\n");
                for (var i = 0; i < o.Members.Length; i++)
                {
                    Indent(sb, indent + 1);
                    WriteString(o.Members[i].Key, sb);
                    sb.Append(": ");
                    Write(o.Members[i].Value, sb, indent + 1);
                    sb.Append(i < o.Members.Length - 1 ? ",\n" : "\n");
                }
                Indent(sb, indent);
                sb.Append('}');
                break;

            case JsonValue.Array a:
                if (a.Items.Length == 0) { sb.Append("[]"); break; }
                sb.Append("[\n");
                for (var i = 0; i < a.Items.Length; i++)
                {
                    Indent(sb, indent + 1);
                    Write(a.Items[i], sb, indent + 1);
                    sb.Append(i < a.Items.Length - 1 ? ",\n" : "\n");
                }
                Indent(sb, indent);
                sb.Append(']');
                break;

            case JsonValue.String s: WriteString(s.Value, sb); break;
            case JsonValue.Number n: WriteNumber(n.Value, sb); break;
            case JsonValue.Bool b: sb.Append(b.Value ? "true" : "false"); break;
            case JsonValue.Null: sb.Append("null"); break;
        }
    }

    private static void Indent(StringBuilder sb, int level) => sb.Append(' ', level * 2);

    /// <summary>
    /// Every numeric field in these fixtures is a small non-negative integer (schema version),
    /// so a full ECMAScript-serialization port is not needed here the way it is in
    /// <c>CanonicalJson</c> -- this writer is for the human-readable wire file, not the
    /// canonical bytes a signature covers.
    /// </summary>
    private static void WriteNumber(double value, StringBuilder sb)
    {
        if (double.IsInteger(value))
            sb.Append(((long)value).ToString(CultureInfo.InvariantCulture));
        else
            sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
    }

    private static void WriteString(string s, StringBuilder sb)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}");
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }
}
