//! Loader for the `conformance/` corpus.
//!
//! `conformance/README.md` documents four directory shapes:
//!
//! - `rfc8785/` — `input-<name>.json` / `output-<name>.json` file pairs, no
//!   `meta.json`, profile implicitly `rfc8785`.
//! - `<family>/<case>/` — the common shape (`c4`, `ordering`, `unicode`,
//!   `numbers`, `admit-reject`, `admit-accept`): `input.json`, `meta.json`,
//!   and either `expected.canonical` + `expected.digest` or `expect-reject`.
//!   An `admit-accept/` case additionally carries `"pairs-with"` in its
//!   `meta.json`, naming its rejecting-side twin (R6.44 addendum).
//! - `envelope/<case>/` — six files, no `input.json`: `submission.json`,
//!   `jwks.json`, `private-keys.json`, `expected.canonical`,
//!   `expected.digest`, `meta.json`.
//! - `merkle/<case>/` — `input.json` holding hex leaf inputs, `expected.json`
//!   holding the root and every audit path and consistency proof, and
//!   `meta.json` (R6.23; "The `merkle/` family").
//! - `acta/<case>/` — the common shape plus `expected.leaf`, the hex leaf
//!   hash of `expected.canonical` (R6.46; "The `acta/` family").
//!
//! `conformance/index.json` (R6.45) names every top-level directory and is
//! loaded by [`Index::load`]; [`Index::check_against_disk`] is what turns a
//! family this loader does not enumerate into a failure instead of a silent
//! omission.
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
    /// The ADMIT phase, then `CanonicalizeWithNfc` (R6.44). The input must
    /// be **admitted**, and the same bytes must then canonicalize to
    /// `expected.canonical` with `expected.digest` as its SHA-256.
    ///
    /// The profile, and never the absence of `expect-reject`, is what
    /// declares acceptance: a vector whose `expect-reject` failed to be
    /// committed must fail rather than silently become an accept vector.
    /// `load_expectation` enforces exactly that — it reads the files, not
    /// the profile — so a directory with neither expectation file is still
    /// [`LoaderError::MissingExpectation`] here, and an `admit-accept`
    /// vector carrying `expect-reject` loads as
    /// [`Expectation::Reject`], a pairing the runner has no arm for and
    /// therefore fails on.
    AdmitAccept,
    /// `CanonicalizeEnvelope` + `Digests.Sha256` + `DetachedJws.Verify`,
    /// end to end.
    Envelope,
    /// [`crate::merkle`]: hash the given leaves, build the RFC 9162 tree,
    /// and reproduce and verify every audit path and consistency proof
    /// (R6.23).
    MerkleTree,
    /// [`crate::canonicalize`] (pure RFC 8785, never the NFC profile), then
    /// [`crate::merkle::leaf_hash`]: R6.46's leaf input, frozen by R15.1.
    ActaLeaf,
}

