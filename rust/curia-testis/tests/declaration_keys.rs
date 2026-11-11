//! PR H (errata G67, R6.62; Amendment 1 rulings 12 and 26): the declaration key's vectors, run by the independent verifier.
//!
//! Each test names the case it is about and what the verifier must make of it, written here by hand from the plan's own table, and not read from the case's
//! `expected.json`, which the generator writes: a test that took its expectation from the file would agree with whatever the generator wrote. A case the family
//! does not hold yet is a failing test that says so (Task 3 generates it). `curia.Client`'s runner takes the same names in
//! `tests/Curia.Client.Tests/DeclarationKeyConformanceTests.cs`, from the same table and not from this file.

use std::fs;
use std::path::Path;
use std::process::{Command, Output};

use curia_testis::conformance::{conformance_dir, Corpus, LifecycleVector};
use curia_testis::lifecycle::verify_lifecycle;

fn read(path: &Path) -> Vec<u8> {
    fs::read(path).unwrap_or_else(|e| panic!("{} must be readable: {e}", path.display()))
}

/// The verdict and short predicate this verifier reaches for a case: the same walk of its documents `tests/lifecycle.rs` makes.
fn judge(vector: &LifecycleVector) -> (String, String) {
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
    let chain: Vec<(Vec<u8>, Vec<u8>)> = indices
        .iter()
        .filter(|i| **i != vector.post_index)
        .map(|i| {
            (
                read(&entries.join(format!("{i}.json"))),
                read(&vector.dir.join("proofs").join(format!("{i}.json"))),
            )
        })
        .collect();
    let head = read(&vector.dir.join("head.json"));
    let log_jwks = read(&vector.dir.join("log-jwks.json"));

    match verify_lifecycle(&post_entry, &chain, &head, &log_jwks) {
        Ok(_) => ("verified".to_string(), "verified".to_string()),
        Err(err) => {
            let verdict = if err.disputed() {
                "disputed"
            } else if err.not_established() {
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

/// One problem line, or the empty string where the case reaches `verdict` and `predicate`.
fn describe(name: &str, verdict: &str, predicate: &str) -> String {
    let corpus = Corpus::load(&conformance_dir()).expect("the corpus loads");
    let Some(vector) = corpus.lifecycle.iter().find(|v| v.case == name) else {
        return format!("{name}: the case is not in conformance/lifecycle/ (Task 3 generates it)");
    };
    let (got_verdict, got_predicate) = judge(vector);
    if (got_verdict.as_str(), got_predicate.as_str()) == (verdict, predicate) {
        String::new()
    } else {
        format!("{name}: expected {verdict} {predicate}, got {got_verdict} {got_predicate}")
    }
}

fn all_hold(problems: &[String]) {
    let failed: Vec<&String> = problems.iter().filter(|p| !p.is_empty()).collect();
    assert!(
        failed.is_empty(),
        "\n{}",
        failed
            .iter()
            .map(|p| p.as_str())
            .collect::<Vec<_>>()
            .join("\n")
    );
}

#[test]
fn hsu18_the_families_hold_the_counts_of_the_declaration_key_vectors() {
    let corpus = Corpus::load(&conformance_dir()).expect("the corpus loads");
    assert_eq!(
        (corpus.lifecycle.len(), corpus.acta.len()),
        (58, 12),
        "conformance/lifecycle/ holds 58 cases and conformance/acta/ 12 (Amendment 1 ruling 26)"
    );
}

#[test]
fn hsu19_a_declaration_signed_by_a_log_key_fails_and_never_disputes() {
    all_hold(&[describe(
        "compromise-signed-by-log-key",
        "failed",
        "declaration-key-unpublished",
    )]);
}

#[test]
fn hsu20_a_version_one_statement_is_could_not_be_checked() {
    all_hold(&[describe(
        "compromise-v1-statement",
        "could-not-check",
        "unknown-lifecycle-kind",
    )]);
}

#[test]
fn hsu21_a_declaration_key_that_is_also_a_log_key_fails() {
    all_hold(&[describe(
        "compromise-declaration-key-is-log-key",
        "failed",
        "declaration-key-is-log-key",
    )]);
}

#[test]
fn hsu22_a_declaration_under_a_second_declaration_key_fails() {
    all_hold(&[describe(
        "compromise-second-declaration-key",
        "failed",
        "declaration-key-not-first",
    )]);
}

#[test]
fn hsu23_a_declaration_key_published_too_late_fails_and_one_with_no_position_cannot_be_checked() {
    all_hold(&[
        describe(
            "compromise-declaration-kid-not-valid-at-position",
            "failed",
            "declaration-key-unpublished",
        ),
        describe(
            "compromise-declaration-kid-position-unknown",
            "could-not-check",
            "declaration-key-position-unknown",
        ),
    ]);
}

/// The control that the new path is not a vacuous refusal: a `/2` declaration under the first published declaration key disputes the post, in the library and
/// through `curia-testis log author` (exit 4). The case's declaration must be a `/2` statement and its key set must serve a `declaration_keys` member.
#[test]
fn hsu24_a_declaration_under_the_first_declaration_key_disputes_the_post() {
    let dir = conformance_dir()
        .join("lifecycle")
        .join("compromised-before-post");
    let mut problems = vec![describe("compromised-before-post", "disputed", "disputed")];

    let v2 = fs::read_dir(dir.join("entries"))
        .expect("the case has entries")
        .map(|e| fs::read_to_string(e.expect("an entry").path()).expect("an entry reads"))
        .filter(|text| text.contains("agent.key-compromised"))
        .any(|text| text.contains("curia.key-compromise/2"));
    if !v2 {
        problems.push(
            "compromised-before-post: the case's declaration statement is not /2".to_string(),
        );
    }
    let served = fs::read_to_string(dir.join("log-jwks.json")).expect("the key set reads");
    if !served.contains("\"declaration_keys\"") {
        problems.push(
            "compromised-before-post: the case's key set has no declaration_keys member"
                .to_string(),
        );
    }

    let path = |kind: &str, index: u64| {
        dir.join(kind)
            .join(format!("{index}.json"))
            .display()
            .to_string()
    };
    let out: Output = Command::new(env!("CARGO_BIN_EXE_curia-testis"))
        .args([
            "log",
            "author",
            "--entry",
            &path("entries", 2),
            "--proof",
            &path("proofs", 2),
            "--key-entry",
            &path("entries", 1),
            "--key-proof",
            &path("proofs", 1),
            "--head",
            &dir.join("head.json").display().to_string(),
            "--log-jwks",
            &dir.join("log-jwks.json").display().to_string(),
        ])
        .args(
            [0_u64, 1, 4]
                .iter()
                .flat_map(|i| {
                    [
                        "--lifecycle-entry".to_string(),
                        path("entries", *i),
                        "--lifecycle-proof".to_string(),
                        path("proofs", *i),
                    ]
                })
                .collect::<Vec<String>>(),
        )
        .output()
        .expect("the binary runs");
    if out.status.code() != Some(4) {
        problems.push(format!(
            "compromised-before-post: log author exits {:?}, not 4",
            out.status.code()
        ));
    }
    all_hold(&problems);
}

/// The pair that carries the difference between an absence and a denial: no `declaration_keys` member is could not be checked; a served empty one is failed.
#[test]
fn hsu25_an_absent_key_set_member_is_not_a_denial_and_an_empty_one_is() {
    all_hold(&[
        describe(
            "compromise-declaration-key-set-absent",
            "could-not-check",
            "declaration-key-set-not-supplied",
        ),
        describe(
            "compromise-declaration-keys-empty",
            "failed",
            "declaration-key-unpublished",
        ),
    ]);
}
