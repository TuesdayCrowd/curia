//! Direct unit coverage for every `curia_testis::json::ParseError` variant.
//!
//! `src/json.rs`'s module doc comment (Task 2) flagged this as a real gap:
//! every vector `parse`/`canonicalize` are graded against is a success
//! case, so no test anywhere previously asserted that a given malformed
//! input produces a specific `ParseError` variant — only that *something*
//! rejects it, indirectly, through `admit`. This file closes that gap by
//! calling `curia_testis::json::parse` directly and matching on the
//! returned variant, one test per variant.

use curia_testis::json::{parse, ParseError};

#[test]
fn unexpected_eof_on_empty_input() {
    match parse(b"") {
        Err(ParseError::UnexpectedEof { pos: 0 }) => {}
        other => panic!("want UnexpectedEof{{pos: 0}}, got {other:?}"),
    }
}

#[test]
fn unexpected_eof_inside_open_array() {
    // `[` with nothing after it: the array is syntactically open, and the
    // parser looks for a value and finds end-of-input instead.
    match parse(b"[") {
        Err(ParseError::UnexpectedEof { .. }) => {}
        other => panic!("want UnexpectedEof, got {other:?}"),
    }
}

#[test]
fn unexpected_eof_inside_unterminated_string() {
    match parse(b"\"abc") {
        Err(ParseError::UnexpectedEof { pos: 0 }) => {}
        other => panic!("want UnexpectedEof{{pos: 0}} (the string's opening quote), got {other:?}"),
    }
}

#[test]
fn unexpected_char_on_bad_literal() {
    // `nul` is not `null`, `true`, or `false`; the `n` branch commits to
    // matching "null" and fails when it doesn't.
    match parse(b"nul") {
        Err(ParseError::UnexpectedChar { pos: 0 }) => {}
        other => panic!("want UnexpectedChar{{pos: 0}}, got {other:?}"),
    }
}

#[test]
fn unexpected_char_on_unrecognized_start_byte() {
    match parse(b"x") {
        Err(ParseError::UnexpectedChar { pos: 0 }) => {}
        other => panic!("want UnexpectedChar{{pos: 0}}, got {other:?}"),
    }
}

#[test]
fn invalid_utf8_rejected_up_front() {
    // 0xFF is never a valid UTF-8 byte (matches
    // `conformance/admit-reject/invalid-utf8`'s own note).
    match parse(&[b'"', 0xFF, 0xFE, b'"']) {
        Err(ParseError::InvalidUtf8) => {}
        other => panic!("want InvalidUtf8, got {other:?}"),
    }
}

#[test]
fn invalid_escape_on_unknown_backslash_sequence() {
    // `\q` is not one of RFC 8259 §7's escape characters.
    match parse(b"\"\\q\"") {
        Err(ParseError::InvalidEscape { pos: 1 }) => {}
        other => panic!("want InvalidEscape{{pos: 1}}, got {other:?}"),
    }
}

#[test]
fn unpaired_surrogate_lone_high() {
    // A high surrogate with nothing following it at all.
    match parse(b"\"\\uD800\"") {
        Err(ParseError::UnpairedSurrogate { pos: 1 }) => {}
        other => panic!("want UnpairedSurrogate{{pos: 1}}, got {other:?}"),
    }
}

#[test]
fn unpaired_surrogate_lone_low() {
    // A low surrogate with no preceding high surrogate.
    match parse(b"\"\\uDC00\"") {
        Err(ParseError::UnpairedSurrogate { pos: 1 }) => {}
        other => panic!("want UnpairedSurrogate{{pos: 1}}, got {other:?}"),
    }
}

#[test]
fn unpaired_surrogate_high_followed_by_non_surrogate() {
    // A high surrogate followed by a `\u` escape that is not a low
    // surrogate.
    match parse(b"\"\\uD800\\u0041\"") {
        Err(ParseError::UnpairedSurrogate { pos: 1 }) => {}
        other => panic!("want UnpairedSurrogate{{pos: 1}}, got {other:?}"),
    }
}

