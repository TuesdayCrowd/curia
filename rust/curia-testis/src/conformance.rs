//! Loader for the `conformance/` corpus.
//!
//! `conformance/README.md` documents three directory shapes:
//!
//! - `rfc8785/` — `input-<name>.json` / `output-<name>.json` file pairs, no
//!   `meta.json`, profile implicitly `rfc8785`.
//! - `<family>/<case>/` — the common shape (`c4`, `ordering`, `unicode`,
//!   `numbers`, `admit-reject`): `input.json`, `meta.json`, and either
//!   `expected.canonical` + `expected.digest` or `expect-reject`.
//! - `envelope/<case>/` — six files, no `input.json`: `submission.json`,
//!   `jwks.json`, `private-keys.json`, `expected.canonical`,
//!   `expected.digest`, `meta.json`.
//!
//! This module is deliberately not the place that decides what any of these
//! bytes *mean* — that is [`crate::canonicalize`], [`crate::canonicalize_with_nfc`],
//! [`crate::admit`], and [`crate::verify_envelope`]. The loader's only job is
//! to get the right bytes, byte-for-byte, off disk and into memory, and to
//! fail with a typed [`LoaderError`] — never a panic — when a corpus file is
//! missing or malformed.
//!
//! ## Why `submission.json`'s `envelope` field is extracted with `RawValue`
//!
//! `envelope/<case>/expected.canonical` pins the canonical form of the
//! `envelope` sub-object exactly as published in `submission.json`, not the
//! outer `{envelope, signature}` wrapper (confirmed against the fixtures by
//! byte inspection: `expected.canonical` for `ed25519-minimal` has no
//! `signature` key). Extracting that sub-object with an ordinary
//! `serde_json::Value` parse-and-reserialize would launder it through
//! `serde_json`'s own formatting decisions before a single line of Task 2/3
//! canonicalization code ever ran — exactly the lossy trap the Task 1 brief
//! warns against. `serde_json::value::RawValue` instead captures the verbatim
//! source bytes of the `envelope` value's span, so the bytes handed to
//! [`crate::canonicalize_with_nfc`] are the *actual* published bytes
//! (including whatever number/string literal spelling and pretty-print
//! whitespace `submission.json` used), not a serde_json reinterpretation of
//! them. This keeps the Task 1 `tests/vectors.rs` assertion genuine: once
//! Task 2/3 land, this exact call is expected to turn green without the test
//! itself changing.

use std::collections::HashMap;
use std::fmt;
use std::fs;
use std::io;
use std::path::{Path, PathBuf};

use serde_json::value::RawValue;
use serde_json::Value;

/// Resolves the corpus root.
///
/// `CURIA_CONFORMANCE_DIR`, if set, wins outright. Otherwise the corpus is
/// resolved relative to this crate's own manifest directory as
/// `../../conformance` — correct both in the cleanroom (`$CLEANROOM/rust/curia-testis`
/// next to `$CLEANROOM/conformance`) and after the crate is moved into the
/// real repository (`<repo>/rust/curia-testis` next to `<repo>/conformance`),
/// per the controller's brief. No absolute path is ever hardcoded.
pub fn conformance_dir() -> PathBuf {
    if let Ok(dir) = std::env::var("CURIA_CONFORMANCE_DIR") {
        return PathBuf::from(dir);
    }
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../../conformance")
}

/// The canonicalization function (or phase) a vector's `profile` field
/// selects. See `conformance/README.md`, "Which function a vector
/// constrains".
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum Profile {
    /// `Canonicalize` — pure RFC 8785, no Unicode normalization.
    Rfc8785,
    /// `CanonicalizeWithNfc` — NFC every key and value, recursively, then
    /// canonicalize.
    CanonicalizeWithNfc,
    /// The ADMIT phase: accept-or-reject, no canonicalization reached.
    Admit,
    /// `CanonicalizeEnvelope` + `Digests.Sha256` + `DetachedJws.Verify`,
    /// end to end.
    Envelope,
}

impl Profile {
    fn parse(raw: &str, path: &Path) -> Result<Self, LoaderError> {
        match raw {
            "rfc8785" => Ok(Profile::Rfc8785),
            "canonicalize-with-nfc" => Ok(Profile::CanonicalizeWithNfc),
            "admit" => Ok(Profile::Admit),
            "envelope" => Ok(Profile::Envelope),
            other => Err(LoaderError::UnknownProfile {
                path: path.to_path_buf(),
                profile: other.to_string(),
            }),
        }
    }

