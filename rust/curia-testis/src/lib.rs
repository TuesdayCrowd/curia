//! `curia-testis` — an offline, independent verifier for signed Cūria post
//! envelopes.
//!
//! This crate is **verify-only**: it SHALL NOT provide a signing function
//! (a verifier able to sign is a verifier that must be trusted with keys),
//! and it is **offline**: nothing it does at runtime touches the network.
//! Malformed, adversarial, or truncated input produces a typed [`Result`]
//! error, never a panic.
//!
//! ## Task 1 status
//!
//! This is the crate scaffolding. Every function below that does real work
//! — [`canonicalize`], [`canonicalize_with_nfc`], [`admit`],
//! [`verify_envelope`] — is a stub that always returns
//! [`NotImplementedError`]. Tasks 2 through 6 replace these bodies one at a
//! time; `tests/vectors.rs` calls each of them against the published
//! conformance corpus and is expected to be red until then. See
//! `task/task-1-report.md` for the current baseline failure count.
//!
//! The [`conformance`] module — the vector loader — is the one piece of this
//! crate that Task 1 actually implements.

#![forbid(unsafe_code)]

use std::fmt;

pub mod canonical;
pub mod conformance;
pub mod json;

/// The predicate name `curia-testis` reports for any check it cannot yet
/// perform. This is deliberately outside the `curia/...` slug namespace real
/// verification predicates use (e.g. `curia/jws/signature-invalid`,
/// `curia/admit/duplicate-key`) — the corpus's `expect-reject` and
/// `expect-verify-failure` fixtures name those, and `curia-testis` is
/// expected to eventually reproduce that exact vocabulary (Tasks 4 and 5).
/// Until then, this predicate names the crate's own implementation status
/// instead of guessing at a real one.
pub const NOT_IMPLEMENTED_PREDICATE: &str = "curia-testis/not-implemented";

/// A stand-in for a check this crate does not yet perform.
///
/// Every stub function in this crate returns this error rather than
/// panicking (`todo!()` et al.), so that a test comparing its result against
/// an expected value fails on a genuine mismatch — `Err(NotImplementedError)
/// != Ok(expected)`, or a predicate string that does not match the
/// corpus-declared slug — rather than aborting the test binary.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct NotImplementedError {
    detail: String,
}

impl NotImplementedError {
    pub fn new(detail: impl Into<String>) -> Self {
        Self {
            detail: detail.into(),
        }
    }

    /// The failing predicate, in the same form a real verification failure
    /// would report it: a stable slug a caller can match on.
    pub fn predicate(&self) -> &str {
        NOT_IMPLEMENTED_PREDICATE
    }

    /// A human-readable explanation of what is missing and which task adds
    /// it.
    pub fn detail(&self) -> &str {
        &self.detail
    }
}

impl fmt::Display for NotImplementedError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}: {}", self.predicate(), self.detail)
    }
}

impl std::error::Error for NotImplementedError {}

/// `Canonicalize` — pure RFC 8785 canonicalization, performing **no**
/// Unicode normalization. Errata D1 (revised R6.8). Implemented in Task 2;
/// see [`canonical::canonicalize`] for the algorithm and its derivation.
///
/// `input` is taken by reference because a rejecting canonicalizer must
/// never need to have admitted, repaired, or otherwise mutated its input
/// first (see CLAUDE.md's "no mutation between verify and persist").
pub use canonical::canonicalize;

/// `CanonicalizeWithNfc` — NFC-normalizes every object member name and
/// string value, recursively, *then* canonicalizes with [`canonicalize`].
/// Errata D1 (revised R6.9). Implemented in Task 3.
pub fn canonicalize_with_nfc(input: &[u8]) -> Result<Vec<u8>, NotImplementedError> {
    let _ = input;
    Err(NotImplementedError::new(
        "CanonicalizeWithNfc (RFC 8785 + NFC, R6.9; the canonicalize-with-nfc \
         profile) is not implemented; see Task 3.",
    ))
}

/// The ADMIT phase: reject-or-pass, no repair. On success this would return
/// the admitted document in a form Task 2/3 can canonicalize; Task 1 does
/// not commit to that return type yet; `()` is a placeholder that later
/// tasks are free to change; note that no `admit` conformance vector expects
/// acceptance (`conformance/admit-reject/` — every case there is a
/// rejection), so `tests/vectors.rs` only exercises the `Err` path today.
/// Errata D5/D6/D7. Implemented in Task 4.
pub fn admit(input: &[u8]) -> Result<(), NotImplementedError> {
    let _ = input;
    Err(NotImplementedError::new(
        "The ADMIT phase (conformance/admit-reject/, the admit profile) is \
         not implemented; see Task 4.",
    ))
}

/// `Digests.Sha256` — the lowercase-hex SHA-256 digest of canonical bytes, in
/// the 64-lowercase-hex-character, no-prefix form the corpus's
/// `expected.digest` files use (errata D9.6). Implemented in Task 5, using
/// the `sha2` dependency pinned in `Cargo.toml`.
pub fn sha256_digest(canonical: &[u8]) -> Result<String, NotImplementedError> {
    let _ = canonical;
    Err(NotImplementedError::new(
        "SHA-256 digest of canonical bytes is not implemented; see Task 5.",
    ))
}

/// The provenance summary `curia-testis verify` prints on success.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Provenance {
    pub author: String,
    pub kid: String,
    pub alg: String,
    /// `sha256:<hex>` — the envelope digest, in the form the CLI prints it.
    pub digest: String,
}

/// Verifies one signed envelope end to end: canonicalize the envelope
/// sub-object, digest it, and verify the detached JWS in `submission` over
/// that digest against a key in `jwks`. This is `CanonicalizeEnvelope` +
/// `Digests.Sha256` + `DetachedJws.Verify` from `conformance/README.md`'s
/// function table, and the whole surface `curia-testis verify` exists to
/// exercise offline.
///
/// `submission` is the full `{"envelope": ..., "signature": ...}` wire
/// object (§6.2, Appendix C.3). `jwks` is a standard JWKS (`{"keys": [...]}`)
/// of public keys. Implemented across Tasks 2, 3, and 5; wired into the CLI
/// in Task 6.
pub fn verify_envelope(submission: &[u8], jwks: &[u8]) -> Result<Provenance, NotImplementedError> {
    let _ = (submission, jwks);
    Err(NotImplementedError::new(
        "End-to-end envelope verification (canonicalize + digest + detached \
         JWS; the envelope profile) is not implemented; see Tasks 2, 3, and 5.",
    ))
}
