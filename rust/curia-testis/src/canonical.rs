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
//!   `Number::toString`, **including its round-half-to-even tie-break**.
//!   See [`format_number`] for the derivation; `node` (`String(x)`) was
//!   used as the oracle against every vector in `conformance/numbers/` and
//!   against every tie case in `tests/number_ties.rs` (see the report,
//!   "Fix round 1" section — an adversarial probe found that Rust's own
//!   `{:e}` formatter, while correct on digit *count*, resolves an exact
//!   decimal tie the wrong way, and `format_number` no longer trusts it
//!   for tie resolution).

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
/// representations tied for smallest `k`, the one closest to `x`, **ties
/// broken to even**) — i.e. `s`/`n` are exactly the *shortest round-trip*
/// decimal digits and decimal point position for `x`.
///
/// Once `k`, `s`, and `n` are known, what remains is purely ECMA-262's own
/// formatting decision — fixed-point vs. exponential, and where the
/// decimal point or padding zeros go:
///
/// - `k <= n <= 21`: integer, `s` padded with `n-k` trailing zeros.
/// - `0 < n <= 21`: decimal point inserted `n` digits into `s`.
/// - `-6 < n <= 0`: `0.`, then `-n` leading zeros, then `s`.
/// - otherwise (`n > 21` or `n <= -6`): exponential notation,
///   `s`'s first digit (`.` + the rest, if `k > 1`) `e` `+`/`-` `|n-1|`.
///
/// That part is [`render_digits`], unchanged since this function's first
/// version. The `n <= -6` boundary (rather than `n < -6`) is confirmed by
/// `conformance/numbers/exponent-switch/` (`1e-7` → `"1e-7"`, exponential)
/// against `conformance/numbers/small-fraction-just-below/` (`1e-6` →
/// `"0.000001"`, fixed) — the meta.json note names it explicitly ("n <= -6
/// exactly: the first magnitude that ECMAScript renders in exponential
/// form").
///
/// ## Where `k`, `s`, `n` come from (revised after the round-1 fix)
///
/// This function's first version read `s`/`n` directly off Rust's `{:e}`
/// (`LowerExp`) formatting, on the premise that it produces the same
/// shortest-round-trip digit string ECMA-262 requires. An adversarial probe
/// (`task/task-2-probe-report.md`) found that premise half right: `{:e}`'s
/// digit **count** `k` is correct (never in dispute, including across the
/// probe's 2,015,396 calls), but on an **exact** decimal tie — the true
/// value of `x` sitting precisely halfway between the two `k`-digit decimal
/// candidates, both of which round-trip back to `x` — `{:e}` always picks
/// the larger (odd-favoring) one, not ECMA-262's even one.
///
/// The fix: keep trusting `{:e}` for `k` alone ([`shortest_round_trip_len`]),
/// and derive `s`/`n` independently, by direct construction rather than by
/// trusting a second thing about `{:e}`. `{:.*e}` (fixed-precision
/// `LowerExp`), unlike `{:e}`, is **exact** once given enough digits — every
/// finite `f64` is `m * 2^e` for integers `m`, `e`, which always has a
/// *finite* decimal expansion, so a large enough requested precision hits
/// nothing but trailing exact zeros past the value's own precision, never a
/// truncation (verified directly on the worst case, `f64::from_bits(1)`,
/// the smallest subnormal — see the fix report). Given that exact
/// expansion, "round to `k` significant digits, ties to even" is answered
/// by simple digit inspection: look at the digit immediately after the kept
/// `k`, and everything after it. [`shortest_round_trip_digits_ties_to_even`]
/// does exactly this, and no longer reads digits from `{:e}` at all — only
/// `k`, its one trusted fact.
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
    let (digits, n) = shortest_round_trip_digits_ties_to_even(x);
    render_digits(&digits, n)
}

/// The digit *count* of the shortest decimal string that round-trips back
/// to `x` (positive, finite, nonzero). Trusted from Rust's `{:e}`
/// formatting — the probe's 2,015,396 calls found this count is never
/// wrong, only (on an exact tie) which `k`-digit string `{:e}` picks, which
/// is why [`format_number`] no longer asks `{:e}` for anything else.
fn shortest_round_trip_len(x: f64) -> usize {
    let scientific = format!("{x:e}");
    let mantissa = scientific
        .split_once('e')
        .expect("Rust's LowerExp output for a finite f64 always contains 'e'")
        .0;
    mantissa.bytes().filter(|&b| b != b'.').count()
}

/// Digits requested (after the leading digit) from `{:.*e}` when computing
/// the exact decimal expansion of an `f64`. Every finite `f64`'s exact
/// decimal expansion terminates (see [`exact_decimal_digits`]'s doc
/// comment); the longest one belongs to the smallest subnormal,
/// `f64::from_bits(1)` (`5e-324`), whose exact expansion needs on the order
/// of 750 significant digits (confirmed directly — see the fix report).
/// 1100 leaves comfortable headroom over that for every finite `f64`.
const EXACT_PRECISION: usize = 1100;