    pub fn as_str(&self) -> &'static str {
        match self {
            Profile::Rfc8785 => "rfc8785",
            Profile::CanonicalizeWithNfc => "canonicalize-with-nfc",
            Profile::Admit => "admit",
            Profile::Envelope => "envelope",
        }
    }
}

/// What a common-shape vector expects: either successful canonicalization
/// (with the exact canonical bytes and digest), or a specific rejection slug.
#[derive(Debug, Clone)]
pub enum Expectation {
    Canonicalize { canonical: Vec<u8>, digest: String },
    Reject { slug: String },
}

/// A vendored RFC 8785 `input-<name>.json` / `output-<name>.json` pair. No
/// `meta.json` exists for these; `conformance/README.md` states the profile
/// is implicitly `rfc8785`.
#[derive(Debug, Clone)]
pub struct Rfc8785Vector {
    pub name: String,
    pub input: Vec<u8>,
    pub expected_output: Vec<u8>,
}

/// A vector from one of the common-shape families: `c4/`, `ordering/`,
/// `unicode/`, `numbers/`, `admit-reject/`.
#[derive(Debug, Clone)]
pub struct DirectoryVector {
    pub family: String,
    pub case: String,
    pub profile: Profile,
    pub requirement: String,
    pub note: Option<String>,
    pub input: Vec<u8>,
    pub expectation: Expectation,
}

/// A vector from the `envelope/` family: the six-file shape described in
/// `conformance/README.md`, "The `envelope/` family".
#[derive(Debug, Clone)]
pub struct EnvelopeVector {
    pub case: String,
    pub requirement: String,
    pub alg: String,
    pub note: Option<String>,
    /// Present only on the two negative cases (`tampered-body`,
    /// `wrong-key`): the RFC 9457 slug verification must fail with.
    pub expect_verify_failure: Option<String>,
    /// The full `{"envelope": ..., "signature": ...}` wire object, exactly
    /// as `submission.json` published it.
    pub submission: Vec<u8>,
    /// The `envelope` sub-object's raw bytes, extracted verbatim from
    /// `submission.json` via `RawValue` (see the module doc comment).
    pub envelope: Vec<u8>,
    /// The decoded `signature` string: the detached-JWS compact
    /// serialization with an empty payload segment.
    pub signature: String,
    /// `jwks.json` — the public key set a verifier is given.
    pub jwks: Vec<u8>,
    /// `private-keys.json` — published on purpose; see
    /// `conformance/README.md`, "Private keys are published on purpose".
    pub private_keys: Vec<u8>,
    pub expected_canonical: Vec<u8>,
    pub expected_digest: String,
}

/// The whole loaded corpus, one field per top-level `conformance/` directory.
#[derive(Debug, Clone, Default)]
pub struct Corpus {
    pub rfc8785: Vec<Rfc8785Vector>,
    pub c4: Vec<DirectoryVector>,
    pub ordering: Vec<DirectoryVector>,
    pub unicode: Vec<DirectoryVector>,
    pub numbers: Vec<DirectoryVector>,
    pub admit_reject: Vec<DirectoryVector>,
    pub envelope: Vec<EnvelopeVector>,
}

impl Corpus {
    /// Loads every family under `root`.
    pub fn load(root: &Path) -> Result<Corpus, LoaderError> {
        Ok(Corpus {
            rfc8785: load_rfc8785(&root.join("rfc8785"))?,
            c4: load_directory_family(root, "c4")?,
            ordering: load_directory_family(root, "ordering")?,
            unicode: load_directory_family(root, "unicode")?,
            numbers: load_directory_family(root, "numbers")?,
            admit_reject: load_directory_family(root, "admit-reject")?,
            envelope: load_envelope_family(root)?,
        })
    }

    /// Loads from [`conformance_dir`].
    pub fn load_default() -> Result<Corpus, LoaderError> {
        Self::load(&conformance_dir())
    }

    /// Total vector count across every family, including the vendored
    /// RFC 8785 pairs.
    pub fn total_len(&self) -> usize {
        self.rfc8785.len()
            + self.c4.len()
            + self.ordering.len()
            + self.unicode.len()
            + self.numbers.len()
            + self.admit_reject.len()
            + self.envelope.len()
    }
}

