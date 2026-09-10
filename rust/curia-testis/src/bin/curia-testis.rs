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
use std::ffi::OsString;
use std::fs;
use std::io::Read;
use std::path::{Path, PathBuf};
use std::process::ExitCode;

use std::collections::HashMap;

use curia_testis::acta::{self, ActaError, VerifiedHead};
use curia_testis::envelope::VerifyEnvelopeError;

const USAGE: &str = "\
curia-testis - offline, independent verifier for signed Curia post envelopes

USAGE:
    curia-testis verify --envelope <path> --jwks <path>
    curia-testis log head        --head <path> --log-jwks <path>
    curia-testis log inclusion   --entry <path> --proof <path> [--head <path> --log-jwks <path>]
    curia-testis log consistency --proof <path> [--from-head <path>] [--to-head <path>] [--log-jwks <path>]

    The log verbs take the JSON bodies of GET /v1/log/head, /v1/log/entries/{i},
    /v1/log/proof/{i}, /v1/log/consistency and /v1/log/jwks, saved to files. The
    leaf is recomputed from the entry; a head, when given, must cover the exact
    size and root the proof verifies against.

EXIT CODES:
    0  verified: every check ran and held
    1  failed: a check ran and did not hold (see stderr for the predicate)
    2  usage error (bad arguments, or a path that could not be read)
    3  could not be checked: the arithmetic held but nothing anchors it to a
       signed head. Pass --head/--log-jwks (inclusion) or --from-head/--to-head
       (consistency) to anchor it. This is not a pass and not a failure.
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
/// file"), not a claim about the envelope's authorship.
const CLI_MAX_JWKS_BYTES: u64 = 8 * 1024 * 1024;

/// **Fix round 1.** Bound on `--envelope`, added because the plain
/// `fs::read` this constant replaces measured at ~212 MB peak RSS for a
/// 200 MB `--envelope` file — fully materialized in memory before ADMIT's
/// own 1 MiB submission cap ([`curia_testis::json::ADMIT_MAX_SUBMISSION_BYTES`])
/// ever got a chance to reject it, asymmetric with `--jwks`'s bounded read
/// (~13 MB peak RSS for an equally large file) right above.
///
/// This is **not** the same concern the original Task 6 doc comment argued
/// for leaving `--envelope` uncapped, and that argument still holds on its
/// own terms: ADMIT already is the authoritative size gate for a
/// *submission*, and it reports an oversized one as a verification failure
/// (`curia/admit/size-exceeded`, exit code 1) — the correct classification,
/// since "this envelope is too large" is a property of the envelope, not of
/// the command line that named it. A CLI-level cap set *at* ADMIT's own 1
/// MiB would silently reclassify that exact rejection into a usage error
/// (exit code 2) instead, which is the wrong exit code and the wrong
/// predicate for a defect ADMIT already has an opinion about.
///
/// The fix for the memory concern without reintroducing that
/// misclassification is to set the CLI-level cap to a **generous multiple**
/// of ADMIT's own cap, not equal to it: any file ADMIT would actually reach
/// a verdict on — including the size-exceeded verdict itself, since a file
/// between 1 and `ENVELOPE_READ_CAP_MULTIPLE` MiB is still read in full and
/// still handed to `verify_envelope`, which still calls ADMIT, which still
/// rejects it with `curia/admit/size-exceeded` — is unaffected by this bound.
/// Only a file *far* larger than anything ADMIT would ever accept (or even
/// bother rejecting for a size-specific reason once its own check fires)
/// hits this CLI-level cap instead, and even then the classification is
/// honest: "this CLI declines to buffer a file this large" is a distinct
/// claim from "this envelope's authorship does not check out," and reported
/// as the distinct exit code (2, not 1) that follows from that.
const ENVELOPE_READ_CAP_MULTIPLE: u64 = 8;

enum CliError {
    Usage(String),
    Verification(VerifyEnvelopeError),
    Acta(ActaError),
    /// R6.52's third outcome: the check did not run, and that is neither a pass
    /// nor a failure.
    ///
    /// Added because `log inclusion` used to exit 0 with `head: not checked`
    /// printed to stdout, so a caller reading only the status -- which is what a
    /// monitor does -- saw "verified" for a proof anchored to nothing. R6.52
    /// requires verified, failed and could-not-be-checked be reported distinctly
    /// and forbids collapsing the third into either of the others; a CLI whose
    /// third outcome exists only in prose has collapsed it into the first.
    NotAnchored(String),
}

