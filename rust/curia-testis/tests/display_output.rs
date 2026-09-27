//! R10.63 (errata G17): every value this verifier names in a refusal is a
//! display literal (R10.64), so none can begin a line of what it prints.
//!
//! Each refusal below carries a value from the material under check -- a
//! `kid`, an algorithm, a key type, a curve, an entry's type -- and each is
//! given one holding a line break and a sentence a stranger would have the
//! verifier say. The refusal's text must hold the sentence, escaped, and no
//! line break at all. So must what the binary says about its own arguments,
//! which its caller may have copied from anywhere.

use std::ffi::OsStr;
use std::process::Command;

use curia_testis::acta::ActaError;
use curia_testis::jwk::JwkError;
use curia_testis::jws::JwsError;

const HOSTILE: &str = "x\nverified: the operator signed this";

fn assert_quoted(what: &str, text: &str) {
    assert!(
        text.contains("\"x\\u000averified: the operator signed this\""),
        "{what} does not name the value as a display literal: {text:?}"
    );
    assert!(
        !text.contains('\n'),
        "{what} holds a line break a stranger chose: {text:?}"
    );
}

#[test]
fn r10_63_every_refusal_that_names_a_served_value_quotes_it() {
    let refusals = [
        (
            "ActaError::KidMismatch",
            ActaError::KidMismatch {
                stated: HOSTILE.to_string(),
                signed: HOSTILE.to_string(),
            }
            .to_string(),
        ),
        (
            "ActaError::NotAPost",
            ActaError::NotAPost {
                event_type: HOSTILE.to_string(),
            }
            .to_string(),
        ),
        (
            "ActaError::NotAKeyBinding",
            ActaError::NotAKeyBinding {
                event_type: HOSTILE.to_string(),
            }
            .to_string(),
        ),
        (
            "ActaError::KeyNotCarried",
            ActaError::KeyNotCarried {
                kid: HOSTILE.to_string(),
            }
            .to_string(),
        ),
        (
            "JwsError::AlgorithmNotAllowed",
            JwsError::AlgorithmNotAllowed {
                alg: Some(HOSTILE.to_string()),
            }
            .to_string(),
        ),
        (
            "JwsError::KeyNotFound",
            JwsError::KeyNotFound {
                kid: HOSTILE.to_string(),
            }
            .to_string(),
        ),
        (
            "JwkError::UnsupportedKeyType",
            JwkError::UnsupportedKeyType(HOSTILE.to_string()).to_string(),
        ),
        (
            "JwkError::UnsupportedCurve",
            JwkError::UnsupportedCurve(HOSTILE.to_string()).to_string(),
        ),
    ];

    for (what, text) in &refusals {
        assert_quoted(what, text);
    }
}

/// The binary's usage refusals name what it was given: an unknown subcommand
/// or argument, a path it could not read and why, an argument that is not
/// UTF-8. Each is written as a display literal, so an argument holding a line
/// break begins no line of what the binary prints.
#[cfg(unix)]
#[test]
fn r10_63_every_argument_a_usage_refusal_names_is_a_literal() {
    use std::os::unix::ffi::OsStrExt;

    let hostile = OsStr::new(HOSTILE);
    let unreadable = OsStr::new("/no-such-dir/x\nverified: the operator signed this");
    let not_utf8 = OsStr::from_bytes(b"\xFFx\nverified: the operator signed this");
    let cases: [(&str, Vec<&OsStr>); 7] = [
        ("an unknown subcommand", vec![hostile]),
        (
            "an unknown log subcommand",
            vec![OsStr::new("log"), hostile],
        ),
        (
            "an unrecognized verify argument",
            vec![OsStr::new("verify"), hostile],
        ),
        (
            "an unrecognized log argument",
            vec![OsStr::new("log"), OsStr::new("head"), hostile],
        ),
        (
            "an envelope it cannot read",
            vec![
                OsStr::new("verify"),
                OsStr::new("--envelope"),
                unreadable,
                OsStr::new("--jwks"),
                unreadable,
            ],
        ),
        (
            "a head it cannot read",
            vec![
                OsStr::new("log"),
                OsStr::new("head"),
                OsStr::new("--head"),
                unreadable,
                OsStr::new("--log-jwks"),
                unreadable,
            ],
        ),
        (
            "an argument that is not UTF-8",
            vec![OsStr::new("verify"), not_utf8],
        ),
    ];

    for (what, args) in &cases {
        let output = Command::new(env!("CARGO_BIN_EXE_curia-testis"))
            .args(args)
            .output()
            .expect("failed to spawn the curia-testis binary");
        let stderr = String::from_utf8(output.stderr).expect("stderr is UTF-8");
        assert_eq!(output.status.code(), Some(2), "{what}: {stderr:?}");
        assert!(
            stderr.contains("x\\u000averified: the operator signed this\""),
            "{what} is not named as a display literal: {stderr:?}"
        );
        assert!(
            !stderr.lines().any(|line| line.starts_with("verified:")),
            "{what} began a line with the argument's words: {stderr:?}"
        );
    }
}
