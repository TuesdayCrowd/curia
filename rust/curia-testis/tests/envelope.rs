//! Task 6, Step 2: the CLI contract, tested against the **compiled
//! binary**, not the library function underneath it.
//!
//! `curia_testis::envelope::verify_envelope` (the library function) already
//! has its own thorough unit-test suite in `src/envelope.rs`, and
//! `tests/vectors.rs`'s `envelope` test drives the whole conformance corpus
//! through it. Neither of those exercises `src/bin/curia-testis.rs` at
//! all — argument parsing, exit codes, and which output stream a message
//! lands on are all behavior that lives only in the binary, and the Task 6
//! brief is explicit that Step 2's contract is about the executable. Every
//! test in this file spawns the actual compiled `curia-testis` binary as a
//! subprocess (via `CARGO_BIN_EXE_curia-testis`, the standard Cargo
//! mechanism for integration tests to find a sibling binary target) and
//! asserts on its exit code and both output streams.

use std::ffi::{OsStr, OsString};
use std::path::{Path, PathBuf};
use std::process::{Command, Output};

fn conformance_dir() -> PathBuf {
    if let Ok(dir) = std::env::var("CURIA_CONFORMANCE_DIR") {
        return PathBuf::from(dir);
    }
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../../conformance")
}

fn envelope_fixture(case: &str, file: &str) -> PathBuf {
    conformance_dir().join("envelope").join(case).join(file)
}

fn run_cli(args: &[&str]) -> Output {
    Command::new(env!("CARGO_BIN_EXE_curia-testis"))
        .args(args)
        .output()
        .expect("failed to spawn the curia-testis binary")
}

/// Like [`run_cli`], but accepts raw [`OsStr`] arguments rather than `&str`
/// — the only way to hand the binary an argument that is not valid UTF-8 at
/// all, which `Command::args(&[&str])` cannot express (a `&str` is already
/// guaranteed valid UTF-8 before this function ever sees it). `Command`
/// itself accepts `impl AsRef<OsStr>` arguments and passes them to
/// `execve`-family calls unmodified — no UTF-8 conversion happens between
/// this process and the child.
fn run_cli_os(args: &[&OsStr]) -> Output {
    Command::new(env!("CARGO_BIN_EXE_curia-testis"))
        .args(args)
        .output()
        .expect("failed to spawn the curia-testis binary")
}

fn stdout_of(output: &Output) -> String {
    String::from_utf8(output.stdout.clone()).expect("stdout must be valid UTF-8")
}

fn stderr_of(output: &Output) -> String {
    String::from_utf8(output.stderr.clone()).expect("stderr must be valid UTF-8")
}

// ---------------------------------------------------------------------
// Step 1 / Step 2: a real, positive fixture verifies through the binary,
// exit 0, provenance summary on stdout, nothing on stderr.
// ---------------------------------------------------------------------

#[test]
fn verify_succeeds_on_a_good_fixture_exit_0_stdout_summary() {
    let envelope = envelope_fixture("ed25519-minimal", "submission.json");
    let jwks = envelope_fixture("ed25519-minimal", "jwks.json");
    let expected_digest =
        std::fs::read_to_string(envelope_fixture("ed25519-minimal", "expected.digest"))
            .expect("expected.digest must exist");
    let expected_digest = expected_digest.trim();

    let output = run_cli(&[
        "verify",
        "--envelope",
        envelope.to_str().unwrap(),
        "--jwks",
        jwks.to_str().unwrap(),
    ]);

    assert!(
        output.status.success(),
        "expected exit 0, got {:?}; stderr: {}",
        output.status.code(),
        stderr_of(&output)
    );
    assert_eq!(output.status.code(), Some(0));

    let stdout = stdout_of(&output);
    assert!(
        stdout.contains("author: agent://curia.example/tuesdaycrowd/scriptor"),
        "stdout must name the author; got: {stdout:?}"
    );
    assert!(
        stdout.contains("kid: conformance-ed25519-minimal"),
        "stdout must name the kid; got: {stdout:?}"
    );
    assert!(
        stdout.contains("alg: EdDSA"),
        "stdout must name the algorithm; got: {stdout:?}"
    );
    assert!(
        stdout.contains(&format!("digest: sha256:{expected_digest}")),
        "stdout must print the digest in sha256:<hex> form matching expected.digest; \
         got: {stdout:?}"
    );

    let stderr = stderr_of(&output);
    assert!(
        stderr.is_empty(),
        "a successful run must print nothing to stderr; got: {stderr:?}"
    );
}