#[test]
fn raw_control_in_string_non_nul() {
    // A raw (unescaped) control byte other than NUL — 0x01 — inside a
    // string literal. Distinct from `admit`'s NUL-specific scan: `parse`
    // itself (RFC 8259 §7) rejects *any* unescaped byte below U+0020.
    let input: &[u8] = b"\"\x01\"";
    match parse(input) {
        Err(ParseError::RawControlInString { pos: 1 }) => {}
        other => panic!("want RawControlInString{{pos: 1}}, got {other:?}"),
    }
}

#[test]
fn invalid_number_bare_minus() {
    match parse(b"-") {
        Err(ParseError::InvalidNumber { pos: 0 }) => {}
        other => panic!("want InvalidNumber{{pos: 0}}, got {other:?}"),
    }
}

#[test]
fn invalid_number_trailing_decimal_point() {
    // A `.` with no digit after it: RFC 8259 §6 requires at least one digit
    // in the fraction part.
    match parse(b"1.") {
        Err(ParseError::InvalidNumber { pos: 0 }) => {}
        other => panic!("want InvalidNumber{{pos: 0}}, got {other:?}"),
    }
}

#[test]
fn invalid_number_trailing_exponent_sign() {
    match parse(b"1e+") {
        Err(ParseError::InvalidNumber { pos: 0 }) => {}
        other => panic!("want InvalidNumber{{pos: 0}}, got {other:?}"),
    }
}

#[test]
fn non_finite_number_overflow() {
    // 1e400 overflows f64 to +Infinity; RFC 8785 has no representation for
    // it (matches `conformance/admit-reject/non-finite-number`).
    match parse(b"1e400") {
        Err(ParseError::NonFiniteNumber { pos: 0 }) => {}
        other => panic!("want NonFiniteNumber{{pos: 0}}, got {other:?}"),
    }
}

#[test]
fn non_finite_number_negative_overflow() {
    match parse(b"-1e400") {
        Err(ParseError::NonFiniteNumber { pos: 0 }) => {}
        other => panic!("want NonFiniteNumber{{pos: 0}}, got {other:?}"),
    }
}

#[test]
fn trailing_data_after_scalar() {
    match parse(b"true false") {
        Err(ParseError::TrailingData { pos: 5 }) => {}
        other => panic!("want TrailingData{{pos: 5}}, got {other:?}"),
    }
}

#[test]
fn trailing_data_after_leading_zero_number() {
    // "01": the number grammar stops after the first "0" (RFC 8259 §6
    // forbids a second digit after a leading zero), leaving "1" as
    // unconsumed trailing data rather than an in-number syntax error.
    match parse(b"01") {
        Err(ParseError::TrailingData { pos: 1 }) => {}
        other => panic!("want TrailingData{{pos: 1}}, got {other:?}"),
    }
}

#[test]
fn depth_limit_exceeded_on_pathological_nesting() {
    // Far beyond `json::MAX_PARSE_DEPTH` (512) but well inside ADMIT's own
    // 1 MiB submission cap — a shape `admit` would also reject (mapped to
    // `curia/admit/depth-exceeded`; see `src/json.rs`'s `map_parse_error`),
    // but this test is specifically about `parse`'s own stack-safety guard
    // firing as a typed error, not about ADMIT's business rule.
    let input = "[".repeat(600);
    match parse(input.as_bytes()) {
        Err(ParseError::DepthLimitExceeded { .. }) => {}
        other => panic!("want DepthLimitExceeded, got {other:?}"),
    }
}

#[test]
fn depth_limit_not_exceeded_just_under_the_guard() {
    // Sanity check on the boundary itself: 511 opens (well under 512) with
    // a valid closing sequence must parse successfully, so the test above
    // is proven to be testing the guard and not some unrelated failure.
    let opens = "[".repeat(511);
    let closes = "]".repeat(511);
    let input = format!("{opens}0{closes}");
    assert!(
        parse(input.as_bytes()).is_ok(),
        "511 levels of nesting must be well within MAX_PARSE_DEPTH (512)"
    );
}
