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

use curia_testis::acta::{self, ActaError, LogKeys, VerifiedHead};
use curia_testis::conformance::{conformance_dir, Corpus, LifecycleVector};
use curia_testis::lifecycle::{verify_lifecycle, verify_lifecycle_under};
use curia_testis::merkle;

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

#[test]
fn r6_54_every_lifecycle_case_reaches_its_verdict_and_names_its_predicate() {
    let corpus = Corpus::load(&conformance_dir()).expect("the corpus loads");
    assert_eq!(
        corpus.lifecycle.len(),
        53,
        "conformance/lifecycle/ holds the 53 cases (the 30 PR 1 cases, the 7 of Task 1.9's rulings and the 16 of PR 2's declarations)"
    );

    let mut failures = Vec::new();
    for vector in &corpus.lifecycle {
        let (verdict, predicate) = judge(vector);
        if let Some(difference) = mismatch(vector, &vector.predicate, &verdict, &predicate) {
            failures.push(format!("{}: {difference}", vector.case));
        }
    }
    assert!(failures.is_empty(), "\n{}", failures.join("\n"));
}

/// The comparison this runner makes for every case, on the verdict and on the predicate: `None` when they agree,
/// else what differs. One function, so the fact below can hand it a wrong expectation and see it refuse: a runner
/// that compared the verdict alone would make every falsify row that goes red on predicate vacuous.
fn mismatch(
    vector: &LifecycleVector,
    expected_predicate: &str,
    verdict: &str,
    predicate: &str,
) -> Option<String> {
    if verdict != vector.verdict {
        return Some(format!(
            "verdict: expected {}, got {verdict}",
            vector.verdict
        ));
    }
    if predicate != expected_predicate {
        return Some(format!(
            "predicate: expected {expected_predicate}, got {predicate}"
        ));
    }
    None
}

