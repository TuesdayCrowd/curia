//! Covering tests for the Task 2 "fix round 1" number-formatting tie-break
//! defect: `curia_testis::canonicalize`'s number formatting (RFC 8785
//! §3.2.2.3, deferring to ECMA-262 `Number::toString`) resolved an exact
//! decimal tie the wrong way — always toward the larger, odd-favoring
//! candidate — instead of ECMA-262's explicit "choose the one that is
//! even." An adversarial differential probe (`task/task-2-probe-report.md`)
//! found this via a 2,000,000-sample scan (509 mismatches, ~1 in 3,930) and
//! minimized it to a single reproducer: `629266065803222.25` (exact double
//! value) canonicalizes to `629266065803222.3`, when the correct,
//! ECMA-262-conformant, `node`-confirmed output is `629266065803222.2`.
//!
//! ## Where these cases came from
//!
//! None of the tie cases below are copied from the prober's report (only
//! the minimized reproducer is, since that's the confirmed defect this
//! task was dispatched to fix) — see `task/task-2-report.md`'s "Fix round
//! 1" section for the derivation in full. In short: a tie occurs exactly
//! when a finite `f64`'s *exact* decimal value sits precisely halfway
//! between its two candidate decimal strings at the shortest round-trip
//! digit count. Two independent, self-written search methods found the
//! twelve cases below (six where the correct answer is the smaller/even
//! candidate and the fix must *stop* rounding up, six where the correct
//! answer is the larger/even candidate and the fix must *still* round up):
//!
//! 1. **Fractional construction**: build a candidate string
//!    `"{random W-digit integer}.{dyadic fraction}"` (fractions from
//!    `{.5, .25, .75, .125, .375, .625, .875, .0625, .3125}` — all exact
//!    binary fractions), parse it to `f64`, and directly test the tie
//!    condition (see below) against the resulting value.
//! 2. **Raw bit-pattern scan**: generate uniformly random `f64` bit
//!    patterns and test the same condition directly.
//!
//! The tie condition itself was tested directly and mathematically, not
//! inferred from Rust's (buggy) output: for a candidate `x`, get the
//! trusted shortest-round-trip digit count `k` (`{x:e}`'s digit count,
//! never in dispute per the probe) and the *exact* decimal expansion (via
//! `{:.1100e}`, confirmed exact — see the report), then check whether
//! digit `k+1` of the exact expansion is `5` and every digit after it is
//! `0`. That is precisely "the exact value is equidistant from the two
//! `k`-digit candidates" — a tie, by the same definition ECMA-262 uses,
//! independent of what any implementation (buggy or not) currently
//! outputs for it.
//!
//! **13 tie cases total were verified against `node`** before being
//! selected for this file: the reported reproducer, all 6 "correct answer
//! is the floor/even candidate" cases below, and all 6 "correct answer is
//! the ceil/even candidate" cases below (the transcript is in the fix
//! report). The two categories matter: the probe's 509-sample scan only
//! ever found the crate erring in the *first* direction (always
//! rounding up when it shouldn't); the second category exists specifically
//! to prove the fix doesn't just "always round down," which would trivially
//! satisfy the probe's observed pattern without implementing the actual
//! tie-break rule — it must still round up when *that* is the even choice.

fn assert_canonicalizes_to(input: &str, expected: &str) {
    let out = curia_testis::canonicalize(input.as_bytes())
        .unwrap_or_else(|e| panic!("canonicalize({input:?}) returned an error: {e}"));
    let out = String::from_utf8(out).expect("canonicalize output is always valid UTF-8");
    assert_eq!(
        out, expected,
        "canonicalize({input:?}): got {out:?}, want {expected:?}"
    );
}

// ---------------------------------------------------------------------
// The confirmed reproducer from the probe report, minimized to a bare
// top-level number (no object wrapper needed — canonicalize accepts any
// single top-level JSON value).
// ---------------------------------------------------------------------

#[test]
fn reported_tie_629266065803222_25() {
    // f64 bits 4301e28362923eb2; exact value 629266065803222.25 precisely
    // (confirmed via {:.30e} in the fix report). node: String(629266065803222.25)
    // === "629266065803222.2" (even last digit 2, not the crate's
    // pre-fix "629266065803222.3").
    assert_canonicalizes_to("629266065803222.25", "629266065803222.2");
}

// ---------------------------------------------------------------------
// Self-derived tie cases, category 1: floor (the smaller, already-truncated
// candidate) is even, so the correct output must NOT round up. This is
// the direction the probe found broken (509/509 mismatches rounded up when
// they should not have).
// ---------------------------------------------------------------------

