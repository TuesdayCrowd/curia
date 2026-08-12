//! `Canonicalize` — pure RFC 8785 JSON Canonicalization Scheme (JCS),
//! performing **no** Unicode normalization.
//!
//! Errata D1 (`spec/curia-whitepaper-ERRATA-AND-ADDENDUM.md`) is why this
//! function exists separately from `CanonicalizeWithNfc` (Task 3): RFC 8785
//! itself performs no normalization, and two of its own official vectors
//! (`unicode.json`, `weird.json`) exist specifically to prove that folding
//! normalization into canonicalization changes which vectors pass. This
//! module implements *only* RFC 8785 — normalizing anything here would be
//! exactly the trap CHARTER §1 and the Task 2 controller ruling describe.
//!
//! ## What RFC 8785 requires, and where each requirement is implemented
//!
//! - **§3.2.3, object member ordering**: sort by the UTF-16 code unit
//!   sequence of the member name, comparing lexicographically (a shorter
//!   name that is a prefix of a longer one sorts first — `strcmp()`
//!   semantics). This is *not* the same as Rust's native `str` `Ord`, nor
//!   UTF-8 byte order: both agree with UTF-16 code-unit order for two
//!   member names entirely inside the Basic Multilingual Plane, but a
//!   character outside it is encoded as a *surrogate pair* under UTF-16 —
//!   two code units starting at `0xD800..=0xDBFF` — while its UTF-8 (and
//!   Rust `str::Ord`, which is UTF-8-byte-order and therefore also
//!   codepoint-order) encoding starts with a lead byte that reflects its
//!   full codepoint value. `design spec §5.3` measures the concrete
//!   divergence (`conformance/ordering/`), and `sort_key` below is written
//!   to key on `encode_utf16()`, never on `str`'s own `Ord`, for exactly
//!   this reason.
//! - **§3.2.2.2, string serialization**: quote (`"`) and backslash (`\`)
//!   are escaped; control characters below `U+0020` are escaped, using the
//!   short forms (`\b \f \n \r \t`) where one exists and `\u00XX`
//!   (lowercase hex) otherwise; every other character — including `/`,
//!   `U+007F` DEL, and everything above `U+007F` — is emitted as its raw
//!   UTF-8 bytes, unescaped. Verified byte-for-byte against
//!   `conformance/rfc8785/output-values.json` and `output-weird.json`
//!   (see `task/task-2-report.md`).
//! - **§3.2.2.3, number serialization**: format the `f64` per ECMAScript's
//!   `Number::toString`. See [`format_number`] for the derivation; `node`
//!   (`String(x)`) was used as the oracle against every vector in
//!   `conformance/numbers/` (see the report).

use crate::json::{self, ParseError, Value};

/// Canonicalizes `input` per RFC 8785, performing no Unicode normalization.
/// `input` is not admitted, repaired, or otherwise mutated first — a
/// malformed input is rejected as a [`ParseError`], never silently fixed
/// up (CLAUDE.md's "no mutation between verify and persist", applied here
/// to "no repair before verify").
pub fn canonicalize(input: &[u8]) -> Result<Vec<u8>, ParseError> {
    let value = json::parse(input)?;
    let mut out = Vec::new();
    render(&value, &mut out);
    Ok(out)
}

fn render(value: &Value, out: &mut Vec<u8>) {
    match value {
        Value::Null => out.extend_from_slice(b"null"),
        Value::Bool(true) => out.extend_from_slice(b"true"),
        Value::Bool(false) => out.extend_from_slice(b"false"),
        Value::Number(n) => out.extend_from_slice(format_number(*n).as_bytes()),
        Value::String(s) => render_string(s, out),
        Value::Array(items) => {
            out.push(b'[');
            for (i, item) in items.iter().enumerate() {
                if i > 0 {
                    out.push(b',');
                }
                render(item, out);
            }
            out.push(b']');
        }
        Value::Object(members) => render_object(members, out),
    }
}

/// RFC 8785 §3.2.3: object members are sorted by the UTF-16 code unit
/// sequence of their name. `encode_utf16()` returns an iterator of `u16`;
/// `Iterator::cmp` compares two iterators lexicographically (shorter-is-less
/// when one is a prefix of the other), which is exactly `strcmp()`
/// semantics over code units — no intermediate `Vec<u16>` needed.
///
/// This function does not deduplicate members with equal keys (see
/// `src/json.rs`'s module doc comment on why duplicate-key rejection is
/// Task 4's concern, not this pure function's) — `sort_by` is a stable
/// sort, so any duplicates are preserved and simply rendered in their
/// original relative order, adjacent to each other.
fn render_object(members: &[(String, Value)], out: &mut Vec<u8>) {
    let mut entries: Vec<&(String, Value)> = members.iter().collect();
    entries.sort_by(|a, b| a.0.encode_utf16().cmp(b.0.encode_utf16()));

    out.push(b'{');
    for (i, (key, value)) in entries.into_iter().enumerate() {
        if i > 0 {
            out.push(b',');
        }
        render_string(key, out);
        out.push(b':');
        render(value, out);
    }
    out.push(b'}');
}

