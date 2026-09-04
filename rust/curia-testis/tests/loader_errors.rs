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

use curia_testis::conformance::{Corpus, Index, LoaderError};

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
        "admit-accept",
        "envelope",
        "merkle",
        "acta",
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

// ---------------------------------------------------------------------
// conformance/index.json — R6.45
//
// `tests/vectors.rs` runs the index check against the real corpus, where it
// passes. These build small corpora whose index deliberately disagrees with
// disk, so the check is known to fail when it should — an index check that
// cannot go red is indistinguishable, in a passing log, from one that can.
// ---------------------------------------------------------------------

/// An `index.json` that matches [`scaffold_empty_corpus`]: every family this
/// runner loads, listed with the right shape and profiles and a count of
/// zero. Tests below break exactly one thing about it.
fn write_matching_index(root: &Path) {
    write(
        &root.join("index.json"),
        r#"{
  "directories": [
    {"name": "rfc8785", "family": true, "shape": "file-pairs", "profiles": ["rfc8785"], "count": 0},
    {"name": "c4", "family": true, "shape": "directory", "profiles": ["canonicalize-with-nfc"], "count": 0},
    {"name": "ordering", "family": true, "shape": "directory", "profiles": ["canonicalize-with-nfc"], "count": 0},
    {"name": "unicode", "family": true, "shape": "directory", "profiles": ["canonicalize-with-nfc"], "count": 0},
    {"name": "numbers", "family": true, "shape": "directory", "profiles": ["canonicalize-with-nfc"], "count": 0},
    {"name": "admit-reject", "family": true, "shape": "directory", "profiles": ["admit"], "count": 0},
    {"name": "admit-accept", "family": true, "shape": "directory", "profiles": ["admit-accept"], "count": 0},
    {"name": "envelope", "family": true, "shape": "envelope", "profiles": ["envelope"], "count": 0},
    {"name": "merkle", "family": true, "shape": "merkle", "profiles": ["merkle-tree"], "count": 0},
    {"name": "acta", "family": true, "shape": "directory", "profiles": ["acta-leaf"], "count": 0}
  ]
}"#,
    );
}

fn index_of(root: &Path) -> Index {
    Index::load(root).expect("the scaffolded index loads")
}

fn mismatch_problems(err: LoaderError) -> Vec<String> {
    match err {
        LoaderError::IndexMismatch { problems, .. } => problems,
        other => panic!("expected LoaderError::IndexMismatch, got: {other:?}"),
    }
}

#[test]
fn an_index_matching_disk_agrees() {
    let root = scratch_dir("index-agrees");
    scaffold_empty_corpus(&root);
    write_matching_index(&root);

    index_of(&root)
        .check_against_disk(&root)
        .expect("an index that matches disk must agree with it");
}

