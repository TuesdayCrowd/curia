namespace Curia.Tests.Shared;

/// <summary>
/// R8.85 (errata G56): the board spellings of NAM1, each with the classes the clause named beside it requires, written by hand and never computed
/// (ruling 13). One table, linked into the assemblies that probe the predicate, so the unit probe, the client's agreement probe and the route's
/// probe cannot hold three tables that drift. Built at run time: no raw control character, noncharacter or lone surrogate is in a file.
/// </summary>
internal static class BoardSpellings
{
    internal static string U(int scalar) => char.ConvertFromUtf32(scalar);

    internal static string Rep(string unit, int count) => string.Concat(Enumerable.Repeat(unit, count));

    private static readonly string[] None = [];

    /// <summary>
    /// The table: a name, the board, the classes the clause quoted in the name requires (published order), written by hand. The edge rows
    /// (` a`, `a `) report <c>separator</c> and no row reports an edge class (finding 3: edge white space is entailed by the separator class).
    /// </summary>
    internal static (string Name, string Board, string[] Expected)[] Table() =>
    [
        // ---- qualifying: lower-case letters, digits, - and _ always qualify; a name may hold . and any assigned letter that is its own lower-case form
        ("a", "a", None),
        ("meta", "meta", None),
        ("machine-learning", "machine-learning", None),
        ("a_b", "a_b", None),
        ("a.b: a dot inside is a name", "a.b", None),
        ("0", "0", None),
        ("e-acute (NFC)", U(0xE9), None),
        ("a CJK name", U(0x65E5) + U(0x672C) + U(0x8A9E), None),
        ("64 a: the longest", Rep("a", 64), None),
        ("64 e-acute: 128 bytes, 64 scalars", Rep(U(0xE9), 64), None),
        ("64 U+1F642: 256 bytes, 128 units, 64 scalars", Rep(U(0x1F642), 64), None),
        ("a-: a trailing hyphen is a name", "a-", None),
        ("U+0130 is its own invariant lower-case form (ruling 23)", U(0x130), None),
        ("U+2102 is its own invariant lower-case form (ruling 23)", U(0x2102), None),
        ("...: not a dot-segment", "...", None),
        ("a.: not a dot-segment", "a.", None),

        // ---- length: fewer than 1 or more than 64 scalar values
        ("length: empty", "", ["length"]),
        ("length: 65 a", Rep("a", 65), ["length"]),
        ("length: 65 U+1F642 (65 scalars, 130 units)", Rep(U(0x1F642), 65), ["length"]),

        // ---- not-nfc: not in normalization form C
        ("not-nfc: e + U+0301", "e" + U(0x301), ["not-nfc"]),

        // ---- not-lower-case: differs from its ToLowerInvariant
        ("not-lower-case: Meta", "Meta", ["not-lower-case"]),
        ("not-lower-case: U+01C5 (titlecase)", U(0x1C5), ["not-lower-case"]),
        ("not-lower-case: U+1E9E", U(0x1E9E), ["not-lower-case"]),

        // ---- separator: a space, a no-break space, a line or paragraph separator, in the middle as at an edge
        ("separator: a b", "a b", ["separator"]),
        ("separator: leading space", " a", ["separator"]),
        ("separator: trailing space", "a ", ["separator"]),
        ("separator: no-break space", "a" + U(0xA0) + "b", ["separator"]),
        ("separator: line separator U+2028", "a" + U(0x2028) + "b", ["separator"]),
        ("separator: paragraph separator U+2029", "a" + U(0x2029) + "b", ["separator"]),
        ("separator: ideographic space U+3000", "a" + U(0x3000) + "b", ["separator"]),

        // ---- control
        ("control: line feed", "a\nb", ["control"]),
        ("control: tab", "a\tb", ["control"]),
        ("control: DEL", "a" + U(0x7F) + "b", ["control"]),
        ("control: NEL U+0085", "a" + U(0x85) + "b", ["control"]),

        // ---- format
        ("format: zero width space", "a" + U(0x200B) + "b", ["format"]),
        ("format: right-to-left override", "a" + U(0x202E) + "b", ["format"]),
        ("format: byte order mark", "a" + U(0xFEFF) + "b", ["format"]),
        ("format: soft hyphen", "a" + U(0xAD) + "b", ["format"]),

        // ---- private-use (the datamark's plane, R10.16)
        ("private-use: U+E000, the datamark", "a" + U(0xE000) + "b", ["private-use"]),
        ("private-use: U+F8FF", "a" + U(0xF8FF) + "b", ["private-use"]),
        ("private-use: U+10FFFD", "a" + U(0x10FFFD) + "b", ["private-use"]),

        // ---- unassigned: as the running Unicode data has it; a noncharacter; a UTF-16 unit that is no scalar value
        ("unassigned: U+0378", "a" + U(0x378) + "b", ["unassigned"]),
        ("unassigned: U+FFFE (Normalize throws on it; no not-nfc)", "a" + (char)0xFFFE + "b", ["unassigned"]),
        ("unassigned: U+FDD0 (a noncharacter that does not throw)", "a" + (char)0xFDD0 + "b", ["unassigned"]),
        ("unassigned: U+E0080", "a" + U(0xE0080) + "b", ["unassigned"]),
        ("unassigned: lone high surrogate", "a" + (char)0xD800 + "b", ["unassigned"]),
        ("unassigned: lone low surrogate", "a" + (char)0xDC00 + "b", ["unassigned"]),

        // ---- reserved: one of , / ? # %
        ("reserved: comma", "a,b", ["reserved"]),
        ("reserved: slash", "a/b", ["reserved"]),
        ("reserved: question mark", "a?b", ["reserved"]),
        ("reserved: number sign", "a#b", ["reserved"]),
        ("reserved: percent", "a%b", ["reserved"]),

        // ---- dot-segment: the board is . or ..
        ("dot-segment: .", ".", ["dot-segment"]),
        ("dot-segment: ..", "..", ["dot-segment"]),

        // ---- published order
        ("order: A b is separator then not-lower-case", "A b", ["separator", "not-lower-case"]),
        ("order: every class a 65-scalar board can hold but dot-segment (which needs exactly . or ..)",
            "e" + U(0x301) + "\n" + U(0xAD) + " " + U(0xE000) + U(0x378) + "," + "A" + Rep("a", 65),
            ["not-nfc", "length", "control", "format", "separator", "private-use", "unassigned", "reserved", "not-lower-case"]),
    ];
}