/// Every positive fixture, not just one — end-to-end through the binary.
#[test]
fn verify_succeeds_on_every_positive_fixture() {
    for (case, expected_kid, expected_alg) in [
        ("ed25519-minimal", "conformance-ed25519-minimal", "EdDSA"),
        ("ed25519-full", "conformance-ed25519-full", "EdDSA"),
        ("ed25519-unicode", "conformance-ed25519-unicode", "EdDSA"),
        ("es256-minimal", "conformance-es256-minimal", "ES256"),
    ] {
        let envelope = envelope_fixture(case, "submission.json");
        let jwks = envelope_fixture(case, "jwks.json");
        let output = run_cli(&[
            "verify",
            "--envelope",
            envelope.to_str().unwrap(),
            "--jwks",
            jwks.to_str().unwrap(),
        ]);
        assert!(
            output.status.success(),
            "{case}: expected exit 0, got {:?}; stderr: {}",
            output.status.code(),
            stderr_of(&output)
        );
        let stdout = stdout_of(&output);
        assert!(
            stdout.contains(&format!("kid: {expected_kid}")),
            "{case}: stdout missing expected kid; got: {stdout:?}"
        );
        assert!(
            stdout.contains(&format!("alg: {expected_alg}")),
            "{case}: stdout missing expected alg; got: {stdout:?}"
        );
    }
}

// ---------------------------------------------------------------------
// Step 1 / Step 2: the two negative fixtures fail, exit 1, the *specific*
// declared predicate on stderr, nothing on stdout. `tampered-body` and
// `wrong-key` share one predicate by design — see conformance/README.md
// and src/jws.rs's module doc comment on why this crate does not try to
// distinguish "wrong key" from "tampered body."
// ---------------------------------------------------------------------

#[test]
fn verify_fails_on_tampered_body_exit_1_names_signature_invalid() {
    let envelope = envelope_fixture("tampered-body", "submission.json");
    let jwks = envelope_fixture("tampered-body", "jwks.json");

    let output = run_cli(&[
        "verify",
        "--envelope",
        envelope.to_str().unwrap(),
        "--jwks",
        jwks.to_str().unwrap(),
    ]);

    assert_eq!(output.status.code(), Some(1));
    let stderr = stderr_of(&output);
    assert!(
        stderr.contains("curia/jws/signature-invalid"),
        "stderr must name the specific failing predicate, not just \"verification failed\"; \
         got: {stderr:?}"
    );
    let stdout = stdout_of(&output);
    assert!(
        stdout.is_empty(),
        "a failed run must print nothing to stdout; got: {stdout:?}"
    );
}

#[test]
fn verify_fails_on_wrong_key_exit_1_names_signature_invalid() {
    let envelope = envelope_fixture("wrong-key", "submission.json");
    let jwks = envelope_fixture("wrong-key", "jwks.json");

    let output = run_cli(&[
        "verify",
        "--envelope",
        envelope.to_str().unwrap(),
        "--jwks",
        jwks.to_str().unwrap(),
    ]);

    assert_eq!(output.status.code(), Some(1));
    let stderr = stderr_of(&output);
    assert!(
        stderr.contains("curia/jws/signature-invalid"),
        "stderr must name the specific failing predicate; got: {stderr:?}"
    );
    let stdout = stdout_of(&output);
    assert!(stdout.is_empty());
}

// ---------------------------------------------------------------------
// Usage errors: exit 2, distinct from a verification failure (exit 1).
// ---------------------------------------------------------------------

#[test]
fn missing_subcommand_is_a_usage_error() {
    let output = run_cli(&[]);
    assert_eq!(output.status.code(), Some(2));
    assert!(stderr_of(&output).contains("missing subcommand"));
}

#[test]
fn unknown_subcommand_is_a_usage_error() {
    let output = run_cli(&["frobnicate"]);
    assert_eq!(output.status.code(), Some(2));
    assert!(stderr_of(&output).contains("unknown subcommand"));
}

