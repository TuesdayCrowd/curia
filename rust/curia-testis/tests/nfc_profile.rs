//! Permanent proof that `curia_testis::canonicalize_with_nfc` reproduces
//! every `canonicalize-with-nfc`-profile vector's `expected.canonical`
//! byte-for-byte — added in Task 3's Fix round 1.
//!
//! Modeled directly on `tests/canonicalize_pure.rs` (Task 2's equivalent
//! proof for the pure `canonicalize` function against `ordering/`/`numbers/`):
//! same pattern, same reasoning for why it needs to exist as a *file*, not a
//! one-off run.
//!
//! ## Why this file has to exist
//!
//! `tests/vectors.rs`'s family harness (`check_directory_vector` /
//! `check_canonicalize`) gates a vector's pass/fail on **both** the
//! canonical bytes matching **and** the digest matching
//! (`curia_testis::sha256_digest`, still a Task 5 stub as of this task). So
//! `cargo test --test vectors` cannot currently distinguish "correct
//! canonicalization, blocked only by an unrelated stub" from "actually
//! broken" for `c4/`, `ordering/`, `unicode/`, and `numbers/` — both show
//! the same `[FAIL] ... curia-testis/not-implemented` line. This file makes
//! that distinction directly and durably: it calls
//! `curia_testis::canonicalize_with_nfc` on each vector's `input.json` and
//! compares only against `expected.canonical`, with no dependency on
//! `sha256_digest`, `admit`, or `verify_envelope`.
//!
//! Fix round 1's reviewer note: the first version of this proof was a
//! scratch test, written, run, and deleted — the evidence left with the
//! file, and `cargo test` regressed to being unable to tell "correct" from
//! "blocked by a stub." This file is the durable replacement; it stays in
//! the tree and is expected to keep passing (and to gain more assertions,
//! not fewer) as Tasks 4/5 land.
//!
//! ## Scope
//!
//! Every vector whose `meta.json` declares `"profile":
//! "canonicalize-with-nfc"` per `conformance/README.md`'s profile table:
//! `c4/` (10), `ordering/` (3), `unicode/` (6, one of them a reject vector),
//! `numbers/` (9) — 27 canonical-bytes vectors,
//! loaded via `Corpus`'s existing per-family `Vec<DirectoryVector>` fields,
//! all of which carry that one profile (Task 2's report confirmed this by
//! grep before either task assumed it). The `envelope/` family's six
//! `expected.canonical` files are also generated the same way
//! (`conformance/README.md`, "The `envelope/` family": "canonicalize a real
//! Table 9 envelope... exactly canonicalize the envelope sub-object") and
//! are included here too, independent of `verify_envelope`/`sha256_digest`,
//! which `tests/vectors.rs`'s own `envelope` test still depends on.

use curia_testis::conformance::{Corpus, DirectoryVector, EnvelopeVector, Expectation};

fn corpus() -> &'static Corpus {
    // Deliberately not shared with tests/vectors.rs's or
    // tests/canonicalize_pure.rs's own `corpus()` caches — this file has no
    // dependency on either beyond the public loader API, and re-loading is
    // cheap (a few dozen small files).
    use std::sync::OnceLock;
    static CORPUS: OnceLock<Corpus> = OnceLock::new();
    CORPUS.get_or_init(|| {
        let root = curia_testis::conformance::conformance_dir();
        Corpus::load(&root).unwrap_or_else(|err| {
            panic!(
                "failed to load the conformance corpus from {}: {err}",
                root.display()
            )
        })
    })
}

/// Calls `curia_testis::canonicalize_with_nfc` directly and compares
/// against `v.expectation`'s `expected.canonical` bytes. Every
/// `canonicalize-with-nfc`-profile vector expects successful
/// canonicalization (none of `c4/ordering/unicode/numbers` is a `Reject`
/// case — rejection is `admit`'s concern), so a `Reject` expectation here
/// indicates a loader/corpus mismatch, not a canonicalization bug.
fn assert_matches_expected_canonical(family: &str, v: &DirectoryVector) {
    let expected = match &v.expectation {
        Expectation::Canonicalize { canonical, .. } => canonical,
        Expectation::Reject { .. } => panic!(
            "{family}/{}: expected a Canonicalize expectation, found a Reject one \
             (this indicates a loader/corpus mismatch, not a canonicalize_with_nfc bug)",
            v.case
        ),
    };
    match curia_testis::canonicalize_with_nfc(&v.input) {
        Ok(actual) => assert_eq!(
            &actual, expected,
            "{family}/{}: canonicalize_with_nfc produced different bytes than \
             expected.canonical",
            v.case
        ),
        Err(e) => panic!(
            "{family}/{}: canonicalize_with_nfc returned an error: {e}",
            v.case
        ),
    }
}

