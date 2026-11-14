//! R6.67 (errata G70): `conformance/operator-acts/`, run by the independent verifier.
//!
//! The fold is written from the requirement text and the vectors alone. A case is a whole small log; the
//! procedure decides every operator-act row of the named stream and the test compares the rows and the state
//! the honoured rows decide with `expected.json`.

use std::fs;
use std::path::Path;

use curia_testis::conformance::{conformance_dir, Corpus, OperatorActsVector};
use curia_testis::lifecycle::Verdict;
use curia_testis::operator_acts::{operator_acts, predicate, ActEntry, Acts, State, Stream};
use serde_json::Value;

/// Cases where the vector and this reading disagree: a case listed here is expected to fail, and the test fails
/// if a listed case agrees. Empty: every case agrees.
const EXPECTED_FAILURES: &[&str] = &[];

/// The cases to decide: the directory `CURIA_OPERATOR_ACTS_DIR` names when it is set (OAS29: a generated corpus whose
/// `expected.json` the Forum's fold wrote), else the family `conformance/operator-acts/`. Never empty: a run that decided
/// nothing proves nothing.
fn vectors() -> Vec<OperatorActsVector> {
    let Ok(dir) = std::env::var("CURIA_OPERATOR_ACTS_DIR") else {
        let corpus = Corpus::load(&conformance_dir()).expect("the corpus loads");
        assert_eq!(
            corpus.operator_acts.len(),
            74,
            "conformance/operator-acts/ holds 74 cases"
        );
        return corpus.operator_acts;
    };
    let mut cases: Vec<_> = fs::read_dir(&dir)
        .unwrap_or_else(|e| panic!("CURIA_OPERATOR_ACTS_DIR {dir} must be readable: {e}"))
        .map(|f| f.expect("a directory entry").path())
        .filter(|p| p.is_dir())
        .collect();
    cases.sort();
    let vectors: Vec<OperatorActsVector> = cases
        .into_iter()
        .map(|path| {
            let meta: Value = serde_json::from_slice(
                &fs::read(path.join("meta.json"))
                    .unwrap_or_else(|e| panic!("{}: meta.json: {e}", path.display())),
            )
            .unwrap_or_else(|e| panic!("{}: meta.json is JSON: {e}", path.display()));
            let text = |key: &str| {
                meta[key]
                    .as_str()
                    .unwrap_or_else(|| {
                        panic!("{}: meta.json has no string `{key}`", path.display())
                    })
                    .to_string()
            };
            OperatorActsVector {
                case: path.file_name().unwrap().to_string_lossy().into_owned(),
                requirement: text("requirement"),
                note: None,
                stream: text("stream"),
                subject: text("subject"),
                dir: path,
            }
        })
        .collect();
    assert!(
        !vectors.is_empty(),
        "CURIA_OPERATOR_ACTS_DIR {dir} holds no case: a run that decided nothing proves nothing"
    );
    vectors
}

fn read_entries(dir: &Path) -> Vec<ActEntry> {
    let entries = dir.join("entries");
    fs::read_dir(&entries)
        .unwrap_or_else(|e| panic!("{} must be readable: {e}", entries.display()))
        .map(|f| {
            let path = f.expect("a directory entry").path();
            let bytes = fs::read(&path).expect("an entry is readable");
            ActEntry::from_document(&bytes).unwrap_or_else(|e| panic!("{}: {e}", path.display()))
        })
        .collect()
}

fn compare(decided: &Acts, expected: &Value) -> Result<(), String> {
    let rows: Vec<(u64, String, String, String)> = decided
        .rows
        .iter()
        .map(|r| {
            let (verdict, predicate) = match &r.verdict {
                Verdict::Honoured => ("honoured", ""),
                Verdict::Skipped(p) => ("skipped", *p),
            };
            (
                r.log_index,
                r.event_type.clone(),
                verdict.to_string(),
                predicate.to_string(),
            )
        })
        .collect();
    let want_rows: Vec<(u64, String, String, String)> = expected["rows"]
        .as_array()
        .ok_or("no rows")?
        .iter()
        .map(|r| {
            (
                r["log_index"].as_u64().unwrap_or(u64::MAX),
                r["type"].as_str().unwrap_or("").to_string(),
                r["verdict"].as_str().unwrap_or("").to_string(),
                r["predicate"].as_str().unwrap_or("").to_string(),
            )
        })
        .collect();
    if rows != want_rows {
        return Err(format!("rows: expected {want_rows:?}, got {rows:?}"));
    }
    let state = &expected["state"];
    match &decided.state {
        State::Agent { owner, verified } => {
            let want_owner = state["owner"].as_str().map(str::to_string);
            let want_verified = state["verified"].as_bool().ok_or("no state.verified")?;
            if *owner != want_owner || *verified != want_verified {
                return Err(format!(
                    "state: expected owner {want_owner:?} verified {want_verified}, got owner {owner:?} verified {verified}"
                ));
            }
        }
        State::Post { served } => {
            let want = state["served"].as_bool().ok_or("no state.served")?;
            if *served != want {
                return Err(format!("state: expected served {want}, got {served}"));
            }
        }
    }
    Ok(())
}

fn decide_case(vector: &curia_testis::conformance::OperatorActsVector) -> Acts {
    let stream = match vector.stream.as_str() {
        "agent" => Stream::Agent,
        "post" => Stream::Post,
        other => panic!("{}: unknown stream `{other}`", vector.case),
    };
    operator_acts(&read_entries(&vector.dir), stream, &vector.subject)
}

fn expected_of(vector: &curia_testis::conformance::OperatorActsVector) -> Value {
    serde_json::from_slice(
        &fs::read(vector.dir.join("expected.json")).expect("expected.json is readable"),
    )
    .expect("expected.json is JSON")
}

#[test]
fn r6_67_every_operator_acts_case_reaches_its_rows_and_state() {
    let vectors = vectors();

    let mut failures = Vec::new();
    for vector in &vectors {
        let outcome = compare(&decide_case(vector), &expected_of(vector));
        match (outcome, EXPECTED_FAILURES.contains(&vector.case.as_str())) {
            (Err(e), false) => failures.push(format!("{}: {e}", vector.case)),
            (Ok(()), true) => failures.push(format!(
                "{}: listed as an expected failure but agrees",
                vector.case
            )),
            _ => {}
        }
    }
    assert!(failures.is_empty(), "\n{}", failures.join("\n"));
    println!("operator-acts cases decided: {}", vectors.len());
}

#[test]
fn r6_67_every_skipped_predicate_is_one_of_the_eight_names() {
    for vector in &vectors() {
        for row in decide_case(vector).rows {
            if let Verdict::Skipped(p) = row.verdict {
                assert!(predicate::ALL.contains(&p), "{}: {p}", vector.case);
            }
        }
    }
}