#[test]
fn missing_envelope_flag_is_a_usage_error() {
    let jwks = envelope_fixture("ed25519-minimal", "jwks.json");
    let output = run_cli(&["verify", "--jwks", jwks.to_str().unwrap()]);
    assert_eq!(output.status.code(), Some(2));
    assert!(stderr_of(&output).contains("--envelope"));
}

#[test]
fn missing_jwks_flag_is_a_usage_error() {
    let envelope = envelope_fixture("ed25519-minimal", "submission.json");
    let output = run_cli(&["verify", "--envelope", envelope.to_str().unwrap()]);
    assert_eq!(output.status.code(), Some(2));
    assert!(stderr_of(&output).contains("--jwks"));
}

#[test]
fn nonexistent_envelope_path_is_a_usage_error_not_a_verification_failure() {
    let jwks = envelope_fixture("ed25519-minimal", "jwks.json");
    let output = run_cli(&[
        "verify",
        "--envelope",
        "/nonexistent/path/does-not-exist.json",
        "--jwks",
        jwks.to_str().unwrap(),
    ]);
    assert_eq!(
        output.status.code(),
        Some(2),
        "a missing --envelope file is a usage error (exit 2), not a verification failure \
         (exit 1) — the caller pointed this CLI at a bad path, which is a different remedy \
         than \"the signature does not verify\""
    );
    assert!(stderr_of(&output).contains("--envelope"));
}

#[test]
fn nonexistent_jwks_path_is_a_usage_error() {
    let envelope = envelope_fixture("ed25519-minimal", "submission.json");
    let output = run_cli(&[
        "verify",
        "--envelope",
        envelope.to_str().unwrap(),
        "--jwks",
        "/nonexistent/path/does-not-exist.json",
    ]);
    assert_eq!(output.status.code(), Some(2));
    assert!(stderr_of(&output).contains("--jwks"));
}

// ---------------------------------------------------------------------
// Fix round 1 — Critical: a non-UTF-8 argument must not panic.
//
// `std::env::args()` panics outright on any argument that is not valid
// UTF-8 (the reviewer reproduced this directly against the pre-fix binary
// via `execve` with a raw invalid byte: exit 101, an `unwrap()` inside the
// stdlib's own convenience wrapper). This was the one boundary none of the
// existing fuzzing (1,513,177 ADMIT cases, 2,000,000+ canonicalizer cases,
// 65 JWS/JWK adversarial probes) ever touched, because none of it drove
// `argv` — every prior fuzz target was a function called directly, not a
// process spawned with attacker-controlled command-line bytes.
// ---------------------------------------------------------------------

/// Builds an `OsStr` containing a byte sequence that is **not** valid
/// UTF-8 at all — not merely non-ASCII, but structurally invalid (a lone
/// continuation byte with no valid lead byte can never appear in
/// well-formed UTF-8). `std::os::unix::ffi::OsStrExt::from_bytes` is exact:
/// unlike `String::from_utf8_lossy` (which would silently replace the bad
/// bytes with `U+FFFD` and defeat the whole point of this test), it hands
/// the raw bytes through to the OS unchanged.
#[cfg(unix)]
fn invalid_utf8_os_string() -> OsString {
    use std::os::unix::ffi::OsStrExt;
    // 0xFF and 0xFE are not valid UTF-8 lead or continuation bytes under
    // any circumstance (RFC 3629 never assigns them a role); this is
    // deliberately not an "edge case" like an overlong encoding or a
    // truncated multi-byte sequence — it is unconditionally invalid.
    OsStr::from_bytes(b"\xFF\xFE\xFFbadpath").to_os_string()
}

#[cfg(unix)]
#[test]
fn a_non_utf8_argument_is_a_usage_error_not_a_panic() {
    let jwks = envelope_fixture("ed25519-minimal", "jwks.json");
    let bad_value = invalid_utf8_os_string();
    let output = run_cli_os(&[
        OsStr::new("verify"),
        OsStr::new("--envelope"),
        &bad_value,
        OsStr::new("--jwks"),
        jwks.as_os_str(),
    ]);

    // The pre-fix binary aborted with SIGABRT/exit 101 (a Rust panic) here.
    // A process killed by a signal has no exit code at all under
    // `std::process::ExitStatus` on Unix — `.code()` returns `None` — so
    // asserting a *specific* exit code (2) is already a stronger check
    // than merely "did not panic": it fails both on the old panic and on
    // any other non-graceful termination.
    assert_eq!(
        output.status.code(),
        Some(2),
        "a non-UTF-8 argument must be reported as a usage error (exit 2), not panic; \
         got status {:?}, stderr: {}",
        output.status,
        stderr_of(&output)
    );
    let stderr = stderr_of(&output);
    assert!(
        stderr.contains("not valid UTF-8"),
        "stderr should explain the argument was not valid UTF-8; got: {stderr:?}"
    );
}

