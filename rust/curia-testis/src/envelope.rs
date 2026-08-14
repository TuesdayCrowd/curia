//! `verify_envelope`: canonicalize an envelope, digest it, and verify its
//! detached JWS — end to end, offline. This is where Tasks 2–5's pieces
//! (`canonicalize`/`canonicalize_with_nfc`, `admit`, `sha256_digest`,
//! `jwk::JwkSet::parse`, `jws::verify`) are assembled into the one pipeline
//! `conformance/README.md`'s `envelope` profile pins
//! (`CanonicalizeEnvelope` + `Digests.Sha256` + `DetachedJws.Verify`), and
//! the whole surface `curia-testis verify --envelope <path> --jwks <path>`
//! exists to exercise offline — R6.19's "confirm authorship without
//! executing Forum-supplied code and without trusting Forum-supplied
//! results."
//!
//! ## Ruling #1: ADMIT runs on the submission bytes, before anything else
//!
//! [`verify_envelope`] calls [`json::admit`] on `submission` first, full
//! stop — before the bytes are parsed for any other purpose, before the
//! `envelope` sub-object is extracted, before canonicalization or digesting
//! or signature verification ever run. The verifier's input is
//! Forum-supplied and therefore untrusted (R6.19); §6.4 places ADMIT as the
//! reject-never-repair gate that produces the document VERIFY consumes.
//! `conformance/README.md`'s profile table lists the `envelope` profile as
//! "canonicalize + digest + verify" — that names the three functions whose
//! *output* the corpus's `expected.canonical`/`expected.digest`/
//! `expect-verify-failure` files pin, not license to skip ADMIT in the
//! pipeline that produces the bytes those functions consume. The pure
//! functions (`canonicalize`, `canonicalize_with_nfc` called directly, as
//! `tests/vectors.rs`'s `c4`/`ordering`/`unicode`/`numbers`/`envelope`
//! canonicalization checks still do) stay free of ADMIT, exactly as before
//! this task; only this end-to-end entry point runs it.
//!
//! ## Ruling #2 (reported, not resolved by the spec): the depth cap applies
//! to the submission as parsed, wrapper included
//!
//! ADMIT's frozen depth cap ([`json::ADMIT_MAX_DEPTH`], 32, counting
//! container *openings* only — errata D6) is defined over "the document."
//! Here "the document" ADMIT actually receives is the wire submission,
//! `{"envelope": ..., "signature": ...}` — not the bare `envelope`
//! sub-object `conformance/admit-reject/over-nested` and
//! `tests/admit_boundaries.rs` pin the depth boundary against. The wrapper
//! object is itself one container opening, so an envelope whose own
//! innermost value sits inside exactly 32 containers — accepted when ADMIT
//! is run against that envelope alone, per `depth_exactly_32_is_accepted_*`
//! in `tests/admit_boundaries.rs` — is **rejected** once wrapped in a
//! submission, because the wrapper spends one level of the shared 32-level
//! budget and the envelope only has 31 left. See
//! `depth_budget_is_shared_with_the_submission_wrapper` below for this
//! traced concretely.
//!
//! Neither `spec/` nor `conformance/` settles whether the cap is meant to
//! apply per-envelope or per-submission — every `admit-reject/` vector pins
//! a bare document, never a submission-wrapped one, and no envelope fixture
//! sits anywhere near the depth boundary. This crate applies the cap to the
//! submission exactly as received (no unwrapping before ADMIT runs), per
//! the Task 6 dispatch's explicit ruling on this question. Separately from
//! the ruling, I think that is also the more defensible reading on the
//! merits: ADMIT's stated rationale (design spec §5.1 — "bounds the sort in
//! canonicalization," "bounds NFC normalization cost") is about bounding
//! the cost of parsing and walking whatever bytes actually arrive on the
//! wire, and what arrives on the wire is the submission, wrapper included.
//! A verifier that stripped the wrapper *before* admitting would be
//! applying the cost bound to a document it had already partially
//! processed and reconstructed itself, not to the bytes it actually
//! received — closer to "repair, then verify" than ADMIT's "reject, never
//! repair" contract allows. The practical cost of this reading is one
//! level of nesting budget (32 becomes an effective 31 for the envelope
//! itself); I judged that acceptable and did not see a principled way to
//! special-case the wrapper without ADMIT needing to know something about
//! Table 9's schema, which no other part of `json::admit` does today.
//! See `task/task-6-report.md` for this same discussion, restated for the
//! report contract.

