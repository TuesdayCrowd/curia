//! R6.54 (errata G16): authorship from the log alone.
//!
//! The fixture is two published vectors that describe one log:
//! `acta/key-bound-entry` binds `envelope/ed25519-minimal`'s key to that
//! envelope's author, and `acta/content-entry` is the post the envelope
//! became. Each test builds a tree over those entries (plus a filler leaf),
//! proves both, and asks [`verify_author`] whether the post was signed by the
//! key its author's binding carries, bound first. The head is constructed, not
//! signed: this crate never signs, and the head's signature is `log head`'s
//! business, which `log_outcomes.rs` and the C# end-to-end test cover. The
//! tests with no head run the binary: whatever can be checked is checked
//! before a missing head is reported, so only documents that pass every
//! check needing no head exit 3.

use std::path::{Path, PathBuf};
use std::process::{Command, Output};

use base64::Engine;
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

/// The post, with its text edited by `edit` before it is hashed.
fn post_entry_with(edit: impl Fn(String) -> String) -> String {
    edit(post_entry())
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

/// Runs `log author` with no `--head` over four documents, written to a scratch
/// directory of the test's own.
fn log_author_without_a_head(name: &str, documents: [&[u8]; 4]) -> Output {
    let dir = std::env::temp_dir().join(format!("curia-log-author-{name}"));
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).expect("the scratch directory must be creatable");
    let mut args = vec!["log".to_string(), "author".to_string()];
    for (flag, bytes) in ["--entry", "--proof", "--key-entry", "--key-proof"]
        .into_iter()
        .zip(documents)
    {
        let path = dir.join(format!("{}.json", flag.trim_start_matches('-')));
        std::fs::write(&path, bytes).expect("a scratch file must be writable");
        args.push(flag.to_string());
        args.push(
            path.to_str()
                .expect("the scratch path is UTF-8")
                .to_string(),
        );
    }
    Command::new(env!("CARGO_BIN_EXE_curia-testis"))
        .args(&args)
        .output()
        .expect("failed to spawn the curia-testis binary")
}

/// The exit code, and whether stderr names `predicate`.
fn exit_and_predicate(output: &Output, predicate: &str) -> (Option<i32>, bool) {
    (
        output.status.code(),
        String::from_utf8_lossy(&output.stderr).contains(predicate),
    )
}

