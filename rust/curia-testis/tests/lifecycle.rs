//! R6.54 (add.): `conformance/lifecycle/`, run by the independent verifier.
//!
//! Each case is a small whole log: the entries and proofs a reader fetches, a signed head over the
//! log, and the log's key set. The verifier folds the author's lifecycle for the post and must reach
//! the case's verdict and name its predicate. `curia.Client`'s `LifecycleCheck` runs the same cases;
//! the two are the stage's cross-implementation check (R14.6's differential comparison takes no
//! lifecycle input in this stage).

use std::fs;
use std::path::Path;

use std::process::{Command, Output};

use curia_testis::acta::{self, ActaError};
use curia_testis::conformance::{conformance_dir, Corpus, LifecycleVector};
use curia_testis::lifecycle::verify_lifecycle;

/// The documents of one case: the post's entry, and each lifecycle entry with its proof, in log order.
struct Documents {
    post_entry: Vec<u8>,
    chain: Vec<(Vec<u8>, Vec<u8>)>,
    head: Vec<u8>,
    log_jwks: Vec<u8>,
}

fn read(path: &Path) -> Vec<u8> {
    fs::read(path).unwrap_or_else(|e| panic!("{} must be readable: {e}", path.display()))
}

fn documents(vector: &LifecycleVector) -> Documents {
    let entries = vector.dir.join("entries");
    let mut indices: Vec<u64> = fs::read_dir(&entries)
        .unwrap_or_else(|e| panic!("{} must be readable: {e}", entries.display()))
        .map(|e| {
            let name = e.expect("a directory entry").file_name();
            name.to_str()
                .and_then(|n| n.strip_suffix(".json"))
                .and_then(|n| n.parse::<u64>().ok())
                .unwrap_or_else(|| {
                    panic!(
                        "{} holds a file that is not <index>.json",
                        entries.display()
                    )
                })
        })
        .collect();
    indices.sort_unstable();

    let post_entry = read(&entries.join(format!("{}.json", vector.post_index)));
    let chain = indices
        .iter()
        .filter(|i| **i != vector.post_index)
        .map(|i| {
            (
                read(&entries.join(format!("{i}.json"))),
                read(&vector.dir.join("proofs").join(format!("{i}.json"))),
            )
        })
        .collect();

    Documents {
        post_entry,
        chain,
        head: read(&vector.dir.join("head.json")),
        log_jwks: read(&vector.dir.join("log-jwks.json")),
    }
}

/// The verdict and short predicate this verifier reaches for a case.
fn judge(vector: &LifecycleVector) -> (String, String) {
    let d = documents(vector);
    match verify_lifecycle(&d.post_entry, &d.chain, &d.head, &d.log_jwks) {
        Ok(_) => ("verified".to_string(), "verified".to_string()),
        Err(err) => {
            let verdict = if err.not_established() {
                "could-not-check"
            } else {
                "failed"
            };
            (
                verdict.to_string(),
                err.predicate()
                    .strip_prefix("curia/acta/")
                    .unwrap_or_else(|| err.predicate())
                    .to_string(),
            )
        }
    }
}

#[test]
fn r6_54_every_lifecycle_case_reaches_its_verdict_and_names_its_predicate() {
    let corpus = Corpus::load(&conformance_dir()).expect("the corpus loads");
    assert_eq!(
        corpus.lifecycle.len(),
        30,
        "conformance/lifecycle/ holds the 30 PR 1 cases"
    );

    let mut failures = Vec::new();
    for vector in &corpus.lifecycle {
        let (verdict, predicate) = judge(vector);
        if verdict != vector.verdict || predicate != vector.predicate {
            failures.push(format!(
                "{}: expected {} / {}, got {verdict} / {predicate}",
                vector.case, vector.verdict, vector.predicate
            ));
        }
    }
    assert!(failures.is_empty(), "\n{}", failures.join("\n"));
}

#[test]
fn r6_54_the_lifecycle_family_covers_each_verdict() {
    let corpus = Corpus::load(&conformance_dir()).expect("the corpus loads");
    for verdict in ["verified", "failed", "could-not-check"] {
        assert!(
            corpus.lifecycle.iter().any(|v| v.verdict == verdict),
            "no lifecycle case expects {verdict}"
        );
    }
    // The type is used so a rename of the error breaks this file rather than only the binary.
    let _ = |e: ActaError| e.not_established();
}

/// R6.54 (add.): a rotation whose authorization was made by a stranger's key fails here as it fails in the
/// client's reader, naming the same predicate. The client's half is `LifecycleVerifierTests` in `Curia.Client.Tests`,
/// which runs the same case through `PostVerifier`.
#[test]
fn r6_54_a_rotation_the_author_did_not_sign_fails_in_both_readers() {
    let corpus = Corpus::load(&conformance_dir()).expect("the corpus loads");
    let vector = corpus
        .lifecycle
        .iter()
        .find(|v| v.case == "forged-rotation")
        .expect("the corpus holds forged-rotation");

    assert_eq!(
        judge(vector),
        ("failed".to_string(), "rotation-unauthorized".to_string())
    );
}

