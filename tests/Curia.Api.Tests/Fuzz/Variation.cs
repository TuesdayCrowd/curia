using System.Globalization;
using System.Text;

namespace Curia.Api.Tests.Fuzz;

/// <summary>
/// One stable id per variation (spec §4.10's table); <see cref="Applies"/> decides which parts get it,
/// and <see cref="Make"/> renders it for the part's position from the exemplar's value there.
/// </summary>
internal sealed record Variation(string Id, Func<Part, bool> Applies, Func<Part, string?, VariedValue> Make);

/// <summary>
/// What a variation puts at a part. JSON and JWS positions get the bytes written at the leaf: nul,
/// lone-high, lone-low and C0 are the six-character JSON escape; their *-raw twins, bad-utf8 and
/// overlong are raw bytes (spec §4.10). Path, query and form positions get percent-encoded text, and
/// headers a string.
/// </summary>
internal abstract record VariedValue
{
    /// <summary>The part is absent: a member, a field, a parameter or a header not sent, a segment left empty.</summary>
    internal sealed record Removed : VariedValue;

    /// <summary>Written verbatim at the leaf's position.</summary>
    internal sealed record RawJson(byte[] Bytes) : VariedValue;

    /// <summary>Already percent-encoded.</summary>
    internal sealed record Encoded(string Text) : VariedValue;

    /// <summary>A header's or a header parameter's text, added without validation.</summary>
    internal sealed record HeaderText(string Text) : VariedValue;

    /// <summary>body-removed (no bytes, no content) and body-truncated; a null content type keeps the exemplar's.</summary>
    internal sealed record Body(byte[] Bytes, string? ContentType) : VariedValue;
}

/// <summary>The closed set, exactly spec §4.10's table. Ids are stable: the ledger keys on them.</summary>
internal static class Variations
{
    /// <summary>The lengths a body or JWS string is grown to.</summary>
    private static readonly int[] BodyLengths = [1_024, 65_536, 262_144];

    /// <summary>The lengths a path, query or header string is grown to.</summary>
    private static readonly int[] LineLengths = [1_024, 7_168];

    private static List<Variation> Build()
    {
        var all = new List<Variation>
        {
            new("removed", _ => true, (_, _) => new VariedValue.Removed()),
        };

        // Retype: every JSON and JWS position, the roots included.
        foreach (var (id, json) in Retypes)
            all.Add(new(id, p => p.Kind is PartKind.Json or PartKind.Jws, (_, _) => new VariedValue.RawJson(Ascii(json))));

        // String: every string part, in each position's own rendering.
        foreach (var row in Strings)
            all.Add(new(row.Id, p => p.ValueKind == PartValueKind.String, (p, _) => Render(p, row.Json, row.Encoded, row.Header)));

        all.Add(new("perturbed", p => p.ValueKind == PartValueKind.String, (p, v) => Text(p, Perturb(v ?? string.Empty))));

        // The first character too: a base64url value's last character carries padding bits that a
        // strict decoder refuses to see set, so only a change at the front alters the bytes it decodes
        // to (Task A5, case 3).
        all.Add(new("perturbed-first", p => p.ValueKind == PartValueKind.String, (p, v) => Text(p, PerturbFirst(v ?? string.Empty))));

        // Long: four fills, grown to the lengths of the part's position.
        foreach (var fill in (string[])["r", "space", "comment", "self"])
        {
            foreach (var n in BodyLengths.Union(LineLengths).Order())
            {
                var line = LineLengths.Contains(n);
                var body = BodyLengths.Contains(n);
                all.Add(new(
                    $"long-{fill}-{n.ToString(CultureInfo.InvariantCulture)}",
                    p => p.ValueKind == PartValueKind.String && (IsLinePosition(p) ? line : body),
                    (p, v) => Text(p, Fill(fill, n, v))));
            }
        }

        // Capped: a part with a published cap, at it and one byte over it.
        all.Add(new("at-cap", p => p.PublishedCap is not null, (p, _) => Text(p, AtCap(p.PublishedCap!.Value))));
        all.Add(new("over-cap", p => p.PublishedCap is not null, (p, _) => Text(p, AtCap(p.PublishedCap!.Value) + "b")));

        // Number: every JSON number.
        foreach (var (id, json) in Numbers)
            all.Add(new(id, p => p.ValueKind == PartValueKind.Number && p.Kind is PartKind.Json or PartKind.Jws, (_, _) => new VariedValue.RawJson(Ascii(json))));

        // Body: a body's root.
        all.Add(new("body-removed", p => p.IsBodyRoot, (_, _) => new VariedValue.Body([], null)));
        all.Add(new("body-truncated", p => p.IsBodyRoot, (_, v) =>
        {
            var bytes = Encoding.UTF8.GetBytes(v ?? string.Empty);
            return new VariedValue.Body(bytes[..(bytes.Length / 2)], null);
        }));

        // Header parameter: the one value that makes MediaTypeHeaderValue.Encoding throw rather than return null.
        all.Add(new("disabled-encoding", p => p.Kind == PartKind.HeaderParameter, (_, _) => new VariedValue.HeaderText("utf-7")));

        return all;
    }

    private static readonly (string Id, string Json)[] Retypes =
    [
        ("null", "null"), ("number", "0"), ("true", "true"), ("array", "[]"), ("object", "{}"), ("string", "\"1\""),
    ];

    private static readonly (string Id, string Json)[] Numbers =
    [
        ("zero", "0"), ("minus-one", "-1"), ("1e13", "1e13"), ("-1e11", "-1e11"),
        ("2^53+1", "9007199254740993"), ("1.5", "1.5"), ("1e400", "1e400"),
    ];

