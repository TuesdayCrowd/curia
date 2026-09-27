//! R6.54 (errata G16): authorship from the log alone.
//!
//! The fixture is two published vectors that describe one log:
//! `acta/key-bound-entry` binds `envelope/ed25519-minimal`'s key to that
//! envelope's author, and `acta/content-entry` is the post the envelope
//! became. Each test builds a tree over those entries (plus a filler leaf),
//! proves both, and asks [`verify_author`] whether the post was signed by the
//! key its author's binding carries, bound first. The head is constructed, not
//! signed: this crate never signs, and the head's signature is `log head`'s
//! business, which `log_outcomes.rs` and the C# end-to-end test cover.

use std::path::{Path, PathBuf};
use std::process::Command;

use curia_testis::acta::{self, ActaError, VerifiedHead};
use curia_testis::merkle::{self, Hash};

fn conformance_dir() -> PathBuf {
    if let Ok(dir) = std::env::var("CURIA_CONFORMANCE_DIR") {
        return PathBuf::from(dir);
    }
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../../conformance")
}

fn vector(name: &str) -> String {
    std::fs::read_to_string(conformance_dir().join("acta").join(name).join("input.json"))
        .unwrap_or_else(|e| panic!("conformance/acta/{name}/input.json must be readable: {e}"))
}

/// The key binding, with its text edited by `edit` before it is hashed, so a
/// tampered entry is still a leaf of a tree that proves it.
fn key_entry(edit: impl Fn(String) -> String) -> String {
    edit(vector("key-bound-entry"))
}

fn post_entry() -> String {
    vector("content-entry")
}

fn leaf_of(entry: &str) -> Hash {
    merkle::leaf_hash(
        &curia_testis::canonicalize(entry.as_bytes()).expect("a vector canonicalizes"),
    )
}

/// A tree over `entries` in order (a `None` is a filler leaf), and for each
/// named index its entry document and proof, plus the head over the whole tree.
struct Log {
    documents: Vec<(Vec<u8>, Vec<u8>)>,
    head: VerifiedHead,
}