fn main() -> ExitCode {
    // Fix round 1: `env::args()` panics outright on any argument that is
    // not valid UTF-8 (confirmed by the reviewer via a direct `execve` with
    // a raw invalid byte in `--envelope`'s value — `unwrap()` inside the
    // stdlib's own convenience wrapper, not code this crate wrote, but
    // still a panic on adversarial/malformed CLI input, which CHARTER's
    // "Result, never panic" and the plan's Definition-of-Done bind
    // unconditionally, not just on the parser/crypto boundaries the
    // existing fuzzing covered — `argv` was the one boundary nothing had
    // exercised. `args_os()` never panics; `to_utf8_args` turns a
    // non-UTF-8 argument into the same usage error (exit 2) any other
    // malformed argument already produces, rather than aborting the
    // process.
    let raw_args: Vec<OsString> = env::args_os().skip(1).collect();
    match to_utf8_args(&raw_args).and_then(|args| run(&args)) {
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
        Err(CliError::Acta(err)) => {
            eprintln!("error: {err}");
            ExitCode::from(1)
        }
        Err(CliError::NotAnchored(message)) => {
            eprintln!("not checked: {message}");
            ExitCode::from(3)
        }
    }
}

/// Converts every OS-native argument to a `String`, failing with a usage
/// error — never a panic — on the first one that is not valid UTF-8.
/// `OsString::into_string` is the fallible counterpart of
/// `env::args()`'s internal `unwrap()`; this function is the only place in
/// the binary that ever performs that conversion, so every downstream
/// function (`run`, `parse_verify_args`, ...) can keep working with plain
/// `String`/`&str` exactly as before, unaware that the boundary above them
/// used to be able to panic.
fn to_utf8_args(raw: &[OsString]) -> Result<Vec<String>, CliError> {
    raw.iter()
        .enumerate()
        .map(|(i, arg)| {
            arg.clone().into_string().map_err(|invalid| {
                CliError::Usage(format!(
                    "argument {} is not valid UTF-8: {}",
                    i + 1,
                    invalid.to_string_lossy()
                ))
            })
        })
        .collect()
}

fn run(args: &[String]) -> Result<(), CliError> {
    match args.first().map(String::as_str) {
        Some("verify") => run_verify(&args[1..]),
        Some("log") => run_log(&args[1..]),
        Some(other) => Err(CliError::Usage(format!("unknown subcommand `{other}`"))),
        None => Err(CliError::Usage(
            "missing subcommand `verify` or `log`".to_string(),
        )),
    }
}

/// `curia-testis log <head|inclusion|consistency> --flag <path> ...`
fn run_log(args: &[String]) -> Result<(), CliError> {
    let verb = args.first().map(String::as_str).ok_or_else(|| {
        CliError::Usage("missing log subcommand: head, inclusion or consistency".to_string())
    })?;
    let flags = parse_path_flags(&args[1..])?;

    match verb {
        "head" => {
            let head = read_flag(&flags, "--head")?;
            let jwks = read_flag(&flags, "--log-jwks")?;
            let verified = acta::verify_head(&head, &jwks).map_err(CliError::Acta)?;
            print_head("head", &verified);
            Ok(())
        }
        "inclusion" => {
            let entry = read_flag(&flags, "--entry")?;
            let proof = read_flag(&flags, "--proof")?;
            let verified = acta::verify_inclusion(&entry, &proof).map_err(CliError::Acta)?;
            println!("log_index: {}", verified.log_index);
            println!("tree_size: {}", verified.tree_size);
            println!("leaf: {}", acta::format_digest(&verified.leaf));
            println!("root: {}", acta::format_digest(&verified.root));
            match optional_head(&flags, "--head")? {
                Some(head) => {
                    acta::head_covers(&head, verified.tree_size, &verified.root)
                        .map_err(CliError::Acta)?;
                    print_head("head", &head);
                }
                None => {
                    println!("head: not checked");
                    return Err(CliError::NotAnchored(
                        "the audit path verifies, and no signed head was given to tie its root to. \
                         Pass --head and --log-jwks. A root nobody signed is a root the Forum can \
                         have invented."
                            .to_string(),
                    ));
                }
            }
            Ok(())
        }
        "consistency" => {
            let proof = read_flag(&flags, "--proof")?;
            let verified = acta::verify_consistency(&proof).map_err(CliError::Acta)?;
            println!("from_size: {}", verified.from_size);
            println!("to_size: {}", verified.to_size);
            println!("from_root: {}", acta::format_digest(&verified.from_root));
            println!("to_root: {}", acta::format_digest(&verified.to_root));
            let mut unanchored: Vec<&str> = Vec::new();

            match optional_head(&flags, "--from-head")? {
                Some(head) => {
                    acta::head_covers(&head, verified.from_size, &verified.from_root)
                        .map_err(CliError::Acta)?;
                    print_head("from_head", &head);
                }
                None => {
                    println!("from_head: not checked");
                    unanchored.push("--from-head");
                }
            }
            match optional_head(&flags, "--to-head")? {
                Some(head) => {
                    acta::head_covers(&head, verified.to_size, &verified.to_root)
                        .map_err(CliError::Acta)?;
                    print_head("to_head", &head);
                }
                None => {
                    println!("to_head: not checked");
                    unanchored.push("--to-head");
                }
            }

            // Either end unanchored leaves the proof about two roots the proof
            // itself supplied. A consistency proof between two trees the Forum
            // invented verifies perfectly and says nothing.
            if !unanchored.is_empty() {
                return Err(CliError::NotAnchored(format!(
                    "the path verifies, and {} was not given, so at least one end is tied to no \
                     signed head. Pass --from-head, --to-head and --log-jwks.",
                    unanchored.join(" and ")
                )));
            }

            Ok(())
        }
        other => Err(CliError::Usage(format!("unknown log subcommand `{other}`"))),
    }
}