#[test]
fn index_without_a_directories_array_is_reported_not_panicked() {
    let root = scratch_dir("index-no-directories");
    scaffold_empty_corpus(&root);
    write(&root.join("index.json"), r#"{"families": []}"#);

    let err = Index::load(&root).expect_err("an index with no `directories` must not load");
    assert!(
        matches!(err, LoaderError::MalformedIndex { .. }),
        "expected LoaderError::MalformedIndex, got: {err:?}"
    );
}

#[test]
fn index_count_disagreeing_with_disk_is_reported() {
    let root = scratch_dir("index-wrong-count");
    scaffold_empty_corpus(&root);
    write_matching_index(&root);
    // The index still says c4 holds no vectors; disk now holds one.
    write(
        &root.join("c4/vector-01/meta.json"),
        r#"{"profile": "canonicalize-with-nfc", "requirement": "R6.8"}"#,
    );
    write(&root.join("c4/vector-01/input.json"), "{}");
    write(&root.join("c4/vector-01/expected.canonical"), "{}");
    write(
        &root.join("c4/vector-01/expected.digest"),
        "0".repeat(64).as_str(),
    );

    let err = index_of(&root)
        .check_against_disk(&root)
        .expect_err("a miscounted family must not agree with disk");
    let problems = mismatch_problems(err);
    assert!(
        problems
            .iter()
            .any(|p| p.contains("count 0") && p.contains('1')),
        "expected a count disagreement for c4, got: {problems:?}"
    );
}

#[test]
fn directory_on_disk_and_absent_from_the_index_is_reported() {
    let root = scratch_dir("index-unlisted-dir");
    scaffold_empty_corpus(&root);
    write_matching_index(&root);
    fs::create_dir_all(root.join("surprise")).expect("can create an unlisted directory");

    let err = index_of(&root)
        .check_against_disk(&root)
        .expect_err("a directory absent from the index must not agree with disk");
    let problems = mismatch_problems(err);
    assert!(
        problems.iter().any(|p| p.contains("surprise")),
        "expected the unlisted directory to be named, got: {problems:?}"
    );
}

#[test]
fn family_in_the_index_that_no_runner_loads_is_reported() {
    // The defect R6.45 exists for: a family added to the corpus and to the
    // index, which `Corpus::load` never enumerates. Without this check it
    // looks, in a passing test-run log, exactly like a family that ran.
    let root = scratch_dir("index-unloaded-family");
    scaffold_empty_corpus(&root);
    write(
        &root.join("index.json"),
        r#"{
  "directories": [
    {"name": "rfc8785", "family": true, "shape": "file-pairs", "profiles": ["rfc8785"], "count": 0},
    {"name": "c4", "family": true, "shape": "directory", "profiles": ["canonicalize-with-nfc"], "count": 0},
    {"name": "ordering", "family": true, "shape": "directory", "profiles": ["canonicalize-with-nfc"], "count": 0},
    {"name": "unicode", "family": true, "shape": "directory", "profiles": ["canonicalize-with-nfc"], "count": 0},
    {"name": "numbers", "family": true, "shape": "directory", "profiles": ["canonicalize-with-nfc"], "count": 0},
    {"name": "admit-reject", "family": true, "shape": "directory", "profiles": ["admit"], "count": 0},
    {"name": "admit-accept", "family": true, "shape": "directory", "profiles": ["admit-accept"], "count": 0},
    {"name": "envelope", "family": true, "shape": "envelope", "profiles": ["envelope"], "count": 0},
    {"name": "merkle", "family": true, "shape": "merkle", "profiles": ["merkle-tree"], "count": 0},
    {"name": "acta", "family": true, "shape": "directory", "profiles": ["acta-leaf"], "count": 0},
    {"name": "newfam", "family": true, "shape": "directory", "profiles": ["canonicalize-with-nfc"], "count": 0}
  ]
}"#,
    );
    fs::create_dir_all(root.join("newfam")).expect("can create the new family dir");

    let err = index_of(&root)
        .check_against_disk(&root)
        .expect_err("a family no runner loads must not agree with disk");
    let problems = mismatch_problems(err);
    assert!(
        problems
            .iter()
            .any(|p| p.contains("newfam") && p.contains("does not enumerate")),
        "expected the unenumerated family to be named, got: {problems:?}"
    );
}

#[test]
fn vector_declaring_a_profile_its_family_forbids_is_reported() {
    let root = scratch_dir("index-forbidden-profile");
    scaffold_empty_corpus(&root);
    write_matching_index(&root);
    // A vector that loads perfectly well, in a family whose index entry
    // permits only `canonicalize-with-nfc`.
    write(
        &root.join("c4/vector-01/meta.json"),
        r#"{"profile": "admit", "requirement": "R6.15"}"#,
    );
    write(&root.join("c4/vector-01/input.json"), "{}");
    write(
        &root.join("c4/vector-01/expect-reject"),
        "curia/admit/depth-exceeded",
    );

    let err = index_of(&root)
        .check_against_disk(&root)
        .expect_err("a vector whose profile its family forbids must not agree with the index");
    let problems = mismatch_problems(err);
    assert!(
        problems
            .iter()
            .any(|p| p.contains("c4/vector-01") && p.contains("does not permit")),
        "expected the forbidden profile to be named, got: {problems:?}"
    );
}