#[test]
fn r6_54_the_lifecycle_family_covers_each_verdict() {
    let corpus = Corpus::load(&conformance_dir()).expect("the corpus loads");
    for verdict in ["verified", "failed", "could-not-check", "disputed"] {
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

/// A post entry moved to a log index far enough along that `before` lifecycle entries can precede it, and
/// a chain of `before` entries that hold nothing but their index. The bound is counted from the indices
/// alone, before any entry is folded, so these entries need not be real.
fn chain_of(before: u64) -> Documents {
    let corpus = Corpus::load(&conformance_dir()).expect("the corpus loads");
    let vector = corpus
        .lifecycle
        .iter()
        .find(|v| v.case == "rotation-then-post")
        .expect("the corpus holds rotation-then-post");
    let mut d = documents(vector);

    let mut post: serde_json::Value = serde_json::from_slice(&d.post_entry).expect("post entry");
    post["log_index"] = serde_json::json!(before + 10);
    d.post_entry = serde_json::to_vec(&post).expect("post entry");

    d.chain = (0..before)
        .map(|i| (format!("{{\"log_index\":{i}}}").into_bytes(), Vec::new()))
        .collect();
    d
}

/// Spec §4.8: the bound is a literal, 1,024, and it is not a bound a refactor may move.
#[test]
fn r6_54_the_bound_on_entries_before_a_post_is_1024() {
    assert_eq!(curia_testis::lifecycle::MAX_ENTRIES_BEFORE_POST, 1024);
}

/// Exactly 1,024 entries before the post is a lifecycle this verifier folds: whatever it reaches, it
/// is not `lifecycle-too-long`.
#[test]
fn r6_54_a_lifecycle_of_1024_entries_before_the_post_is_folded() {
    let d = chain_of(1024);
    let err = verify_lifecycle(&d.post_entry, &d.chain, &d.head, &d.log_jwks)
        .expect_err("entries that hold nothing do not fold to a verdict");
    assert!(
        !matches!(err, ActaError::LifecycleTooLong),
        "1,024 entries before the post are within the bound, got {err:?}"
    );
}

/// One more than 1,024 before the post is not folded: it is not established.
#[test]
fn r6_54_a_lifecycle_of_1025_entries_before_the_post_is_too_long() {
    let d = chain_of(1025);
    let err = verify_lifecycle(&d.post_entry, &d.chain, &d.head, &d.log_jwks)
        .expect_err("1,025 entries are too many");
    assert!(matches!(err, ActaError::LifecycleTooLong), "got {err:?}");
    assert!(err.not_established());
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

fn case(name: &str) -> LifecycleVector {
    let corpus = Corpus::load(&conformance_dir()).expect("the corpus loads");
    corpus
        .lifecycle
        .into_iter()
        .find(|v| v.case == name)
        .unwrap_or_else(|| panic!("the corpus holds {name}"))
}

/// R6.57: this runner compares `predicate` as well as `verdict` for every compromise case. A case run against an
/// expectation whose verdict is right and whose predicate is wrong is refused, for each distinct verdict the PR 2
/// cases reach. The Rust twin of `R6_57_TheRunnerComparisonFailsOnAWrongPredicate`.
#[test]
fn runner_comparison_fails_on_a_wrong_predicate() {
    for name in [
        "compromised-before-post",
        "compromise-kid-of-other-identity",
        "compromise-unknown-reason",
    ] {
        let vector = case(name);
        let (verdict, predicate) = judge(&vector);
        assert_eq!(
            mismatch(&vector, &vector.predicate, &verdict, &predicate),
            None,
            "{name} agrees with itself"
        );
        let wrong = format!("not-{}", vector.predicate);
        let difference = mismatch(&vector, &wrong, &verdict, &predicate)
            .unwrap_or_else(|| panic!("{name}: a wrong expected predicate must be refused"));
        assert!(difference.starts_with("predicate:"), "{difference}");
    }
}

/// R6.57: the sixteen cases of PR 2 are all present with the verdict and predicate the vocabulary names, so a case
/// dropped from the family is a red fact and not a smaller count.
#[test]
fn the_sixteen_compromise_cases_are_all_present_with_their_vocabulary() {
    let expected = [
        ("compromised-before-post", "disputed", "disputed"),
        (
            "compromised-after-intervening-rotation",
            "disputed",
            "disputed",
        ),
        ("compromise-post-under-cascaded-key", "disputed", "disputed"),
        ("compromise-sibling-declared-alone", "disputed", "disputed"),
        (
            "declaration-signed-as-head",
            "failed",
            "lifecycle-typ-mismatch",
        ),
        (
            "compromise-kids-omits-cascade",
            "failed",
            "declaration-mismatch",
        ),
        (
            "compromise-kids-reordered",
            "failed",
            "declaration-mismatch",
        ),
        (
            "compromise-kids-siblings-reordered",
            "failed",
            "declaration-mismatch",
        ),
        (
            "compromise-from-index-after-declaration",
            "failed",
            "declaration-mismatch",
        ),
        (
            "compromise-from-index-at-declaration",
            "failed",
            "declaration-mismatch",
        ),
        (
            "compromise-from-index-before-binding",
            "failed",
            "declaration-mismatch",
        ),
        (
            "compromise-kid-of-other-identity",
            "failed",
            "kid-not-bound-to-agent",
        ),
        (
            "compromise-declaration-kid-not-valid-at-position",
            "failed",
            "declaration-key-unpublished",
        ),
        (
            "compromise-unknown-reason",
            "could-not-check",
            "unknown-reason",
        ),
        (
            "compromise-declaration-kid-position-unknown",
            "could-not-check",
            "log-key-position-unknown",
        ),
        (
            "compromise-v2-under-unknown-signer",
            "could-not-check",
            "unknown-lifecycle-kind",
        ),
    ];
    assert_eq!(expected.len(), 16);
    for (name, verdict, predicate) in expected {
        let vector = case(name);
        assert_eq!(
            (vector.verdict.as_str(), vector.predicate.as_str()),
            (verdict, predicate),
            "{name} expects"
        );
        assert_eq!(
            judge(&vector),
            (verdict.to_string(), predicate.to_string()),
            "{name} reaches"
        );
    }
}

/// R6.26 (revised), F2-25, F2-26: a log key whose `log_index` is negative is a position this verifier cannot read,
/// as an absent one is, and the declaration is not established, never verified. The key set is
/// `compromise-declaration-kid-position-unknown`'s with the declaring key's position set to -1, and again to -5; the
/// control gives it the position its `log.key` entry has (3, before the declaration at 4), and the declaration holds.
/// The Rust twin of `R6_26_ANegativeLogIndexCouldNotBeChecked`.
#[test]
fn negative_log_index_could_not_be_checked() {
    let vector = case("compromise-declaration-kid-position-unknown");
    let d = documents(&vector);

    let with_position = |position: &str| -> Vec<u8> {
        let mut jwks: serde_json::Value = serde_json::from_slice(&d.log_jwks).expect("log jwks");
        let keys = jwks["keys"].as_array_mut().expect("keys");
        let declaring = keys
            .iter_mut()
            .find(|k| k["alg"] == "ES256")
            .expect("the declaring key");
        declaring["log_index"] = serde_json::from_str(position).expect("a JSON number");
        serde_json::to_vec(&jwks).expect("log jwks")
    };
    let judged = |jwks: &[u8]| match verify_lifecycle(&d.post_entry, &d.chain, &d.head, jwks) {
        Ok(_) => ("verified".to_string(), "verified".to_string()),
        Err(err) if err.disputed() => ("disputed".to_string(), "disputed".to_string()),
        Err(err) => (
            if err.not_established() {
                "could-not-check"
            } else {
                "failed"
            }
            .to_string(),
            err.predicate()
                .trim_start_matches("curia/acta/")
                .to_string(),
        ),
    };

    assert_eq!(
        judged(&d.log_jwks),
        (
            "could-not-check".to_string(),
            "log-key-position-unknown".to_string()
        )
    );
    assert_eq!(
        judged(&with_position("3")),
        ("disputed".to_string(), "disputed".to_string()),
        "the control: the position the log.key entry has"
    );
    for position in ["-1", "-5", "1.5", "9007199254740992"] {
        assert_eq!(
            judged(&with_position(position)),
            (
                "could-not-check".to_string(),
                "log-key-position-unknown".to_string()
            ),
            "log_index {position}"
        );
    }
}

/// R6.49, R6.26 (revised) (a regression guard, green before this stage): a head signed by the log key under a
/// declaration's `typ` is refused, by exact `typ`. The signature is pinned beside `rotation-then-post`
/// (`head-as-compromise.json`): the same head, by the same log key, over the same bytes; the control is the head
/// itself, which verifies. The Rust twin of `R6_26_TheHeadVerifierRefusesACompromiseTyp`.
#[test]
fn head_verifier_refuses_a_compromise_typ() {
    let dir = conformance_dir()
        .join("lifecycle")
        .join("rotation-then-post");
    let jwks = read(&dir.join("log-jwks.json"));

    acta::verify_head(&read(&dir.join("head.json")), &jwks).expect("the head itself verifies");

    let err = acta::verify_head(&read(&dir.join("head-as-compromise.json")), &jwks)
        .expect_err("a head signed under a declaration's typ is refused");
    assert_eq!(err.predicate(), "curia/jws/typ-invalid");
}

/// The pinned signature of `compromised-before-post`'s statement by the agent key `conformance-ed25519-minimal` under
/// the declaration's header `typ`. Ed25519 is deterministic and this crate has no signer, so the string is pinned;
/// the C# twin signs it live and holds both to the same bytes (`CompromiseReaderTests.AgentSignedDeclaration`).
const AGENT_SIGNED_DECLARATION: &str = "eyJhbGciOiJFZERTQSIsImtpZCI6ImNvbmZvcm1hbmNlLWVkMjU1MTktbWluaW1hbCIsInR5cCI6ImN1cmlhLWtleS1jb21wcm9taXNlXHUwMDJCandzIiwiYjY0IjpmYWxzZSwiY3JpdCI6WyJiNjQiXX0..xLupPvseTID4h-fbaeplsDeGQPgvQkkgNcWUet2XdkKa-6J3KCOEK5t615VlPlFrfCRXIAEzq1blWb6NW80oAw";

fn parse_digest(text: &str) -> merkle::Hash {
    let hex = text.strip_prefix("sha256:").expect("a sha256: digest");
    let mut hash = [0u8; 32];
    for (i, byte) in hash.iter_mut().enumerate() {
        *byte = u8::from_str_radix(&hex[2 * i..2 * i + 2], 16).expect("hex");
    }
    hash
}

/// Each lifecycle entry's body and the body of its proof, in log order.
type Chain = Vec<(Vec<u8>, Vec<u8>)>;

/// `compromised-before-post` with its declaration replaced: the entry's leaf is recomputed, and so are the tree and
/// every proof, from the leaves the case holds. The case's log has five leaves; the one it does not supply, the
/// `log.key` entry at 3, is the first node of the post's audit path. Returns the chain and the head over the new tree.
fn with_declaration(d: &Documents, declaration: &str) -> (Chain, VerifiedHead) {
    let post: serde_json::Value = serde_json::from_slice(&d.post_entry).expect("post entry");
    let post_proof_path = {
        let proofs = conformance_dir()
            .join("lifecycle")
            .join("compromised-before-post")
            .join("proofs")
            .join("2.json");
        let proof: serde_json::Value = serde_json::from_slice(&read(&proofs)).expect("proof");
        parse_digest(proof["audit_path"][0].as_str().expect("path"))
    };

    let mut entries: Vec<serde_json::Value> = d
        .chain
        .iter()
        .map(|(e, _)| serde_json::from_slice(e).expect("entry"))
        .collect();
    let at = entries
        .iter()
        .position(|e| e["entry"]["event_type"] == "agent.key-compromised")
        .expect("a declaration");
    entries[at]["entry"]["payload"]["declaration"] = serde_json::json!(declaration);
    let canonical =
        curia_testis::canonicalize(&serde_json::to_vec(&entries[at]["entry"]).expect("entry"))
            .expect("canonical");
    let leaf = merkle::leaf_hash(&canonical);
    entries[at]["leaf_hash"] = serde_json::json!(acta::format_digest(&leaf));

    let mut leaves: Vec<Option<merkle::Hash>> = vec![None; 5];
    leaves[3] = Some(post_proof_path);
    leaves[post["log_index"].as_u64().expect("index") as usize] =
        Some(parse_digest(post["leaf_hash"].as_str().expect("leaf")));
    for e in &entries {
        let index = e["log_index"].as_u64().expect("index") as usize;
        leaves[index] = Some(parse_digest(e["leaf_hash"].as_str().expect("leaf")));
    }
    let leaves: Vec<merkle::Hash> = leaves.into_iter().map(|l| l.expect("a leaf")).collect();
    let root = merkle::root(&leaves);

    let chain = entries
        .iter()
        .map(|e| {
            let index = e["log_index"].as_u64().expect("index") as usize;
            let proof = serde_json::json!({
                "log_index": index,
                "tree_size": 5,
                "leaf_hash": acta::format_digest(&leaves[index]),
                "audit_path": merkle::inclusion_path(&leaves, index)
                    .iter()
                    .map(acta::format_digest)
                    .collect::<Vec<_>>(),
                "root_hash": acta::format_digest(&root),
                "head_signed": true,
            });
            (
                serde_json::to_vec(e).expect("entry"),
                serde_json::to_vec(&proof).expect("proof"),
            )
        })
        .collect();
    let head = VerifiedHead {
        root,
        tree_size: 5,
        timestamp: "2026-09-05T12:00:00Z".to_string(),
        kid: "conformance-log-ed25519".to_string(),
        alg: "EdDSA".to_string(),
    };
    (chain, head)
}

/// R6.26 (revised): a declaration an agent's key signed is refused by a verifier, which verifies it under the log's
/// key set and under nothing else. The variant is `compromised-before-post`'s log with its declaration re-signed by
/// `conformance-ed25519-minimal`, a key the author's lifecycle binds: the entry is re-hashed and its proofs and head
/// recomputed, so the log is as sound as the original and the signer is the one defect. (The acta vector
/// `key-compromised-entry` is a single entry with no proofs, so it cannot be folded alone; this case carries the same
/// statement shape and the same header `typ`.) The control is the original log, which is disputed.
#[test]
fn a_declaration_signed_by_an_agent_key_is_refused_by_readers() {
    let vector = case("compromised-before-post");
    let d = documents(&vector);
    let head = acta::verify_head(&d.head, &d.log_jwks).expect("the head");
    let keys = LogKeys::parse(&d.log_jwks).expect("the log keys");

    // The control: the original log is disputed, and the rebuilt log with the original declaration is too.
    let original = d
        .chain
        .iter()
        .filter_map(|(e, _)| serde_json::from_slice::<serde_json::Value>(e).ok())
        .find(|e| e["entry"]["event_type"] == "agent.key-compromised")
        .expect("a declaration");
    let signed_by_log_key = original["entry"]["payload"]["declaration"]
        .as_str()
        .expect("declaration")
        .to_string();
    let (rebuilt, rebuilt_head) = with_declaration(&d, &signed_by_log_key);
    let control = verify_lifecycle_under(&d.post_entry, &rebuilt, &rebuilt_head, &keys);
    assert!(
        matches!(control, Err(ref e) if e.disputed()),
        "the rebuilt log is as sound as the original, got {control:?}"
    );
    assert!(
        verify_lifecycle_under(&d.post_entry, &d.chain, &head, &keys).is_err_and(|e| e.disputed())
    );

    let (chain, variant_head) = with_declaration(&d, AGENT_SIGNED_DECLARATION);
    let err = verify_lifecycle_under(&d.post_entry, &chain, &variant_head, &keys)
        .expect_err("a declaration an agent key signed is refused");
    assert_eq!(err.predicate(), "curia/acta/declaration-key-unpublished");
    assert!(!err.not_established() && !err.disputed());
}

/// R6.57: a declaration that precedes the post names the post's key at a position where the key was already declared:
/// the post could not have been accepted under it as the author's, and the verifier fails it, naming
/// `compromised-before-post` (the failed predicate; the case of that name is the disputed one). The log is
/// `compromised-before-post`'s with the post's leaf moved past the declaration, which the fold reads from the post's
/// own entry; the control is the unmoved log.
#[test]
fn a_declaration_that_precedes_the_post_fails_it() {
    let vector = case("compromised-before-post");
    let d = documents(&vector);
    assert_eq!(
        judge(&vector),
        ("disputed".to_string(), "disputed".to_string())
    );

    let mut post: serde_json::Value = serde_json::from_slice(&d.post_entry).expect("post entry");
    post["log_index"] = serde_json::json!(6);
    let moved = serde_json::to_vec(&post).expect("post entry");

    let err = verify_lifecycle(&moved, &d.chain, &d.head, &d.log_jwks)
        .expect_err("a declaration before the post fails it");
    assert_eq!(err.predicate(), "curia/acta/compromised-before-post");
    assert!(!err.not_established() && !err.disputed());
}

/// R6.57, the Rust twin of `R6_57_ACascadedKeysKeySetEntryCarriesTheDeclarationIndex`: the declaration of
/// `compromise-post-under-cascaded-key` names the cascaded key k2 as well as k1, and a post under k2 is disputed by
/// the declaration at its own index. The key set the Forum serves names a declaration for every kid it lists (the C#
/// half serves it from a real log); this verifier is given that declaration with the lifecycle and reaches it.
#[test]
fn a_cascaded_keys_key_set_entry_carries_the_declaration_index() {
    let vector = case("compromise-post-under-cascaded-key");
    let d = documents(&vector);
    let declaration = d
        .chain
        .iter()
        .filter_map(|(e, _)| serde_json::from_slice::<serde_json::Value>(e).ok())
        .find(|e| e["entry"]["event_type"] == "agent.key-compromised")
        .expect("a declaration");
    let kids = declaration["entry"]["payload"]["kids"]
        .as_array()
        .expect("kids");
    assert_eq!(
        kids.len(),
        2,
        "the declaration names the compromised key and the key it bound"
    );

    let err = verify_lifecycle(&d.post_entry, &d.chain, &d.head, &d.log_jwks)
        .expect_err("a post under the cascaded key is disputed");
    match err {
        ActaError::Disputed {
            kid,
            declaration: at,
            ..
        } => {
            assert_eq!(
                kid,
                kids[1].as_str().expect("kid"),
                "the post's key is the cascaded one"
            );
            assert_eq!(Some(at), declaration["log_index"].as_u64());
        }
        other => panic!("expected disputed, got {other:?}"),
    }
}

/// R6.57: `log author` exits 4 on a disputed post, with `lifecycle: disputed` on stdout and the predicate on
/// stderr; failed outranks it (1), and not established (3) outranks it. 4 is its own code, free until now.
#[test]
fn r6_57_log_author_exits_four_on_a_disputed_post() {
    let out = log_author("compromised-before-post", 2, 1, &[0, 1, 4]);
    assert_eq!(out.status.code(), Some(4), "{}", text(&out.stderr));
    assert!(
        text(&out.stdout).contains("lifecycle: disputed"),
        "{}",
        text(&out.stdout)
    );
    assert!(
        text(&out.stderr).contains("curia/acta/disputed"),
        "{}",
        text(&out.stderr)
    );
    assert!(
        !text(&out.stdout).contains("lifecycle: verified"),
        "disputed is never printed as verified"
    );

    // Failed outranks it: a declaration that fails KF3 never disputes, it fails.
    let failed = log_author("compromise-kids-omits-cascade", 2, 1, &[0, 1, 3, 5]);
    assert_eq!(failed.status.code(), Some(1), "{}", text(&failed.stderr));
    assert!(text(&failed.stderr).contains("curia/acta/declaration-mismatch"));

    // Not established outranks it too.
    let unknown = log_author("compromise-unknown-reason", 2, 1, &[0, 1, 4]);
    assert_eq!(unknown.status.code(), Some(3), "{}", text(&unknown.stderr));
}

/// A post under the cascaded key is disputed through the binary as through the library.
#[test]
fn r6_57_log_author_disputes_a_post_under_a_cascaded_key() {
    let out = log_author("compromise-post-under-cascaded-key", 3, 2, &[0, 1, 2, 5]);
    assert_eq!(out.status.code(), Some(4), "{}", text(&out.stderr));
}