use crate::json::{self, Value};
use crate::jwk::{JwkError, JwkSet};
use crate::jws::{self, JwsError};
use crate::nfc::{self, NfcError};
use crate::{canonicalize_with_nfc, sha256_digest, Provenance};

/// Every way [`verify_envelope`] can fail to establish provenance, named for
/// the specific check that failed. The CLI contract (Task 1, unchanged by
/// this task) requires naming the failing predicate on stderr, not merely
/// reporting that verification failed — [`VerifyEnvelopeError::predicate`]
/// is what makes that possible.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum VerifyEnvelopeError {
    /// ADMIT rejected the raw submission bytes (Ruling #1: this runs before
    /// anything else touches them). Wraps [`json::AdmitError`] verbatim, so
    /// its `curia/admit/...` slug and message pass through unchanged —
    /// a caller sees the exact same predicate `curia_testis::admit` would
    /// report for the same defect.
    Admit(json::AdmitError),
    /// The admitted submission's top-level JSON value is not an object.
    /// Table 9 / Appendix C.3's wire shape is always
    /// `{"envelope": ..., "signature": ...}`.
    SubmissionNotObject,
    /// The submission object has no `envelope` member, or no `signature`
    /// member.
    MissingSubmissionField(&'static str),
    /// `signature` is present but is not a JSON string.
    SignatureNotString,
    /// `envelope` is present but is not a JSON object.
    EnvelopeNotObject,
    /// The envelope object has no `author` member, or `author` is present
    /// but is not a JSON string. Table 9 requires `author`; nothing
    /// upstream of this check (ADMIT, JSON syntax) knows about Table 9's
    /// schema, so this is where a missing/malformed `author` is first
    /// noticed.
    AuthorMissing,
    /// [`canonicalize_with_nfc`] rejected the envelope sub-object
    /// (RFC 8785 combined with NFC, errata D1 / R6.9) — either a parse
    /// failure of the re-encoded envelope bytes (should not happen; see
    /// [`crate::nfc::encode`]'s losslessness guarantee — ADMIT has already
    /// parsed these bytes once successfully) or a duplicate member name
    /// that only collides after NFC normalization (a raw duplicate is
    /// impossible here: ADMIT's own recursive duplicate-key check already
    /// covers the whole submission tree, `envelope` included, before this
    /// ever runs).
    Canonicalize(NfcError),
    /// `--jwks` bytes did not parse into a usable key set.
    Jwks(JwkError),
    /// The detached JWS over the canonicalized envelope did not verify.
    Jws(JwsError),
}

impl VerifyEnvelopeError {
    /// The stable predicate slug the CLI reports on stderr. Delegates
    /// verbatim to the inner error's own predicate wherever one exists
    /// (`Admit`, `Canonicalize`, `Jwks`, `Jws`), so a caller sees the exact
    /// same slug this crate reports for the same defect at any other entry
    /// point (`curia_testis::admit`, `canonicalize_with_nfc`,
    /// `JwkSet::parse`, `jws::verify`, called directly). The remaining
    /// variants are submission-shape defects that ADMIT itself has no
    /// opinion about — Table 9's envelope schema is not something
    /// `json::admit` knows anything about — and get their own slugs in a
    /// `curia/envelope/...` namespace, matching every other layer's
    /// `curia/<layer>/<condition>` convention.
    pub fn predicate(&self) -> &str {
        match self {
            VerifyEnvelopeError::Admit(e) => e.predicate(),
            VerifyEnvelopeError::SubmissionNotObject => "curia/envelope/submission-not-object",
            VerifyEnvelopeError::MissingSubmissionField(field) => match *field {
                "envelope" => "curia/envelope/missing-envelope",
                "signature" => "curia/envelope/missing-signature",
                _ => "curia/envelope/missing-field",
            },
            VerifyEnvelopeError::SignatureNotString => "curia/envelope/signature-not-string",
            VerifyEnvelopeError::EnvelopeNotObject => "curia/envelope/envelope-not-object",
            VerifyEnvelopeError::AuthorMissing => "curia/envelope/author-missing",
            VerifyEnvelopeError::Canonicalize(e) => e.predicate(),
            VerifyEnvelopeError::Jwks(e) => e.predicate(),
            VerifyEnvelopeError::Jws(e) => e.predicate(),
        }
    }
}

