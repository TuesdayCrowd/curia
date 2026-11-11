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
    let log_jwks = read(&vector.dir.join("log-jwks.json"));
    judge_under(vector, &log_jwks)
}

/// [`judge`] with `log_jwks` served in place of the case's own key set.
fn judge_under(vector: &LifecycleVector, log_jwks: &[u8]) -> (String, String) {
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

    match verify_lifecycle(&post_entry, &chain, &head, log_jwks) {
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
        (75, 12),
        "conformance/lifecycle/ holds 75 cases (the 58 of Amendment 1 ruling 26, the control of HSU22, the 6 of R6.62's undeclared decisions, the 6 of HSU49 and the 4 of HSU51) and conformance/acta/ 12"
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

/// The control is `compromise-first-of-two-declaration-keys`, the same log with the declaration under the first key: a vector the plan's list did not name.
#[test]
fn hsu22_a_declaration_under_a_second_declaration_key_fails_and_under_the_first_disputes() {
    all_hold(&[
        describe(
            "compromise-second-declaration-key",
            "failed",
            "declaration-key-not-first",
        ),
        describe(
            "compromise-first-of-two-declaration-keys",
            "disputed",
            "disputed",
        ),
    ]);
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

/// The exit `curia-testis log author` gives a case: 4 disputed, 1 failed, 3 not established (R6.54, R6.57). A declaration under a log key is a failure and never a
/// dispute (HSU19), and a `/1` statement is not established whoever signed it (HSU20).
fn log_author_exit(case: &str, lifecycle: &[u64]) -> Option<i32> {
    let dir = conformance_dir().join("lifecycle").join(case);
    let path = |kind: &str, index: u64| {
        dir.join(kind)
            .join(format!("{index}.json"))
            .display()
            .to_string()
    };
    Command::new(env!("CARGO_BIN_EXE_curia-testis"))
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
            lifecycle
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
        .expect("the binary runs")
        .status
        .code()
}

#[test]
fn hsu19_20_log_author_fails_a_log_key_declaration_and_cannot_establish_a_version_one_statement() {
    assert_eq!(
        (
            log_author_exit("compromise-signed-by-log-key", &[0, 1, 5]),
            log_author_exit("compromise-v1-statement", &[0, 1, 4]),
            log_author_exit("compromise-second-declaration-key", &[0, 1, 5]),
        ),
        (Some(1), Some(3), Some(1)),
        "a declaration under a log key and one under a second declaration key fail (1) and never dispute (4); a /1 statement is not established (3)"
    );
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

/// R6.62's rules the decision order alone left open: a present non-array member is read as absent, a log key is a log key by its public key
/// under another kid, any declaration key with no position makes the result could not be checked, and a repeated kid is its earliest listing.
#[test]
fn r6_62_the_undeclared_decisions_reach_their_verdicts() {
    all_hold(&[
        describe(
            "compromise-declaration-keys-null",
            "could-not-check",
            "declaration-key-set-not-supplied",
        ),
        describe(
            "compromise-declaration-keys-object",
            "could-not-check",
            "declaration-key-set-not-supplied",
        ),
        describe(
            "compromise-log-key-kid-position-unknown",
            "could-not-check",
            "declaration-key-position-unknown",
        ),
        describe(
            "compromise-declaration-key-is-log-key-under-another-kid",
            "failed",
            "declaration-key-is-log-key",
        ),
        describe(
            "compromise-another-declaration-key-position-unknown",
            "could-not-check",
            "declaration-key-position-unknown",
        ),
        describe(
            "compromise-declaration-kid-published-twice",
            "failed",
            "declaration-signature-invalid",
        ),
    ]);
}

/// The base of HSU50: a valid case whose declaration disputes the post under its one declaration key, and whose key set serves one log key.
const HSU50_BASE: &str = "compromised-before-post";

fn hsu50_base() -> (LifecycleVector, serde_json::Value) {
    let corpus = Corpus::load(&conformance_dir()).expect("the corpus loads");
    let vector = corpus
        .lifecycle
        .into_iter()
        .find(|v| v.case == HSU50_BASE)
        .expect("the base case is in conformance/lifecycle/");
    let jwks = serde_json::from_slice(&read(&vector.dir.join("log-jwks.json")))
        .expect("the key set parses");
    (vector, jwks)
}

/// A mutation of one key entry; `None` removes the member.
type Mutation = (&'static str, fn(&mut serde_json::Value));

fn drop_member(entry: &mut serde_json::Value, name: &str) {
    entry.as_object_mut().expect("an object").remove(name);
}

/// Applies `mutate` to `member`'s entry: a `declaration_keys` entry is changed in place, and a `keys` entry is a copy of the head key appended after it (the head
/// key itself is the one the head verifies under, and a key set whose head cannot be verified is a different case).
fn mutated_key_set(
    base: &serde_json::Value,
    member: &str,
    mutate: &dyn Fn(&mut serde_json::Value),
) -> Vec<u8> {
    let mut jwks = base.clone();
    let list = jwks[member].as_array_mut().expect("the member is an array");
    if member == "keys" {
        let copy = list[0].clone();
        list.push(copy);
    }
    let last = list.len() - 1;
    mutate(&mut list[last]);
    serde_json::to_vec(&jwks).expect("the key set serializes")
}

#[test]
fn hsu50_an_unreadable_key_entry_leaves_the_key_set_without_a_usable_member() {
    let (vector, base) = hsu50_base();
    assert_eq!(
        judge(&vector),
        ("disputed".to_string(), "disputed".to_string()),
        "the unmutated base disputes"
    );
    let table1: [Mutation; 6] = [
        ("drop kid", |e| drop_member(e, "kid")),
        ("drop kty", |e| drop_member(e, "kty")),
        ("drop alg", |e| drop_member(e, "alg")),
        ("drop x", |e| drop_member(e, "x")),
        ("replace by 7", |e| *e = serde_json::json!(7)),
        ("replace by null", |e| *e = serde_json::Value::Null),
    ];
    let mut problems = Vec::new();
    for member in ["declaration_keys", "keys"] {
        for (name, mutate) in &table1 {
            let served = mutated_key_set(&base, member, mutate);
            let got = judge_under(&vector, &served);
            if got
                != (
                    "could-not-check".to_string(),
                    "declaration-key-set-not-supplied".to_string(),
                )
            {
                problems.push(format!("{member} / {name}: got {} {}", got.0, got.1));
            }
        }
    }
    all_hold(&problems);
}

/// Table 2, measured: what the reader makes of entries that carry all four strings and are still not usable keys, or are listed oddly. It asserts nothing on a
/// verdict; run with `--nocapture` to read it.
#[test]
fn hsu50_measured_entries_that_are_readable_as_objects_but_not_as_keys() {
    let (vector, base) = hsu50_base();
    let rows: [Mutation; 5] = [
        ("EC key without y", |e| drop_member(e, "y")),
        ("x that does not decode", |e| {
            e["x"] = serde_json::json!("!!not base64!!")
        }),
        ("kty RSA", |e| e["kty"] = serde_json::json!("RSA")),
        ("kid a number", |e| e["kid"] = serde_json::json!(7)),
        // Replaced below: one kid listed twice at one position with different x.
        ("kid twice at one position, different x", |_| {}),
    ];
    for (name, mutate) in &rows {
        let mut jwks = base.clone();
        let list = jwks["declaration_keys"].as_array_mut().expect("an array");
        if name.starts_with("kid twice") {
            let mut other = list[0].clone();
            other["x"] = serde_json::json!("mfCWmaHAcO2_WNMXZKV0PuEHXot69w_OxH2gYaAdtMM");
            other["y"] = serde_json::json!("_su9B0jJ07Rka17m9G8MiFHbdEl9C0sl0u1Ytb0Pdjo");
            list.push(other);
        } else {
            mutate(&mut list[0]);
        }
        let served = serde_json::to_vec(&jwks).expect("serializes");
        let (verdict, predicate) = judge_under(&vector, &served);
        println!("table2: {name}: {verdict} {predicate}");
    }
}
