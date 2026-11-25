//! R4.46 (errata G79): `conformance/enroll-proof/`, judged by the independent verifier.
//!
//! The verifier in `curia_testis::enroll_proof` is written from the errata's sentence and the vectors alone. Each
//! case is a request, the Forum's context (audience, clock, current nonces, spent `jti`s) and the verdict the
//! construction fixes; the test compares type and detail with `expected.json`. A case where the two disagree is a
//! defect of one of them, and none is listed.

use std::fs;

use curia_testis::conformance::{conformance_dir, Corpus};
use curia_testis::enroll_proof::{verify, Context, Request, Verdict};
use serde_json::Value;

fn strings(value: &Value) -> Vec<String> {
    value
        .as_array()
        .map(|a| {
            a.iter()
                .filter_map(|v| v.as_str().map(str::to_string))
                .collect()
        })
        .unwrap_or_default()
}

#[test]
fn r4_46_every_enroll_proof_case_reaches_its_verdict() {
    let corpus = Corpus::load(&conformance_dir()).expect("the corpus loads");
    assert_eq!(
        corpus.enroll_proof.len(),
        56,
        "conformance/enroll-proof/ holds 56 cases"
    );

    let mut failures = Vec::new();
    for case in &corpus.enroll_proof {
        let read = |name: &str| -> Value {
            serde_json::from_slice(
                &fs::read(case.dir.join(name))
                    .unwrap_or_else(|e| panic!("{}: {name}: {e}", case.case)),
            )
            .unwrap_or_else(|e| panic!("{}: {name} is JSON: {e}", case.case))
        };
        let input = read("input.json");
        let expected = read("expected.json");

        let request = &input["request"];
        let context = &input["context"];
        let context = Context {
            audience: context["audience"].as_str().unwrap_or_default().to_string(),
            now: context["now"].as_i64().expect("context.now is an integer"),
            current_nonces: strings(&context["current_nonces"]),
            spent_jtis: strings(&context["spent_jtis"]),
        };
        let verdict = verify(
            &Request {
                agent_id: request["agent_id"].as_str().unwrap_or_default(),
                alg: request["alg"].as_str().unwrap_or_default(),
                kid: request["kid"].as_str().unwrap_or_default(),
                public_key: request["public_key"].as_str().unwrap_or_default(),
                proof: request["proof"].as_str(),
            },
            &context,
        );

        let agrees = match (&verdict, expected["verdict"].as_str()) {
            (Verdict::Ok, Some("ok")) => true,
            (Verdict::Refused { type_slug, detail }, Some("refused")) => {
                expected["type"].as_str() == Some(*type_slug)
                    && expected["detail"].as_str() == detail.as_deref()
            }
            _ => false,
        };
        if !agrees {
            failures.push(format!(
                "{}: got {verdict:?}, expected {expected}",
                case.case
            ));
        }
    }
    assert!(failures.is_empty(), "\n{}", failures.join("\n"));
}
