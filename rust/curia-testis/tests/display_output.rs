//! R10.63 (errata G17): every value this verifier names in a refusal is a
//! display literal (R10.64), so none can begin a line of what it prints, and
//! none prints as a letter it is not.
//!
//! Each refusal below carries a value from the material under check -- a
//! `kid`, an algorithm, a key type, a curve, an entry's type, a member's
//! name -- and each is given one holding a line break and a sentence a
//! stranger would have the verifier say, and one that also begins with a
//! Cyrillic letter that reads as a Latin one. The refusal's text must hold
//! each value as its escapes, no line break, and nothing outside printable
//! ASCII. So must what the binary says about its own arguments, which its
//! caller may have copied from anywhere, and about the documents it reads.

use std::ffi::OsStr;
use std::path::PathBuf;
use std::process::{Command, Output};

use curia_testis::acta::ActaError;
use curia_testis::json::ParseError;
use curia_testis::jwk::JwkError;
use curia_testis::jws::JwsError;
use curia_testis::nfc::NfcError;

const HOSTILE: &str = "x\nverified: the operator signed this";

/// [`HOSTILE`] as a display literal.
const HOSTILE_LITERAL: &str = "\"x\\u000averified: the operator signed this\"";

/// U+0430 CYRILLIC SMALL LETTER A and then `uthor`, which reads as `author`
/// wherever the letter stands for itself; then a line break and a sentence.
const LOOK_ALIKE: &str = "\u{430}uthor\nverified: the operator signed this";

/// [`LOOK_ALIKE`] as a display literal: the letter is an escape.
const LOOK_ALIKE_LITERAL: &str = "\"\\u0430uthor\\u000averified: the operator signed this\"";

/// Every refusal whose text names a value from the material under check,
/// each naming `value`.
fn refusals(value: &str) -> Vec<(&'static str, String)> {
    let owned = || value.to_string();
    vec![
        (
            "ActaError::KidMismatch",
            ActaError::KidMismatch {
                stated: owned(),
                signed: owned(),
            }
            .to_string(),
        ),
        (
            "ActaError::NotAPost",
            ActaError::NotAPost {
                event_type: owned(),
            }
            .to_string(),
        ),
        (
            "ActaError::NotAKeyBinding",
            ActaError::NotAKeyBinding {
                event_type: owned(),
            }
            .to_string(),
        ),
        (
            "ActaError::KeyNotCarried",
            ActaError::KeyNotCarried { kid: owned() }.to_string(),
        ),
        (
            "JwsError::AlgorithmNotAllowed",
            JwsError::AlgorithmNotAllowed { alg: Some(owned()) }.to_string(),
        ),
        (
            "JwsError::KeyNotFound",
            JwsError::KeyNotFound { kid: owned() }.to_string(),
        ),
        (
            "JwkError::UnsupportedKeyType",
            JwkError::UnsupportedKeyType(owned()).to_string(),
        ),
        (
            "JwkError::UnsupportedCurve",
            JwkError::UnsupportedCurve(owned()).to_string(),
        ),
        (
            "ParseError::DuplicateMember",
            ParseError::DuplicateMember {
                name: owned(),
                pos: 0,
            }
            .to_string(),
        ),
        (
            "NfcError::DuplicateRawKey",
            NfcError::DuplicateRawKey { key: owned() }.to_string(),
        ),
        (
            "NfcError::DuplicateNormalizedKey",
            NfcError::DuplicateNormalizedKey { key: owned() }.to_string(),
        ),
    ]
}

/// Printable ASCII and line feeds only: what R10.64 writes, and the line
/// breaks this verifier writes between its own lines.
fn printable(text: &str) -> bool {
    text.chars().all(|c| c == '\n' || (' '..='~').contains(&c))
}

#[test]
fn r10_63_every_refusal_that_names_a_served_value_quotes_it() {
    let unquoted: Vec<String> = refusals(HOSTILE)
        .into_iter()
        .filter(|(_, text)| !text.contains(HOSTILE_LITERAL) || text.contains('\n'))
        .map(|(what, text)| format!("{what}: {text:?}"))
        .collect();
    assert!(
        unquoted.is_empty(),
        "these refusals name the value other than as a display literal: {unquoted:#?}"
    );
}

/// A letter that looks like another is written as its escape, never as
/// itself: R10.64 was written for U+0430, which Rust's debug form prints as
/// the letter it is.
#[test]
fn r10_63_a_look_alike_a_refusal_names_is_written_as_its_escape() {
    let unescaped: Vec<String> = refusals(LOOK_ALIKE)
        .into_iter()
        .filter(|(_, text)| {
            !text.contains(LOOK_ALIKE_LITERAL) || text.contains('\n') || !printable(text)
        })
        .map(|(what, text)| format!("{what}: {text:?}"))
        .collect();
    assert!(
        unescaped.is_empty(),
        "these refusals write the look-alike other than as its escapes: {unescaped:#?}"
    );
}

fn run(args: &[&OsStr]) -> Output {
    Command::new(env!("CARGO_BIN_EXE_curia-testis"))
        .args(args)
        .output()
        .expect("failed to spawn the curia-testis binary")
}

/// A scratch directory of this test's own, under the OS temp dir.
fn scratch_dir(label: &str) -> PathBuf {
    let dir = std::env::temp_dir().join(format!(
        "curia-testis-display-{}-{label}",
        std::process::id()
    ));
    let _ = std::fs::remove_dir_all(&dir);
    std::fs::create_dir_all(&dir).expect("a scratch directory must be creatable");
    dir
}