fn log(entries: &[Option<&str>]) -> Log {
    let leaves: Vec<Hash> = entries
        .iter()
        .enumerate()
        .map(|(i, e)| match e {
            Some(entry) => leaf_of(entry),
            None => merkle::leaf_hash(format!("filler-{i}").as_bytes()),
        })
        .collect();
    let root = merkle::root(&leaves);

    let documents = entries
        .iter()
        .enumerate()
        .map(|(i, e)| {
            let entry = e.unwrap_or("{}");
            let document = format!(r#"{{"log_index":{i},"entry":{entry}}}"#).into_bytes();
            let path: Vec<String> = merkle::inclusion_path(&leaves, i)
                .iter()
                .map(acta::format_digest)
                .collect();
            let proof = serde_json::to_vec(&serde_json::json!({
                "log_index": i,
                "tree_size": leaves.len(),
                "leaf_hash": acta::format_digest(&leaves[i]),
                "audit_path": path,
                "root_hash": acta::format_digest(&root),
                "head_signed": true,
            }))
            .expect("a proof serializes");
            (document, proof)
        })
        .collect();

    Log {
        documents,
        head: VerifiedHead {
            root,
            tree_size: leaves.len() as u64,
            timestamp: "2026-09-04T17:00:00.000000Z".to_string(),
            kid: "log-test".to_string(),
            alg: "ES256".to_string(),
        },
    }
}

fn author(log: &Log, post: usize, key: usize) -> Result<acta::VerifiedAuthor, ActaError> {
    acta::verify_author(
        &log.documents[post].0,
        &log.documents[post].1,
        &log.documents[key].0,
        &log.documents[key].1,
        &log.head,
    )
}

#[test]
fn r6_54_a_post_signed_under_the_key_its_author_bound_before_it_verifies() {
    let key = key_entry(|s| s);
    let post = post_entry();
    let log = log(&[Some(&key), None, Some(&post)]);

    let verified = author(&log, 2, 0).expect("the log alone establishes authorship");

    assert_eq!(
        (
            verified.author.as_str(),
            verified.kid.as_str(),
            verified.alg.as_str(),
            verified.key_index,
            verified.post_index
        ),
        (
            "agent://curia.example/tuesdaycrowd/scriptor",
            "conformance-ed25519-minimal",
            "EdDSA",
            0,
            2
        )
    );
}

/// The author's own binding, after the post: the log says nothing about
/// which key was the author's when the post was accepted. R6.54's third
/// outcome, never a failure: an identity whose first binding came after its
/// history (it enrolled before the log recorded enrollments, and anyone
/// re-announced its public key) would otherwise read as forged throughout.
#[test]
fn r6_54_a_key_bound_after_the_post_is_not_established() {
    let key = key_entry(|s| s);
    let post = post_entry();
    let log = log(&[Some(&post), None, Some(&key)]);

    let err = author(&log, 0, 2).unwrap_err();
    assert_eq!(
        (err.predicate(), err.not_established()),
        ("curia/acta/bound-after-post", true),
        "{err}"
    );
}

#[test]
fn r6_54_a_binding_carrying_another_key_fails_at_the_signature() {
    // envelope/ed25519-full's key: a real Ed25519 key, and not the one that signed the post.
    let key = key_entry(|s| {
        s.replace(
            "HRzJlnTufZYYTZyCDBpyP5ldQ38JlbCeDOQHgIozgg8",
            "8p38omqqbKnPOw4UNosc4kVE9KdjrnDPnl7fCLTv0uo",
        )
    });
    let post = post_entry();
    let log = log(&[Some(&key), None, Some(&post)]);

    let err = author(&log, 2, 0).unwrap_err();
    assert!(
        matches!(err, ActaError::Author(_)),
        "expected the signature to fail under the bound key, got {err}"
    );
}

#[test]
fn r6_54_a_binding_for_another_identity_fails() {
    let key = key_entry(|s| s.replace("tuesdaycrowd/scriptor", "tuesdaycrowd/someone-else"));
    let post = post_entry();
    let log = log(&[Some(&key), None, Some(&post)]);

    let err = author(&log, 2, 0).unwrap_err();
    assert_eq!(err.predicate(), "curia/acta/binding-mismatch", "{err}");
}

#[test]
fn r6_54_an_enrollment_that_names_the_kid_alone_is_not_checked() {
    let enrolled = r#"{"actor_id":"agent://curia.example/tuesdaycrowd/scriptor","aggregate_id":"agent://curia.example/tuesdaycrowd/scriptor","event_id":"01K4CQ1TZ0M2P4R6T8V0X2Z4B6","event_type":"agent.enrolled","payload":{"agent_id":"agent://curia.example/tuesdaycrowd/scriptor","kid":"conformance-ed25519-minimal","reason":"Enrollment accepted: agent key registered with the Registrar"},"server_ts":"2026-09-04T14:00:00.000000Z"}"#;
    let post = post_entry();
    let log = log(&[Some(enrolled), None, Some(&post)]);

    let err = author(&log, 2, 0).unwrap_err();
    assert_eq!(
        (err.predicate(), err.not_established()),
        ("curia/acta/key-not-carried", true),
        "{err}"
    );
}

/// R6.54 reports only the author's own enrollment naming the post's kid as
/// could-not-be-checked; another identity's enrollment, naming the same kid,
/// binds nothing the post names and fails.
#[test]
fn r6_54_another_identitys_enrollment_fails() {
    let enrolled = r#"{"actor_id":"agent://curia.example/tuesdaycrowd/someone-else","aggregate_id":"agent://curia.example/tuesdaycrowd/someone-else","event_id":"01K4CQ1TZ0M2P4R6T8V0X2Z4B6","event_type":"agent.enrolled","payload":{"agent_id":"agent://curia.example/tuesdaycrowd/someone-else","kid":"conformance-ed25519-minimal","reason":"Enrollment accepted: agent key registered with the Registrar"},"server_ts":"2026-09-04T14:00:00.000000Z"}"#;
    let post = post_entry();
    let log = log(&[Some(enrolled), None, Some(&post)]);

    let err = author(&log, 2, 0).unwrap_err();
    assert_eq!(
        (err.predicate(), err.not_established()),
        ("curia/acta/binding-mismatch", false),
        "{err}"
    );
}

#[test]
fn r6_54_an_entry_that_is_not_a_post_fails() {
    let key = key_entry(|s| s);
    let log = log(&[Some(&key), None, Some(&key)]);

    let err = author(&log, 2, 0).unwrap_err();
    assert_eq!(err.predicate(), "curia/acta/not-a-post", "{err}");
}

#[test]
fn r6_54_a_head_over_another_tree_fails() {
    let key = key_entry(|s| s);
    let post = post_entry();
    let mut log = log(&[Some(&key), None, Some(&post)]);
    log.head.tree_size += 1;

    let err = author(&log, 2, 0).unwrap_err();
    assert_eq!(err.predicate(), "curia/acta/head-size-mismatch", "{err}");
}

/// The CLI's third outcome: with no `--head`, nothing anchors the two proofs,
/// and `log author` says so with exit 3 rather than 0.
#[test]
fn r6_54_log_author_without_a_head_is_not_checked() {
    let key = key_entry(|s| s);
    let post = post_entry();
    let log = log(&[Some(&key), None, Some(&post)]);

    let dir = std::env::temp_dir().join("curia-log-author-unanchored");
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).expect("the scratch directory must be creatable");
    let files = [
        ("entry.json", &log.documents[2].0),
        ("proof.json", &log.documents[2].1),
        ("key-entry.json", &log.documents[0].0),
        ("key-proof.json", &log.documents[0].1),
    ];
    for (name, bytes) in files {
        std::fs::write(dir.join(name), bytes).expect("a scratch file must be writable");
    }

    let output = Command::new(env!("CARGO_BIN_EXE_curia-testis"))
        .args([
            "log",
            "author",
            "--entry",
            dir.join("entry.json").to_str().unwrap(),
            "--proof",
            dir.join("proof.json").to_str().unwrap(),
            "--key-entry",
            dir.join("key-entry.json").to_str().unwrap(),
            "--key-proof",
            dir.join("key-proof.json").to_str().unwrap(),
        ])
        .output()
        .expect("failed to spawn the curia-testis binary");

    assert_eq!(
        output.status.code(),
        Some(3),
        "log author with no head must not exit 0.\nstdout:\n{}\nstderr:\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
}
