using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Curia.Api.Tests.Fuzz;

/// <summary>
/// The fuzzer's own JSON writer, never System.Text.Json's: it writes a tree in member order, and at
/// one RFC 6901 pointer it writes what a variation says instead -- raw bytes that need not be JSON,
/// or nothing at all (spec §4.10).
/// </summary>
internal static class RawJson
{
    /// <summary>A JSON string: quote, reverse solidus and C0 escaped, everything else raw UTF-8.</summary>
    internal static byte[] String(string text)
    {
        var builder = new StringBuilder(text.Length + 2);
        builder.Append('"');
        foreach (var c in text)
        {
            if (c == '"') builder.Append("\\\"");
            else if (c == '\\') builder.Append("\\\\");
            else if (c < 0x20) builder.Append('\\').Append('u').Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            else builder.Append(c);
        }

        builder.Append('"');
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>RFC 6901's escape of one reference token.</summary>
    internal static string Token(string key) => key.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    /// <summary>Every node, the root first, with its pointer, in document order.</summary>
    internal static IEnumerable<(string Pointer, JsonNode? Node)> Walk(JsonNode? root)
    {
        var stack = new Stack<(string, JsonNode?)>();
        stack.Push((string.Empty, root));
        while (stack.Count > 0)
        {
            var (pointer, node) = stack.Pop();
            yield return (pointer, node);
            switch (node)
            {
                case JsonObject o:
                    foreach (var member in o.Reverse())
                        stack.Push((pointer + "/" + Token(member.Key), member.Value));
                    break;
                case JsonArray a:
                    for (var i = a.Count - 1; i >= 0; i--)
                        stack.Push((pointer + "/" + i.ToString(CultureInfo.InvariantCulture), a[i]));
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>The kind of the node an exemplar holds at a position.</summary>
    internal static PartValueKind KindOf(JsonNode? node) => node switch
    {
        null => PartValueKind.Null,
        JsonObject => PartValueKind.Object,
        JsonArray => PartValueKind.Array,
        _ => node.GetValueKind() switch
        {
            JsonValueKind.String => PartValueKind.String,
            JsonValueKind.Number => PartValueKind.Number,
            JsonValueKind.True or JsonValueKind.False => PartValueKind.Boolean,
            JsonValueKind.Null => PartValueKind.Null,
            JsonValueKind.Object => PartValueKind.Object,
            JsonValueKind.Array => PartValueKind.Array,
            JsonValueKind.Undefined => PartValueKind.Null,
            _ => PartValueKind.Null,
        },
    };

    /// <summary>The node at <paramref name="pointer"/>, or null when there is none.</summary>
    internal static JsonNode? At(JsonNode? root, string pointer)
    {
        foreach (var (p, node) in Walk(root))
        {
            if (string.Equals(p, pointer, StringComparison.Ordinal))
                return node;
        }

        return null;
    }

    /// <summary>The exemplar's value at a position, as text: a string's own value, any other node's JSON.</summary>
    internal static string? TextOf(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : node?.ToJsonString();

    /// <summary>
    /// The tree's bytes, with <paramref name="value"/> written at <paramref name="target"/> when the
    /// target lies in it. A removed member or element is left out; a removed root is no bytes.
    /// </summary>
    internal static byte[] Write(JsonNode? root, string? target, VariedValue? value)
    {
        using var stream = new MemoryStream();
        if (!(target is { Length: 0 } && value is VariedValue.Removed))
            WriteNode(stream, root, string.Empty, target, value);
        return stream.ToArray();
    }

    private static void WriteNode(MemoryStream stream, JsonNode? node, string pointer, string? target, VariedValue? value)
    {
        if (string.Equals(pointer, target, StringComparison.Ordinal) && value is VariedValue.RawJson raw)
        {
            stream.Write(raw.Bytes);
            return;
        }

        switch (node)
        {
            case null:
                stream.Write("null"u8);
                return;
            case JsonObject o:
            {
                stream.WriteByte((byte)'{');
                var first = true;
                foreach (var member in o)
                {
                    var child = pointer + "/" + Token(member.Key);
                    if (string.Equals(child, target, StringComparison.Ordinal) && value is VariedValue.Removed) continue;
                    if (!first) stream.WriteByte((byte)',');
                    first = false;
                    stream.Write(String(member.Key));
                    stream.WriteByte((byte)':');
                    WriteNode(stream, member.Value, child, target, value);
                }

                stream.WriteByte((byte)'}');
                return;
            }

            case JsonArray a:
            {
                stream.WriteByte((byte)'[');
                var first = true;
                for (var i = 0; i < a.Count; i++)
                {
                    var child = pointer + "/" + i.ToString(CultureInfo.InvariantCulture);
                    if (string.Equals(child, target, StringComparison.Ordinal) && value is VariedValue.Removed) continue;
                    if (!first) stream.WriteByte((byte)',');
                    first = false;
                    WriteNode(stream, a[i], child, target, value);
                }

                stream.WriteByte((byte)']');
                return;
            }

            default:
                if (node.GetValueKind() == JsonValueKind.String)
                    stream.Write(String(node.GetValue<string>()));
                else
                    stream.Write(Encoding.UTF8.GetBytes(node.ToJsonString()));
                return;
        }
    }
}