/// RFC 8785 §3.2.2.2. Escapes `"`, `\`, and control characters below
/// `U+0020` (using the named short escapes where ECMA-404 defines one,
/// `\u00XX` lowercase hex otherwise); every other character, including `/`
/// and everything at or above `U+007F`, is written out as its literal
/// UTF-8 bytes. Confirmed against every string in the six vendored RFC 8785
/// vectors, including `weird.json`'s DEL (`U+007F`, left unescaped) and
/// `values.json`'s ``/`\n`/`\"`/`\\`/unescaped `/` mix.
fn render_string(s: &str, out: &mut Vec<u8>) {
    out.push(b'"');
    for c in s.chars() {
        match c {
            '"' => out.extend_from_slice(b"\\\""),
            '\\' => out.extend_from_slice(b"\\\\"),
            '\u{0008}' => out.extend_from_slice(b"\\b"),
            '\u{000C}' => out.extend_from_slice(b"\\f"),
            '\n' => out.extend_from_slice(b"\\n"),
            '\r' => out.extend_from_slice(b"\\r"),
            '\t' => out.extend_from_slice(b"\\t"),
            c if (c as u32) < 0x20 => {
                out.extend_from_slice(format!("\\u{:04x}", c as u32).as_bytes());
            }
            c => {
                let mut buf = [0u8; 4];
                out.extend_from_slice(c.encode_utf8(&mut buf).as_bytes());
            }
        }
    }
    out.push(b'"');
}

/// RFC 8785 §3.2.2.3: format `x` exactly as ECMAScript's `Number::toString`
/// would (ECMA-262 6.1.6.1.20). `json::parse` has already rejected any
/// non-finite value (see its module doc comment), so `x` here is always
/// finite.
///
/// ## Derivation
///
/// The abstract operation is defined in terms of integers `k` (count of
/// significant digits), `s` (those digits, as a string, no leading zero),
/// and `n` (decimal exponent) such that `s`, read as an integer, times
/// `10^(n-k)`, equals `x`, with `k` as small as possible (and, among
/// representations tied for smallest `k`, the one closest to `x`, ties
/// broken to even) — i.e. `s`/`n` are exactly the *shortest round-trip*
/// decimal digits and decimal point position for `x`.
///
/// Rust's `{:e}` (`LowerExp`) formatting of `f64` independently produces
/// that same shortest-round-trip digit string, in normalized scientific
/// form `d[.ddd]e<exponent>` with a single nonzero leading digit — this was
/// checked against every value in `conformance/numbers/` plus
/// `conformance/rfc8785/`'s `values.json`/`structures.json` numbers (see
/// `task/task-2-report.md` for the exact comparison against `node`'s
/// `String(x)`). So `k`, `s`, and `n` are read directly off that string
/// (`s` = the digits with the `.` removed, `k` = `s.len()`, and `n` =
/// (Rust's parsed exponent) `+ 1`, since `d.ddd * 10^exponent` is the same
/// number as `s * 10^(n-k)` when `n = exponent + 1`), and what remains is
/// purely ECMA-262's own formatting decision — fixed-point vs. exponential,
/// and where the decimal point or padding zeros go — applied to those
/// already-correct digits:
///
/// - `k <= n <= 21`: integer, `s` padded with `n-k` trailing zeros.
/// - `0 < n <= 21`: decimal point inserted `n` digits into `s`.
/// - `-6 < n <= 0`: `0.`, then `-n` leading zeros, then `s`.
/// - otherwise (`n > 21` or `n <= -6`): exponential notation,
///   `s`'s first digit (`.` + the rest, if `k > 1`) `e` `+`/`-` `|n-1|`.
///
/// The `n <= -6` boundary (rather than `n < -6`) is confirmed by
/// `conformance/numbers/exponent-switch/` (`1e-7` → `"1e-7"`, exponential)
/// against `conformance/numbers/small-fraction-just-below/` (`1e-6` →
/// `"0.000001"`, fixed) — the meta.json note names it explicitly ("n <= -6
/// exactly: the first magnitude that ECMAScript renders in exponential
/// form").
fn format_number(x: f64) -> String {
    if x == 0.0 {
        // Covers both +0.0 and -0.0 (IEEE 754 equality treats them equal);
        // ECMA-262 Number::toString step 2 returns "0" for either sign.
        return "0".to_string();
    }
    if x.is_sign_negative() {
        return format!("-{}", format_number(-x));
    }

    // x is finite, positive, nonzero from here on.
    let scientific = format!("{x:e}");
    let (mantissa, exponent_str) = scientific
        .split_once('e')
        .expect("Rust's LowerExp output for a finite f64 always contains 'e'");
    let exponent: i64 = exponent_str
        .parse()
        .expect("Rust's LowerExp exponent is always a plain signed integer");
    let digits = mantissa.replace('.', "");
    let k = digits.len() as i64;
    let n = exponent + 1;

    if k <= n && n <= 21 {
        let mut result = digits;
        result.push_str(&"0".repeat((n - k) as usize));
        result
    } else if 0 < n && n <= 21 {
        let (int_part, frac_part) = digits.split_at(n as usize);
        format!("{int_part}.{frac_part}")
    } else if -6 < n && n <= 0 {
        format!("0.{}{digits}", "0".repeat((-n) as usize))
    } else {
        let e = n - 1;
        let sign = if e >= 0 { "+" } else { "-" };
        if k == 1 {
            format!("{digits}e{sign}{}", e.abs())
        } else {
            let (first, rest) = digits.split_at(1);
            format!("{first}.{rest}e{sign}{}", e.abs())
        }
    }
}
