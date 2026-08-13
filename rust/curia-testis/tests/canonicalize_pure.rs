//! Targeted proof for Task 2 Steps 2 and 3, per the controller ruling in
//! `task/task-2-brief.md`.
//!
//! `conformance/ordering/` and `conformance/numbers/` both carry
//! `"profile": "canonicalize-with-nfc"` in every vector's `meta.json` (see
//! `task/task-2-report.md` for the grep confirming this), so
//! `tests/vectors.rs`'s profile-routed family harness sends every vector in
//! both families to `curia_testis::canonicalize_with_nfc` — Task 3's
//! function, still `NotImplementedError` after this task. That routing is
//! correct and this file does not change it.
//!
//! The ruling is that Task 2 proves the *pure* `canonicalize` function
//! handles the UTF-16 key-ordering behavior `ordering/` exists to test and
//! the ECMAScript number formatting `numbers/` exists to test, by calling
//! `curia_testis::canonicalize` directly on each vector's `input.json` and
//! comparing against that same vector's `expected.canonical` — sound
//! because NFC is the identity transform on all twelve of these inputs (no
//! key or value in either family contains a character with a canonical
//! decomposition), so `expected.canonical` is the same byte string either
//! function must produce. See the report for the independent spot-check of
//! that NFC-identity claim.
//!
//! This file deliberately does **not** touch `expected.digest`: digest
//! comparison is `curia_testis::sha256_digest`'s concern (Task 5, still a
//! stub), and is out of scope for what these two Steps ask this task to
//! prove.

use curia_testis::conformance::{Corpus, DirectoryVector};

fn corpus() -> &'static Corpus {
    // Deliberately not shared with tests/vectors.rs's own `corpus()` cache —
    // this file has no dependency on that module beyond the public loader
    // API, and re-loading is cheap (a few dozen small files).
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

/// Calls `curia_testis::canonicalize` directly (not `canonicalize_with_nfc`,
/// and not through `check_directory_vector`'s profile routing) and compares
/// against `v.expectation`'s `expected.canonical` bytes.
fn assert_pure_canonicalize_matches(v: &DirectoryVector) {
    let expected = match &v.expectation {
        curia_testis::conformance::Expectation::Canonicalize { canonical, .. } => canonical,
        curia_testis::conformance::Expectation::Reject { .. } => {
            panic!(
                "{}/{}: expected a Canonicalize expectation, found a Reject one \
                 (this indicates a loader/corpus mismatch, not a canonicalize bug)",
                v.family, v.case
            )
        }
    };
    match curia_testis::canonicalize(&v.input) {
        Ok(actual) => assert_eq!(
            &actual, expected,
            "{}/{}: curia_testis::canonicalize produced different bytes than \
             expected.canonical",
            v.family, v.case
        ),
        Err(e) => panic!(
            "{}/{}: curia_testis::canonicalize returned an error: {e}",
            v.family, v.case
        ),
    }
}

#[test]
fn ordering_family_passes_through_pure_canonicalize() {
    let vectors = &corpus().ordering;
    assert_eq!(vectors.len(), 3, "conformance/ordering/ vector count");
    for v in vectors {
        assert_pure_canonicalize_matches(v);
    }
}

#[test]
fn numbers_family_passes_through_pure_canonicalize() {
    let vectors = &corpus().numbers;
    assert_eq!(vectors.len(), 9, "conformance/numbers/ vector count");
    for v in vectors {
        assert_pure_canonicalize_matches(v);
    }
}