#[test]
fn self_derived_ties_floor_is_even() {
    let cases: &[(&str, &str)] = &[
        ("9668674093580.0625", "9668674093580.062"),
        ("9914481542787.0625", "9914481542787.062"),
        ("-9789431268902.0625", "-9789431268902.062"),
        ("-9356919416932.0625", "-9356919416932.062"),
        ("-9817943341338.0625", "-9817943341338.062"),
        ("-8975258902829.0625", "-8975258902829.062"),
    ];
    for (input, expected) in cases {
        assert_canonicalizes_to(input, expected);
    }
}

// ---------------------------------------------------------------------
// Self-derived tie cases, category 2: ceil (the larger candidate) is even,
// so the correct output MUST round up. Rust's pre-fix `{:e}` already always
// rounds up, so these cases happened to already pass before the fix — they
// are included specifically to prove the *fixed* code still rounds up when
// that is the even (correct) choice, rather than having overcorrected into
// "always keep the floor."
// ---------------------------------------------------------------------

#[test]
fn self_derived_ties_ceil_is_even() {
    let cases: &[(&str, &str)] = &[
        ("82109349177293.375", "82109349177293.38"),
        ("-75139545666212.375", "-75139545666212.38"),
        ("-99099120894729.375", "-99099120894729.38"),
        ("87710758523537.375", "87710758523537.38"),
        ("72490054649030.375", "72490054649030.38"),
        ("-86772742533763.375", "-86772742533763.38"),
    ];
    for (input, expected) in cases {
        assert_canonicalizes_to(input, expected);
    }
}

// ---------------------------------------------------------------------
// Full-carry-through regression: rounding an exact expansion up from an
// all-9s digit prefix must collapse to a single significant digit ("1")
// with the exponent bumped, not grow the digit count. This is not a tie
// case (1e-7's remainder starts with '9', not '5' — see the fix report),
// but it is a real regression an early version of this fix introduced
// while getting the tie-break right (initial output was "1.0e-7", not
// "1e-7") and conformance/numbers/exponent-switch/ pins the correct
// answer, so it is repeated here as a targeted, self-contained guard on
// exactly the code path the tie-break rewrite touched.
// ---------------------------------------------------------------------

#[test]
fn full_carry_through_collapses_to_one_significant_digit() {
    assert_canonicalizes_to("1e-7", "1e-7");
}

// ---------------------------------------------------------------------
// Regression pins for all nine conformance/numbers/ vectors, so the
// tie-break rewrite of format_number is checked against every case the
// family already covers, self-contained in this file (in addition to
// tests/canonicalize_pure.rs's own corpus-driven coverage of the same
// nine vectors). Values transcribed from conformance/numbers/*/input.json
// and expected.canonical; the object-wrapper shape matches those fixtures
// exactly ({"n":...}, no spaces).
// ---------------------------------------------------------------------

#[test]
fn numbers_family_regression_pins() {
    let cases: &[(&str, &str, &str)] = &[
        ("exponent-switch", r#"{"n":1e-7}"#, r#"{"n":1e-7}"#),
        ("exponent", r#"{"n":1e2}"#, r#"{"n":100}"#),
        ("integer-plain", r#"{"n":1}"#, r#"{"n":1}"#),
        (
            "large-exact-expansion",
            r#"{"n":123456789012345680000}"#,
            r#"{"n":123456789012345680000}"#,
        ),
        ("negative-zero", r#"{"n":-0}"#, r#"{"n":0}"#),
        (
            "safe-max",
            r#"{"n":9007199254740991}"#,
            r#"{"n":9007199254740991}"#,
        ),
        (
            "small-fraction-boundary",
            r#"{"n":1e-5}"#,
            r#"{"n":0.00001}"#,
        ),
        (
            "small-fraction-just-below",
            r#"{"n":1e-6}"#,
            r#"{"n":0.000001}"#,
        ),
        ("trailing-zero", r#"{"n":1.0}"#, r#"{"n":1}"#),
    ];
    for (case, input, expected) in cases {
        let out = curia_testis::canonicalize(input.as_bytes())
            .unwrap_or_else(|e| panic!("{case}: canonicalize returned an error: {e}"));
        let out = String::from_utf8(out).expect("canonicalize output is always valid UTF-8");
        assert_eq!(&out, expected, "{case}: got {out:?}, want {expected:?}");
    }
}
