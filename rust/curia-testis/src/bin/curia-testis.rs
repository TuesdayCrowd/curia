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
//! Task 1 fixed this contract; Task 6 makes it real:
//! [`curia_testis::verify_envelope`] is fully implemented, so `verify` exits
//! 0 on success (printing author/kid/alg/digest to stdout) or 1 on failure,
//! naming the specific failing predicate
//! ([`curia_testis::envelope::VerifyEnvelopeError::predicate`]) on stderr —
//! never merely "verification failed."

#![forbid(unsafe_code)]

use std::env;
use std::fs;
use std::io::Read;
use std::path::{Path, PathBuf};
use std::process::ExitCode;

use curia_testis::envelope::VerifyEnvelopeError;

const USAGE: &str = "\
curia-testis - offline, independent verifier for signed Curia post envelopes

USAGE:
    curia-testis verify --envelope <path> --jwks <path>

EXIT CODES:
    0  verification succeeded
    1  verification failed (see stderr for the failing predicate)
    2  usage error (bad arguments, or a path that could not be read)
";

/// Deliberate, documented bound on `--jwks`, per the Task 6 brief's ruling
/// that the CLI's `fs::read` of that file must not stay unbounded.
///
/// A real JWKS is small — this crate's own fixtures are a few hundred bytes,
/// one key each — but a Forum could legitimately publish one JWKS covering
/// many agents at once, so the bound has to be generous relative to
/// [`curia_testis::json::ADMIT_MAX_SUBMISSION_BYTES`] (the 1 MiB cap ADMIT
/// applies to an *envelope* submission), not equal to it. 8 MiB is chosen
/// deliberately: a JWKS entry (`kty`/`crv`/`kid`/`x`[/`y`], all short
/// base64url strings) costs on the order of 150-300 bytes, so 8 MiB holds
/// tens of thousands of keys — far beyond any realistic single-Forum JWKS —
/// while still bounding the worst case (a corrupted, truncated, or hostile
/// `--jwks` path) to a fixed, small amount of memory rather than reading an
/// arbitrarily large file in full before any check runs.
///
/// This is a CLI-level, operator-facing bound, not a verification predicate:
/// exceeding it is a usage error (exit code 2, "you pointed me at a bad
/// file"), not a claim about the envelope's authorship — unlike ADMIT's
/// submission-size cap, which *is* part of what `verify_envelope` decides
/// and is reported as a `curia/admit/too-large` verification failure (exit
/// code 1). `--envelope` is deliberately *not* given an analogous CLI-level
/// cap for exactly this reason: ADMIT already is the size gate for a
/// submission, and pre-capping the read here would just reclassify that
/// same rejection from "verification failed" to "usage error," which is the
/// wrong exit code for it.
const CLI_MAX_JWKS_BYTES: u64 = 8 * 1024 * 1024;

enum CliError {
    Usage(String),
    Verification(VerifyEnvelopeError),
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

/// Reads `path` in full, refusing to read past `max_bytes + 1` bytes. A
/// file at or under `max_bytes` is returned whole; a file over it is
/// rejected as a usage error naming the cap, without ever holding more than
/// `max_bytes + 1` bytes in memory at once — this does not trust the file's
/// metadata length (which a hostile or unusual filesystem entry could
/// misreport), only what `Read` actually delivers, capped by
/// [`std::io::Read::take`].
fn read_bounded(path: &Path, max_bytes: u64, what: &str) -> Result<Vec<u8>, CliError> {
    let file = fs::File::open(path).map_err(|source| {
        CliError::Usage(format!("cannot read {what} {}: {source}", path.display()))
    })?;
    let mut limited = file.take(max_bytes + 1);
    let mut buf = Vec::new();
    limited.read_to_end(&mut buf).map_err(|source| {
        CliError::Usage(format!("cannot read {what} {}: {source}", path.display()))
    })?;
    if buf.len() as u64 > max_bytes {
        return Err(CliError::Usage(format!(
            "{what} {} exceeds the {max_bytes}-byte cap",
            path.display()
        )));
    }
    Ok(buf)
}

fn run_verify(args: &[String]) -> Result<(), CliError> {
    let parsed = parse_verify_args(args)?;

    // ADMIT applies its own 1 MiB submission-size cap inside
    // `verify_envelope` itself and reports it as a verification failure
    // (curia/admit/too-large, exit code 1) — that is the correct gate for
    // an envelope, so `--envelope` is read here without a CLI-level cap of
    // its own. See `CLI_MAX_JWKS_BYTES`'s doc comment for why `--jwks` is
    // different.
    let submission = fs::read(&parsed.envelope).map_err(|source| {
        CliError::Usage(format!(
            "cannot read --envelope {}: {source}",
            parsed.envelope.display()
        ))
    })?;
    let jwks = read_bounded(&parsed.jwks, CLI_MAX_JWKS_BYTES, "--jwks")?;

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
