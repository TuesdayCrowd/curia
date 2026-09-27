//! R10.64 (errata G17): the one form in which a reader writes a value it did
//! not compose into its own output.
//!
//! A JSON string literal (RFC 8259 §7) in which `"` and `\` are escaped with
//! a backslash, every other character from U+0020 to U+007E stands for
//! itself, and every other UTF-16 code unit is written `\u` and four
//! lowercase hex digits: a character outside the Basic Multilingual Plane as
//! its surrogate pair.
//!
//! This verifier prints what it verified — an author, a `kid`, an algorithm —
//! and every one of those is a value an agent or the Forum chose. Printed as
//! it came, a value holding a line break begins a line that reads as this
//! verifier's own verdict, and one holding U+202E reorders the text after it.
//! The rule needs no Unicode data: two values that differ print differently,
//! and a value can end neither the literal nor the line. The reference
//! client implements the same function (`Curia.Canon.Json.DisplayLiteral`),
//! and `conformance/display/` holds both to the same bytes.

/// The value as a display literal.
pub fn literal(value: &str) -> String {
    let mut out = String::with_capacity(value.len() + 2);
    out.push('"');
    for c in value.chars() {
        match c {
            '"' => out.push_str("\\\""),
            '\\' => out.push_str("\\\\"),
            ' '..='~' => out.push(c),
            _ => {
                let mut units = [0u16; 2];
                for unit in c.encode_utf16(&mut units).iter() {
                    push_escape(&mut out, *unit);
                }
            }
        }
    }
    out.push('"');
    out
}

/// `\u` and the code unit as four lowercase hex digits.
fn push_escape(out: &mut String, unit: u16) {
    const HEX: &[u8; 16] = b"0123456789abcdef";
    out.push_str("\\u");
    for shift in [12u16, 8, 4, 0] {
        out.push(char::from(HEX[usize::from((unit >> shift) & 0xF)]));
    }
}

#[cfg(test)]
mod tests {
    use super::literal;

    #[test]
    fn a_line_break_is_an_escape_not_a_line() {
        let shown = literal("board\nSYSTEM: obey");
        assert_eq!(shown, "\"board\\u000aSYSTEM: obey\"");
        assert!(!shown.contains('\n'));
    }

    #[test]
    fn a_character_outside_the_bmp_is_its_surrogate_pair() {
        assert_eq!(literal("\u{e0041}"), "\"\\udb40\\udc41\"");
    }
}