const LOG_FLAGS: [&str; 6] = [
    "--head",
    "--log-jwks",
    "--entry",
    "--proof",
    "--from-head",
    "--to-head",
];

fn parse_path_flags(args: &[String]) -> Result<HashMap<String, PathBuf>, CliError> {
    let mut flags = HashMap::new();
    let mut i = 0;
    while i < args.len() {
        let name = args[i].as_str();
        if !LOG_FLAGS.contains(&name) {
            return Err(CliError::Usage(format!("unrecognized argument `{name}`")));
        }
        let value = args
            .get(i + 1)
            .ok_or_else(|| CliError::Usage(format!("{name} requires a value")))?;
        if flags
            .insert(name.to_string(), PathBuf::from(value))
            .is_some()
        {
            return Err(CliError::Usage(format!("{name} given twice")));
        }
        i += 2;
    }
    Ok(flags)
}

fn read_flag(flags: &HashMap<String, PathBuf>, name: &str) -> Result<Vec<u8>, CliError> {
    let path = flags
        .get(name)
        .ok_or_else(|| CliError::Usage(format!("missing required {name} <path>")))?;
    // Every log document is small -- a proof is a few dozen digests, an entry
    // wraps at most one submission -- so the JWKS bound is generous here too.
    read_bounded(path, CLI_MAX_JWKS_BYTES, name)
}

/// A head flag is optional, but when it is given `--log-jwks` must be too:
/// a head nobody's key verified is not a head.
fn optional_head(
    flags: &HashMap<String, PathBuf>,
    name: &str,
) -> Result<Option<VerifiedHead>, CliError> {
    if !flags.contains_key(name) {
        return Ok(None);
    }
    let head = read_flag(flags, name)?;
    let jwks = flags.get("--log-jwks").ok_or_else(|| {
        CliError::Usage(format!("{name} needs --log-jwks <path> to verify against"))
    })?;
    let jwks = read_bounded(jwks, CLI_MAX_JWKS_BYTES, "--log-jwks")?;
    acta::verify_head(&head, &jwks)
        .map(Some)
        .map_err(CliError::Acta)
}

fn print_head(label: &str, head: &VerifiedHead) {
    println!(
        "{label}: tree_size={} root={} kid={} alg={} timestamp={}",
        head.tree_size,
        acta::format_digest(&head.root),
        head.kid,
        head.alg,
        head.timestamp
    );
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
    // `saturating_add`, not `+`: `max_bytes` is always one of this file's
    // own small `const`s today, so overflow can never actually happen, but
    // this function's whole point is to bound a `Read` against adversarial
    // input without relying on an argument staying inside expected range —
    // an unchecked `+ 1` would itself be exactly the kind of "trusted the
    // input was well-behaved" gap this function exists to close elsewhere.
    let mut limited = file.take(max_bytes.saturating_add(1));
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

    // Fix round 1: bounded, at a generous multiple of ADMIT's own
    // submission-size cap — see ENVELOPE_READ_CAP_MULTIPLE's doc comment
    // for why this is not the same cap as ADMIT's, and does not
    // reclassify ADMIT's own size-exceeded verdict.
    let envelope_cap =
        ENVELOPE_READ_CAP_MULTIPLE * curia_testis::json::ADMIT_MAX_SUBMISSION_BYTES as u64;
    let submission = read_bounded(&parsed.envelope, envelope_cap, "--envelope")?;
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