/// R6.49 (a regression guard, green before this stage): a head signed by the log key under a lifecycle `typ`
/// is refused, by exact `typ`. The signatures are pinned beside `rotation-then-post`: the same head, by the same
/// log key, over the same bytes; the control is the head itself, which verifies.
#[test]
fn head_verifier_refuses_a_lifecycle_typ() {
    let dir = conformance_dir()
        .join("lifecycle")
        .join("rotation-then-post");
    let jwks = read(&dir.join("log-jwks.json"));

    acta::verify_head(&read(&dir.join("head.json")), &jwks).expect("the head itself verifies");

    for file in ["head-as-rotation.json", "head-as-retirement.json"] {
        let err = acta::verify_head(&read(&dir.join(file)), &jwks)
            .expect_err("a head signed under a lifecycle typ is refused");
        assert_eq!(err.predicate(), "curia/jws/typ-invalid", "{file}");
    }
}

fn testis(args: &[&str]) -> Output {
    Command::new(env!("CARGO_BIN_EXE_curia-testis"))
        .args(args)
        .output()
        .expect("the binary runs")
}

/// `log author` over one lifecycle case: the post, its key's binding, the head and key set, and then `chain`, the
/// lifecycle indices to pass in order.
fn log_author(case: &str, post: u64, key: u64, chain: &[u64]) -> Output {
    let dir = conformance_dir().join("lifecycle").join(case);
    let path = |kind: &str, index: u64| {
        dir.join(kind)
            .join(format!("{index}.json"))
            .display()
            .to_string()
    };
    let mut owned = vec![
        "log".to_string(),
        "author".to_string(),
        "--entry".to_string(),
        path("entries", post),
        "--proof".to_string(),
        path("proofs", post),
        "--key-entry".to_string(),
        path("entries", key),
        "--key-proof".to_string(),
        path("proofs", key),
        "--head".to_string(),
        dir.join("head.json").display().to_string(),
        "--log-jwks".to_string(),
        dir.join("log-jwks.json").display().to_string(),
    ];
    for index in chain {
        owned.push("--lifecycle-entry".to_string());
        owned.push(path("entries", *index));
        owned.push("--lifecycle-proof".to_string());
        owned.push(path("proofs", *index));
    }
    let args: Vec<&str> = owned.iter().map(String::as_str).collect();
    testis(&args)
}

fn text(bytes: &[u8]) -> String {
    String::from_utf8_lossy(bytes).into_owned()
}

#[test]
fn r6_54_log_author_with_the_lifecycle_verifies_through_the_last_entry() {
    let out = log_author("rotation-then-post", 3, 2, &[0, 1, 2, 4]);
    assert_eq!(out.status.code(), Some(0), "{}", text(&out.stderr));
    assert!(
        text(&out.stdout).contains("lifecycle: verified through leaf 4"),
        "{}",
        text(&out.stdout)
    );
}

/// An offline verifier given no lifecycle cannot say the key was not retired: exit 3, naming the predicate, and the
/// stdout line says so in words a caller reading only it cannot take for a pass.
#[test]
fn r6_54_log_author_without_a_lifecycle_could_not_check() {
    let out = log_author("rotation-then-post", 3, 2, &[]);
    assert_eq!(out.status.code(), Some(3), "{}", text(&out.stderr));
    assert!(text(&out.stdout).contains("lifecycle: could not be checked: no lifecycle supplied"));
    assert!(
        text(&out.stderr).contains("curia/acta/lifecycle-not-supplied"),
        "{}",
        text(&out.stderr)
    );
}

#[test]
fn r6_54_log_author_with_a_lifecycle_that_retired_the_key_fails() {
    let out = log_author("post-after-retirement", 3, 1, &[0, 1, 2]);
    assert_eq!(out.status.code(), Some(1), "{}", text(&out.stderr));
    assert!(
        text(&out.stderr).contains("curia/acta/retired-before-post"),
        "{}",
        text(&out.stderr)
    );
}

/// A failure the existing checks find still exits 1: failed outranks could not be checked, so a key the log bound
/// after the post is reported as the log's silence (exit 3) before the lifecycle is consulted, and a lifecycle is not
/// needed to say so.
#[test]
fn r6_54_a_binding_after_the_post_is_still_reported_before_the_lifecycle_is_consulted() {
    let out = log_author("pre-g16-first-binding-after-post", 1, 2, &[0, 2]);
    assert_eq!(out.status.code(), Some(3), "{}", text(&out.stderr));
    assert!(
        text(&out.stderr).contains("curia/acta/bound-after-post"),
        "{}",
        text(&out.stderr)
    );
}

#[test]
fn r6_54_a_lifecycle_entry_without_its_proof_is_a_usage_error() {
    let dir = conformance_dir()
        .join("lifecycle")
        .join("rotation-then-post");
    let entry = dir.join("entries").join("0.json").display().to_string();
    let out = testis(&["log", "author", "--lifecycle-entry", &entry]);
    assert_eq!(out.status.code(), Some(2));
}
