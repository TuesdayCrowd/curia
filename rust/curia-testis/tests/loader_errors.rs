//! Direct evidence for the Task 1 brief's Step 4 constraint on the loader:
//! "no panics in the loader itself." A missing or malformed corpus file must
//! surface as a `LoaderError`, never a panic.
//!
//! `tests/vectors.rs` only ever points the loader at the real, well-formed
//! `conformance/` corpus, so it cannot exercise these paths. This file
//! builds small deliberately-broken corpora under the OS temp directory
//! (never under `conformance/` itself) and asserts `Corpus::load` returns
//! the specific typed error for each defect, using only `std` — no
//! additional dependency was added to test this.

use std::fs;
use std::path::{Path, PathBuf};

use curia_testis::conformance::{Corpus, LoaderError};

/// A fresh, empty scratch directory under the OS temp dir, unique to this
/// test process and this call site. Not under `conformance/` or anywhere
/// else in the cleanroom or the repository.
fn scratch_dir(label: &str) -> PathBuf {
    let dir = std::env::temp_dir().join(format!(
        "curia-testis-loader-test-{}-{}-{}",
        std::process::id(),
        label,
        fastrand_ish()
    ));
    fs::create_dir_all(&dir).expect("can create a scratch dir under the OS temp dir");
    dir
}

/// A cheap decorrelator so parallel test threads (`cargo test` runs these
/// concurrently by default) never race on the same directory name. Not a
/// real RNG - just the address of a fresh heap allocation, which ASLR and
/// the allocator both make vary run to run and thread to thread.
fn fastrand_ish() -> usize {
    let b = Box::new(0u8);
    Box::into_raw(b) as usize
}

fn write(path: &Path, contents: &str) {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).expect("can create parent dirs in the scratch corpus");
    }
    fs::write(path, contents).expect("can write a file in the scratch corpus");
}

fn write_bytes(path: &Path, contents: &[u8]) {
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent).expect("can create parent dirs in the scratch corpus");
    }
    fs::write(path, contents).expect("can write a file in the scratch corpus");
}

/// Scaffolds every top-level family directory, empty, so `Corpus::load`
/// finds a structurally valid (if vector-less) corpus before a test adds the
/// one broken case it wants to exercise.
fn scaffold_empty_corpus(root: &Path) {
    for family in [
        "rfc8785",
        "c4",
        "ordering",
        "unicode",
        "numbers",
        "admit-reject",
        "envelope",
    ] {
        fs::create_dir_all(root.join(family)).expect("can scaffold an empty family dir");
    }
}

#[test]
fn empty_corpus_loads_with_zero_vectors_in_every_family() {
    let root = scratch_dir("empty");
    scaffold_empty_corpus(&root);

    let corpus = Corpus::load(&root).expect("an empty but structurally valid corpus loads");
    assert_eq!(corpus.total_len(), 0);
}

#[test]
fn missing_root_is_a_typed_io_error_not_a_panic() {
    let root = std::env::temp_dir().join(format!(
        "curia-testis-loader-test-does-not-exist-{}",
        fastrand_ish()
    ));
    assert!(!root.exists());

    let err = Corpus::load(&root).expect_err("a nonexistent root must not load successfully");
    assert!(
        matches!(err, LoaderError::Io { .. }),
        "expected LoaderError::Io, got: {err:?}"
    );
}

#[test]
fn malformed_meta_json_is_a_typed_json_error() {
    let root = scratch_dir("bad-meta-json");
    scaffold_empty_corpus(&root);
    write(&root.join("c4/broken-case/meta.json"), "{ not valid json");
    write(&root.join("c4/broken-case/input.json"), "{}");
    write(&root.join("c4/broken-case/expected.canonical"), "{}");
    write(
        &root.join("c4/broken-case/expected.digest"),
        "0".repeat(64).as_str(),
    );

    let err = Corpus::load(&root).expect_err("malformed meta.json must not load");
    assert!(
        matches!(err, LoaderError::Json { .. }),
        "expected LoaderError::Json, got: {err:?}"
    );
}

#[test]
fn meta_json_missing_requirement_is_reported_not_panicked() {
    let root = scratch_dir("missing-requirement");
    scaffold_empty_corpus(&root);
    write(
        &root.join("c4/broken-case/meta.json"),
        r#"{"profile": "canonicalize-with-nfc"}"#,
    );
    write(&root.join("c4/broken-case/input.json"), "{}");
    write(&root.join("c4/broken-case/expected.canonical"), "{}");
    write(
        &root.join("c4/broken-case/expected.digest"),
        "0".repeat(64).as_str(),
    );

    let err = Corpus::load(&root).expect_err("meta.json without `requirement` must not load");
    assert!(
        matches!(
            err,
            LoaderError::MissingMetaField {
                field: "requirement",
                ..
            }
        ),
        "expected LoaderError::MissingMetaField{{field: \"requirement\"}}, got: {err:?}"
    );
}

#[test]
fn meta_json_empty_requirement_is_reported_not_panicked() {
    let root = scratch_dir("empty-requirement");
    scaffold_empty_corpus(&root);
    write(
        &root.join("c4/broken-case/meta.json"),
        r#"{"profile": "canonicalize-with-nfc", "requirement": "   "}"#,
    );
    write(&root.join("c4/broken-case/input.json"), "{}");
    write(&root.join("c4/broken-case/expected.canonical"), "{}");
    write(
        &root.join("c4/broken-case/expected.digest"),
        "0".repeat(64).as_str(),
    );

    let err = Corpus::load(&root).expect_err("a vector citing no requirement must not load");
    assert!(
        matches!(err, LoaderError::EmptyRequirement { .. }),
        "expected LoaderError::EmptyRequirement, got: {err:?}"
    );
}

