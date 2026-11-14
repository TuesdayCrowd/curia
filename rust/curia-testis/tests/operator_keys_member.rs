//! R6.66: the Forum serves the operator-acts keys in `GET /v1/log/jwks` as a member `operator_keys`. The crate's key-set
//! parser ([`LogKeys::parse`]) must read a key set that carries that member exactly as it reads the same key set without it:
//! the same `keys`, the same `declaration_keys`, no error.
//!
//! This test was green on arrival: the parser reads the members it knows by name and ignores the rest, so no change to
//! the parser was needed. The test pins that, for a member present and empty, present with an entry, and present and not
//! an array.

use std::fs;

use curia_testis::acta::LogKeys;
use curia_testis::conformance::conformance_dir;
use serde_json::{json, Value};

/// What a parsed key set holds, as the parser's public surface shows it: each `keys` entry found by its `kid`, and the
/// `declaration_keys` list.
fn shown(parsed: &LogKeys, kids: &[&str]) -> String {
    let keys: Vec<String> = kids
        .iter()
        .map(|kid| format!("{kid}: {:?}", parsed.find(kid)))
        .collect();
    format!("{keys:?} / {:?}", parsed.declaration_keys())
}

#[test]
fn an_operator_keys_member_changes_nothing_the_parser_reads() {
    let path = conformance_dir()
        .join("lifecycle")
        .join("compromise-declaration-key-and-log-key-off-curve")
        .join("log-jwks.json");
    let original = fs::read(&path).expect("the vector's key set is readable");
    let base: Value = serde_json::from_slice(&original).expect("the key set is JSON");
    let kids: Vec<&str> = base["keys"]
        .as_array()
        .expect("keys")
        .iter()
        .map(|k| k["kid"].as_str().expect("a kid"))
        .collect();
    assert!(
        base.get("declaration_keys").is_some_and(|d| d.is_array()),
        "the vector's key set must publish declaration keys"
    );
    let without = LogKeys::parse(&original).expect("the key set parses");
    let want = shown(&without, &kids);
    assert!(without.declaration_keys().is_some_and(|d| !d.is_empty()));

    let operator_entry = base["declaration_keys"][0].clone();
    for member in [
        json!([]),
        json!([operator_entry]),
        json!("not an array"),
        json!(null),
    ] {
        let mut with = base.clone();
        with["operator_keys"] = member.clone();
        let bytes = serde_json::to_vec(&with).expect("serializes");
        let parsed = LogKeys::parse(&bytes).unwrap_or_else(|e| {
            panic!("operator_keys {member}: the key set no longer parses: {e}")
        });
        assert_eq!(
            shown(&parsed, &kids),
            want,
            "operator_keys {member}: the parser reads the key set differently"
        );
    }
}
