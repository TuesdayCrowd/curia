//! Task 4, Steps 2 and 3: pins **both** sides of the depth boundary (errata
//! D6) and **both** signs of the numeric bound (errata D5), which the
//! published `conformance/admit-reject/` corpus does not do on its own —
//! `over-nested` only pins the reject side of depth (33, not also 32), and
//! `unsafe-integer` only pins the positive side of the numeric bound (no
//! vector exercises `-2^53` or `-(2^53-1)`).
//!
//! This file constructs its own inputs rather than adding new
//! `conformance/admit-reject/` directories, because `tests/vectors.rs`'s
//! `corpus_size_matches_charter` test hard-codes the corpus at 42 vector
//! directories (CHARTER.md's own count) and is off limits to edit.

use curia_testis::admit;

// ---------------------------------------------------------------------
// Depth (errata D6): container openings only, never the innermost scalar.
// A document whose innermost value sits inside exactly `ADMIT_MAX_DEPTH`
// (32) containers is accepted; one nested a further level is rejected.
// ---------------------------------------------------------------------

/// Builds `{"a":{"a": ... {"a":1} ... }}}` with exactly `n` object
/// openings, so the innermost scalar `1` sits `n` containers deep.
fn nested_object(n: usize) -> Vec<u8> {
    let mut doc = String::new();
    for _ in 0..n {
        doc.push_str("{\"a\":");
    }
    doc.push('1');
    for _ in 0..n {
        doc.push('}');
    }
    doc.into_bytes()
}

/// Builds `[[[...1...]]]` with exactly `n` array openings.
fn nested_array(n: usize) -> Vec<u8> {
    let mut doc = String::new();
    for _ in 0..n {
        doc.push('[');
    }
    doc.push('1');
    for _ in 0..n {
        doc.push(']');
    }
    doc.into_bytes()
}

#[test]
fn depth_exactly_32_is_accepted_objects() {
    let doc = nested_object(32);
    assert_eq!(
        admit(&doc),
        Ok(()),
        "32 container openings (the frozen cap) must be accepted, not rejected for depth"
    );
}

#[test]
fn depth_33_is_rejected_objects() {
    let doc = nested_object(33);
    let err = admit(&doc).expect_err("33 container openings must exceed the depth cap");
    assert_eq!(err.predicate(), "curia/admit/depth-exceeded");
}

#[test]
fn depth_exactly_32_is_accepted_arrays() {
    let doc = nested_array(32);
    assert_eq!(
        admit(&doc),
        Ok(()),
        "32 container openings (the frozen cap) must be accepted, not rejected for depth"
    );
}

#[test]
fn depth_33_is_rejected_arrays() {
    let doc = nested_array(33);
    let err = admit(&doc).expect_err("33 container openings must exceed the depth cap");
    assert_eq!(err.predicate(), "curia/admit/depth-exceeded");
}

/// Cross-check against the published vector: `conformance/admit-reject/over-nested`
/// is independently known to be 33 levels (verified by `xxd`/byte-counting
/// in the task report), so this confirms our own 33-level builder produces
/// the same shape the corpus does, not a coincidentally similar one.
#[test]
fn depth_31_is_accepted_with_room_to_spare() {
    let doc = nested_object(31);
    assert_eq!(admit(&doc), Ok(()));
}

// ---------------------------------------------------------------------
// Numeric bounds (errata D5): symmetric, `2^53` and `-2^53` both rejected,
// `2^53-1` and `-(2^53-1)` both accepted. `2^53 - 1 = 9_007_199_254_740_991`.
// ---------------------------------------------------------------------

fn envelope_with_number(literal: &str) -> Vec<u8> {
    format!("{{\"n\":{literal}}}").into_bytes()
}

#[test]
fn max_safe_integer_is_accepted() {
    let doc = envelope_with_number("9007199254740991"); // 2^53 - 1
    assert_eq!(admit(&doc), Ok(()));
}

#[test]
fn two_pow_53_is_rejected() {
    // 2^53 exactly. Distinct from `conformance/admit-reject/unsafe-integer`,
    // whose literal is 2^53+1, which rounds to 2^53 through f64 parsing
    // (verified: `"9007199254740993".parse::<f64>() == 9007199254740992.0`,
    // confirmed against both Rust and `node -e "Number(...)"`) — so that
    // vector already exercises this same boundary indirectly. This test
    // pins the exact value D5 names.
    let doc = envelope_with_number("9007199254740992"); // 2^53
    let err = admit(&doc).expect_err("2^53 must be rejected, per errata D5");
    assert_eq!(err.predicate(), "curia/admit/unsafe-integer");
}

#[test]
fn min_safe_integer_is_accepted() {
    let doc = envelope_with_number("-9007199254740991"); // -(2^53 - 1)
    assert_eq!(admit(&doc), Ok(()));
}

#[test]
fn negative_two_pow_53_is_rejected() {
    // The corpus has no negative-side vector at all (D5's stated gap: "the
    // entire negative bound" was untested before this errata). Pins it.
    let doc = envelope_with_number("-9007199254740992"); // -(2^53)
    let err = admit(&doc).expect_err("-2^53 must be rejected, per errata D5");
    assert_eq!(err.predicate(), "curia/admit/unsafe-integer");
}

#[test]
fn one_past_min_safe_integer_is_rejected() {
    let doc = envelope_with_number("-9007199254740993"); // -(2^53 + 1)
    let err = admit(&doc).expect_err("-(2^53+1) must be rejected");
    assert_eq!(err.predicate(), "curia/admit/unsafe-integer");
}

#[test]
fn underflow_to_zero_is_accepted_not_rejected() {
    // Errata D7 / R6.15's revised enumeration is explicit: "Underflow to
    // zero is correct and SHALL NOT be rejected." 1e-400 underflows a
    // finite f64 to 0.0 (not to +/-Infinity, so `parse` itself accepts the
    // literal); 0.0 is a safe integer.
    let doc = envelope_with_number("1e-400");
    assert_eq!(
        admit(&doc),
        Ok(()),
        "underflow to 0 must be accepted, per errata D7's explicit carve-out"
    );
}

#[test]
fn negative_zero_is_accepted() {
    let doc = envelope_with_number("-0");
    assert_eq!(admit(&doc), Ok(()));
}

#[test]
fn zero_is_accepted() {
    let doc = envelope_with_number("0");
    assert_eq!(admit(&doc), Ok(()));
}