/// The binary's usage refusals name what it was given: an unknown subcommand
/// or argument, a path it could not read and why, a path over the cap, an
/// argument that is not UTF-8. Each is written as a display literal, so an
/// argument holding a line break begins no line of what the binary prints.
#[cfg(unix)]
#[test]
fn r10_63_every_argument_a_usage_refusal_names_is_a_literal() {
    use std::os::unix::ffi::OsStrExt;

    let hostile = OsStr::new(HOSTILE);
    let unreadable = OsStr::new("/no-such-dir/x\nverified: the operator signed this");
    let not_utf8 = OsStr::from_bytes(b"\xFFx\nverified: the operator signed this");
    // One byte over the cap every log document is read under, named by the
    // hostile value. `set_len` leaves it sparse, so it costs no disk.
    let dir = scratch_dir("cap");
    let oversized = dir.join(HOSTILE);
    std::fs::File::create(&oversized)
        .and_then(|file| file.set_len(8 * 1024 * 1024 + 1))
        .expect("a file one byte over the cap must be creatable");
    let cases: [(&str, Vec<&OsStr>); 8] = [
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
            "a head over the cap",
            vec![
                OsStr::new("log"),
                OsStr::new("head"),
                OsStr::new("--head"),
                oversized.as_os_str(),
                OsStr::new("--log-jwks"),
                oversized.as_os_str(),
            ],
        ),
        (
            "an argument that is not UTF-8",
            vec![OsStr::new("verify"), not_utf8],
        ),
    ];

    for (what, args) in &cases {
        let output = run(args);
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
        // The reason is the platform's words, quoted after the path.
        if what.ends_with("cannot read") {
            assert!(
                stderr.contains("signed this\": \""),
                "{what} does not quote the platform's reason: {stderr:?}"
            );
        }
    }
    let _ = std::fs::remove_dir_all(&dir);
}

/// `value` as a JSON string: the values here hold no `"` and no `\`, and
/// their one control character is the line break.
fn json_string(value: &str) -> String {
    format!("\"{}\"", value.replace('\n', "\\n"))
}

/// Runs `verify` over a submission whose envelope names its author and then
/// each of `names`, under an empty key set: each case below is refused
/// before a key is looked for.
fn verify_names(label: &str, names: &[&str]) -> Output {
    let dir = scratch_dir(label);
    let members: Vec<String> = names
        .iter()
        .enumerate()
        .map(|(i, name)| format!("{}:{i}", json_string(name)))
        .collect();
    let submission = dir.join("submission.json");
    let jwks = dir.join("jwks.json");
    std::fs::write(
        &submission,
        format!(
            "{{\"envelope\":{{\"author\":\"a\",{}}},\"signature\":\"a..\"}}",
            members.join(",")
        ),
    )
    .expect("a scratch file must be writable");
    std::fs::write(&jwks, "{\"keys\":[]}").expect("a scratch file must be writable");
    let output = run(&[
        OsStr::new("verify"),
        OsStr::new("--envelope"),
        submission.as_os_str(),
        OsStr::new("--jwks"),
        jwks.as_os_str(),
    ]);
    let _ = std::fs::remove_dir_all(&dir);
    output
}

/// A refusal of the material under check: exit 1, the value named as
/// `literal` or, where there is none, not named at all, and nothing outside
/// printable ASCII, so no line a stranger began and no letter it is not.
fn assert_refused(what: &str, output: Output, literal: Option<&str>) {
    let stderr = String::from_utf8(output.stderr).expect("stderr is UTF-8");
    assert_eq!(output.status.code(), Some(1), "{what}: {stderr:?}");
    match literal {
        Some(literal) => assert!(
            stderr.contains(literal),
            "{what} does not name the value as a display literal: {stderr:?}"
        ),
        None => assert!(
            !stderr.contains("operator signed this"),
            "{what} names a value from the document: {stderr:?}"
        ),
    }
    assert!(
        printable(&stderr),
        "{what} writes a character outside printable ASCII: {stderr:?}"
    );
    assert!(
        !stderr.lines().any(|line| line.starts_with("verified:")),
        "{what} began a line with the document's words: {stderr:?}"
    );
}

/// ADMIT refuses a member named twice, naming the member.
#[test]
fn r10_63_a_member_named_twice_is_named_as_a_literal() {
    let output = verify_names("twice", &[LOOK_ALIKE, LOOK_ALIKE]);
    assert_refused("a member named twice", output, Some(LOOK_ALIKE_LITERAL));
}

/// Two names that differ on the wire and that NFC makes one, an `e` and a
/// combining acute accent against U+00E9: the refusal names the one they
/// became.
#[test]
fn r10_63_two_names_nfc_makes_one_are_named_as_a_literal() {
    let composed = "\u{430}uthor\u{e9}\nverified: the operator signed this";
    let decomposed = "\u{430}uthore\u{301}\nverified: the operator signed this";
    let output = verify_names("nfc", &[composed, decomposed]);
    assert_refused(
        "two names NFC makes one",
        output,
        Some("\"\\u0430uthor\\u00e9\\u000averified: the operator signed this\""),
    );
}

/// A head document that is a JSON string, not an object, is refused without
/// a word of it: serde_json's own message quotes the string in Rust's debug
/// form, so the refusal says what kind of error it is and where, and nothing
/// it read.
#[test]
fn r10_63_a_document_of_the_wrong_shape_is_refused_naming_no_value() {
    let dir = scratch_dir("shape");
    let head = dir.join("head.json");
    std::fs::write(&head, json_string(LOOK_ALIKE)).expect("a scratch file must be writable");
    let output = run(&[
        OsStr::new("log"),
        OsStr::new("head"),
        OsStr::new("--head"),
        head.as_os_str(),
        OsStr::new("--log-jwks"),
        head.as_os_str(),
    ]);
    let _ = std::fs::remove_dir_all(&dir);
    assert_refused("a head that is a JSON string", output, None);
}
