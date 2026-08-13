//! `curia-testis` — an offline, independent verifier for signed Cūria post
//! envelopes.
//!
//! This crate is **verify-only**: it SHALL NOT provide a signing function
//! (a verifier able to sign is a verifier that must be trusted with keys),
//! and it is **offline**: nothing it does at runtime touches the network.
//! Malformed, adversarial, or truncated input produces a typed [`Result`]
//! error, never a panic.
//!
//! ## Status (post Task 6)
//!
//! Every function below that does real work — [`canonicalize`],
//! [`canonicalize_with_nfc`], [`admit`], [`sha256_digest`],
//! [`verify_envelope`] — is implemented, and `tests/vectors.rs` runs the
//! full published conformance corpus against it. [`verify_envelope`]
//! (Task 6) is where the others are assembled end to end: it is the
//! function `curia-testis verify --envelope <path> --jwks <path>`
//! (`src/bin/curia-testis.rs`) calls, and the surface R6.19's offline
//! authorship claim is about.
//!
//! The [`conformance`] module is the vector loader; [`envelope`] holds
//! [`verify_envelope`] and its error type.

#![forbid(unsafe_code)]

pub mod canonical;
pub mod conformance;
pub mod digest;
pub mod envelope;
pub mod json;
pub mod jwk;
pub mod jws;
pub mod nfc;

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
/// Errata D1 (revised R6.9). Implemented in Task 3; see
/// [`nfc::canonicalize_with_nfc`] for the algorithm and its derivation.
pub use nfc::canonicalize_with_nfc;

/// The ADMIT phase: reject-or-pass, no repair. Errata D5 (numeric bounds),
/// D6 (depth-counting convention), D7 (four rejection classes R6.15's
/// original enumeration omits). See [`json::admit`] for the algorithm and
/// the derivation of every `curia/admit/...` slug from
/// `conformance/admit-reject/`.
///
/// The crate-level surface stays `Result<(), AdmitError>`: no
/// `admit-reject/` vector expects acceptance, so callers of this function
/// (in particular `tests/vectors.rs`'s `check_admit`) only ever need the
/// `Err` path. [`json::admit`]'s own success value (the parsed document) is
/// discarded here rather than threaded through, so that later tasks
/// building on top of ADMIT are free to call `json::admit` directly for the
/// value when they need it, without this function's shape constraining
/// them. [`envelope::verify_envelope`] is exactly such a caller: it calls
/// [`json::admit`] directly (Task 6, Ruling #1) rather than through this
/// crate-level wrapper, because it needs the admitted [`json::Value`], not
/// just the accept/reject outcome.
pub fn admit(input: &[u8]) -> Result<(), json::AdmitError> {
    json::admit(input).map(|_| ())
}

/// `Digests.Sha256` — the lowercase-hex SHA-256 digest of canonical bytes, in
/// the 64-lowercase-hex-character, no-prefix form the corpus's
/// `expected.digest` files use (errata D9.6). Implemented in Task 5; see
/// [`digest::sha256_digest`] for the algorithm. `Err` is unreachable
/// (`std::convert::Infallible`) — hashing a byte slice has no rejection
/// condition — but the `Result` shape is kept so existing callers of this
/// function do not need to change.
pub use digest::sha256_digest;

/// The provenance summary `curia-testis verify` prints on success.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Provenance {
    pub author: String,
    pub kid: String,
    pub alg: String,
    /// `sha256:<hex>` — the envelope digest, in the form the CLI prints it.
    pub digest: String,
}

/// Verifies one signed envelope end to end: ADMIT the submission,
/// canonicalize its `envelope` sub-object, digest it, and verify the
/// detached JWS in `submission` over that digest against a key in `jwks`.
/// This is `CanonicalizeEnvelope` + `Digests.Sha256` + `DetachedJws.Verify`
/// from `conformance/README.md`'s function table, run behind ADMIT
/// (Task 6, Ruling #1 — see [`envelope`]'s module doc comment) — the whole
/// surface `curia-testis verify` exists to exercise offline.
///
/// `submission` is the full `{"envelope": ..., "signature": ...}` wire
/// object (§6.2, Appendix C.3). `jwks` is a standard JWKS (`{"keys": [...]}`)
/// of public keys. See [`envelope::verify_envelope`] for the implementation
/// and [`envelope::VerifyEnvelopeError`] for every way this can fail, named
/// for the specific check that failed.
pub use envelope::verify_envelope;
