//! `curia-differential` — the Rust endpoint of the Cūria canonicalizer's
//! differential harness (design spec §9 / Task 7).
//!
//! ## The wire protocol
//!
//! Reads NDJSON on stdin, writes NDJSON on stdout: exactly one output line
//! per input line, in input order, and nothing else is ever written to
//! stdout. Diagnostics go to stderr.
//!
//! Input line:
//! ```text
//! {"id":"<string>","op":"<op>","input_b64":"<standard base64 of the RAW INPUT BYTES>"}
//! ```
//! `op` is `"admit"`, `"canonicalize"`, or `"canonicalize_nfc"`. The payload
//! is base64 because the corpus this harness drives contains invalid UTF-8,
//! raw NUL bytes, and lone surrogates that cannot survive a text field, a
//! shell argument, or argv unscathed.
//!
//! Output line, success:
//! ```text
//! {"id":"<same id>","ok":true,"out_b64":"<standard base64 of the RAW OUTPUT BYTES>"}
//! ```
//! For `"admit"`, success carries no output bytes — `out_b64` is always the
//! empty string.
//!
//! Output line, failure:
//! ```text
//! {"id":"<same id>","ok":false,"slug":"<error slug>"}
//! ```
//!
//! ## Why a new binary target here, not a new crate
//!
//! This lives at `src/bin/curia-differential.rs`, inside the existing
//! `curia-testis` crate, as a second `[[bin]]` target — rather than as a
//! separate crate depending on `curia-testis` by path. `[[bin]]` targets are
//! additive and independent in Cargo: adding this one does not touch the
//! `curia-testis` binary, the `[lib]` target, or any test target, and this
//! file contains no `pub` item and does not change what the library exports
//! (`lib.rs` is untouched). A separate crate would need its own
//! `Cargo.toml`, and either its own `Cargo.lock` or workspace plumbing to
//! share one, plus its own copy of `deny-network.sb` and CI wiring — all to
//! reach the exact same three functions
//! (`curia_testis::json::admit`, `curia_testis::canonicalize`,
//! `curia_testis::canonicalize_with_nfc`) this binary already has direct,
//! in-tree access to as a sibling of `src/bin/curia-testis.rs`. Given the
//! brief's constraint that the library's public API must not change, the
//! lighter-weight option that does not add a second crate to reason about
//! wins.
//!
//! ## Never crash
//!
//! Three independent defenses:
//!
//! - Malformed JSON, a missing/mistyped field, or invalid base64 in a
//!   request line is reported as an ordinary `ok:false` line, not a panic
//!   — see [`parse_request`].
//! - The actual dispatch into `curia_testis` runs inside
//!   [`std::panic::catch_unwind`] (see [`handle_line`]). `curia-testis` is
//!   contractually panic-free (`#![forbid(unsafe_code)]`, every public
//!   fallible function returns a typed `Result`), but this endpoint does
//!   not take that on faith: a panic there is reported as `ok:false` with
//!   a `curia-differential/CRASH: <message>` slug rather than taking the
//!   whole endpoint down, exactly as the wire protocol specifies.
//! - A genuine stdin/stdout I/O error is the one condition this binary
//!   cannot paper over with a protocol-shaped response — there is no
//!   request line to answer — so it is the only path that exits non-zero.

#![forbid(unsafe_code)]

use std::any::Any;
use std::io::{self, BufRead, Write};
use std::panic::{self, AssertUnwindSafe};
use std::process::ExitCode;

use base64::engine::general_purpose::STANDARD;
use base64::Engine;
use serde_json::{json, Value};

/// Reused rather than invented: [`curia_testis::nfc::NfcError::Parse`]
/// already names this exact condition — the input did not parse as JSON at
/// all — `curia/canon/parse-error`, for [`curia_testis::canonicalize_with_nfc`].
/// [`curia_testis::canonicalize`] (pure RFC 8785, no NFC) can fail the same
/// way, through a bare [`curia_testis::json::ParseError`] that has no
/// `predicate()` of its own; giving it this same slug means the comparison
/// side of the harness sees one predicate for "not JSON at all" from either
/// canonicalization function, not two spellings of the same fact.
const CANON_PARSE_ERROR_SLUG: &str = "curia/canon/parse-error";

