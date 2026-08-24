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

// ---------------------------------------------------------------------
// R6.39's other three caps: member count, string length, submission size.
//
// R6.39's second sentence is explicit -- "Published vectors SHALL exercise
// both sides of each of the four boundaries -- the value at the limit
// (accepted) and one past it (rejected)" -- and before this section only
// depth had both sides anywhere, in either implementation. `admit_fuzz.rs`
// sweeps values around three of the four, but grades every case on panic
// freedom alone and discards the verdict; the published corpus has one
// reject-side vector (`over-nested`) and nothing else.
//
// Expectations are derived from the `ADMIT_MAX_*` constants rather than from
// literals, which is only legitimate because
// `tests/published_admit_limits.rs` now pins those constants against R6.39's
// own sentence. Before that file existed, deriving from the constant is
// exactly what made this whole family blind to the constant's value.
// ---------------------------------------------------------------------

use curia_testis::json::{
    ADMIT_MAX_OBJECT_MEMBERS, ADMIT_MAX_STRING_BYTES, ADMIT_MAX_SUBMISSION_BYTES,
};

/// `{"k0":0,...}` with exactly `n` distinct members.
fn object_with_members(n: usize) -> Vec<u8> {
    let members: Vec<String> = (0..n).map(|i| format!("\"k{i}\":0")).collect();
    format!("{{{}}}", members.join(",")).into_bytes()
}

/// `{"s":"<body>"}` — the cap under test applies to the member *value*.
///
/// Member names and `\uXXXX`-escaped values are deliberately avoided: the two
/// implementations disagree about both, R6.39's wording does not settle
/// either, and a test here would freeze one reading by accident. Both are
/// recorded as open in `tools/differential-oracle/compare.mjs`'s supplemental
/// cases 8 and 9 instead.
fn object_with_string_value(body: &str) -> Vec<u8> {
    format!("{{\"s\":\"{body}\"}}").into_bytes()
}

/// A syntactically valid document of exactly `total` bytes that no cap other
/// than the submission-size cap can decide: sixteen members (far under the
/// 1,024-member cap), each holding a string far under the 256 KiB string cap,
/// nested one level deep.
fn document_of_exact_size(total: usize) -> Vec<u8> {
    const SLOTS: usize = 16;
    let skeleton = 2 + SLOTS * 6 + (SLOTS - 1); // {"a":"", ... ,"p":""}
    assert!(total >= skeleton, "{total} is smaller than the skeleton");

    let payload = total - skeleton;
    let base = payload / SLOTS;
    let extra = payload % SLOTS;
    assert!(
        base + extra <= ADMIT_MAX_STRING_BYTES,
        "a {total}-byte document spread over {SLOTS} members would trip the string cap first"
    );

    let mut doc = String::with_capacity(total);
    doc.push('{');
    for i in 0..SLOTS {
        if i > 0 {
            doc.push(',');
        }
        doc.push('"');
        doc.push((b'a' + i as u8) as char);
        doc.push_str("\":\"");
        for _ in 0..base + if i == 0 { extra } else { 0 } {
            doc.push('a');
        }
        doc.push('"');
    }
    doc.push('}');

    assert_eq!(doc.len(), total, "exact-size builder is off");
    doc.into_bytes()
}

#[test]
fn members_exactly_at_the_cap_are_accepted() {
    let doc = object_with_members(ADMIT_MAX_OBJECT_MEMBERS);
    assert_eq!(admit(&doc), Ok(()));
}

#[test]
fn members_one_past_the_cap_are_rejected() {
    let doc = object_with_members(ADMIT_MAX_OBJECT_MEMBERS + 1);
    let err = admit(&doc).expect_err("one member past the cap must be rejected");
    assert_eq!(err.predicate(), "curia/admit/members-exceeded");
}

#[test]
fn a_string_exactly_at_the_cap_is_accepted() {
    let doc = object_with_string_value(&"a".repeat(ADMIT_MAX_STRING_BYTES));
    assert_eq!(admit(&doc), Ok(()));
}

#[test]
fn a_string_one_byte_past_the_cap_is_rejected() {
    let doc = object_with_string_value(&"a".repeat(ADMIT_MAX_STRING_BYTES + 1));
    let err = admit(&doc).expect_err("one byte past the string cap must be rejected");
    assert_eq!(err.predicate(), "curia/admit/string-too-long");
}

/// R6.39 says the string cap is "measured in UTF-8 bytes", and an all-ASCII
/// suite cannot tell that from "measured in characters" -- the two agree on
/// every fixture above. U+00E9 is two UTF-8 bytes, so exactly half the cap's
/// worth of them sits on the boundary and one more character crosses it by
/// two bytes; an implementation counting characters would accept both, having
/// seen only 131,073 of a permitted 262,144.
///
/// Written as literal (unescaped) UTF-8, which is the one form both
/// implementations agree on -- see `object_with_string_value`.
#[test]
fn the_string_cap_counts_utf8_bytes_not_characters() {
    let at_cap = "\u{00e9}".repeat(ADMIT_MAX_STRING_BYTES / 2);
    assert_eq!(at_cap.len(), ADMIT_MAX_STRING_BYTES);
    assert_eq!(admit(&object_with_string_value(&at_cap)), Ok(()));

    let past_cap = "\u{00e9}".repeat(ADMIT_MAX_STRING_BYTES / 2 + 1);
    assert_eq!(past_cap.len(), ADMIT_MAX_STRING_BYTES + 2);
    let err = admit(&object_with_string_value(&past_cap))
        .expect_err("262,146 UTF-8 bytes must be rejected however few characters they are");
    assert_eq!(err.predicate(), "curia/admit/string-too-long");
}

#[test]
fn a_submission_exactly_at_the_size_cap_is_accepted() {
    let doc = document_of_exact_size(ADMIT_MAX_SUBMISSION_BYTES);
    assert_eq!(doc.len(), ADMIT_MAX_SUBMISSION_BYTES);
    assert_eq!(admit(&doc), Ok(()));
}

#[test]
fn a_submission_one_byte_past_the_size_cap_is_rejected() {
    let doc = document_of_exact_size(ADMIT_MAX_SUBMISSION_BYTES + 1);
    let err = admit(&doc).expect_err("one byte past the submission cap must be rejected");
    assert_eq!(err.predicate(), "curia/admit/size-exceeded");
}
