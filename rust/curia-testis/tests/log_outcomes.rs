//! R6.52's three outcomes, as the published verifier's **exit codes**.
//!
//! The defect this file closes: `log inclusion` used to exit `0` when given no
//! `--head`, printing `head: not checked` to stdout. A caller reading only the
//! status — which is what a monitor does, and what every shell `&&` does — saw
//! "verified" for an audit path anchored to nothing at all. R6.52 requires
//! *verified*, *failed* and *could not be checked* be reported distinctly and
//! forbids collapsing the third into either of the others; a CLI whose third
//! outcome exists only in prose has collapsed it into the first.
//!
//! Recorded in `IMPLEMENTATION_PLAN.md` as observed during the MCP plan's Stage
//! 3 and not acted on, with the reason it mattered: nothing in this repository
//! read the exit code that way, but this binary is the *published independent
//! verifier*, and its exit code is what an outside monitor will read.
//!
//! Every test here spawns the compiled binary, because exit codes live only in
//! the binary — the library function underneath returns a `Result` and has no
//! opinion about status.

use std::path::{Path, PathBuf};
use std::process::{Command, Output};

fn conformance_dir() -> PathBuf {
    if let Ok(dir) = std::env::var("CURIA_CONFORMANCE_DIR") {
        return PathBuf::from(dir);
    }
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../../conformance")
}

fn run_cli(args: &[&str]) -> Output {
    Command::new(env!("CARGO_BIN_EXE_curia-testis"))
        .args(args)
        .output()
        .expect("failed to spawn the curia-testis binary")
}

fn code(output: &Output) -> i32 {
    output
        .status
        .code()
        .expect("the binary must exit with a code")
}

fn stdout_of(output: &Output) -> String {
    String::from_utf8(output.stdout.clone()).expect("stdout must be valid UTF-8")
}

fn stderr_of(output: &Output) -> String {
    String::from_utf8(output.stderr.clone()).expect("stderr must be valid UTF-8")
}

/// Writes the four documents `log inclusion` consumes, from the `acta/` and
/// `merkle/` families, into a scratch directory.
///
/// A one-leaf tree is enough and is the honest minimum: the audit path is empty,
/// so the arithmetic is trivially true and the *only* thing distinguishing a
/// pass from a not-checked is whether a head was supplied — which is exactly the
/// property under test. A larger tree would prove the same thing while making it
/// harder to see that it did.
fn inclusion_fixture(dir: &Path) -> (PathBuf, PathBuf) {
    let entry_input = conformance_dir().join("acta/content-entry/input.json");
    let leaf_hex =
        std::fs::read_to_string(conformance_dir().join("acta/content-entry/expected.leaf"))
            .expect("the acta/content-entry vector must carry an expected.leaf");
    let leaf = leaf_hex.trim();

    let entry_body = std::fs::read_to_string(&entry_input).expect("the vector must be readable");

    let entry_path = dir.join("entry.json");
    std::fs::write(
        &entry_path,
        format!(r#"{{"log_index":0,"leaf_hash":"sha256:{leaf}","entry":{entry_body}}}"#),
    )
    .expect("the entry document must be writable");

    // A single-leaf tree: the root IS the leaf, and the audit path is empty.
    let proof_path = dir.join("proof.json");
    std::fs::write(
        &proof_path,
        format!(
            r#"{{"log_index":0,"tree_size":1,"leaf_hash":"sha256:{leaf}","audit_path":[],"root_hash":"sha256:{leaf}","head_signed":false}}"#
        ),
    )
    .expect("the proof document must be writable");

    (entry_path, proof_path)
}

fn scratch(name: &str) -> PathBuf {
    let dir = std::env::temp_dir().join(format!("curia-log-outcomes-{name}"));
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).expect("the scratch directory must be creatable");
    dir
}

/// **Could not be checked (3).** The path verifies and nothing anchors its root.
///
/// This is the case that used to exit `0`. The assertion is on the code, not on
/// the prose, because the prose was already correct — it said `head: not
/// checked` — and the status contradicted it.
#[test]
fn r6_52_an_unanchored_inclusion_proof_is_not_reported_as_verified() {
    let dir = scratch("unanchored");
    let (entry, proof) = inclusion_fixture(&dir);

    let output = run_cli(&[
        "log",
        "inclusion",
        "--entry",
        entry.to_str().unwrap(),
        "--proof",
        proof.to_str().unwrap(),
    ]);

    assert_eq!(
        code(&output),
        3,
        "an audit path tied to no signed head must not exit 0 (R6.52's third outcome).\nstdout:\n{}\nstderr:\n{}",
        stdout_of(&output),
        stderr_of(&output)
    );

    // Non-vacuity: the arithmetic really did verify, so exit 3 is about the
    // missing anchor rather than about a proof that failed.
    let stdout = stdout_of(&output);
    assert!(
        stdout.contains("log_index: 0") && stdout.contains("root: sha256:"),
        "the proof should have verified before the anchor was found missing:\n{stdout}"
    );

    // And the reason reaches stderr, where a caller looks after a non-zero exit.
    assert!(
        stderr_of(&output).contains("--head"),
        "the message must name what would anchor it:\n{}",
        stderr_of(&output)
    );
}