/// A typed loader failure. The loader never panics: a missing or malformed
/// corpus file always surfaces here.
#[derive(Debug)]
pub enum LoaderError {
    Io {
        path: PathBuf,
        source: io::Error,
    },
    Json {
        path: PathBuf,
        source: serde_json::Error,
    },
    NotUtf8 {
        path: PathBuf,
    },
    /// `meta.json` is missing a required field.
    MissingMetaField {
        path: PathBuf,
        field: &'static str,
    },
    /// `meta.json`'s `requirement` field is present but empty.
    ///
    /// `conformance/README.md`: "A vector citing no requirement does not
    /// belong in the set."
    EmptyRequirement {
        path: PathBuf,
    },
    /// `meta.json`'s `profile` is not one of the four documented values.
    UnknownProfile {
        path: PathBuf,
        profile: String,
    },
    /// A case directory has neither `expected.canonical` nor `expect-reject`.
    MissingExpectation {
        path: PathBuf,
    },
    /// A case directory has both `expected.canonical` and `expect-reject`,
    /// which `conformance/README.md` never describes as valid.
    AmbiguousExpectation {
        path: PathBuf,
    },
    /// A `submission.json` is missing its `envelope` or `signature` field.
    MissingSubmissionField {
        path: PathBuf,
        field: &'static str,
    },
    /// A `conformance/rfc8785/input-<name>.json` has no matching
    /// `output-<name>.json`, or vice versa.
    UnpairedRfc8785Vector {
        path: PathBuf,
        name: String,
    },
}

impl fmt::Display for LoaderError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            LoaderError::Io { path, source } => {
                write!(f, "{}: {source}", path.display())
            }
            LoaderError::Json { path, source } => {
                write!(f, "{}: invalid JSON: {source}", path.display())
            }
            LoaderError::NotUtf8 { path } => {
                write!(f, "{}: not valid UTF-8", path.display())
            }
            LoaderError::MissingMetaField { path, field } => {
                write!(f, "{}: missing required field `{field}`", path.display())
            }
            LoaderError::EmptyRequirement { path } => {
                write!(
                    f,
                    "{}: `requirement` is empty; a vector citing no requirement \
                     does not belong in the corpus (conformance/README.md)",
                    path.display()
                )
            }
            LoaderError::UnknownProfile { path, profile } => {
                write!(
                    f,
                    "{}: unknown profile `{profile}` (expected one of: rfc8785, \
                     canonicalize-with-nfc, admit, envelope)",
                    path.display()
                )
            }
            LoaderError::MissingExpectation { path } => {
                write!(
                    f,
                    "{}: has neither expected.canonical nor expect-reject",
                    path.display()
                )
            }
            LoaderError::AmbiguousExpectation { path } => {
                write!(
                    f,
                    "{}: has both expected.canonical and expect-reject",
                    path.display()
                )
            }
            LoaderError::MissingSubmissionField { path, field } => {
                write!(f, "{}: submission is missing `{field}`", path.display())
            }
            LoaderError::UnpairedRfc8785Vector { path, name } => {
                write!(
                    f,
                    "{}: `{name}` has an input-*.json with no matching output-*.json (or vice versa)",
                    path.display()
                )
            }
        }
    }
}

impl std::error::Error for LoaderError {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        match self {
            LoaderError::Io { source, .. } => Some(source),
            LoaderError::Json { source, .. } => Some(source),
            _ => None,
        }
    }
}

fn read_file(path: &Path) -> Result<Vec<u8>, LoaderError> {
    fs::read(path).map_err(|source| LoaderError::Io {
        path: path.to_path_buf(),
        source,
    })
}

/// Reads a small text file (a digest or a reject slug) and trims a single
/// trailing newline, if any. The corpus files observed carry none, but
/// trimming costs nothing and protects against an editor adding one.
fn read_text_trimmed(path: &Path) -> Result<String, LoaderError> {
    let bytes = read_file(path)?;
    let text = String::from_utf8(bytes).map_err(|_| LoaderError::NotUtf8 {
        path: path.to_path_buf(),
    })?;
    Ok(text.trim_end_matches(['\n', '\r']).to_string())
}