    /// <summary>The JSON escape of one UTF-16 code unit, built so that no tool between this file and the compiler decodes it.</summary>
    private static string Esc(string hex) => "\\" + "u" + hex;

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    private static byte[] Quoted(params byte[] inner) => [0x22, .. inner, 0x22];

    private static string Latin1(params byte[] bytes) => Encoding.Latin1.GetString(bytes);

    private static string Char(int code) => ((char)code).ToString();

    /// <summary>The string class: its JSON bytes, its percent-encoded text, and its header text.</summary>
    private static readonly (string Id, byte[] Json, string Encoded, string Header)[] Strings =
    [
        ("empty", Quoted(), "", ""),
        ("space", Quoted(0x20), "%20", " "),
        ("whitespace", Quoted(Ascii(Esc("0009") + " " + Esc("000a"))), "%09%20%0A", "\t \n"),
        ("nul", Quoted(Ascii(Esc("0000"))), "%00", "\0"),
        ("nul-raw", Quoted(0x00), "%00", "\0"),
        ("lone-high", Quoted(Ascii(Esc("d800"))), "%ED%A0%80", Char(0xD800)),
        ("lone-high-raw", Quoted(0xED, 0xA0, 0x80), "%ED%A0%80", Latin1(0xED, 0xA0, 0x80)),
        ("lone-low", Quoted(Ascii(Esc("dc00"))), "%ED%B0%80", Char(0xDC00)),
        ("lone-low-raw", Quoted(0xED, 0xB0, 0x80), "%ED%B0%80", Latin1(0xED, 0xB0, 0x80)),
        ("bad-utf8", Quoted(0xFF), "%FF", Latin1(0xFF)),
        ("overlong", Quoted(0xC0, 0x80), "%C0%80", Latin1(0xC0, 0x80)),
        ("u2028", Quoted(0xE2, 0x80, 0xA8), "%E2%80%A8", Char(0x2028)),
        ("ufffe", Quoted(0xEF, 0xBF, 0xBE), "%EF%BF%BE", Char(0xFFFE)),
        ("linebreak", Quoted(Ascii("a" + Esc("000a") + "b")), "a%0Ab", "a\nb"),
        ("not-nfc", Quoted(0x65, 0xCC, 0x81), "e%CC%81", "e" + Char(0x0301)),
    ];

    private static bool IsLinePosition(Part part) => part.Kind is PartKind.Path or PartKind.Query or PartKind.Header or PartKind.HeaderParameter;

    private static VariedValue Render(Part part, byte[] json, string encoded, string header) => part.Kind switch
    {
        PartKind.Json or PartKind.Jws => new VariedValue.RawJson(json),
        PartKind.Path or PartKind.Query or PartKind.Form => new VariedValue.Encoded(encoded),
        PartKind.Header or PartKind.HeaderParameter => new VariedValue.HeaderText(header),
        _ => throw new ArgumentOutOfRangeException(nameof(part), part.Kind, "not a position"),
    };

    /// <summary>A string as its position writes one: JSON-escaped, percent-encoded, or as is.</summary>
    private static VariedValue Text(Part part, string text) => part.Kind switch
    {
        PartKind.Json or PartKind.Jws => new VariedValue.RawJson(Fuzz.RawJson.String(text)),
        PartKind.Path or PartKind.Query or PartKind.Form => new VariedValue.Encoded(Uri.EscapeDataString(text)),
        PartKind.Header or PartKind.HeaderParameter => new VariedValue.HeaderText(text),
        _ => throw new ArgumentOutOfRangeException(nameof(part), part.Kind, "not a position"),
    };

    /// <summary>The exemplar's value with its last character replaced by the next one of its own class, each wrapping; any other character becomes <c>x</c>.</summary>
    internal static string Perturb(string value) =>
        value.Length == 0 ? "x" : string.Concat(value.AsSpan(0, value.Length - 1), Next(value[^1]).ToString());

    /// <summary>The exemplar's value with its first character replaced as <see cref="Perturb"/> replaces the last.</summary>
    internal static string PerturbFirst(string value) =>
        value.Length == 0 ? "x" : string.Concat(Next(value[0]).ToString(), value.AsSpan(1));

    /// <summary>The next character of a character's own class (digit, lowercase, uppercase), each wrapping; any other character becomes <c>x</c>.</summary>
    private static char Next(char c) => c switch
    {
        >= '0' and < '9' or >= 'a' and < 'z' or >= 'A' and < 'Z' => (char)(c + 1),
        '9' => '0',
        'z' => 'a',
        'Z' => 'A',
        _ => 'x',
    };

    private static string Fill(string fill, int n, string? exemplar)
    {
        var unit = fill switch
        {
            "r" => "r",
            "space" => " ",
            "comment" => "<!--",
            "self" => string.IsNullOrEmpty(exemplar) ? "x" : exemplar,
            _ => throw new ArgumentOutOfRangeException(nameof(fill), fill, "not a fill"),
        };
        var builder = new StringBuilder(n + unit.Length);
        while (builder.Length < n) builder.Append(unit);
        return builder.ToString(0, n);
    }

    /// <summary>The set, built after every table above is initialized.</summary>
    internal static IReadOnlyList<Variation> Closed { get; } = Build();

    /// <summary>Exactly <paramref name="cap"/> UTF-8 bytes: U+4E2D, three bytes each, then ASCII for the remainder. Over the cap appends one byte.</summary>
    private static string AtCap(int cap) => new string((char)0x4E2D, cap / 3) + new string('a', cap % 3);
}