/// The CLI's third outcome: with no `--head`, nothing anchors the two proofs,
/// and `log author` says so with exit 3 rather than 0.
#[test]
fn r6_54_log_author_without_a_head_is_not_checked() {
    let key = key_entry(|s| s);
    let post = post_entry();
    let log = log(&[Some(&key), None, Some(&post)]);

    let output = log_author_without_a_head(
        "unanchored",
        [
            &log.documents[2].0,
            &log.documents[2].1,
            &log.documents[0].0,
            &log.documents[0].1,
        ],
    );

    assert_eq!(
        output.status.code(),
        Some(3),
        "log author with no head must not exit 0.\nstdout:\n{}\nstderr:\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
}

/// R6.54 holds the binding's proof to the same signed head as the post's.
/// Here the signed tree holds the post and no binding at all, and the key's
/// proof comes from a tree of the same size that nobody signed: its root is
/// not the head's, and the binding in it counts for nothing.
#[test]
fn r6_54_a_binding_proven_under_a_tree_the_head_does_not_cover_fails() {
    let key = key_entry(|s| s);
    let post = post_entry();
    let signed = log(&[None, None, Some(&post)]);
    let unsigned = log(&[Some(&key), None, Some(&post)]);

    let err = acta::verify_author(
        &signed.documents[2].0,
        &signed.documents[2].1,
        &unsigned.documents[0].0,
        &unsigned.documents[0].1,
        &signed.head,
    )
    .unwrap_err();
    assert_eq!(err.predicate(), "curia/acta/head-root-mismatch", "{err}");
}

/// The author's own enrollment, before the post, of a `kid` the post does not
/// name: it binds nothing the post names, so it fails, and is not the third
/// outcome R6.54 keeps for the author's enrollment of the post's own `kid`.
#[test]
fn r6_54_the_authors_enrollment_of_another_kid_fails() {
    let enrolled = r#"{"actor_id":"agent://curia.example/tuesdaycrowd/scriptor","aggregate_id":"agent://curia.example/tuesdaycrowd/scriptor","event_id":"01K4CQ1TZ0M2P4R6T8V0X2Z4B6","event_type":"agent.enrolled","payload":{"agent_id":"agent://curia.example/tuesdaycrowd/scriptor","kid":"another-kid","reason":"Enrollment accepted: agent key registered with the Registrar"},"server_ts":"2026-09-04T14:00:00.000000Z"}"#;
    let post = post_entry();
    let log = log(&[Some(enrolled), None, Some(&post)]);

    let err = author(&log, 2, 0).unwrap_err();
    assert_eq!(
        (err.predicate(), err.not_established()),
        ("curia/acta/binding-mismatch", false),
        "{err}"
    );
}

/// A binding appended to another identity's stream, though its payload names
/// the post's author and carries the very key that signed: whoever watches the
/// author's own stream never sees it, so it binds nothing for the author.
#[test]
fn r6_54_a_binding_in_another_identitys_stream_fails() {
    let key = key_entry(|s| {
        s.replace(
            r#""aggregate_id":"agent://curia.example/tuesdaycrowd/scriptor""#,
            r#""aggregate_id":"agent://curia.example/tuesdaycrowd/someone-else""#,
        )
    });
    let post = post_entry();
    let log = log(&[Some(&key), None, Some(&post)]);

    let err = author(&log, 2, 0).unwrap_err();
    assert_eq!(err.predicate(), "curia/acta/binding-mismatch", "{err}");
}

/// An entry of the author's stream, naming the post's `kid` and carrying the
/// key that signed it, whose type is neither a binding nor an enrollment: a
/// revocation carries the same members, and never reads as a binding.
#[test]
fn r6_54_an_entry_of_another_type_fails_though_it_carries_the_key() {
    let key = key_entry(|s| {
        s.replace(
            r#""event_type":"agent.key-bound""#,
            r#""event_type":"agent.key-revoked""#,
        )
    });
    let post = post_entry();
    let log = log(&[Some(&key), None, Some(&post)]);

    let err = author(&log, 2, 0).unwrap_err();
    assert_eq!(err.predicate(), "curia/acta/not-a-key-binding", "{err}");
}

/// Whatever can be checked is checked before a missing head is reported:
/// documents that are not JSON fail with no head as they fail with one.
#[test]
fn r6_54_log_author_without_a_head_fails_what_is_not_json() {
    let garbage: &[u8] = b"not json";
    let output = log_author_without_a_head("not-json", [garbage, garbage, garbage, garbage]);

    assert_eq!(
        exit_and_predicate(&output, "curia/acta/malformed"),
        (Some(1), true),
        "stdout:\n{}\nstderr:\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
}

/// A post's entry altered after its leaf was proven fails its own leaf with no
/// head, as `log inclusion` fails it with none.
#[test]
fn r6_54_log_author_without_a_head_fails_a_forged_entry() {
    let key = key_entry(|s| s);
    let post = post_entry();
    let log = log(&[Some(&key), None, Some(&post)]);
    let forged = String::from_utf8(log.documents[2].0.clone())
        .expect("an entry document is UTF-8")
        .replace("Minimal fixture", "Forged fixture");

    let output = log_author_without_a_head(
        "forged",
        [
            forged.as_bytes(),
            &log.documents[2].1,
            &log.documents[0].0,
            &log.documents[0].1,
        ],
    );

    assert_eq!(
        exit_and_predicate(&output, "curia/acta/leaf-mismatch"),
        (Some(1), true),
        "stdout:\n{}\nstderr:\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
}

/// With no head, the two proofs must still be against one tree: any head
/// covering both would be that tree's, so proofs against two trees fail with
/// no head as they fail with one.
#[test]
fn r6_54_without_a_head_proofs_against_two_trees_fail() {
    let key = key_entry(|s| s);
    let post = post_entry();
    let three = log(&[Some(&key), None, Some(&post)]);
    let four = log(&[Some(&key), None, Some(&post), None]);

    let err = acta::check_author_unanchored(
        &three.documents[2].0,
        &three.documents[2].1,
        &four.documents[0].0,
        &four.documents[0].1,
    )
    .unwrap_err();
    assert_eq!(err.predicate(), "curia/acta/tree-mismatch", "{err}");
}

/// An envelope no Forum could have accepted -- `author` twice, the second the
/// binding's -- fails ADMIT, as the signature check would fail it, though the
/// binding's order alone would have made the post read as not checked.
#[test]
fn r6_54_an_envelope_admit_refuses_fails_though_its_key_is_bound_after_it() {
    let key = key_entry(|s| s);
    let post = post_entry_with(|s| {
        s.replacen(
            r#"{\"author\":"#,
            r#"{\"author\":\"agent://curia.example/tuesdaycrowd/someone-else\",\"author\":"#,
            1,
        )
    });
    let log = log(&[Some(&post), None, Some(&key)]);

    let err = author(&log, 0, 2).unwrap_err();
    assert_eq!(
        (err.predicate(), err.not_established()),
        ("curia/acta/malformed", false),
        "{err}"
    );
}

/// A protected header no Forum could have accepted -- `kid` twice, the second
/// the binding's -- fails as the signature check would fail it, though the
/// binding's order alone would have made the post read as not checked.
#[test]
fn r6_54_a_signature_header_naming_a_member_twice_fails_though_its_key_is_bound_after_it() {
    let base64url = base64::engine::general_purpose::URL_SAFE_NO_PAD;
    let original = post_entry();
    let fields: serde_json::Value = serde_json::from_str(&original).expect("the vector parses");
    let header = fields["payload"]["signature"]
        .as_str()
        .and_then(|s| s.split('.').next())
        .expect("the vector's signature has a protected header");
    let decoded = String::from_utf8(base64url.decode(header).expect("the header is base64url"))
        .expect("the header is UTF-8");
    let doubled = decoded.replacen(r#""kid":"#, r#""kid":"another-kid","kid":"#, 1);
    let post = original.replace(header, &base64url.encode(doubled));
    let key = key_entry(|s| s);
    let log = log(&[Some(&post), None, Some(&key)]);

    let err = author(&log, 0, 2).unwrap_err();
    assert_eq!(
        (err.predicate(), err.not_established()),
        ("curia/acta/malformed", false),
        "{err}"
    );
}