fn list_dir_sorted(dir: &Path) -> Result<Vec<PathBuf>, LoaderError> {
    let read_dir = fs::read_dir(dir).map_err(|source| LoaderError::Io {
        path: dir.to_path_buf(),
        source,
    })?;
    let mut entries = Vec::new();
    for entry in read_dir {
        let entry = entry.map_err(|source| LoaderError::Io {
            path: dir.to_path_buf(),
            source,
        })?;
        entries.push(entry.path());
    }
    entries.sort();
    Ok(entries)
}

struct RawMeta {
    profile: String,
    requirement: String,
    note: Option<String>,
}

fn parse_meta_value(bytes: &[u8], path: &Path) -> Result<Value, LoaderError> {
    serde_json::from_slice(bytes).map_err(|source| LoaderError::Json {
        path: path.to_path_buf(),
        source,
    })
}

fn load_meta(path: &Path) -> Result<RawMeta, LoaderError> {
    let bytes = read_file(path)?;
    let value = parse_meta_value(&bytes, path)?;
    let profile = value
        .get("profile")
        .and_then(Value::as_str)
        .ok_or(LoaderError::MissingMetaField {
            path: path.to_path_buf(),
            field: "profile",
        })?
        .to_string();
    let requirement = value
        .get("requirement")
        .and_then(Value::as_str)
        .ok_or(LoaderError::MissingMetaField {
            path: path.to_path_buf(),
            field: "requirement",
        })?
        .to_string();
    if requirement.trim().is_empty() {
        return Err(LoaderError::EmptyRequirement {
            path: path.to_path_buf(),
        });
    }
    let note = value
        .get("note")
        .and_then(Value::as_str)
        .map(|s| s.to_string());
    Ok(RawMeta {
        profile,
        requirement,
        note,
    })
}

fn load_expectation(case_dir: &Path) -> Result<Expectation, LoaderError> {
    let canonical_path = case_dir.join("expected.canonical");
    let reject_path = case_dir.join("expect-reject");
    let has_canonical = canonical_path.is_file();
    let has_reject = reject_path.is_file();
    match (has_canonical, has_reject) {
        (true, false) => {
            let canonical = read_file(&canonical_path)?;
            let digest = read_text_trimmed(&case_dir.join("expected.digest"))?;
            Ok(Expectation::Canonicalize { canonical, digest })
        }
        (false, true) => {
            let slug = read_text_trimmed(&reject_path)?;
            Ok(Expectation::Reject { slug })
        }
        (true, true) => Err(LoaderError::AmbiguousExpectation {
            path: case_dir.to_path_buf(),
        }),
        (false, false) => Err(LoaderError::MissingExpectation {
            path: case_dir.to_path_buf(),
        }),
    }
}

fn load_rfc8785(dir: &Path) -> Result<Vec<Rfc8785Vector>, LoaderError> {
    let mut names: Vec<String> = Vec::new();
    for path in list_dir_sorted(dir)? {
        if let Some(file_name) = path.file_name().and_then(|n| n.to_str()) {
            if let Some(rest) = file_name
                .strip_prefix("input-")
                .and_then(|s| s.strip_suffix(".json"))
            {
                names.push(rest.to_string());
            }
        }
    }
    names.sort();

    let mut vectors = Vec::with_capacity(names.len());
    for name in names {
        let input_path = dir.join(format!("input-{name}.json"));
        let output_path = dir.join(format!("output-{name}.json"));
        if !output_path.is_file() {
            return Err(LoaderError::UnpairedRfc8785Vector {
                path: output_path,
                name,
            });
        }
        let input = read_file(&input_path)?;
        let expected_output = read_file(&output_path)?;
        vectors.push(Rfc8785Vector {
            name,
            input,
            expected_output,
        });
    }
    Ok(vectors)
}

fn load_directory_family(root: &Path, family: &str) -> Result<Vec<DirectoryVector>, LoaderError> {
    let family_dir = root.join(family);
    let mut vectors = Vec::new();
    for path in list_dir_sorted(&family_dir)? {
        if !path.is_dir() {
            continue;
        }
        let case = path
            .file_name()
            .and_then(|n| n.to_str())
            .ok_or(LoaderError::NotUtf8 { path: path.clone() })?
            .to_string();

        let meta = load_meta(&path.join("meta.json"))?;
        let profile = Profile::parse(&meta.profile, &path.join("meta.json"))?;
        let input = read_file(&path.join("input.json"))?;
        let expectation = load_expectation(&path)?;

        vectors.push(DirectoryVector {
            family: family.to_string(),
            case,
            profile,
            requirement: meta.requirement,
            note: meta.note,
            input,
            expectation,
        });
    }
    Ok(vectors)
}

