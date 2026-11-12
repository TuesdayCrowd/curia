//! R6.64 (errata G68): `conformance/honoured/`, run by the independent verifier, and the same procedure over the
//! entries of `conformance/lifecycle/`'s reader vectors.
//!
//! The honoured fold is written from the requirement text and the vectors alone. A case is a whole small log; the
//! procedure decides every lifecycle row of the named agent's stream and the test compares rows, keys and bound.

use std::collections::BTreeMap;
use std::fs;
use std::path::Path;

use curia_testis::conformance::{conformance_dir, Corpus};
use curia_testis::lifecycle::{
    honoured, predicate, Honoured, LogEntry, Verdict, LOG_DECLARATION_KEY, LOG_KEY,
};
use serde_json::Value;

/// Cases where the vector and this reading disagree. Empty: every case agrees.
const EXPECTED_FAILURES: &[&str] = &[];

/// Reader vectors whose reader verdict is about something R6.64's stream does not contain, so the Forum has no row to
/// decide for it: the stream is the events of the agent's own aggregate, and a row is one of the four lifecycle types.
const NOT_A_ROW_OF_THE_STREAM: &[(&str, &str)] = &[
    (
        "foreign-identity-entry",
        "the listed entry is another identity's aggregate: the Forum's stream never holds it",
    ),
    (
        "unknown-lifecycle-kind",
        "agent.key-frozen is not one of the four lifecycle types, so it is not a row",
    ),
];

/// Reader vectors where the reader's predicate and R6.64's text disagree. Empty since Amendment 3 (ruling 44): R6.64 states KF5, so the reader
/// vector `rotation-self-authorized` and `honoured/rotation-self-authorized` agree on `statement-mismatch`.
const READER_DISAGREEMENTS: &[(&str, &str)] = &[];

fn read_entries(dir: &Path) -> Vec<LogEntry> {
    let entries = dir.join("entries");
    fs::read_dir(&entries)
        .unwrap_or_else(|e| panic!("{} must be readable: {e}", entries.display()))
        .map(|f| {
            let path = f.expect("a directory entry").path();
            let bytes = fs::read(&path).expect("an entry is readable");
            LogEntry::from_document(&bytes).unwrap_or_else(|e| panic!("{}: {e}", path.display()))
        })
        .collect()
}

/// What `expected.json` (or one of its `also` members) states for an agent.
fn compare(decided: &Honoured, expected: &Value) -> Result<(), String> {
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
    let keys: BTreeMap<String, String> = decided
        .keys
        .iter()
        .map(|(k, s)| (k.clone(), s.as_str().to_string()))
        .collect();
    let want_keys: BTreeMap<String, String> = expected["keys"]
        .as_object()
        .ok_or("no keys")?
        .iter()
        .map(|(k, v)| (k.clone(), v.as_str().unwrap_or("").to_string()))
        .collect();
    if keys != want_keys {
        return Err(format!("keys: expected {want_keys:?}, got {keys:?}"));
    }
    let want_bound: Vec<String> = expected["bound"]
        .as_array()
        .ok_or("no bound")?
        .iter()
        .map(|v| v.as_str().unwrap_or("").to_string())
        .collect();
    if decided.bound != want_bound {
        return Err(format!(
            "bound: expected {want_bound:?}, got {:?}",
            decided.bound
        ));
    }
    Ok(())
}

#[test]
fn r6_64_every_honoured_case_reaches_its_rows_keys_and_bound() {
    let corpus = Corpus::load(&conformance_dir()).expect("the corpus loads");
    assert_eq!(
        corpus.honoured.len(),
        51,
        "conformance/honoured/ holds 51 cases"
    );

    let mut failures = Vec::new();
    let mut agreed_with_also = 0;
    for vector in &corpus.honoured {
        let log = read_entries(&vector.dir);
        let expected: Value = serde_json::from_slice(
            &fs::read(vector.dir.join("expected.json")).expect("expected.json is readable"),
        )
        .expect("expected.json is JSON");

        let mut outcome = compare(&honoured(&log, &vector.agent), &expected);
        if let Some(also) = expected.get("also").and_then(Value::as_array) {
            agreed_with_also += 1;
            for other in also {
                let agent = other["agent"]
                    .as_str()
                    .expect("an also member names its agent");
                if let Err(e) = compare(&honoured(&log, agent), other) {
                    outcome = outcome.and(Err(format!("also {agent}: {e}")));
                }
            }
        }
        match (outcome, EXPECTED_FAILURES.contains(&vector.case.as_str())) {
            (Err(e), false) => failures.push(format!("{}: {e}", vector.case)),
            (Ok(()), true) => failures.push(format!(
                "{}: listed as an expected failure but agrees",
                vector.case
            )),
            _ => {}
        }
    }
    assert_eq!(agreed_with_also, 2, "cases 14 and 42 carry a second stream");
    assert!(failures.is_empty(), "\n{}", failures.join("\n"));
}