#[cfg(unix)]
#[test]
fn a_non_utf8_subcommand_is_also_a_usage_error_not_a_panic() {
    // Not just the flag *value* — the subcommand token itself, and (by the
    // same code path) a flag *name*, must be equally panic-free, since
    // `to_utf8_args` converts every argument up front rather than only the
    // ones a later branch happens to inspect.
    let bad = invalid_utf8_os_string();
    let output = run_cli_os(&[&bad]);
    assert_eq!(output.status.code(), Some(2));
    assert!(stderr_of(&output).contains("not valid UTF-8"));
}

// ---------------------------------------------------------------------
// Malformed content, read successfully but rejected — exit 1, exit 2, or
// (for a submission ADMIT itself rejects) exit 1 naming the ADMIT slug,
// exercised through the real binary rather than only the library.
// ---------------------------------------------------------------------

#[test]
fn a_submission_with_invalid_json_is_a_verification_failure_not_a_usage_error() {
    let dir = scratch_dir("invalid-json-envelope");
    let envelope_path = dir.join("submission.json");
    std::fs::write(&envelope_path, b"{not valid json").unwrap();
    let jwks = envelope_fixture("ed25519-minimal", "jwks.json");

    let output = run_cli(&[
        "verify",
        "--envelope",
        envelope_path.to_str().unwrap(),
        "--jwks",
        jwks.to_str().unwrap(),
    ]);

    // The file was read successfully; ADMIT is what rejects its content.
    // That is a verification failure (exit 1), not a usage error.
    assert_eq!(output.status.code(), Some(1));
    let stderr = stderr_of(&output);
    assert!(
        stderr.contains("curia/admit/"),
        "malformed JSON content must be rejected by ADMIT with a curia/admit/... predicate, \
         named on stderr; got: {stderr:?}"
    );
}