#[test]
fn unknown_profile_is_reported_not_panicked() {
    let root = scratch_dir("unknown-profile");
    scaffold_empty_corpus(&root);
    write(
        &root.join("c4/broken-case/meta.json"),
        r#"{"profile": "not-a-real-profile", "requirement": "R6.8"}"#,
    );
    write(&root.join("c4/broken-case/input.json"), "{}");
    write(&root.join("c4/broken-case/expected.canonical"), "{}");
    write(
        &root.join("c4/broken-case/expected.digest"),
        "0".repeat(64).as_str(),
    );

    let err = Corpus::load(&root).expect_err("an unrecognized profile must not load");
    assert!(
        matches!(err, LoaderError::UnknownProfile { .. }),
        "expected LoaderError::UnknownProfile, got: {err:?}"
    );
}

#[test]
fn case_with_both_canonical_and_reject_is_ambiguous_not_panicked() {
    let root = scratch_dir("ambiguous-expectation");
    scaffold_empty_corpus(&root);
    write(
        &root.join("c4/broken-case/meta.json"),
        r#"{"profile": "canonicalize-with-nfc", "requirement": "R6.8"}"#,
    );
    write(&root.join("c4/broken-case/input.json"), "{}");
    write(&root.join("c4/broken-case/expected.canonical"), "{}");
    write(
        &root.join("c4/broken-case/expected.digest"),
        "0".repeat(64).as_str(),
    );
    write(
        &root.join("c4/broken-case/expect-reject"),
        "curia/admit/whatever",
    );

    let err = Corpus::load(&root)
        .expect_err("a case with both expected.canonical and expect-reject must not load");
    assert!(
        matches!(err, LoaderError::AmbiguousExpectation { .. }),
        "expected LoaderError::AmbiguousExpectation, got: {err:?}"
    );
}

#[test]
fn case_with_neither_canonical_nor_reject_is_reported_not_panicked() {
    let root = scratch_dir("missing-expectation");
    scaffold_empty_corpus(&root);
    write(
        &root.join("c4/broken-case/meta.json"),
        r#"{"profile": "canonicalize-with-nfc", "requirement": "R6.8"}"#,
    );
    write(&root.join("c4/broken-case/input.json"), "{}");

    let err = Corpus::load(&root)
        .expect_err("a case with neither expected.canonical nor expect-reject must not load");
    assert!(
        matches!(err, LoaderError::MissingExpectation { .. }),
        "expected LoaderError::MissingExpectation, got: {err:?}"
    );
}

#[test]
fn unpaired_rfc8785_input_is_reported_not_panicked() {
    let root = scratch_dir("unpaired-rfc8785");
    scaffold_empty_corpus(&root);
    // input-orphan.json with no output-orphan.json.
    write(&root.join("rfc8785/input-orphan.json"), "{}");

    let err = Corpus::load(&root)
        .expect_err("an input-*.json with no matching output-*.json must not load");
    assert!(
        matches!(err, LoaderError::UnpairedRfc8785Vector { .. }),
        "expected LoaderError::UnpairedRfc8785Vector, got: {err:?}"
    );
}

#[test]
fn envelope_submission_missing_envelope_field_is_reported_not_panicked() {
    let root = scratch_dir("envelope-missing-field");
    scaffold_empty_corpus(&root);
    write(
        &root.join("envelope/broken-case/meta.json"),
        r#"{"profile": "envelope", "requirement": "R6.37", "alg": "EdDSA"}"#,
    );
    // No "envelope" key.
    write(
        &root.join("envelope/broken-case/submission.json"),
        r#"{"signature": "a.b"}"#,
    );
    write(
        &root.join("envelope/broken-case/jwks.json"),
        r#"{"keys": []}"#,
    );
    write(
        &root.join("envelope/broken-case/private-keys.json"),
        r#"{"keys": []}"#,
    );
    write(&root.join("envelope/broken-case/expected.canonical"), "{}");
    write(
        &root.join("envelope/broken-case/expected.digest"),
        "0".repeat(64).as_str(),
    );

    let err = Corpus::load(&root)
        .expect_err("a submission.json with no \"envelope\" field must not load");
    assert!(
        matches!(
            err,
            LoaderError::MissingSubmissionField {
                field: "envelope",
                ..
            }
        ),
        "expected LoaderError::MissingSubmissionField{{field: \"envelope\"}}, got: {err:?}"
    );
}

#[test]
fn non_utf8_bytes_in_a_slug_file_are_reported_not_panicked() {
    let root = scratch_dir("non-utf8-slug");
    scaffold_empty_corpus(&root);
    write(
        &root.join("admit-reject/broken-case/meta.json"),
        r#"{"profile": "admit", "requirement": "R6.15"}"#,
    );
    write(&root.join("admit-reject/broken-case/input.json"), "{}");
    // 0xFF is never valid UTF-8 on its own.
    write_bytes(
        &root.join("admit-reject/broken-case/expect-reject"),
        &[0xFF, 0xFE, 0x00],
    );

    let err = Corpus::load(&root).expect_err("a non-UTF-8 expect-reject file must not load");
    assert!(
        matches!(err, LoaderError::NotUtf8 { .. }),
        "expected LoaderError::NotUtf8, got: {err:?}"
    );
}
