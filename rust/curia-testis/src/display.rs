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

    /// Every Unicode scalar value, alone. The vectors pin sixteen strings'
    /// bytes; this pins what they cannot enumerate: that the literal of any
    /// character is printable ASCII between two quotes, that a printable
    /// character other than `"` and `\` stands for itself, and that reading
    /// the literal back by the rule's own text gives the value's UTF-16 code
    /// units.
    #[test]
    fn every_scalar_value_is_printable_ascii_and_reads_back_as_itself() {
        let mut count = 0u32;
        for scalar in (0..=0x10_FFFF_u32).filter_map(char::from_u32) {
            let value = scalar.to_string();
            let shown = literal(&value);
            let code = u32::from(scalar);
            assert!(
                shown.len() >= 2
                    && shown.starts_with('"')
                    && shown.ends_with('"')
                    && shown.bytes().all(|b| (0x20..=0x7E).contains(&b)),
                "U+{code:04X} printed {shown:?}"
            );
            assert_eq!(
                read_back(&shown),
                value.encode_utf16().collect::<Vec<u16>>(),
                "U+{code:04X} printed {shown:?}"
            );
            count += 1;
        }
        assert_eq!(count, 1_112_064, "every scalar value, and no other");
    }

    /// The inverse of the rule as R10.64 states it, written from the text
    /// rather than from [`literal`]: `\"` and `\\` are the quote and the
    /// backslash, `\u` and four lowercase hex digits are a code unit outside
    /// printable ASCII, and any other character stands for itself.
    fn read_back(shown: &str) -> Vec<u16> {
        let inner = &shown[1..shown.len() - 1];
        let mut units = Vec::new();
        let mut chars = inner.chars();
        while let Some(c) = chars.next() {
            if c != '\\' {
                assert!(c != '"', "a bare quote inside {shown:?}");
                units.push(u16::try_from(u32::from(c)).expect("printable ASCII"));
                continue;
            }
            match chars.next() {
                Some('u') => {
                    let hex: String = chars.by_ref().take(4).collect();
                    assert!(
                        hex.len() == 4
                            && hex
                                .chars()
                                .all(|h| h.is_ascii_digit() || ('a'..='f').contains(&h)),
                        "not four lowercase hex digits in {shown:?}"
                    );
                    let unit = u16::from_str_radix(&hex, 16).expect("four hex digits");
                    assert!(
                        !(0x20..=0x7E).contains(&unit),
                        "a printable character escaped in {shown:?}"
                    );
                    units.push(unit);
                }
                Some(escaped @ ('"' | '\\')) => {
                    units.push(u16::try_from(u32::from(escaped)).expect("ASCII"));
                }
                other => panic!("an escape the rule does not write, {other:?}, in {shown:?}"),
            }
        }
        units
    }
}