impl std::fmt::Display for VerifyEnvelopeError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            VerifyEnvelopeError::Admit(e) => write!(f, "{e}"),
            VerifyEnvelopeError::SubmissionNotObject => {
                write!(
                    f,
                    "{}: submission top-level value is not a JSON object",
                    self.predicate()
                )
            }
            VerifyEnvelopeError::MissingSubmissionField(field) => {
                write!(
                    f,
                    "{}: submission has no `{field}` member",
                    self.predicate()
                )
            }
            VerifyEnvelopeError::SignatureNotString => {
                write!(
                    f,
                    "{}: `signature` is present but is not a JSON string",
                    self.predicate()
                )
            }
            VerifyEnvelopeError::EnvelopeNotObject => {
                write!(
                    f,
                    "{}: `envelope` is present but is not a JSON object",
                    self.predicate()
                )
            }
            VerifyEnvelopeError::AuthorMissing => {
                write!(
                    f,
                    "{}: envelope has no `author` member, or it is not a string",
                    self.predicate()
                )
            }
            VerifyEnvelopeError::Canonicalize(e) => write!(f, "{e}"),
            VerifyEnvelopeError::Jwks(e) => write!(f, "{e}"),
            VerifyEnvelopeError::Jws(e) => write!(f, "{e}"),
        }
    }
}

impl std::error::Error for VerifyEnvelopeError {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        match self {
            VerifyEnvelopeError::Admit(e) => Some(e),
            VerifyEnvelopeError::Canonicalize(e) => Some(e),
            VerifyEnvelopeError::Jwks(e) => Some(e),
            VerifyEnvelopeError::Jws(e) => Some(e),
            VerifyEnvelopeError::SubmissionNotObject
            | VerifyEnvelopeError::MissingSubmissionField(_)
            | VerifyEnvelopeError::SignatureNotString
            | VerifyEnvelopeError::EnvelopeNotObject
            | VerifyEnvelopeError::AuthorMissing => None,
        }
    }
}

/// Looks up `key` in an already-ADMITted object's members. ADMIT's own
/// recursive duplicate-key check has already run over the whole submission
/// tree by the time this is ever called (see [`verify_envelope`]), so there
/// is at most one occurrence of `key` to find — this is a plain lookup, not
/// a first-vs-last tie-break.
fn find<'a>(members: &'a [(String, Value)], key: &str) -> Option<&'a Value> {
    members.iter().find(|(k, _)| k == key).map(|(_, v)| v)
}