impl Profile {
    /// Parses `meta.json`'s `profile` string. An unrecognized value is
    /// [`LoaderError::UnknownProfile`], never a skip: "a skipped vector is
    /// indistinguishable in a passing log from a satisfied one" (R6.44).
    fn parse(raw: &str, path: &Path) -> Result<Self, LoaderError> {
        match raw {
            "rfc8785" => Ok(Profile::Rfc8785),
            "canonicalize-with-nfc" => Ok(Profile::CanonicalizeWithNfc),
            "admit" => Ok(Profile::Admit),
            "admit-accept" => Ok(Profile::AdmitAccept),
            "envelope" => Ok(Profile::Envelope),
            "merkle-tree" => Ok(Profile::MerkleTree),
            "acta-leaf" => Ok(Profile::ActaLeaf),
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
            Profile::AdmitAccept => "admit-accept",
            Profile::Envelope => "envelope",
            Profile::MerkleTree => "merkle-tree",
            Profile::ActaLeaf => "acta-leaf",
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
/// `unicode/`, `numbers/`, `admit-reject/`, `admit-accept/`.
#[derive(Debug, Clone)]
pub struct DirectoryVector {
    pub family: String,
    pub case: String,
    pub profile: Profile,
    pub requirement: String,
    pub note: Option<String>,
    /// `meta.json`'s `"pairs-with"`: `"<family>/<case>"` naming the
    /// rejecting-side twin of an accepting-side boundary vector (R6.44
    /// addendum). Only `admit-accept/` vectors carry it today, and a runner
    /// SHALL fail when the named vector is absent from the corpus — see
    /// [`Corpus::contains_case`], which is how `tests/vectors.rs` resolves
    /// it. Optional here rather than required, because the loader's job is
    /// to report the bytes on disk faithfully; whether a *given profile*
    /// must carry the key is the runner's assertion, not the loader's.
    pub pairs_with: Option<String>,
    pub input: Vec<u8>,
    pub expectation: Expectation,
    /// `expected.leaf`: the lowercase hex of `SHA-256(0x00 ‖ expected.canonical)`.
    /// Only `acta/` vectors carry it; the runner, not the loader, insists
    /// that an `acta-leaf` vector has one.
    pub expected_leaf: Option<String>,
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
    pub admit_accept: Vec<DirectoryVector>,
    pub envelope: Vec<EnvelopeVector>,
    pub merkle: Vec<MerkleVector>,
    pub acta: Vec<DirectoryVector>,
}

/// Every family name this loader enumerates, in the order [`Corpus::load`]
/// loads them.
///
/// This list is the thing R6.45 exists to police. `Corpus::load` hard-codes
/// its families in source, so a family added to `conformance/` is invisible
/// here until this file is separately edited — and its absence looks, in a
/// passing test-run log, exactly like a family that ran. Naming the list
/// once lets [`Index::check_against_disk`] compare it against
/// `conformance/index.json` and fail when the two disagree.
pub const LOADED_FAMILIES: &[&str] = &[
    "rfc8785",
    "c4",
    "ordering",
    "unicode",
    "numbers",
    "admit-reject",
    "admit-accept",
    "envelope",
    "merkle",
    "acta",
];

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
            admit_accept: load_directory_family(root, "admit-accept")?,
            envelope: load_envelope_family(root)?,
            merkle: load_merkle_family(root)?,
            acta: load_directory_family(root, "acta")?,
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
            + self.admit_accept.len()
            + self.envelope.len()
            + self.merkle.len()
            + self.acta.len()
    }

    /// Whether the corpus holds `<family>/<case>` — the shape
    /// `meta.json`'s `"pairs-with"` uses.
    ///
    /// `None` means *this loader does not enumerate a family of that name*,
    /// which is a different failure from "the case is missing" and must not
    /// be reported as the same thing: one says the corpus lost a vector,
    /// the other says the runner never looked. Callers are expected to fail
    /// loudly on both (R6.44 addendum).
    pub fn contains_case(&self, family: &str, case: &str) -> Option<bool> {
        match family {
            "rfc8785" => Some(self.rfc8785.iter().any(|v| v.name == case)),
            "envelope" => Some(self.envelope.iter().any(|v| v.case == case)),
            "merkle" => Some(self.merkle.iter().any(|v| v.case == case)),
            other => self
                .directory_family(other)
                .map(|vectors| vectors.iter().any(|v| v.case == case)),
        }
    }

    /// The loaded vectors of a common-shape family, by its on-disk
    /// directory name. `None` when this loader does not enumerate it.
    pub fn directory_family(&self, family: &str) -> Option<&[DirectoryVector]> {
        match family {
            "c4" => Some(&self.c4),
            "ordering" => Some(&self.ordering),
            "unicode" => Some(&self.unicode),
            "numbers" => Some(&self.numbers),
            "admit-reject" => Some(&self.admit_reject),
            "admit-accept" => Some(&self.admit_accept),
            "acta" => Some(&self.acta),
            _ => None,
        }
    }
}

/// One audit path of a `merkle/` vector: the leaf index it proves and the
/// path's nodes as lowercase hex, leaf-side first.
#[derive(Debug, Clone)]
pub struct MerkleInclusion {
    pub index: usize,
    pub path: Vec<String>,
}

/// One consistency proof of a `merkle/` vector: the earlier tree size and
/// the proof's nodes as lowercase hex. `from` equal to the vector's size
/// carries an empty path.
#[derive(Debug, Clone)]
pub struct MerkleConsistency {
    pub from: usize,
    pub path: Vec<String>,
}

/// A vector from the `merkle/` family: `conformance/README.md`, "The
/// `merkle/` family". The leaf inputs are the decoded bytes; every
/// expectation stays as the lowercase hex the corpus publishes, so a runner
/// compares spellings rather than re-encoding.
#[derive(Debug, Clone)]
pub struct MerkleVector {
    pub case: String,
    pub requirement: String,
    pub note: Option<String>,
    /// The leaf *inputs* — what gets prefixed with `0x00` and hashed — in
    /// tree order. Their number is the tree size.
    pub leaves: Vec<Vec<u8>>,
    pub root: String,
    pub leaf_hashes: Vec<String>,
    pub inclusion: Vec<MerkleInclusion>,
    pub consistency: Vec<MerkleConsistency>,
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
    /// `meta.json`'s `profile` is not one of the five documented values.
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
    /// A `merkle/` vector's `input.json` or `expected.json` is well-formed
    /// JSON with the wrong shape: a missing key, a non-hex string, an
    /// index that is not a number.
    MalformedMerkleVector {
        path: PathBuf,
        problem: String,
    },
    /// A `conformance/rfc8785/input-<name>.json` has no matching
    /// `output-<name>.json`, or vice versa.
    UnpairedRfc8785Vector {
        path: PathBuf,
        name: String,
    },
    /// `conformance/index.json` is structurally malformed: a required key is
    /// missing, or a key has the wrong JSON type. Distinct from
    /// [`LoaderError::IndexMismatch`], which is a well-formed index that
    /// disagrees with disk.
    MalformedIndex {
        path: PathBuf,
        problem: String,
    },
    /// `conformance/index.json` disagrees with what is on disk (R6.45).
    /// Every disagreement found is reported at once: fixing them one
    /// round-trip at a time is how a second one gets missed.
    IndexMismatch {
        path: PathBuf,
        problems: Vec<String>,
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
                     canonicalize-with-nfc, admit, admit-accept, envelope, merkle-tree, \
                     acta-leaf)",
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
            LoaderError::MalformedMerkleVector { path, problem } => {
                write!(f, "{}: {problem}", path.display())
            }
            LoaderError::UnpairedRfc8785Vector { path, name } => {
                write!(
                    f,
                    "{}: `{name}` has an input-*.json with no matching output-*.json (or vice versa)",
                    path.display()
                )
            }
            LoaderError::MalformedIndex { path, problem } => {
                write!(f, "{}: {problem}", path.display())
            }
            LoaderError::IndexMismatch { path, problems } => {
                write!(
                    f,
                    "{} disagrees with the corpus on disk (R6.45):",
                    path.display()
                )?;
                for problem in problems {
                    write!(f, "\n  - {problem}")?;
                }
                Ok(())
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
    /// `"pairs-with"` — hyphenated on the wire, so it is read by its literal
    /// key here rather than by a field name. See [`DirectoryVector::pairs_with`].
    pairs_with: Option<String>,
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
    let pairs_with = value
        .get("pairs-with")
        .and_then(Value::as_str)
        .map(|s| s.to_string());
    Ok(RawMeta {
        profile,
        requirement,
        note,
        pairs_with,
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
        let leaf_path = path.join("expected.leaf");
        let expected_leaf = if leaf_path.is_file() {
            Some(read_text_trimmed(&leaf_path)?)
        } else {
            None
        };

        vectors.push(DirectoryVector {
            family: family.to_string(),
            case,
            profile,
            requirement: meta.requirement,
            note: meta.note,
            pairs_with: meta.pairs_with,
            input,
            expectation,
            expected_leaf,
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

fn load_merkle_family(root: &Path) -> Result<Vec<MerkleVector>, LoaderError> {
    let family_dir = root.join("merkle");
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
        let meta = load_meta(&meta_path)?;
        // As for `envelope/`: a `merkle/` case that declares anything but
        // `merkle-tree` is reported, never routed elsewhere or skipped.
        match Profile::parse(&meta.profile, &meta_path)? {
            Profile::MerkleTree => {}
            _ => {
                return Err(LoaderError::UnknownProfile {
                    path: meta_path,
                    profile: meta.profile,
                })
            }
        }

        let input_path = path.join("input.json");
        let input = parse_meta_value(&read_file(&input_path)?, &input_path)?;
        let leaves = hex_array(&input, "leaves", &input_path)?;

        let expected_path = path.join("expected.json");
        let expected = parse_meta_value(&read_file(&expected_path)?, &expected_path)?;
        let malformed = |problem: String| LoaderError::MalformedMerkleVector {
            path: expected_path.clone(),
            problem,
        };
        let root = expected
            .get("root")
            .and_then(Value::as_str)
            .ok_or_else(|| malformed("missing string `root`".to_string()))?
            .to_string();
        let leaf_hashes = string_array(&expected, "leaf_hashes", &expected_path)?;

        let mut inclusion = Vec::new();
        for entry in json_array(&expected, "inclusion", &expected_path)? {
            let index = entry.get("index").and_then(Value::as_u64).ok_or_else(|| {
                malformed("an `inclusion` entry has no integer `index`".to_string())
            })?;
            inclusion.push(MerkleInclusion {
                index: index as usize,
                path: string_array(entry, "path", &expected_path)?,
            });
        }

        let mut consistency = Vec::new();
        for entry in json_array(&expected, "consistency", &expected_path)? {
            let from = entry.get("from").and_then(Value::as_u64).ok_or_else(|| {
                malformed("a `consistency` entry has no integer `from`".to_string())
            })?;
            consistency.push(MerkleConsistency {
                from: from as usize,
                path: string_array(entry, "path", &expected_path)?,
            });
        }

        vectors.push(MerkleVector {
            case,
            requirement: meta.requirement,
            note: meta.note,
            leaves,
            root,
            leaf_hashes,
            inclusion,
            consistency,
        });
    }
    Ok(vectors)
}

fn json_array<'a>(
    value: &'a Value,
    key: &'static str,
    path: &Path,
) -> Result<&'a [Value], LoaderError> {
    value
        .get(key)
        .and_then(Value::as_array)
        .map(Vec::as_slice)
        .ok_or_else(|| LoaderError::MalformedMerkleVector {
            path: path.to_path_buf(),
            problem: format!("missing array `{key}`"),
        })
}

fn string_array(value: &Value, key: &'static str, path: &Path) -> Result<Vec<String>, LoaderError> {
    json_array(value, key, path)?
        .iter()
        .map(|item| {
            item.as_str()
                .map(|s| s.to_string())
                .ok_or_else(|| LoaderError::MalformedMerkleVector {
                    path: path.to_path_buf(),
                    problem: format!("`{key}` has a non-string entry"),
                })
        })
        .collect()
}

fn hex_array(value: &Value, key: &'static str, path: &Path) -> Result<Vec<Vec<u8>>, LoaderError> {
    string_array(value, key, path)?
        .iter()
        .map(|s| {
            decode_hex(s).ok_or_else(|| LoaderError::MalformedMerkleVector {
                path: path.to_path_buf(),
                problem: format!("`{key}` entry `{s}` is not lowercase hex of whole bytes"),
            })
        })
        .collect()
}

/// Lowercase hex, an even number of digits, to bytes. Uppercase is refused
/// because the corpus publishes lowercase and a runner that accepted both
/// would compare spellings less strictly than the README promises.
fn decode_hex(s: &str) -> Option<Vec<u8>> {
    if !s.len().is_multiple_of(2) {
        return None;
    }
    let digit = |c: u8| match c {
        b'0'..=b'9' => Some(c - b'0'),
        b'a'..=b'f' => Some(c - b'a' + 10),
        _ => None,
    };
    s.as_bytes()
        .chunks(2)
        .map(|pair| Some(digit(pair[0])? << 4 | digit(pair[1])?))
        .collect()
}

// ---------------------------------------------------------------------
// conformance/index.json — R6.45
// ---------------------------------------------------------------------

/// One entry of `conformance/index.json`'s `directories` array: a top-level
/// directory of the corpus, and whether it is a vector family.
#[derive(Debug, Clone)]
pub struct IndexEntry {
    pub name: String,
    pub family: bool,
    /// `"directory"`, `"file-pairs"`, `"envelope"` or `"merkle"`. Required
    /// on a family entry, absent on a non-family one.
    pub shape: Option<String>,
    /// The profiles this family's vectors may declare. Empty for a
    /// non-family entry.
    pub profiles: Vec<String>,
    /// How many vectors the family holds. Required on a family entry.
    pub count: Option<usize>,
    /// Why a non-family directory is not a vector family. `red-team/`
    /// carries one; the README explains that recording the decision is the
    /// whole point of listing it.
    pub note: Option<String>,
}

/// `conformance/index.json` — R6.45's machine-readable index of every
/// top-level directory in the corpus.
///
/// It exists because this loader hard-codes its family list in source (see
/// [`LOADED_FAMILIES`]): a family added to `conformance/` is invisible here
/// until `conformance.rs` is separately edited, and its absence looks, in a
/// passing test-run log, exactly like a family that ran.
/// [`Index::check_against_disk`] is the assertion that makes that omission
/// loud.
#[derive(Debug, Clone)]
pub struct Index {
    pub directories: Vec<IndexEntry>,
    /// The path the index was read from, so failures name the file.
    pub path: PathBuf,
}

impl Index {
    /// Loads `<root>/index.json`.
    pub fn load(root: &Path) -> Result<Index, LoaderError> {
        let path = root.join("index.json");
        let bytes = read_file(&path)?;
        let value = parse_meta_value(&bytes, &path)?;

        let raw_entries = value
            .get("directories")
            .and_then(Value::as_array)
            .ok_or_else(|| LoaderError::MalformedIndex {
                path: path.clone(),
                problem: "missing a `directories` array".to_string(),
            })?;

        let mut directories = Vec::with_capacity(raw_entries.len());
        for raw in raw_entries {
            directories.push(parse_index_entry(raw, &path)?);
        }
        Ok(Index { directories, path })
    }

    /// Loads from [`conformance_dir`].
    pub fn load_default() -> Result<Index, LoaderError> {
        Self::load(&conformance_dir())
    }

    fn entry(&self, name: &str) -> Option<&IndexEntry> {
        self.directories.iter().find(|e| e.name == name)
    }

    /// Fails when the index disagrees with the corpus on disk (R6.45).
    ///
    /// Four disagreements are checked, and every one found is reported
    /// together:
    ///
    /// 1. **Membership, both ways.** Every top-level directory on disk is
    ///    named in the index, and every name in the index is a directory on
    ///    disk.
    /// 2. **Counts.** A family's `count` equals the number of vectors
    ///    actually present — subdirectories for `shape` `"directory"`,
    ///    `"envelope"` and `"merkle"`, `input-*.json` files for `"file-pairs"`.
    /// 3. **Profiles.** Every vector's declared `profile` is one the
    ///    family's `profiles` list permits, and every profile the index
    ///    lists is one this runner recognizes. A `"file-pairs"` family has
    ///    no `meta.json` to read, so its implicit `rfc8785` profile
    ///    (`conformance/README.md`) is what must be listed.
    /// 4. **Enumeration.** Every family the index declares is one
    ///    [`Corpus::load`] actually loads, and vice versa. This is the
    ///    defect R6.45 was written for: an unenumerated family contributes
    ///    no assurance while looking exactly like one that does.
    pub fn check_against_disk(&self, root: &Path) -> Result<(), LoaderError> {
        let mut problems = Vec::new();

        let mut on_disk: Vec<String> = Vec::new();
        for path in list_dir_sorted(root)? {
            if !path.is_dir() {
                continue;
            }
            let name = path
                .file_name()
                .and_then(|n| n.to_str())
                .ok_or(LoaderError::NotUtf8 { path: path.clone() })?;
            // Tool and VCS directories are not corpus content and are not
            // what R6.45 is counting.
            if name.starts_with('.') {
                continue;
            }
            on_disk.push(name.to_string());
        }

        for name in &on_disk {
            if self.entry(name).is_none() {
                problems.push(format!(
                    "`{name}/` exists on disk but is not named in the index"
                ));
            }
        }
        for entry in &self.directories {
            if !on_disk.iter().any(|name| name == &entry.name) {
                problems.push(format!(
                    "the index names `{}`, which is not a directory under {}",
                    entry.name,
                    root.display()
                ));
            }
        }

        for entry in &self.directories {
            if !entry.family {
                continue;
            }
            if !LOADED_FAMILIES.contains(&entry.name.as_str()) {
                problems.push(format!(
                    "`{}` is declared a vector family but this runner does not \
                     enumerate it (see LOADED_FAMILIES in src/conformance.rs); a \
                     family no runner loads contributes no assurance",
                    entry.name
                ));
            }
            let dir = root.join(&entry.name);
            if !dir.is_dir() {
                // Already reported above; nothing further can be checked.
                continue;
            }
            self.check_family(entry, &dir, &mut problems)?;
        }

        for family in LOADED_FAMILIES {
            match self.entry(family) {
                Some(entry) if entry.family => {}
                Some(_) => problems.push(format!(
                    "this runner loads `{family}` as a vector family, but the \
                     index says it is not one"
                )),
                None => problems.push(format!(
                    "this runner loads `{family}`, which the index does not name"
                )),
            }
        }

        if problems.is_empty() {
            Ok(())
        } else {
            Err(LoaderError::IndexMismatch {
                path: self.path.clone(),
                problems,
            })
        }
    }

    fn check_family(
        &self,
        entry: &IndexEntry,
        dir: &Path,
        problems: &mut Vec<String>,
    ) -> Result<(), LoaderError> {
        let name = &entry.name;

        for profile in &entry.profiles {
            if Profile::parse(profile, &self.path).is_err() {
                problems.push(format!(
                    "`{name}` lists profile `{profile}`, which this runner does \
                     not recognize"
                ));
            }
        }

        let shape = entry.shape.as_deref().unwrap_or_default();
        let actual = match shape {
            "directory" | "envelope" | "merkle" => {
                let cases: Vec<PathBuf> = list_dir_sorted(dir)?
                    .into_iter()
                    .filter(|p| p.is_dir())
                    .collect();
                for case in &cases {
                    let meta = load_meta(&case.join("meta.json"))?;
                    if !entry.profiles.contains(&meta.profile) {
                        problems.push(format!(
                            "`{}/{}` declares profile `{}`, which `{name}`'s index \
                             entry does not permit ({:?})",
                            name,
                            case.file_name().and_then(|n| n.to_str()).unwrap_or("?"),
                            meta.profile,
                            entry.profiles
                        ));
                    }
                }
                cases.len()
            }
            "file-pairs" => {
                // No `meta.json` exists in this shape: `conformance/README.md`
                // says the family "carries the rfc8785 profile implicitly",
                // so the index must say so too or the two disagree about what
                // the vendored pairs test.
                if !entry.profiles.iter().any(|p| p == "rfc8785") {
                    problems.push(format!(
                        "`{name}` has shape `file-pairs`, whose vectors carry the \
                         `rfc8785` profile implicitly, but the index does not list \
                         it ({:?})",
                        entry.profiles
                    ));
                }
                list_dir_sorted(dir)?
                    .into_iter()
                    .filter(|p| {
                        p.file_name()
                            .and_then(|n| n.to_str())
                            .is_some_and(|n| n.starts_with("input-") && n.ends_with(".json"))
                    })
                    .count()
            }
            other => {
                problems.push(format!(
                    "`{name}` declares unknown shape `{other}` (expected one of: \
                     directory, file-pairs, envelope, merkle)"
                ));
                return Ok(());
            }
        };

        match entry.count {
            Some(count) if count == actual => {}
            Some(count) => problems.push(format!(
                "`{name}` says count {count}, but {actual} vectors are on disk"
            )),
            None => problems.push(format!("`{name}` is a family but states no count")),
        }
        Ok(())
    }
}

fn parse_index_entry(raw: &Value, path: &Path) -> Result<IndexEntry, LoaderError> {
    let malformed = |problem: String| LoaderError::MalformedIndex {
        path: path.to_path_buf(),
        problem,
    };

    let name = raw
        .get("name")
        .and_then(Value::as_str)
        .ok_or_else(|| malformed("a `directories` entry has no string `name`".to_string()))?
        .to_string();
    let family = raw
        .get("family")
        .and_then(Value::as_bool)
        .ok_or_else(|| malformed(format!("`{name}` has no boolean `family`")))?;

    let shape = raw
        .get("shape")
        .and_then(Value::as_str)
        .map(|s| s.to_string());
    let count = raw.get("count").and_then(Value::as_u64).map(|n| n as usize);
    let note = raw
        .get("note")
        .and_then(Value::as_str)
        .map(|s| s.to_string());
    let profiles = match raw.get("profiles") {
        None => Vec::new(),
        Some(value) => {
            let array = value
                .as_array()
                .ok_or_else(|| malformed(format!("`{name}`'s `profiles` is not an array")))?;
            let mut profiles = Vec::with_capacity(array.len());
            for item in array {
                profiles.push(
                    item.as_str()
                        .ok_or_else(|| {
                            malformed(format!("`{name}` has a non-string entry in `profiles`"))
                        })?
                        .to_string(),
                );
            }
            profiles
        }
    };

    // A family entry must be complete enough to be checkable at all. A
    // missing `shape` or `count` here would otherwise degrade the R6.45
    // check into a no-op for that family, which is the failure mode the
    // requirement exists to prevent.
    if family {
        if shape.is_none() {
            return Err(malformed(format!(
                "`{name}` is a family but states no shape"
            )));
        }
        if count.is_none() {
            return Err(malformed(format!(
                "`{name}` is a family but states no count"
            )));
        }
        if profiles.is_empty() {
            return Err(malformed(format!(
                "`{name}` is a family but lists no profiles"
            )));
        }
    }

    Ok(IndexEntry {
        name,
        family,
        shape,
        profiles,
        count,
        note,
    })
}