fn main() -> ExitCode {
    let stdin = io::stdin();
    let reader = io::BufReader::new(stdin.lock());
    let stdout = io::stdout();
    let mut writer = stdout.lock();

    // `BufRead::split` yields raw byte chunks delimited by `\n`, with no
    // UTF-8 requirement of its own — unlike `BufRead::lines`, which would
    // itself error (or, on some standard-library versions, has historically
    // been a source of panics in adjacent code) on a request line that is
    // not valid UTF-8. `serde_json::from_slice` below is what actually
    // decides whether a line is well-formed; this loop never assumes text
    // before that point.
    for chunk in reader.split(b'\n') {
        let mut raw = match chunk {
            Ok(raw) => raw,
            Err(err) => {
                eprintln!("curia-differential: fatal stdin read error: {err}");
                return ExitCode::from(1);
            }
        };
        // Tolerate CRLF-terminated input without treating the trailing
        // `\r` as part of the JSON payload.
        if raw.last() == Some(&b'\r') {
            raw.pop();
        }

        let response = handle_line(&raw);
        if let Err(err) = writeln!(writer, "{response}") {
            eprintln!("curia-differential: fatal stdout write error: {err}");
            return ExitCode::from(1);
        }
        if let Err(err) = writer.flush() {
            eprintln!("curia-differential: fatal stdout flush error: {err}");
            return ExitCode::from(1);
        }
    }

    ExitCode::SUCCESS
}

/// Handles exactly one request line, producing exactly one response line
/// (no trailing newline — the caller adds it). Never panics: a panic
/// anywhere in [`dispatch`] is caught and turned into a `CRASH` response
/// line instead of propagating.
fn handle_line(raw: &[u8]) -> String {
    let (id, op, input) = match parse_request(raw) {
        Ok(parsed) => parsed,
        Err((id, slug)) => return failure_line(&id, &slug),
    };

    match panic::catch_unwind(AssertUnwindSafe(|| dispatch(&op, &input))) {
        Ok(Ok(out_bytes)) => success_line(&id, &out_bytes),
        Ok(Err(slug)) => failure_line(&id, &slug),
        Err(payload) => failure_line(
            &id,
            &format!(
                "curia-differential/CRASH: {}",
                panic_message(payload.as_ref())
            ),
        ),
    }
}

/// Parses one request line into `(id, op, decoded input bytes)`.
///
/// On any malformation, returns `Err((id, slug))` — `id` is `""` when it
/// could not be recovered (the line was not JSON at all, or `"id"` was
/// missing or not a string), otherwise the id the request line actually
/// named, so a response can still be correlated with its request wherever
/// that much of the line parsed.
fn parse_request(raw: &[u8]) -> Result<(String, String, Vec<u8>), (String, String)> {
    let request: Value = serde_json::from_slice(raw).map_err(|err| {
        eprintln!("curia-differential: request line is not valid JSON: {err}");
        (
            String::new(),
            "curia-differential/malformed-request-json".to_owned(),
        )
    })?;

    let id = request
        .get("id")
        .and_then(Value::as_str)
        .map(str::to_owned)
        .unwrap_or_default();

    let op = match request.get("op").and_then(Value::as_str) {
        Some(op) => op.to_owned(),
        None => return Err((id, "curia-differential/missing-op".to_owned())),
    };

    let input_b64 = match request.get("input_b64").and_then(Value::as_str) {
        Some(s) => s,
        None => return Err((id, "curia-differential/missing-input-b64".to_owned())),
    };

    let input = match STANDARD.decode(input_b64) {
        Ok(bytes) => bytes,
        Err(err) => {
            eprintln!("curia-differential: id {id:?}: input_b64 is not valid base64: {err}");
            return Err((id, "curia-differential/invalid-base64".to_owned()));
        }
    };

    Ok((id, op, input))
}

/// Runs the requested operation against the real crate. `Ok` carries the
/// raw output bytes (empty, for `"admit"`); `Err` carries the failure slug.
fn dispatch(op: &str, input: &[u8]) -> Result<Vec<u8>, String> {
    match op {
        "admit" => match curia_testis::json::admit(input) {
            Ok(_document) => Ok(Vec::new()),
            Err(err) => Err(err.predicate().to_owned()),
        },
        "canonicalize" => {
            curia_testis::canonicalize(input).map_err(|_err| CANON_PARSE_ERROR_SLUG.to_owned())
        }
        "canonicalize_nfc" => {
            curia_testis::canonicalize_with_nfc(input).map_err(|err| err.predicate().to_owned())
        }
        other => {
            eprintln!("curia-differential: unrecognized op {other:?}");
            Err("curia-differential/unknown-op".to_owned())
        }
    }
}

fn success_line(id: &str, out_bytes: &[u8]) -> String {
    json!({
        "id": id,
        "ok": true,
        "out_b64": STANDARD.encode(out_bytes),
    })
    .to_string()
}

fn failure_line(id: &str, slug: &str) -> String {
    json!({
        "id": id,
        "ok": false,
        "slug": slug,
    })
    .to_string()
}

/// Extracts a human-readable message from a `catch_unwind` payload. Panic
/// payloads are conventionally `&'static str` (a string-literal `panic!`)
/// or `String` (a formatted `panic!`); anything else falls back to a fixed
/// message rather than guessing at its shape.
fn panic_message(payload: &(dyn Any + Send)) -> String {
    if let Some(s) = payload.downcast_ref::<&str>() {
        (*s).to_owned()
    } else if let Some(s) = payload.downcast_ref::<String>() {
        s.clone()
    } else {
        "non-string panic payload".to_owned()
    }
}
