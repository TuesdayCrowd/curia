//! Task 4, Step 1, verified a second way.
//!
//! `tests/vectors.rs`'s `admit_reject` test already proves every
//! `conformance/admit-reject/` vector is rejected with its declared slug,
//! loading the corpus through `curia_testis::conformance::Corpus` and
//! routing by `profile`. This file duplicates that check by hand, against
//! the exact table in `task/task-4-brief.md`, deliberately without going
//! through the loader — so a bug in the loader's routing (or a future edit
//! to `tests/vectors.rs`'s assertions, which this task must not make)
//! cannot hide a regression in `admit` itself. If this file and the family
//! harness ever disagree, that disagreement is the bug to chase.

use curia_testis::conformance::conformance_dir;

fn read(case: &str) -> Vec<u8> {
    let path = conformance_dir()
        .join("admit-reject")
        .join(case)
        .join("input.json");
    std::fs::read(&path).unwrap_or_else(|e| panic!("failed to read {}: {e}", path.display()))
}

fn assert_rejects(case: &str, expected_slug: &str) {
    let input = read(case);
    match curia_testis::admit(&input) {
        Ok(()) => panic!("admit-reject/{case}: expected rejection `{expected_slug}`, was accepted"),
        Err(e) => assert_eq!(
            e.predicate(),
            expected_slug,
            "admit-reject/{case}: rejected with `{}`, want `{expected_slug}`",
            e.predicate()
        ),
    }
}

#[test]
fn duplicate_keys() {
    assert_rejects("duplicate-keys", "curia/admit/duplicate-key");
}

#[test]
fn invalid_utf8() {
    assert_rejects("invalid-utf8", "curia/admit/invalid-utf8");
}

#[test]
fn non_finite_number() {
    assert_rejects("non-finite-number", "curia/admit/non-finite-number");
}

#[test]
fn non_integer_number() {
    assert_rejects("non-integer-number", "curia/admit/non-integer-number");
}

#[test]
fn noncharacter() {
    assert_rejects("noncharacter", "curia/admit/noncharacter");
}

#[test]
fn over_nested() {
    assert_rejects("over-nested", "curia/admit/depth-exceeded");
}

#[test]
fn raw_nul_byte() {
    assert_rejects("raw-nul-byte", "curia/admit/nul-byte");
}

#[test]
fn unpaired_surrogate() {
    assert_rejects("unpaired-surrogate", "curia/admit/unpaired-surrogate");
}

#[test]
fn unsafe_integer() {
    assert_rejects("unsafe-integer", "curia/admit/unsafe-integer");
}
