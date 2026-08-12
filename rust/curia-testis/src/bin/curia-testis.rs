//! The `curia-testis` CLI.
//!
//! ```text
//! curia-testis verify --envelope <path> --jwks <path>
//! ```
//!
//! Exit codes:
//!   0  verification succeeded; the provenance summary was printed to stdout
//!   1  verification failed; the failing predicate was printed to stderr
//!   2  usage error: bad arguments, or a path that could not be read
//!
//! This split (1 vs. 2) matters for a caller scripting against this CLI: "the
//! signature does not verify" and "you pointed me at a file that does not
//! exist" are different failures with different remedies, and a script that
//! only checks for a nonzero exit code should not have to guess which one it
//! got.
//!
//! Task 1 wires this contract up but cannot yet satisfy it:
//! [`curia_testis::verify_envelope`] is a stub, so `verify` always exits 1
//! naming [`curia_testis::NOT_IMPLEMENTED_PREDICATE`] on stderr. Tasks 2-6
//! make it real; this file's argument surface and both output shapes are
//! what `tests/envelope.rs` (Task 6) is expected to test, so they should not
//! need to change out from under it.

#![forbid(unsafe_code)]

use std::env;
use std::fs;
use std::path::PathBuf;
use std::process::ExitCode;

use curia_testis::NotImplementedError;

const USAGE: &str = "\
curia-testis - offline, independent verifier for signed Curia post envelopes

USAGE:
    curia-testis verify --envelope <path> --jwks <path>

EXIT CODES:
    0  verification succeeded
    1  verification failed (see stderr for the failing predicate)
    2  usage error (bad arguments, or a path that could not be read)
";

enum CliError {
    Usage(String),
    Verification(NotImplementedError),
}

fn main() -> ExitCode {
    let args: Vec<String> = env::args().skip(1).collect();
    match run(&args) {
        Ok(()) => ExitCode::SUCCESS,
        Err(CliError::Usage(message)) => {
            eprintln!("error: {message}");
            eprint!("{USAGE}");
            ExitCode::from(2)
        }
        Err(CliError::Verification(err)) => {
            eprintln!("error: {err}");
            ExitCode::from(1)
        }
    }
}

fn run(args: &[String]) -> Result<(), CliError> {
    match args.first().map(String::as_str) {
        Some("verify") => run_verify(&args[1..]),
        Some(other) => Err(CliError::Usage(format!("unknown subcommand `{other}`"))),
        None => Err(CliError::Usage("missing subcommand `verify`".to_string())),
    }
}

struct VerifyArgs {
    envelope: PathBuf,
    jwks: PathBuf,
}

fn parse_verify_args(args: &[String]) -> Result<VerifyArgs, CliError> {
    let mut envelope: Option<PathBuf> = None;
    let mut jwks: Option<PathBuf> = None;

    let mut i = 0;
    while i < args.len() {
        match args[i].as_str() {
            "--envelope" => {
                let value = args
                    .get(i + 1)
                    .ok_or_else(|| CliError::Usage("--envelope requires a value".to_string()))?;
                envelope = Some(PathBuf::from(value));
                i += 2;
            }
            "--jwks" => {
                let value = args
                    .get(i + 1)
                    .ok_or_else(|| CliError::Usage("--jwks requires a value".to_string()))?;
                jwks = Some(PathBuf::from(value));
                i += 2;
            }
            other => return Err(CliError::Usage(format!("unrecognized argument `{other}`"))),
        }
    }

    let envelope = envelope
        .ok_or_else(|| CliError::Usage("missing required --envelope <path>".to_string()))?;
    let jwks = jwks.ok_or_else(|| CliError::Usage("missing required --jwks <path>".to_string()))?;
    Ok(VerifyArgs { envelope, jwks })
}

fn run_verify(args: &[String]) -> Result<(), CliError> {
    let parsed = parse_verify_args(args)?;

    let submission = fs::read(&parsed.envelope).map_err(|source| {
        CliError::Usage(format!(
            "cannot read --envelope {}: {source}",
            parsed.envelope.display()
        ))
    })?;
    let jwks = fs::read(&parsed.jwks).map_err(|source| {
        CliError::Usage(format!(
            "cannot read --jwks {}: {source}",
            parsed.jwks.display()
        ))
    })?;

    match curia_testis::verify_envelope(&submission, &jwks) {
        Ok(provenance) => {
            println!("author: {}", provenance.author);
            println!("kid: {}", provenance.kid);
            println!("alg: {}", provenance.alg);
            println!("digest: {}", provenance.digest);
            Ok(())
        }
        Err(err) => Err(CliError::Verification(err)),
    }
}