/// The *exact* (not rounded, not truncated) decimal significant digits of
/// `x` (positive, finite, nonzero), out to [`EXACT_PRECISION`] digits past
/// the leading one, plus the decimal exponent `n` in this module's
/// convention (`x == 0.<digits> * 10^n`, matching [`format_number`]'s `n`).
///
/// This relies on `{:.*e}` (fixed-precision `LowerExp`) being *exact*, not
/// merely correctly-rounded-and-truncated: every finite `f64` equals
/// `m * 2^e` for integers `m` and `e`; for `e >= 0` that is an exact
/// integer, and for `e < 0` it equals `m * 5^|e| / 10^|e|`, an exact
/// *finite* decimal fraction (never a repeating one — the only prime
/// factors involved are 2 and 5). So there is no infinite tail to round
/// away: past `x`'s own finite precision, every further requested digit is
/// an exact `0`, not an approximation. Verified directly (see the fix
/// report) on `f64::from_bits(1)` (the smallest subnormal, the longest
/// possible exact expansion) and `f64::MAX`, both of which show only exact
/// trailing zeros well within [`EXACT_PRECISION`] digits.
fn exact_decimal_digits(x: f64) -> (Vec<u8>, i64) {
    let exact = format!("{:.*e}", EXACT_PRECISION, x);
    let (mantissa, exponent_str) = exact
        .split_once('e')
        .expect("Rust's LowerExp output for a finite f64 always contains 'e'");
    let exponent: i64 = exponent_str
        .parse()
        .expect("Rust's LowerExp exponent is always a plain signed integer");
    let digits: Vec<u8> = mantissa.bytes().filter(|&b| b != b'.').collect();
    (digits, exponent + 1)
}

/// Computes ECMA-262 `Number::toString`'s `s` (as ASCII digit bytes) and
/// `n` for `x` (positive, finite, nonzero): the `k`-digit decimal string
/// nearest `x`, **ties broken to even**, where `k` is
/// [`shortest_round_trip_len`]'s trusted digit count. This is the function
/// the round-1 fix report is about — see [`format_number`]'s doc comment
/// for why it no longer reads digits from `{:e}`.
///
/// Works by truncating [`exact_decimal_digits`]'s exact expansion to `k`
/// digits and inspecting exactly one more digit's worth of remainder to
/// decide whether to round up, down, or (an exact tie: the very next digit
/// is `5` and every digit after it is `0`) toward the even neighbor.
fn shortest_round_trip_digits_ties_to_even(x: f64) -> (Vec<u8>, i64) {
    let k = shortest_round_trip_len(x);
    let (exact, exact_n) = exact_decimal_digits(x);
    debug_assert!(
        exact.len() > k,
        "EXACT_PRECISION digits ({}) must always outnumber the shortest \
         round-trip length k ({k}); the longest possible k for any finite \
         f64 is 17",
        exact.len()
    );

    let mut kept: Vec<u8> = exact[..k].to_vec();
    let remainder = &exact[k..];

    let round_up = if remainder[0] < b'5' {
        false
    } else if remainder[0] > b'5' {
        true
    } else if remainder[1..].iter().any(|&d| d != b'0') {
        // Strictly more than half: "5" followed by a nonzero digit.
        true
    } else {
        // Exact tie ("5" followed by nothing but "0"s): ECMA-262 requires
        // the even candidate. `kept` here is still the smaller (floor)
        // candidate, so round up only if *it* is odd.
        (kept[k - 1] - b'0') % 2 == 1
    };

    // If `round_up` carries all the way through `kept` (every kept digit
    // was '9'), the rounded value is an exact power of ten -- which
    // collapses to a *single* significant digit "1" at `n+1`, not to a
    // (k+1)-digit "1" followed by k trailing zeros. This is not a cosmetic
    // choice: dropping the trailing zeros is what keeps `k` the *shortest*
    // round-trip length, matching what `shortest_round_trip_len` (Rust's
    // trusted `{:e}`) would itself report for the rounded value. Caught as
    // a genuine regression while fixing this function (`numbers/exponent-switch`,
    // `1e-7`, whose exact expansion is `9.999999999999999...e-8` — an
    // initial version of this fix grew the digit count to 2 ("10") here
    // instead of collapsing to 1 ("1"), producing `1.0e-7` instead of
    // `1e-7`; see the fix report for the full trace).
    let (kept, n) = if round_up && increment_decimal_digits(&mut kept) {
        (vec![b'1'], exact_n + 1)
    } else {
        (kept, exact_n)
    };
    (kept, n)
}

/// Adds 1 to the decimal integer `digits` (ASCII `'0'..='9'` bytes, most
/// significant first) represents, in place, with carry propagation.
/// Returns `true` if the carry rippled past the most significant digit
/// (`digits` was entirely `'9'`s) — the caller collapses that case to a
/// single significant digit rather than keeping the grown, trailing-zero
/// digit string (see the caller's doc comment for why).
fn increment_decimal_digits(digits: &mut [u8]) -> bool {
    for d in digits.iter_mut().rev() {
        if *d == b'9' {
            *d = b'0';
        } else {
            *d += 1;
            return false;
        }
    }
    true
}

/// Applies ECMA-262 `Number::toString`'s fixed-vs-exponential notation
/// decision to already-correct digits (`digits`, ASCII `'0'..='9'` bytes)
/// and decimal exponent `n` — the part of the derivation the round-1 fix
/// left untouched; see [`format_number`]'s doc comment for the branch
/// derivation and the `n <= -6` boundary's vector citation.
fn render_digits(digits: &[u8], n: i64) -> String {
    let s = std::str::from_utf8(digits).expect("digits are always ASCII '0'..='9' bytes");
    let k = digits.len() as i64;

    if k <= n && n <= 21 {
        format!("{s}{}", "0".repeat((n - k) as usize))
    } else if 0 < n && n <= 21 {
        let (int_part, frac_part) = s.split_at(n as usize);
        format!("{int_part}.{frac_part}")
    } else if -6 < n && n <= 0 {
        format!("0.{}{s}", "0".repeat((-n) as usize))
    } else {
        let e = n - 1;
        let sign = if e >= 0 { "+" } else { "-" };
        if k == 1 {
            format!("{s}e{sign}{}", e.abs())
        } else {
            let (first, rest) = s.split_at(1);
            format!("{first}.{rest}e{sign}{}", e.abs())
        }
    }
}