#[test]
fn a_jwks_file_over_the_cli_cap_is_a_usage_error() {
    // CLI_MAX_JWKS_BYTES in src/bin/curia-testis.rs is 8 MiB; this writes
    // one byte past it. Not a duplicate of the constant's own justification
    // (see that doc comment) — this test exists to prove the CLI actually
    // enforces some bound, not merely that a bound is documented.
    let dir = scratch_dir("oversized-jwks");
    let jwks_path = dir.join("jwks.json");
    let cap: usize = 8 * 1024 * 1024;
    let padding = "x".repeat(cap);
    let contents = format!(r#"{{"keys":[],"padding":"{padding}"}}"#);
    assert!(
        contents.len() as u64 > cap as u64,
        "test fixture must actually exceed the cap"
    );
    std::fs::write(&jwks_path, contents.as_bytes()).unwrap();

    let envelope = envelope_fixture("ed25519-minimal", "submission.json");
    let output = run_cli(&[
        "verify",
        "--envelope",
        envelope.to_str().unwrap(),
        "--jwks",
        jwks_path.to_str().unwrap(),
    ]);

    assert_eq!(
        output.status.code(),
        Some(2),
        "an oversized --jwks file must be rejected as a usage error before any verification \
         logic runs; stderr: {}",
        stderr_of(&output)
    );
    let stderr = stderr_of(&output);
    assert!(
        stderr.contains("--jwks") && stderr.contains("cap"),
        "stderr should explain the file exceeded the --jwks cap; got: {stderr:?}"
    );
}

#[test]
fn a_jwks_file_at_exactly_the_real_fixture_size_still_works() {
    // Regression guard: the bounded reader must not off-by-one reject a
    // real, well-under-the-cap JWKS file.
    let envelope = envelope_fixture("ed25519-minimal", "submission.json");
    let jwks = envelope_fixture("ed25519-minimal", "jwks.json");
    let output = run_cli(&[
        "verify",
        "--envelope",
        envelope.to_str().unwrap(),
        "--jwks",
        jwks.to_str().unwrap(),
    ]);
    assert!(output.status.success());
}

// ---------------------------------------------------------------------
// Fix round 1 — Important: `--envelope` is now bounded too (a generous
// multiple of ADMIT's own submission cap, not equal to it — see
// ENVELOPE_READ_CAP_MULTIPLE's doc comment in src/bin/curia-testis.rs for
// why those are different concerns that both needed addressing).
// ---------------------------------------------------------------------

#[test]
fn an_envelope_between_admit_cap_and_cli_cap_still_gets_admits_own_verdict() {
    // 1.5 MiB: over ADMIT_MAX_SUBMISSION_BYTES (1 MiB), comfortably under
    // the CLI's own read cap (8 MiB, ENVELOPE_READ_CAP_MULTIPLE = 8). This
    // file is read in full by the CLI and handed to verify_envelope, which
    // still runs ADMIT, which still rejects it — so this must surface as a
    // *verification failure* (exit 1, curia/admit/size-exceeded), never a
    // usage error. This is the exact reclassification the original Task 6
    // report argued against, now re-confirmed as still true after adding
    // the CLI-level cap for the memory concern.
    let dir = scratch_dir("envelope-between-caps");
    let envelope_path = dir.join("submission.json");
    let admit_cap: usize = 1024 * 1024;
    let size = admit_cap + admit_cap / 2;
    std::fs::write(&envelope_path, vec![b'x'; size]).unwrap();
    let jwks = envelope_fixture("ed25519-minimal", "jwks.json");

    let output = run_cli(&[
        "verify",
        "--envelope",
        envelope_path.to_str().unwrap(),
        "--jwks",
        jwks.to_str().unwrap(),
    ]);

    assert_eq!(
        output.status.code(),
        Some(1),
        "a file between ADMIT's cap and the CLI's own cap must still reach ADMIT and be \
         rejected as a verification failure, not a usage error; stderr: {}",
        stderr_of(&output)
    );
    let stderr = stderr_of(&output);
    assert!(
        stderr.contains("curia/admit/size-exceeded"),
        "must be ADMIT's own size-exceeded predicate, not a CLI-level cap message; got: {stderr:?}"
    );
}

#[test]
fn an_envelope_file_over_the_cli_cap_is_a_usage_error() {
    // Past the CLI's own read cap: the read is truncated and rejected
    // before verify_envelope (and therefore ADMIT) ever runs — the case
    // the memory-blowup finding was about (a 200 MB --envelope file
    // previously peaked at ~212 MB RSS; this must never fully materialize
    // a file this large in memory).
    let dir = scratch_dir("envelope-over-cli-cap");
    let envelope_path = dir.join("submission.json");
    let cli_cap: usize = 8 * 1024 * 1024;
    std::fs::write(&envelope_path, vec![b'x'; cli_cap + 1]).unwrap();
    let jwks = envelope_fixture("ed25519-minimal", "jwks.json");

    let output = run_cli(&[
        "verify",
        "--envelope",
        envelope_path.to_str().unwrap(),
        "--jwks",
        jwks.to_str().unwrap(),
    ]);

    assert_eq!(
        output.status.code(),
        Some(2),
        "a file over the CLI's --envelope read cap must be a usage error, not a \
         verification failure; stderr: {}",
        stderr_of(&output)
    );
    let stderr = stderr_of(&output);
    assert!(
        stderr.contains("--envelope") && stderr.contains("cap"),
        "stderr should explain the file exceeded the --envelope cap; got: {stderr:?}"
    );
}

/// A fresh scratch directory under the OS temp dir, unique to this test
/// process and call site — mirrors `tests/loader_errors.rs`'s own
/// `scratch_dir` helper (never under `conformance/` or the cleanroom).
fn scratch_dir(label: &str) -> PathBuf {
    let dir = std::env::temp_dir().join(format!(
        "curia-testis-cli-test-{}-{}-{}",
        std::process::id(),
        label,
        fastrand_ish()
    ));
    std::fs::create_dir_all(&dir).expect("can create a scratch dir under the OS temp dir");
    dir
}

fn fastrand_ish() -> usize {
    let b = Box::new(0u8);
    Box::into_raw(b) as usize
}