fn assert_family_matches(family: &'static str, vectors: &[DirectoryVector], expected_len: usize) {
    assert_eq!(
        vectors.len(),
        expected_len,
        "conformance/{family}/ vector count"
    );
    for v in vectors {
        // A canonicalization family may now contain `expect-reject` vectors:
        // `canonicalize_with_nfc` must reject a document whose member names
        // collide only *after* normalization, which ADMIT cannot catch because
        // the input genuinely has no duplicate. Those vectors are checked by
        // `tests/vectors.rs`; this file exists to pin the canonical-bytes claim
        // independently of the digest gate, so it has nothing to say about them.
        if matches!(v.expectation, Expectation::Reject { .. }) {
            continue;
        }
        assert_matches_expected_canonical(family, v);
    }
}

#[test]
fn c4_matches_expected_canonical() {
    assert_family_matches("c4", &corpus().c4, 10);
}

#[test]
fn ordering_matches_expected_canonical() {
    assert_family_matches("ordering", &corpus().ordering, 3);
}

#[test]
fn unicode_matches_expected_canonical() {
    assert_family_matches("unicode", &corpus().unicode, 6);
}

#[test]
fn numbers_matches_expected_canonical() {
    assert_family_matches("numbers", &corpus().numbers, 9);
}

/// The `envelope/` family's `expected.canonical` is the canonical form of
/// `submission.json`'s `envelope` sub-object (`conformance/README.md`), so
/// this checks `canonicalize_with_nfc` on `v.envelope` — the same bytes
/// `tests/vectors.rs`'s `check_envelope_vector` feeds it — but compares only
/// canonical bytes, independent of `sha256_digest`/`verify_envelope`.
fn assert_envelope_matches_expected_canonical(v: &EnvelopeVector) {
    match curia_testis::canonicalize_with_nfc(&v.envelope) {
        Ok(actual) => assert_eq!(
            actual, v.expected_canonical,
            "envelope/{}: canonicalize_with_nfc(envelope sub-object) produced \
             different bytes than expected.canonical",
            v.case
        ),
        Err(e) => panic!(
            "envelope/{}: canonicalize_with_nfc returned an error: {e}",
            v.case
        ),
    }
}

#[test]
fn envelope_matches_expected_canonical() {
    let vectors = &corpus().envelope;
    assert_eq!(vectors.len(), 6, "conformance/envelope/ vector count");
    for v in vectors {
        assert_envelope_matches_expected_canonical(v);
    }
}

// ---------------------------------------------------------------------
// Fix round 1, Finding 1 — normalization-induced duplicate member names
// must be rejected, not silently emitted into canonical output. Errata D1
// (revised R6.9) requires building "a normalized tree"; a tree with two
// equal keys in one object is not a tree canonicalize_with_nfc may hand to
// RFC 8785 ordering, because RFC 8785 §3.2.3 (and I-JSON) assume unique
// member names.
// ---------------------------------------------------------------------

/// `café` written two different ways — precomposed (`caf` + `U+00E9`) and
/// decomposed (`cafe` + `U+0301` COMBINING ACUTE ACCENT) — are distinct raw
/// wire keys that both normalize to the identical NFC string. This is the
/// coordinator's exact reproducer.
const CAFE_PRECOMPOSED: &str = "caf\u{00e9}"; // "café", U+00E9 already composed
const CAFE_DECOMPOSED: &str = "cafe\u{0301}"; // "café", e + combining acute (NFD)

fn duplicate_key_error_or_panic(input: &str) -> curia_testis::nfc::NfcError {
    match curia_testis::canonicalize_with_nfc(input.as_bytes()) {
        Ok(bytes) => panic!(
            "expected a duplicate-key rejection (raw or normalized), but \
             canonicalize_with_nfc succeeded with: {}",
            String::from_utf8_lossy(&bytes)
        ),
        Err(e) => e,
    }
}