/// Verifies one signed envelope end to end: ADMIT the submission, extract
/// and canonicalize its `envelope` sub-object, digest the canonical bytes,
/// and verify the detached JWS in `signature` over that digest against a
/// key in `jwks`. See the module doc comment for the two rulings this
/// function's structure follows.
///
/// `submission` is the full `{"envelope": ..., "signature": ...}` wire
/// object (§6.2, Appendix C.3). `jwks` is a standard JWKS
/// (`{"keys": [...]}`) of public keys. Neither argument is mutated,
/// repaired, or trusted before ADMIT and the cryptographic checks below
/// have run over it — CLAUDE.md's "no mutation between verify and persist,"
/// applied here as "no repair before verify."
pub fn verify_envelope(submission: &[u8], jwks: &[u8]) -> Result<Provenance, VerifyEnvelopeError> {
    // Ruling #1: ADMIT runs first, over the whole submission, before
    // anything else touches it.
    let admitted = json::admit(submission).map_err(VerifyEnvelopeError::Admit)?;

    let root = match &admitted {
        Value::Object(members) => members,
        _ => return Err(VerifyEnvelopeError::SubmissionNotObject),
    };

    let envelope_value =
        find(root, "envelope").ok_or(VerifyEnvelopeError::MissingSubmissionField("envelope"))?;
    let signature_value =
        find(root, "signature").ok_or(VerifyEnvelopeError::MissingSubmissionField("signature"))?;

    let signature = match signature_value {
        Value::String(s) => s.as_str(),
        _ => return Err(VerifyEnvelopeError::SignatureNotString),
    };

    let envelope_members = match envelope_value {
        Value::Object(members) => members,
        _ => return Err(VerifyEnvelopeError::EnvelopeNotObject),
    };

    let author = match find(envelope_members, "author") {
        Some(Value::String(s)) => s.clone(),
        _ => return Err(VerifyEnvelopeError::AuthorMissing),
    };

    // Re-encode the (already ADMITted) envelope sub-object losslessly back
    // to bytes, so `canonicalize_with_nfc` — which, like every canonicalizer
    // in this crate, takes `&[u8]` and does its own parse rather than
    // accepting an in-memory `Value` that skipped it — sees the envelope
    // exactly as ADMIT admitted it. `crate::nfc::encode` is the same
    // lossless re-encoder `canonicalize_with_nfc` uses on its own first
    // pass; reusing it here means there is exactly one place in this crate
    // that answers "how do I turn a parsed `Value` back into
    // round-trippable JSON bytes," not a second copy that could drift.
    let mut envelope_bytes = Vec::new();
    nfc::encode(envelope_value, &mut envelope_bytes);

    let canonical =
        canonicalize_with_nfc(&envelope_bytes).map_err(VerifyEnvelopeError::Canonicalize)?;

    // Infallible: hashing a byte slice has no rejection condition (see
    // `crate::digest`'s module doc comment) — `std::convert::Infallible`
    // cannot be constructed, so this is a total match, not an escape hatch.
    let digest = sha256_digest(&canonical).unwrap_or_else(|e| match e {});

    let jwk_set = JwkSet::parse(jwks).map_err(VerifyEnvelopeError::Jwks)?;

    let verified =
        jws::verify(signature, &canonical, &jwk_set).map_err(VerifyEnvelopeError::Jws)?;

    Ok(Provenance {
        author,
        kid: verified.kid,
        alg: verified.alg,
        digest: format!("sha256:{digest}"),
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::conformance::conformance_dir;

    fn load(case: &str, file: &str) -> Vec<u8> {
        std::fs::read(conformance_dir().join("envelope").join(case).join(file))
            .unwrap_or_else(|e| panic!("failed to read {case}/{file}: {e}"))
    }

    // -----------------------------------------------------------------
    // Step 1 / Step 4: every envelope/ fixture, driven straight through
    // `verify_envelope` — the same function `tests/vectors.rs`'s `envelope`
    // test and the CLI both call.
    // -----------------------------------------------------------------

    #[test]
    fn positive_fixtures_verify_end_to_end() {
        for (case, expected_author, expected_kid, expected_alg) in [
            (
                "ed25519-minimal",
                "agent://curia.example/tuesdaycrowd/scriptor",
                "conformance-ed25519-minimal",
                "EdDSA",
            ),
            (
                "ed25519-full",
                "agent://curia.example/tuesdaycrowd/scriptor",
                "conformance-ed25519-full",
                "EdDSA",
            ),
            (
                "ed25519-unicode",
                "agent://curia.example/tuesdaycrowd/scriptor",
                "conformance-ed25519-unicode",
                "EdDSA",
            ),
            (
                "es256-minimal",
                "agent://curia.example/tuesdaycrowd/scriptor",
                "conformance-es256-minimal",
                "ES256",
            ),
        ] {
            let submission = load(case, "submission.json");
            let jwks = load(case, "jwks.json");
            let expected_digest = String::from_utf8(load(case, "expected.digest")).unwrap();

            let provenance = verify_envelope(&submission, &jwks)
                .unwrap_or_else(|e| panic!("{case} expected to verify, got {e}"));

            assert_eq!(provenance.author, expected_author, "{case} author");
            assert_eq!(provenance.kid, expected_kid, "{case} kid");
            assert_eq!(provenance.alg, expected_alg, "{case} alg");
            assert_eq!(
                provenance.digest,
                format!("sha256:{expected_digest}"),
                "{case} digest"
            );
        }
    }

    #[test]
    fn tampered_body_fails_with_signature_invalid() {
        let submission = load("tampered-body", "submission.json");
        let jwks = load("tampered-body", "jwks.json");
        let err = verify_envelope(&submission, &jwks).unwrap_err();
        assert_eq!(err.predicate(), "curia/jws/signature-invalid");
    }

    #[test]
    fn wrong_key_fails_with_signature_invalid() {
        let submission = load("wrong-key", "submission.json");
        let jwks = load("wrong-key", "jwks.json");
        let err = verify_envelope(&submission, &jwks).unwrap_err();
        assert_eq!(err.predicate(), "curia/jws/signature-invalid");
    }

    // -----------------------------------------------------------------
    // Ruling #1: ADMIT runs before anything else touches the submission.
    // -----------------------------------------------------------------

    #[test]
    fn a_raw_nul_byte_is_rejected_by_admit_before_anything_else() {
        // A syntactically-plausible-looking submission carrying a raw NUL
        // byte: ADMIT rejects this before JSON parsing for any other
        // purpose, before the envelope is ever extracted, and long before
        // any cryptographic material is examined.
        let mut submission = br#"{"envelope":{"author":"x"},"signature":"a.."}"#.to_vec();
        submission.push(0x00);
        let jwks = load("ed25519-minimal", "jwks.json");
        let err = verify_envelope(&submission, &jwks).unwrap_err();
        assert_eq!(err.predicate(), "curia/admit/nul-byte");
    }

    #[test]
    fn a_duplicate_top_level_submission_key_is_rejected_by_admit() {
        let submission =
            br#"{"envelope":{"author":"x"},"signature":"a..","signature":"b.."}"#.to_vec();
        let jwks = load("ed25519-minimal", "jwks.json");
        let err = verify_envelope(&submission, &jwks).unwrap_err();
        assert_eq!(err.predicate(), "curia/admit/duplicate-key");
    }

    #[test]
    fn an_oversized_submission_is_rejected_by_admit() {
        // Bigger than ADMIT_MAX_SUBMISSION_BYTES; must be rejected as
        // size-exceeded, not read further.
        let padding = "x".repeat(json::ADMIT_MAX_SUBMISSION_BYTES + 1);
        let submission = format!(r#"{{"envelope":{{"author":"{padding}"}},"signature":"a.."}}"#);
        let jwks = load("ed25519-minimal", "jwks.json");
        let err = verify_envelope(submission.as_bytes(), &jwks).unwrap_err();
        assert_eq!(err.predicate(), "curia/admit/size-exceeded");
    }

    // -----------------------------------------------------------------
    // Ruling #2: the depth budget is shared with the submission wrapper.
    // -----------------------------------------------------------------

    /// Builds `depth` nested `{"a": ...}` container-openings around a
    /// scalar leaf, e.g. `depth=2` -> `{"a":{"a":0}}`.
    fn nested_object(depth: usize) -> String {
        let mut s = String::new();
        for _ in 0..depth {
            s.push_str(r#"{"a":"#);
        }
        s.push('0');
        for _ in 0..depth {
            s.push('}');
        }
        s
    }

    #[test]
    fn depth_budget_is_shared_with_the_submission_wrapper() {
        // An envelope nested exactly 32 container-openings deep is, on its
        // own, exactly at ADMIT's cap and therefore accepted (pinned
        // independently by tests/admit_boundaries.rs's
        // `depth_exactly_32_is_accepted_objects`).
        let bare_envelope = nested_object(json::ADMIT_MAX_DEPTH);
        assert!(
            json::admit(bare_envelope.as_bytes()).is_ok(),
            "an envelope nested exactly to the cap must be admitted on its own"
        );

        // The same envelope, wrapped in a submission object, now sits one
        // level deeper than the wrapper alone would suggest: the wrapper
        // object is itself a container opening, so the total is 33, one
        // past the cap.
        let wrapped = format!(r#"{{"envelope":{bare_envelope},"signature":"a.."}}"#);
        let jwks = load("ed25519-minimal", "jwks.json");
        let err = verify_envelope(wrapped.as_bytes(), &jwks).unwrap_err();
        assert_eq!(
            err.predicate(),
            "curia/admit/depth-exceeded",
            "an envelope at the bare-document depth cap must be rejected once wrapped in a \
             submission, because ADMIT runs on the submission as parsed (Ruling #1/#2), and \
             the wrapper object spends one level of the shared 32-level budget"
        );

        // One level shallower, the same envelope fits within the shared
        // budget once wrapped (31 for the envelope + 1 for the wrapper =
        // 32 total).
        let bare_envelope_31 = nested_object(json::ADMIT_MAX_DEPTH - 1);
        let wrapped_31 = format!(r#"{{"envelope":{bare_envelope_31},"signature":"a.."}}"#);
        assert!(
            json::admit(wrapped_31.as_bytes()).is_ok(),
            "an envelope nested one level shallower than the bare-document cap must still be \
             admitted once wrapped, since the wrapper's one extra level exactly consumes the \
             remaining budget"
        );
    }

    // -----------------------------------------------------------------
    // Submission-shape defects: ADMIT has no opinion on Table 9's schema.
    // -----------------------------------------------------------------

    #[test]
    fn submission_not_an_object_is_rejected() {
        let jwks = load("ed25519-minimal", "jwks.json");
        let err = verify_envelope(b"[1,2,3]", &jwks).unwrap_err();
        assert_eq!(err.predicate(), "curia/envelope/submission-not-object");
    }

    #[test]
    fn missing_envelope_field_is_rejected() {
        let jwks = load("ed25519-minimal", "jwks.json");
        let err = verify_envelope(br#"{"signature":"a.."}"#, &jwks).unwrap_err();
        assert_eq!(err.predicate(), "curia/envelope/missing-envelope");
    }

    #[test]
    fn missing_signature_field_is_rejected() {
        let jwks = load("ed25519-minimal", "jwks.json");
        let err = verify_envelope(br#"{"envelope":{"author":"x"}}"#, &jwks).unwrap_err();
        assert_eq!(err.predicate(), "curia/envelope/missing-signature");
    }

    #[test]
    fn signature_not_a_string_is_rejected() {
        let jwks = load("ed25519-minimal", "jwks.json");
        let err =
            verify_envelope(br#"{"envelope":{"author":"x"},"signature":1}"#, &jwks).unwrap_err();
        assert_eq!(err.predicate(), "curia/envelope/signature-not-string");
    }

    #[test]
    fn envelope_not_an_object_is_rejected() {
        let jwks = load("ed25519-minimal", "jwks.json");
        let err = verify_envelope(br#"{"envelope":"nope","signature":"a.."}"#, &jwks).unwrap_err();
        assert_eq!(err.predicate(), "curia/envelope/envelope-not-object");
    }

    #[test]
    fn missing_author_is_rejected() {
        let jwks = load("ed25519-minimal", "jwks.json");
        let err = verify_envelope(br#"{"envelope":{},"signature":"a.."}"#, &jwks).unwrap_err();
        assert_eq!(err.predicate(), "curia/envelope/author-missing");
    }

    #[test]
    fn author_not_a_string_is_rejected() {
        let jwks = load("ed25519-minimal", "jwks.json");
        let err =
            verify_envelope(br#"{"envelope":{"author":1},"signature":"a.."}"#, &jwks).unwrap_err();
        assert_eq!(err.predicate(), "curia/envelope/author-missing");
    }

    // -----------------------------------------------------------------
    // JWKS parsing failures surface through the same pipeline.
    // -----------------------------------------------------------------

    #[test]
    fn malformed_jwks_is_rejected() {
        let submission = load("ed25519-minimal", "submission.json");
        let err = verify_envelope(&submission, b"not json").unwrap_err();
        assert_eq!(err.predicate(), "curia/jws/jwks-malformed");
    }
}