/// Splits a `submission.json` wire object into the raw `envelope` sub-object
/// bytes and the decoded `signature` string, without reinterpreting the
/// envelope's bytes through `serde_json::Value`. See the module doc comment.
fn split_submission(bytes: &[u8], path: &Path) -> Result<(Vec<u8>, String), LoaderError> {
    let map: HashMap<String, Box<RawValue>> =
        serde_json::from_slice(bytes).map_err(|source| LoaderError::Json {
            path: path.to_path_buf(),
            source,
        })?;
    let envelope_raw = map
        .get("envelope")
        .ok_or(LoaderError::MissingSubmissionField {
            path: path.to_path_buf(),
            field: "envelope",
        })?;
    let envelope_bytes = envelope_raw.get().as_bytes().to_vec();

    let signature_raw = map
        .get("signature")
        .ok_or(LoaderError::MissingSubmissionField {
            path: path.to_path_buf(),
            field: "signature",
        })?;
    let signature: String =
        serde_json::from_str(signature_raw.get()).map_err(|source| LoaderError::Json {
            path: path.to_path_buf(),
            source,
        })?;

    Ok((envelope_bytes, signature))
}

fn load_envelope_family(root: &Path) -> Result<Vec<EnvelopeVector>, LoaderError> {
    let family_dir = root.join("envelope");
    let mut vectors = Vec::new();
    for path in list_dir_sorted(&family_dir)? {
        if !path.is_dir() {
            continue;
        }
        let case = path
            .file_name()
            .and_then(|n| n.to_str())
            .ok_or(LoaderError::NotUtf8 { path: path.clone() })?
            .to_string();

        let meta_path = path.join("meta.json");
        let meta_bytes = read_file(&meta_path)?;
        let meta_value = parse_meta_value(&meta_bytes, &meta_path)?;

        let profile_str = meta_value.get("profile").and_then(Value::as_str).ok_or(
            LoaderError::MissingMetaField {
                path: meta_path.clone(),
                field: "profile",
            },
        )?;
        // Re-use Profile::parse so an envelope meta.json that drifts from
        // `"profile": "envelope"` is reported the same way any other
        // unknown profile would be, rather than silently ignored.
        match Profile::parse(profile_str, &meta_path)? {
            Profile::Envelope => {}
            _ => {
                return Err(LoaderError::UnknownProfile {
                    path: meta_path,
                    profile: profile_str.to_string(),
                })
            }
        }

        let requirement = meta_value
            .get("requirement")
            .and_then(Value::as_str)
            .ok_or(LoaderError::MissingMetaField {
                path: meta_path.clone(),
                field: "requirement",
            })?
            .to_string();
        if requirement.trim().is_empty() {
            return Err(LoaderError::EmptyRequirement { path: meta_path });
        }
        let alg = meta_value
            .get("alg")
            .and_then(Value::as_str)
            .ok_or(LoaderError::MissingMetaField {
                path: meta_path.clone(),
                field: "alg",
            })?
            .to_string();
        let note = meta_value
            .get("note")
            .and_then(Value::as_str)
            .map(|s| s.to_string());
        let expect_verify_failure = meta_value
            .get("expect-verify-failure")
            .and_then(Value::as_str)
            .map(|s| s.to_string());

        let submission_path = path.join("submission.json");
        let submission = read_file(&submission_path)?;
        let (envelope, signature) = split_submission(&submission, &submission_path)?;

        let jwks = read_file(&path.join("jwks.json"))?;
        let private_keys = read_file(&path.join("private-keys.json"))?;
        let expected_canonical = read_file(&path.join("expected.canonical"))?;
        let expected_digest = read_text_trimmed(&path.join("expected.digest"))?;

        vectors.push(EnvelopeVector {
            case,
            requirement,
            alg,
            note,
            expect_verify_failure,
            submission,
            envelope,
            signature,
            jwks,
            private_keys,
            expected_canonical,
            expected_digest,
        });
    }
    Ok(vectors)
}