#[test]
fn duplicate_after_normalization_is_rejected() {
    // {"café":1,"café":2} — precomposed first, decomposed second (the
    // coordinator's exact repro, spelled unambiguously via \u escapes since
    // the two keys are visually identical).
    let input = format!(r#"{{"{CAFE_PRECOMPOSED}":1,"{CAFE_DECOMPOSED}":2}}"#);
    let err = duplicate_key_error_or_panic(&input);
    assert_eq!(err.predicate(), "curia/canon/duplicate-normalized-key");
    assert!(
        matches!(&err, curia_testis::nfc::NfcError::DuplicateNormalizedKey { key } if key == "café"),
        "unexpected error variant/key: {err:?}"
    );
}

#[test]
fn duplicate_after_normalization_is_rejected_regardless_of_order() {
    // Same collision, reversed: decomposed first, precomposed second.
    // Swapping which spelling occupies which position must not change the
    // outcome (it doesn't change canonical.rs's key-ordering-independent
    // rejection either — the check runs before any sorting happens).
    let input = format!(r#"{{"{CAFE_DECOMPOSED}":1,"{CAFE_PRECOMPOSED}":2}}"#);
    let err = duplicate_key_error_or_panic(&input);
    assert_eq!(err.predicate(), "curia/canon/duplicate-normalized-key");
    assert!(
        matches!(&err, curia_testis::nfc::NfcError::DuplicateNormalizedKey { key } if key == "café"),
        "unexpected error variant/key: {err:?}"
    );
}

#[test]
fn duplicate_after_normalization_is_rejected_when_nested() {
    // The collision is inside a nested object, not at the top level — the
    // check must apply "at every level of nesting" (the brief's words),
    // not just the document root.
    let input = format!(r#"{{"outer":{{"{CAFE_PRECOMPOSED}":1,"{CAFE_DECOMPOSED}":2}}}}"#);
    let err = duplicate_key_error_or_panic(&input);
    assert_eq!(err.predicate(), "curia/canon/duplicate-normalized-key");
    assert!(
        matches!(&err, curia_testis::nfc::NfcError::DuplicateNormalizedKey { key } if key == "café"),
        "unexpected error variant/key: {err:?}"
    );
}

#[test]
fn distinct_normalized_keys_in_different_objects_are_not_a_collision() {
    // Two objects (siblings, under different parents) can each legitimately
    // contain a member that normalizes to "café" — the rule is scoped to
    // one object's own member list, not the whole document.
    let input = format!(r#"{{"a":{{"{CAFE_PRECOMPOSED}":1}},"b":{{"{CAFE_DECOMPOSED}":2}}}}"#);
    let result = curia_testis::canonicalize_with_nfc(input.as_bytes());
    assert!(
        result.is_ok(),
        "two different objects each containing a \"café\"-normalizing key must \
         not collide with each other: {result:?}"
    );
}

#[test]
fn genuinely_distinct_keys_are_still_accepted() {
    // Control: "café" and "cafe" (no accent) are genuinely different strings
    // even after NFC — this must succeed, with both members present, so the
    // collision check is not so broad it rejects ordinary distinct keys.
    let input = format!(r#"{{"{CAFE_PRECOMPOSED}":1,"cafe":2}}"#);
    let actual = curia_testis::canonicalize_with_nfc(input.as_bytes())
        .expect("\"café\" and \"cafe\" are distinct NFC strings and must be accepted");
    let text = String::from_utf8(actual).expect("canonical output is valid UTF-8");
    // RFC 8785 §3.2.3: UTF-16 code-unit order. "cafe" (all ASCII, U+0065
    // last) sorts before "café" (last unit U+00E9) — both are entirely
    // within the BMP, so UTF-16 code-unit order agrees with codepoint order
    // here: comparing the fourth character, 'e' (U+0065) < 'é' (U+00E9).
    assert_eq!(text, "{\"cafe\":2,\"caf\u{00e9}\":1}");
}

// ---------------------------------------------------------------------
// Fix round 2 — the duplicate check must distinguish two conditions
// (byte-identical raw names vs. names that only collide after NFC) rather
// than reporting both with the NFC slug and a message that is simply false
// for the raw case. See src/nfc.rs's module doc comment, "Normalization
// can create duplicate object members," for the full reasoning.
// ---------------------------------------------------------------------

#[test]
fn raw_duplicate_reuses_admit_slug_not_the_nfc_slug() {
    // The re-review's exact repro: two byte-identical raw names, no
    // normalization difference anywhere. This must NOT be reported as
    // curia/canon/duplicate-normalized-key (Fix round 1's bug) — the two
    // names are not "distinct," they are the same bytes twice, which is
    // precisely what conformance/admit-reject/duplicate-keys pins under
    // curia/admit/duplicate-key.
    let input = r#"{"a":1,"a":2}"#;
    let err = duplicate_key_error_or_panic(input);
    assert_eq!(
        err.predicate(),
        "curia/admit/duplicate-key",
        "a byte-identical raw duplicate must be reported under ADMIT's own \
         slug, not curia/canon/duplicate-normalized-key: {err:?}"
    );
    assert!(
        matches!(&err, curia_testis::nfc::NfcError::DuplicateRawKey { key } if key == "a"),
        "unexpected error variant/key: {err:?}"
    );
}

#[test]
fn raw_duplicate_message_does_not_claim_the_names_are_distinct() {
    // Fix round 1's Display text unconditionally said "two distinct member
    // names normalize to the same string" — false for {"a":1,"a":2}, where
    // the two names are not distinct at all. Pin the corrected wording
    // directly so a future edit can't silently reintroduce the false claim
    // for this input shape.
    let input = r#"{"a":1,"a":2}"#;
    let err = duplicate_key_error_or_panic(input);
    let message = err.to_string();
    assert!(
        !message.contains("distinct"),
        "the raw-duplicate message must not claim the two names are \
         distinct: {message:?}"
    );
    assert!(
        message.contains("curia/admit/duplicate-key") && message.contains("\"a\""),
        "message should name the ADMIT slug and the offending key: {message:?}"
    );
}

// ---------------------------------------------------------------------
// Fix round 3 — the ruling: when an object contains BOTH a raw duplicate
// and a separate NFC-induced collision, the raw duplicate always wins,
// regardless of member order. Fix round 2 made the outcome positional
// (whichever collision the single combined scan reached first); the
// re-review judged that a cross-implementation hazard, since an
// independently written verifier checking raw duplicates before
// normalization (an equally natural design) would always report the raw
// duplicate, and a member-order-dependent answer here could make two
// individually-correct implementations disagree on the reported slug for
// the same document — a false-positive release blocker under R14.6 with no
// actual defect behind it. All three orderings the re-review considered are
// pinned below so the order-*in*dependence is what is actually tested, not
// merely one direction of it.
// ---------------------------------------------------------------------

fn assert_raw_duplicate_wins(input: &str) {
    let err = duplicate_key_error_or_panic(input);
    assert_eq!(
        err.predicate(),
        "curia/admit/duplicate-key",
        "the raw duplicate must always win over a separate NFC collision, \
         regardless of member order: {err:?}"
    );
    assert!(
        matches!(&err, curia_testis::nfc::NfcError::DuplicateRawKey { key } if key == "a"),
        "unexpected error variant/key: {err:?}"
    );
}

#[test]
fn raw_duplicate_always_wins_when_raw_duplicate_is_first() {
    // "a"/"a" (raw duplicate) precedes the café/café NFC collision in
    // member order.
    let input = format!(r#"{{"a":1,"a":2,"{CAFE_PRECOMPOSED}":3,"{CAFE_DECOMPOSED}":4}}"#);
    assert_raw_duplicate_wins(&input);
}

#[test]
fn raw_duplicate_always_wins_when_nfc_collision_is_first() {
    // Reversed: the café/café NFC collision now precedes the "a"/"a" raw
    // duplicate in member order. Fix round 2 would have reported the NFC
    // collision here (it appears first); Fix round 3 must still report the
    // raw duplicate, because pass 1 scans every raw name in the object
    // before pass 2 ever computes a normalized name.
    let input = format!(r#"{{"{CAFE_PRECOMPOSED}":1,"{CAFE_DECOMPOSED}":2,"a":3,"a":4}}"#);
    assert_raw_duplicate_wins(&input);
}

#[test]
fn raw_duplicate_always_wins_with_an_unrelated_key_between_them() {
    // The third permutation the re-review used: the café/café NFC collision
    // first, then an unrelated key, then the "a"/"a" raw duplicate last —
    // confirming the outcome is a genuine property of pass-1-before-pass-2,
    // not an artifact of the two colliding pairs happening to sit next to
    // each other in the earlier two tests.
    let input =
        format!(r#"{{"{CAFE_PRECOMPOSED}":1,"{CAFE_DECOMPOSED}":2,"unrelated":5,"a":3,"a":4}}"#);
    assert_raw_duplicate_wins(&input);
}

// ---------------------------------------------------------------------
// R6.43 — the predicate names the condition, not the entry point that
// noticed it (errata E13's first recorded Rust divergence; measured by the
// differential harness once E14's comparison-rule gap was closed).
//
// `canonicalize` and `canonicalize_with_nfc` share one parser, so for any
// input the parser rejects they must report one slug. They did not:
// `NfcError::Parse(_)` collapsed every parse condition onto a single
// `curia/canon/parse-error`, a predicate naming the mechanism. The pair
// below is the whole property — same bytes, two entry points, one answer —
// and it is checked here rather than in the corpus because a published
// vector pins an entry point (R14.7).
// ---------------------------------------------------------------------

#[track_caller]
fn assert_both_canonicalizers_report(input: &[u8], expected: &str) {
    let pure = curia_testis::canonicalize(input)
        .err()
        .unwrap_or_else(|| panic!("canonicalize accepted input it must reject: {input:?}"));
    let nfc = curia_testis::canonicalize_with_nfc(input)
        .err()
        .unwrap_or_else(|| {
            panic!("canonicalize_with_nfc accepted input it must reject: {input:?}")
        });
    assert_eq!(
        pure.predicate(),
        expected,
        "canonicalize named the wrong condition for {input:?}"
    );
    assert_eq!(
        nfc.predicate(),
        expected,
        "canonicalize_with_nfc named the wrong condition for {input:?} \
         (this is the arm that used to answer curia/canon/parse-error)"
    );
}

#[test]
fn unpaired_surrogate_names_the_condition_at_both_entry_points() {
    assert_both_canonicalizers_report(br#""\uD800""#, "curia/admit/unpaired-surrogate");
}

#[test]
fn raw_nul_names_the_condition_at_both_entry_points() {
    assert_both_canonicalizers_report(b"\"\x00\"", "curia/admit/nul-byte");
    assert_both_canonicalizers_report(b"\x00", "curia/admit/nul-byte");
}

#[test]
fn invalid_utf8_names_the_condition_at_both_entry_points() {
    assert_both_canonicalizers_report(b"\x80", "curia/admit/invalid-utf8");
}

#[test]
fn raw_control_character_names_the_condition_at_both_entry_points() {
    assert_both_canonicalizers_report(b"\"\x01\"", "curia/admit/raw-control-character");
}

#[test]
fn non_finite_number_names_the_condition_at_both_entry_points() {
    assert_both_canonicalizers_report(b"9e702", "curia/admit/non-finite-number");
}

#[test]
fn truncated_input_names_the_condition_at_both_entry_points() {
    assert_both_canonicalizers_report(b"\"", "curia/admit/malformed-json");
}

#[test]
fn excessive_nesting_names_the_condition_at_both_entry_points() {
    // The parser's own stack-safety guard, not ADMIT's depth-32 rule — but
    // it still has a condition name, and both entry points must use it.
    let input = "[".repeat(600);
    assert_both_canonicalizers_report(input.as_bytes(), "curia/admit/depth-exceeded");
}

#[test]
fn raw_duplicate_member_names_the_condition_at_both_entry_points() {
    // The one condition that already agreed before this fix, because
    // `NfcError`'s own `DuplicateRawKey` variant reuses ADMIT's slug. Pinned
    // alongside the rest so the property is stated once for all of them.
    assert_both_canonicalizers_report(br#"{"a":1,"a":2}"#, "curia/admit/duplicate-key");
}

#[test]
fn no_entry_point_reports_a_mechanism_shaped_predicate() {
    // R6.43 names `curia/canon/parse-error` and
    // `curia/canon/normalization-failed` as the shapes forbidden outright.
    // A slug enumeration would rot; asserting the negative over every
    // condition above does not.
    for input in [
        br#""\uD800""#.as_slice(),
        b"\"\x00\"".as_slice(),
        b"\x80".as_slice(),
        b"\"\x01\"".as_slice(),
        b"9e702".as_slice(),
        b"\"".as_slice(),
        br#"{"a":1,"a":2}"#.as_slice(),
    ] {
        let slug = curia_testis::canonicalize_with_nfc(input)
            .err()
            .unwrap_or_else(|| panic!("expected a rejection for {input:?}"))
            .predicate()
            .to_owned();
        assert!(
            !slug.contains("parse-error") && !slug.contains("normalization-failed"),
            "canonicalize_with_nfc reported the mechanism, not the condition, \
             for {input:?}: {slug}"
        );
    }
}