#[test]
fn r6_64_every_skipped_predicate_is_one_of_the_twelve_names() {
    let corpus = Corpus::load(&conformance_dir()).expect("the corpus loads");
    for vector in &corpus.honoured {
        let log = read_entries(&vector.dir);
        for row in honoured(&log, &vector.agent).rows {
            if let Verdict::Skipped(p) = row.verdict {
                assert!(predicate::ALL.contains(&p), "{}: {p}", vector.case);
            }
        }
    }
}

/// The reader vectors' log: the entries on disk, and the log keys and declaration keys their `log-jwks.json` publishes,
/// each as the `log.key` or `log.declaration-key` entry at the `log_index` the key set gives it (a key that gives none is
/// published by no entry).
fn reader_log(dir: &Path) -> Vec<LogEntry> {
    let mut log = read_entries(dir);
    let jwks: Value =
        serde_json::from_slice(&fs::read(dir.join("log-jwks.json")).expect("log-jwks.json"))
            .expect("log-jwks.json is JSON");
    for (member, event_type) in [("keys", LOG_KEY), ("declaration_keys", LOG_DECLARATION_KEY)] {
        let Some(items) = jwks.get(member).and_then(Value::as_array) else {
            continue;
        };
        for item in items {
            let (Some(at), Some(object)) = (
                item.get("log_index").and_then(Value::as_u64),
                item.as_object(),
            ) else {
                continue;
            };
            let mut jwk = object.clone();
            jwk.remove("log_index");
            jwk.remove("valid_from");
            log.push(LogEntry {
                log_index: at,
                event_type: event_type.to_string(),
                aggregate_id: "log".to_string(),
                payload: serde_json::json!({"jwk": jwk, "kid": object.get("kid")}),
            });
        }
    }
    log
}

/// What R6.64 lets be asserted over a reader vector. "The Forum's decisions SHALL agree with `conformance/lifecycle/`'s
/// verdicts where a reader's verdict names a row's predicate": the reader folds the chain in log order and stops at the
/// first row that fails, naming a predicate; the Forum decides the same rows in the same order against the same prefix, so
/// when the reader's predicate is one of R6.64's twelve names (a verdict of `failed`, or `could-not-check` for the
/// version and reason a reader does not know), the first row the Forum skips is skipped with that predicate. When the
/// reader says `verified`, the Forum skips no row below the post.
#[test]
fn r6_64_the_forum_agrees_with_the_reader_vectors_where_the_reader_names_a_row_predicate() {
    let corpus = Corpus::load(&conformance_dir()).expect("the corpus loads");
    assert_eq!(corpus.lifecycle.len(), 81);

    let mut checked = 0;
    let mut failures = Vec::new();
    for vector in &corpus.lifecycle {
        if NOT_A_ROW_OF_THE_STREAM
            .iter()
            .any(|(case, _)| *case == vector.case)
        {
            continue;
        }
        let log = reader_log(&vector.dir);
        let decided = honoured(&log, &agent_of(&vector.dir));
        let first_skipped = decided.rows.iter().find_map(|r| match r.verdict {
            Verdict::Skipped(p) => Some((r.log_index, p)),
            Verdict::Honoured => None,
        });
        if vector.verdict == "verified" {
            checked += 1;
            if let Some((at, p)) = first_skipped.filter(|(at, _)| *at < vector.post_index) {
                failures.push(format!(
                    "{}: reader verified; Forum skipped row {at} ({p}) below the post",
                    vector.case
                ));
            }
        } else if predicate::ALL.contains(&vector.predicate.as_str()) {
            checked += 1;
            match first_skipped {
                Some((_, p)) if p == vector.predicate => {}
                other => failures.push(format!(
                    "{}: reader {} {}; Forum's first skipped row: {other:?}",
                    vector.case, vector.verdict, vector.predicate
                )),
            }
        }
    }
    assert!(
        checked >= 40,
        "only {checked} reader vectors were comparable"
    );
    // No reader vector may disagree with R6.64 (ruling 44): the list is empty and every failure is a failure.
    let disagreements: &[(&str, &str)] = READER_DISAGREEMENTS;
    assert!(
        disagreements.is_empty(),
        "READER_DISAGREEMENTS must be empty: {disagreements:?}"
    );
    assert!(failures.is_empty(), "\n{}", failures.join("\n"));
}

fn agent_of(dir: &Path) -> String {
    let meta: Value =
        serde_json::from_slice(&fs::read(dir.join("meta.json")).expect("meta.json")).expect("JSON");
    meta["agent"].as_str().expect("agent").to_string()
}