/// **Failed (1).** A tampered entry hashes to a different leaf, and that is a
/// statement about the material rather than about what was supplied.
///
/// Kept beside the case above because the two must not collapse: before this
/// change both an unanchored proof and a sound one exited `0`, and only a
/// tampered one exited `1`, so the CLI had two outcomes where R6.52 names three.
#[test]
fn r6_52_a_tampered_entry_still_fails_rather_than_reporting_not_checked() {
    let dir = scratch("tampered");
    let (entry, proof) = inclusion_fixture(&dir);

    let body = std::fs::read_to_string(&entry).unwrap();
    let tampered = dir.join("entry-tampered.json");
    std::fs::write(
        &tampered,
        body.replace(
            "\"board\":\"distributed-systems\"",
            "\"board\":\"somewhere-else\"",
        ),
    )
    .unwrap();

    assert_ne!(
        std::fs::read_to_string(&entry).unwrap(),
        std::fs::read_to_string(&tampered).unwrap(),
        "the fixture must actually differ, or this test asserts nothing"
    );

    let output = run_cli(&[
        "log",
        "inclusion",
        "--entry",
        tampered.to_str().unwrap(),
        "--proof",
        proof.to_str().unwrap(),
    ]);

    assert_eq!(
        code(&output),
        1,
        "an altered entry is a failure, not an absence:\n{}",
        stderr_of(&output)
    );
    assert!(
        stderr_of(&output).contains("leaf-mismatch"),
        "{}",
        stderr_of(&output)
    );
}

/// **Usage (2).** Still distinct from all three verification outcomes: a path
/// that cannot be read is not a verdict about a log.
#[test]
fn r6_52_a_missing_file_is_a_usage_error_and_not_a_verification_outcome() {
    let dir = scratch("usage");
    let (_, proof) = inclusion_fixture(&dir);

    let output = run_cli(&[
        "log",
        "inclusion",
        "--entry",
        dir.join("does-not-exist.json").to_str().unwrap(),
        "--proof",
        proof.to_str().unwrap(),
    ]);

    assert_eq!(code(&output), 2, "{}", stderr_of(&output));
}

/// A consistency proof with neither head is unanchored at both ends, and says
/// which. Either end unanchored leaves the proof about roots the proof itself
/// supplied — two trees a Forum invented verify against each other perfectly.
#[test]
fn r6_52_an_unanchored_consistency_proof_is_not_reported_as_verified() {
    let dir = scratch("consistency");

    // size-2 -> the 1-leaf prefix, from the published merkle vector.
    let expected: serde_json::Value = serde_json::from_str(
        &std::fs::read_to_string(conformance_dir().join("merkle/size-2/expected.json"))
            .expect("the merkle/size-2 vector must be readable"),
    )
    .expect("expected.json must parse");

    let root = expected["root"].as_str().unwrap();
    let first_leaf = expected["leaf_hashes"][0].as_str().unwrap();
    let path = &expected["consistency"][0]["path"];

    let proof = dir.join("consistency.json");
    std::fs::write(
        &proof,
        format!(
            r#"{{"from_size":1,"to_size":2,"from_root":"sha256:{first_leaf}","to_root":"sha256:{root}","path":[{}]}}"#,
            path.as_array()
                .unwrap()
                .iter()
                .map(|n| format!(r#""sha256:{}""#, n.as_str().unwrap()))
                .collect::<Vec<_>>()
                .join(",")
        ),
    )
    .unwrap();

    let output = run_cli(&["log", "consistency", "--proof", proof.to_str().unwrap()]);

    assert_eq!(
        code(&output),
        3,
        "a consistency proof with neither end anchored must not exit 0.\nstdout:\n{}\nstderr:\n{}",
        stdout_of(&output),
        stderr_of(&output)
    );

    // Non-vacuity: the path itself verified first.
    assert!(
        stdout_of(&output).contains("from_size: 1"),
        "the proof should have verified before the anchors were found missing:\n{}",
        stdout_of(&output)
    );

    assert!(
        stderr_of(&output).contains("--from-head") && stderr_of(&output).contains("--to-head"),
        "the message must name both missing anchors:\n{}",
        stderr_of(&output)
    );
}

/// The published exit codes are the ones the binary uses. Read from `--help`'s
/// own text so a renumbering moves the contract and this assertion together
/// rather than leaving one behind — the shape `PublishedAdmitLimits` already
/// uses against the white paper.
#[test]
fn the_published_exit_codes_name_all_four_outcomes() {
    let usage = stderr_of(&run_cli(&["--help"]));

    for expected in [
        "0  verified",
        "1  failed",
        "2  usage error",
        "3  could not be checked",
    ] {
        assert!(
            usage.contains(expected),
            "the usage text must publish {expected:?}, or a caller cannot know what to branch on:\n{usage}"
        );
    }
}
