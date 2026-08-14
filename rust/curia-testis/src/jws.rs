//! Detached JWS verification: the algorithm allow-list, protected-header
//! validation, and the signing-input construction that errata **D3**
//! corrects.
//!
//! ## D3 — the signing input, and why it is not what References [7] says
//!
//! The specification's own References entry for RFC 7515 says: "Appendix F
//! specifies detached content, the mode §6 depends on." That is a different
//! mechanism from the one the protected header actually invokes. RFC 7515
//! Appendix F still base64url-encodes the payload when computing the
//! signing input; only the wire serialization omits that segment. Under
//! RFC 7797 — which is what `b64: false` with `crit: ["b64"]` invokes — the
//! signing input contains the payload's **raw bytes, unencoded**. An
//! implementer who followed the References entry literally would compute
//!
//! ```text
//! ASCII(BASE64URL(header)) || "." || BASE64URL(canonical payload)
//! ```
//!
//! and every signature so produced verifies only against that same mistake
//! (D3: "well-formed, self-consistent, and verifies against that
//! implementation — and against no other"). This module instead builds
//! (errata's proposed **R6.37**):
//!
//! ```text
//! ASCII(BASE64URL(UTF8(protected header))) || 0x2E || <raw canonical bytes, unencoded>
//! ```
//!
//! `protected_b64` — the first compact-serialization segment — is already
//! `ASCII(BASE64URL(UTF8(protected header)))` verbatim, exactly as
//! transmitted, so [`build_signing_input`] does not re-encode the header at
//! all: it concatenates that segment's own ASCII bytes, a single `0x2E`
//! (`.`), and the caller-supplied canonical payload bytes, byte for byte.
//! Re-deriving the base64url encoding here (rather than reusing the wire
//! segment) would risk silently reintroducing exactly the encoding
//! ambiguity D3 is about; using the transmitted segment's own bytes makes
//! that impossible.
//!
//! ## Step 3 — the algorithm allow-list, and proving the ordering
//!
//! The allow-list is exactly `EdDSA` and `ES256`. [`verify`] checks `alg`
//! immediately after the protected header parses as a JSON object — before
//! `typ`/`b64`/`crit`/`kid` are even inspected, before the signature
//! segment is base64url-decoded, before any JWKS lookup, and therefore
//! before any cryptographic operation of any kind runs. See this module's
//! `tests::algorithm_rejected_before_key_lookup_or_signature_decode` for a
//! test constructed so that it would fail (by reporting the *wrong*
//! predicate) if that check were moved after either the key lookup or the
//! signature decode — not merely a test that a disallowed algorithm is
//! rejected.

use std::collections::HashSet;

use base64::engine::general_purpose::URL_SAFE_NO_PAD;
use base64::Engine;
use p256::ecdsa::signature::Verifier as _;

use crate::json::{self, Value};
use crate::jwk::{JwkError, JwkSet, PublicKey};

/// The two algorithms this crate ever verifies. Every other `alg` value —
/// `none`, every `HS*`, RSA variants, anything unrecognized — is rejected
/// in [`verify`] before it ever reaches this type.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Algorithm {
    EdDsa,
    Es256,
}

impl Algorithm {
    fn from_header_value(alg: &str) -> Option<Algorithm> {
        match alg {
            "EdDSA" => Some(Algorithm::EdDsa),
            "ES256" => Some(Algorithm::Es256),
            _ => None,
        }
    }
}

/// What a successful [`verify`] call establishes about the JWS it checked.
/// Carries exactly the fields `crate::Provenance` needs from the JWS layer
/// (`author` comes from the envelope itself, `digest` from
/// [`crate::digest::sha256_digest`] — both are Task 6's concern to
/// assemble, not this module's).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct VerifiedHeader {
    pub kid: String,
    pub alg: String,
}

/// Verifies a detached compact JWS (`b64: false`, `crit: ["b64"]`) over
/// `payload` — the raw canonical bytes of whatever was signed — against a
/// key in `jwks`.
///
/// `compact` is the full three-segment compact serialization, e.g.
/// `submission.json`'s `signature` field: `<protected>..<signature>`, with
/// an empty middle (payload) segment, per RFC 7797 / errata D3.
///
/// Every rejection is a typed [`JwsError`]; this function never panics on
/// malformed or adversarial input (CHARTER.md §2).
pub fn verify(compact: &str, payload: &[u8], jwks: &JwkSet) -> Result<VerifiedHeader, JwsError> {
    let (protected_b64, payload_segment, signature_b64) = split_compact(compact)?;

    if !payload_segment.is_empty() {
        return Err(JwsError::PayloadNotDetached);
    }

    let header_bytes = URL_SAFE_NO_PAD
        .decode(protected_b64)
        .map_err(|_| JwsError::ProtectedHeaderNotBase64)?;
    let header_value = json::parse(&header_bytes).map_err(|e| match e {
        // See the equivalent note in `jwk.rs`: `json::parse` now rejects
        // duplicate member names as a well-definedness violation, before
        // this module's own `reject_duplicate_members` walk runs. The
        // predicate must still name the duplicate, not a generic malformed
        // header — a duplicated `alg` is the case this slug exists for.
        json::ParseError::DuplicateMember { .. } => JwsError::ProtectedHeaderDuplicateMember,
        _ => JwsError::ProtectedHeaderMalformed,
    })?;
    // Fix round 1: reject a duplicate member name in the header (or in any
    // object nested inside it) before reading a single field out of it —
    // in particular before the `alg` extraction just below, so a
    // duplicated `alg` can never be resolved by "whichever occurrence the
    // parser or a later reader happens to prefer". See
    // `reject_duplicate_members`'s doc comment.
    reject_duplicate_members(&header_value)
        .map_err(|_| JwsError::ProtectedHeaderDuplicateMember)?;
    let header = as_object(&header_value).ok_or(JwsError::ProtectedHeaderMalformed)?;

    // Step 3: the algorithm gate. This runs first — before typ/b64/crit,
    // before kid, before the signature segment is even decoded, before any
    // JWKS lookup — so that no cryptographic operation, and nothing that
    // depends on cryptographic material, can run for a disallowed
    // algorithm. See the module doc comment and
    // `tests::algorithm_rejected_before_key_lookup_or_signature_decode`.
    //
    // `alg_str` and `algorithm` are bound together, here, at the one place
    // that establishes both: `alg_owned` cannot exist without `algorithm`
    // having already been confirmed valid from that same string, so the
    // `VerifiedHeader` constructed at the end of this function has no path
    // that reaches it without a checked `alg`. (Fix round 1: this replaces
    // an `Option::expect()` on a `String` clone of `alg_str` at the return
    // site, which review confirmed was provably unreachable but held that
    // invariant only in a comment, not in the type.)
    let alg_str = get_str(header, "alg");
    let (algorithm, alg_owned) = match alg_str
        .and_then(|s| Algorithm::from_header_value(s).map(|algorithm| (algorithm, s.to_string())))
    {
        Some(pair) => pair,
        None => {
            return Err(JwsError::AlgorithmNotAllowed {
                alg: alg_str.map(str::to_string),
            })
        }
    };

    // Step 2 (errata D3 / proposed R6.37): typ, b64, crit.
    match get_str(header, "typ") {
        Some("curia-post+jws") => {}
        _ => return Err(JwsError::TypInvalid),
    }
    match get_bool(header, "b64") {
        Some(false) => {}
        // Absent defaults to `true` under RFC 7797's own rule, and an
        // explicit `true` is rejected outright by R6.37 — both mean the
        // header does not establish the unencoded-payload signing input
        // this module requires, so both are rejected under one predicate.
        _ => return Err(JwsError::B64NotFalse),
    }
    let crit_ok = match get_array(header, "crit") {
        Some(items) => items.len() == 1 && matches!(&items[0], Value::String(s) if s == "b64"),
        None => false,
    };
    if !crit_ok {
        return Err(JwsError::CritInvalid);
    }

    let kid = get_str(header, "kid")
        .ok_or(JwsError::KidMissing)?
        .to_string();

    let signature_bytes = URL_SAFE_NO_PAD
        .decode(signature_b64)
        .map_err(|_| JwsError::SignatureNotBase64)?;

    let jwk = jwks
        .find_by_kid(&kid)
        .ok_or_else(|| JwsError::KeyNotFound { kid: kid.clone() })?;

    let signing_input = build_signing_input(protected_b64, payload);

    match (algorithm, &jwk.key) {
        (Algorithm::EdDsa, PublicKey::Ed25519(verifying_key)) => {
            verify_ed25519(verifying_key, &signature_bytes, &signing_input)?;
        }
        (Algorithm::Es256, PublicKey::EcdsaP256(verifying_key)) => {
            verify_es256(verifying_key, &signature_bytes, &signing_input)?;
        }
        _ => return Err(JwsError::KeyAlgorithmMismatch),
    }

    Ok(VerifiedHeader {
        kid,
        alg: alg_owned,
    })
}

/// Errata D3 / proposed R6.37: `ASCII(BASE64URL(UTF8(protected header)))`,
/// then `0x2E`, then the raw canonical payload bytes, unencoded.
/// `protected_b64` is reused verbatim from the compact serialization — see
/// the module doc comment for why this function does not re-encode it.
fn build_signing_input(protected_b64: &str, payload: &[u8]) -> Vec<u8> {
    let mut out = Vec::with_capacity(protected_b64.len() + 1 + payload.len());
    out.extend_from_slice(protected_b64.as_bytes());
    out.push(0x2E); // '.'
    out.extend_from_slice(payload);
    out
}

/// Splits a compact JWS into exactly three segments. Anything other than
/// exactly two `.` separators — no signature segment, an extra segment, a
/// missing dot — is `MalformedCompactSerialization`, checked here rather
/// than left to `str::split`'s `None`s to surface confusingly later.
fn split_compact(compact: &str) -> Result<(&str, &str, &str), JwsError> {
    let mut parts = compact.split('.');
    let protected = parts
        .next()
        .ok_or(JwsError::MalformedCompactSerialization)?;
    let payload = parts
        .next()
        .ok_or(JwsError::MalformedCompactSerialization)?;
    let signature = parts
        .next()
        .ok_or(JwsError::MalformedCompactSerialization)?;
    if parts.next().is_some() {
        return Err(JwsError::MalformedCompactSerialization);
    }
    Ok((protected, payload, signature))
}

/// RFC 8032 Ed25519 verification. Signatures are always the fixed 64-byte
/// `R || S` form; a signature that does not decode to exactly 64 bytes is
/// rejected as malformed before `ed25519_dalek` ever sees it.
///
/// Uses `verify_strict` rather than `verify`: `verify_strict` additionally
/// rejects non-canonical (malleable) `S` values and small-order public
/// keys (the "cofactored vs. cofactorless" hardening from Chalkias et al.,
/// which `ed25519-dalek` documents as the safer default for new protocols).
/// This is a strict superset of RFC 8032's minimum checks — it can only
/// reject signatures `verify` would accept, never the reverse — so it never
/// turns a conformant fixture green into red; see the report for why this
/// choice does not affect any published vector.
fn verify_ed25519(
    verifying_key: &ed25519_dalek::VerifyingKey,
    signature_bytes: &[u8],
    signing_input: &[u8],
) -> Result<(), JwsError> {
    let array: [u8; 64] = signature_bytes
        .try_into()
        .map_err(|_| JwsError::SignatureMalformed)?;
    let signature = ed25519_dalek::Signature::from_bytes(&array);
    verifying_key
        .verify_strict(signing_input, &signature)
        .map_err(|_| JwsError::SignatureInvalid)
}

/// **Step 4**: ES256 signatures are the fixed-width `R || S` form of
/// RFC 7518 §3.4 — 64 bytes (32-byte `R`, 32-byte `S`), never DER.
/// `p256::ecdsa::Signature::from_slice` (via the `ecdsa` crate this crate's
/// `p256` dependency re-exports) parses exactly that fixed-width
/// concatenation, not an ASN.1 DER `SEQUENCE { r, s }` — DER's tag/length
/// prefixes and variable-width `INTEGER` encoding mean a DER signature over
/// the same content is essentially never exactly 64 bytes, and even in the
/// improbable case that it were, `from_slice` still interprets the bytes as
/// raw `R || S`, not as DER, so it does not accidentally decode a DER
/// payload as if it were fixed-width. Either way, a DER signature is
/// rejected by this function, satisfying Step 4's requirement.
fn verify_es256(
    verifying_key: &p256::ecdsa::VerifyingKey,
    signature_bytes: &[u8],
    signing_input: &[u8],
) -> Result<(), JwsError> {
    if signature_bytes.len() != 64 {
        return Err(JwsError::SignatureMalformed);
    }
    let signature = p256::ecdsa::Signature::from_slice(signature_bytes)
        .map_err(|_| JwsError::SignatureMalformed)?;
    verifying_key
        .verify(signing_input, &signature)
        .map_err(|_| JwsError::SignatureInvalid)
}

fn as_object(value: &Value) -> Option<&[(String, Value)]> {
    match value {
        Value::Object(members) => Some(members.as_slice()),
        _ => None,
    }
}

/// **Fix round 1.** Rejects a header (or anything nested inside it, though
/// RFC 7515/7797 headers are flat in practice) that has two members with
/// the same name in the same object.
///
/// `crate::json::parse` deliberately preserves duplicates rather than
/// rejecting them (see its module doc comment — that is `admit`'s concern,
/// applied to the envelope). Resolving a duplicate here by "first
/// occurrence wins" was this module's original behavior; review flagged it
/// as the same hazard errata D7 names for the envelope, and the controller
/// ruled it out specifically because `alg` selects the algorithm:
/// `{"alg":"none","alg":"EdDSA",...}` must not verify under either a
/// first-wins or a last-wins reading — a duplicated `alg` (or `kid`, `typ`,
/// `b64`, `crit`) is rejected outright, before any of those fields is
/// extracted. See `crate::jwk`'s identical function for the JWKS side of
/// this same rule.
///
/// **Fix round 2.** Per object level, a single pass through `members`
/// inserting each name into a `HashSet`, rejecting on the first name
/// `insert` reports as already present — O(n) name comparisons per level
/// (amortized), not the O(n²) nested-loop scan this function started with.
/// A protected header only ever has a handful of members in this crate's
/// own vectors, but nothing about the header's *parsing* path bounds that
/// — this function runs on whatever `json::parse` returns from the
/// caller-supplied compact JWS, before any size or shape assumption is
/// checked, so the per-level cost has to be linear on its own rather than
/// relying on the input being small in practice. See `crate::jwk`'s
/// identical function and the round-2 report for the measurement that
/// motivated this.
fn reject_duplicate_members(value: &Value) -> Result<(), ()> {
    match value {
        Value::Object(members) => {
            let mut seen: HashSet<&str> = HashSet::with_capacity(members.len());
            for (name, _) in members {
                if !seen.insert(name.as_str()) {
                    return Err(());
                }
            }
            for (_, member_value) in members {
                reject_duplicate_members(member_value)?;
            }
            Ok(())
        }
        Value::Array(items) => {
            for item in items {
                reject_duplicate_members(item)?;
            }
            Ok(())
        }
        Value::Null | Value::Bool(_) | Value::Number(_) | Value::String(_) => Ok(()),
    }
}

/// Looks up `key` in an object's members. By the time this is called, the
/// header has already passed [`reject_duplicate_members`], so there is at
/// most one occurrence of `key` to find.
fn get<'a>(object: &'a [(String, Value)], key: &str) -> Option<&'a Value> {
    object.iter().find(|(k, _)| k == key).map(|(_, v)| v)
}

fn get_str<'a>(object: &'a [(String, Value)], key: &str) -> Option<&'a str> {
    match get(object, key) {
        Some(Value::String(s)) => Some(s.as_str()),
        _ => None,
    }
}

fn get_bool(object: &[(String, Value)], key: &str) -> Option<bool> {
    match get(object, key) {
        Some(Value::Bool(b)) => Some(*b),
        _ => None,
    }
}

fn get_array<'a>(object: &'a [(String, Value)], key: &str) -> Option<&'a [Value]> {
    match get(object, key) {
        Some(Value::Array(items)) => Some(items.as_slice()),
        _ => None,
    }
}

/// Every rejection [`verify`] can produce, named for the predicate that
/// failed. `predicate()` renders each as the `curia/jws/...` slug the
/// conformance fixtures' `expect-verify-failure` field uses (today,
/// `Curia.Canon.Jws.JwsErrors`' vocabulary — this crate is expected to
/// reproduce it, not invent a parallel one).
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum JwsError {
    /// The compact serialization does not have exactly three `.`-separated
    /// segments.
    MalformedCompactSerialization,
    /// The payload (middle) segment is non-empty. A detached JWS per
    /// RFC 7797 / D3 always has an empty payload segment on the wire.
    PayloadNotDetached,
    /// The protected-header segment is not valid base64url.
    ProtectedHeaderNotBase64,
    /// The decoded protected header is not a JSON object (or is not valid
    /// JSON at all).
    ProtectedHeaderMalformed,
    /// **Fix round 1.** The protected header (or an object nested inside
    /// it) has a duplicate member name — e.g. `"alg"` appearing twice.
    /// Rejected outright rather than resolved by first-or-last occurrence;
    /// see `reject_duplicate_members`'s doc comment for why this matters
    /// specifically for `alg`.
    ProtectedHeaderDuplicateMember,
    /// `alg` is missing, not a string, or not one of `EdDSA`/`ES256`. This
    /// is the one check Step 3 requires to run before any cryptographic
    /// operation; see the module doc comment. `alg` carries what the
    /// header actually said, if it said anything parseable as a string, so
    /// the rejection reason is diagnosable without re-reading the header.
    AlgorithmNotAllowed { alg: Option<String> },
    /// `typ` is missing or is not exactly `curia-post+jws`.
    TypInvalid,
    /// `b64` is missing (defaults to `true` per RFC 7797) or is `true`.
    /// R6.37 requires `b64: false` exactly.
    B64NotFalse,
    /// `crit` is missing or is not exactly the single-element array
    /// `["b64"]`.
    CritInvalid,
    /// The header has no `kid`, so no key can be looked up.
    KidMissing,
    /// The signature segment is not valid base64url.
    SignatureNotBase64,
    /// The JWKS itself, or the specific key entry once found, is malformed
    /// — delegates to [`JwkError`]'s own predicate.
    Key(JwkError),
    /// No key in the JWKS has the header's `kid`.
    KeyNotFound { kid: String },
    /// The key found for `kid` is a different key type than the header's
    /// `alg` requires (e.g. `alg: "EdDSA"` but the resolved key is an EC
    /// P-256 key). Rejected before any crypto call is attempted with
    /// mismatched key/algorithm material.
    KeyAlgorithmMismatch,
    /// The signature segment decoded to the wrong byte length for its
    /// algorithm (both `EdDSA` and `ES256` signatures here are always
    /// exactly 64 raw bytes) — this is what catches a DER-encoded ES256
    /// signature (Step 4).
    SignatureMalformed,
    /// The cryptographic check itself failed: the bytes are shaped
    /// correctly, but do not constitute a valid signature by this key over
    /// this payload. Deliberately the *same* predicate for "wrong key" and
    /// "tampered payload" — see the module doc comment on why this crate
    /// does not try to distinguish them.
    SignatureInvalid,
}

impl From<JwkError> for JwsError {
    fn from(err: JwkError) -> Self {
        JwsError::Key(err)
    }
}

impl JwsError {
    /// The RFC 9457 slug this error reports as.
    pub fn predicate(&self) -> &str {
        match self {
            JwsError::MalformedCompactSerialization => "curia/jws/malformed-compact-serialization",
            JwsError::PayloadNotDetached => "curia/jws/payload-not-detached",
            JwsError::ProtectedHeaderNotBase64 => "curia/jws/protected-header-not-base64",
            JwsError::ProtectedHeaderMalformed => "curia/jws/protected-header-malformed",
            JwsError::ProtectedHeaderDuplicateMember => {
                "curia/jws/protected-header-duplicate-member"
            }
            JwsError::AlgorithmNotAllowed { .. } => "curia/jws/algorithm-not-allowed",
            JwsError::TypInvalid => "curia/jws/typ-invalid",
            JwsError::B64NotFalse => "curia/jws/b64-not-false",
            JwsError::CritInvalid => "curia/jws/crit-invalid",
            JwsError::KidMissing => "curia/jws/kid-missing",
            JwsError::SignatureNotBase64 => "curia/jws/signature-not-base64",
            JwsError::Key(inner) => inner.predicate(),
            JwsError::KeyNotFound { .. } => "curia/jws/key-not-found",
            JwsError::KeyAlgorithmMismatch => "curia/jws/key-algorithm-mismatch",
            JwsError::SignatureMalformed => "curia/jws/signature-malformed",
            JwsError::SignatureInvalid => "curia/jws/signature-invalid",
        }
    }
}

impl std::fmt::Display for JwsError {
    /// **Task 6 fix.** Previously every arm here printed only a
    /// human-readable detail, with no predicate slug anywhere in the
    /// rendered string — unlike [`crate::json::AdmitError`] and
    /// [`crate::nfc::NfcError`], whose `Display` impls always lead with
    /// `{slug}: `. That inconsistency became a real defect once
    /// `curia_testis::verify_envelope` (Task 6) started printing `Display`
    /// output directly to the CLI's stderr: for the two negative envelope
    /// fixtures (`tampered-body`, `wrong-key`), the CLI printed
    /// `"error: signature does not verify"` with no `curia/jws/...` slug
    /// anywhere — failing the CLI contract's "name the failing predicate,"
    /// caught by `tests/envelope.rs`'s
    /// `verify_fails_on_tampered_body_exit_1_names_signature_invalid`.
    ///
    /// `Key(inner)` is deliberately **not** given its own prefix here: once
    /// [`crate::jwk::JwkError`]'s own `Display` carries the same fix,
    /// `inner`'s rendering already leads with the identical slug
    /// [`JwsError::predicate`] would compute for this variant (it delegates
    /// to `inner.predicate()` — see that method). Prefixing again here
    /// would double it (`"slug: slug: detail"`).
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        if let JwsError::Key(inner) = self {
            return write!(f, "{inner}");
        }
        write!(f, "{}: ", self.predicate())?;
        match self {
            JwsError::MalformedCompactSerialization => {
                write!(
                    f,
                    "compact JWS does not have exactly three '.'-separated segments"
                )
            }
            JwsError::PayloadNotDetached => {
                write!(
                    f,
                    "payload segment is non-empty; a detached JWS carries no payload segment"
                )
            }
            JwsError::ProtectedHeaderNotBase64 => {
                write!(f, "protected header is not valid base64url")
            }
            JwsError::ProtectedHeaderMalformed => {
                write!(f, "protected header is not a JSON object")
            }
            JwsError::ProtectedHeaderDuplicateMember => {
                write!(f, "protected header has a duplicate member name")
            }
            JwsError::AlgorithmNotAllowed { alg: Some(alg) } => {
                write!(
                    f,
                    "algorithm `{alg}` is not allowed (only EdDSA and ES256 are)"
                )
            }
            JwsError::AlgorithmNotAllowed { alg: None } => {
                write!(f, "header has no usable `alg` string")
            }
            JwsError::TypInvalid => write!(f, "`typ` is not exactly `curia-post+jws`"),
            JwsError::B64NotFalse => write!(f, "`b64` is not present-and-exactly `false`"),
            JwsError::CritInvalid => write!(f, "`crit` is not exactly `[\"b64\"]`"),
            JwsError::KidMissing => write!(f, "header has no `kid`"),
            JwsError::SignatureNotBase64 => write!(f, "signature segment is not valid base64url"),
            JwsError::Key(inner) => write!(f, "{inner}"),
            JwsError::KeyNotFound { kid } => write!(f, "no key found for kid `{kid}`"),
            JwsError::KeyAlgorithmMismatch => {
                write!(
                    f,
                    "the resolved key's type does not match the header's `alg`"
                )
            }
            JwsError::SignatureMalformed => {
                write!(
                    f,
                    "signature bytes are not the expected fixed-width raw form"
                )
            }
            JwsError::SignatureInvalid => write!(f, "signature does not verify"),
        }
    }
}

impl std::error::Error for JwsError {}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::conformance::conformance_dir;

    fn load(case: &str, file: &str) -> Vec<u8> {
        std::fs::read(conformance_dir().join("envelope").join(case).join(file))
            .unwrap_or_else(|e| panic!("failed to read {case}/{file}: {e}"))
    }

    fn signature_of(case: &str) -> String {
        let submission: serde_json::Value =
            serde_json::from_slice(&load(case, "submission.json")).unwrap();
        submission
            .get("signature")
            .and_then(serde_json::Value::as_str)
            .unwrap()
            .to_string()
    }

    /// Every positive envelope fixture must verify, end to end, using
    /// `expected.canonical` directly as the payload — exactly what the
    /// brief prescribes as the way to be independent of Task 3's
    /// (currently stubbed) `canonicalize_with_nfc`.
    #[test]
    fn positive_fixtures_verify() {
        for case in [
            "ed25519-minimal",
            "ed25519-full",
            "ed25519-unicode",
            "es256-minimal",
        ] {
            let canonical = load(case, "expected.canonical");
            let jwks = JwkSet::parse(&load(case, "jwks.json")).unwrap();
            let compact = signature_of(case);
            let result = verify(&compact, &canonical, &jwks);
            assert!(result.is_ok(), "{case} expected to verify, got {result:?}");
        }
    }

    /// D3's own failure mode, reproduced directly: the References-entry
    /// formula (Appendix F semantics: base64url-encode the payload before
    /// signing) must NOT verify against a real fixture's signature. If it
    /// did, this crate would have made the exact citation error D3 warns
    /// about.
    #[test]
    fn references_entry_formula_does_not_verify() {
        let canonical = load("ed25519-minimal", "expected.canonical");
        let jwks = JwkSet::parse(&load("ed25519-minimal", "jwks.json")).unwrap();
        let compact = signature_of("ed25519-minimal");
        let protected_b64 = compact.split('.').next().unwrap();
        let signature_b64 = compact.rsplit('.').next().unwrap();

        // The wrong formula: base64url-encode the payload too.
        let wrong_payload_b64 = URL_SAFE_NO_PAD.encode(&canonical);
        let signing_input = format!("{protected_b64}.{wrong_payload_b64}");

        let signature_bytes = URL_SAFE_NO_PAD.decode(signature_b64).unwrap();
        let jwk = jwks.find_by_kid("conformance-ed25519-minimal").unwrap();
        let PublicKey::Ed25519(verifying_key) = &jwk.key else {
            panic!("expected an Ed25519 key");
        };
        let array: [u8; 64] = signature_bytes.try_into().unwrap();
        let signature = ed25519_dalek::Signature::from_bytes(&array);
        let outcome = verifying_key.verify_strict(signing_input.as_bytes(), &signature);
        assert!(
            outcome.is_err(),
            "the RFC 7515 Appendix F (encoded-payload) formula must NOT verify against a \
             real R6.37/RFC 7797 signature — if it did, D3's citation error would have been \
             reproduced here"
        );
    }

    #[test]
    fn tampered_body_fails_with_signature_invalid() {
        let canonical = load("tampered-body", "expected.canonical");
        let jwks = JwkSet::parse(&load("tampered-body", "jwks.json")).unwrap();
        let compact = signature_of("tampered-body");
        let err = verify(&compact, &canonical, &jwks).unwrap_err();
        assert_eq!(err.predicate(), "curia/jws/signature-invalid");
    }

    #[test]
    fn wrong_key_fails_with_signature_invalid() {
        let canonical = load("wrong-key", "expected.canonical");
        let jwks = JwkSet::parse(&load("wrong-key", "jwks.json")).unwrap();
        let compact = signature_of("wrong-key");
        let err = verify(&compact, &canonical, &jwks).unwrap_err();
        assert_eq!(err.predicate(), "curia/jws/signature-invalid");
    }

    // -----------------------------------------------------------------
    // Step 3: the algorithm allow-list, rejected before any crypto op.
    // -----------------------------------------------------------------

    fn header_b64(json_header: &str) -> String {
        URL_SAFE_NO_PAD.encode(json_header.as_bytes())
    }

    #[test]
    fn alg_none_is_rejected() {
        let header = header_b64(
            r#"{"alg":"none","kid":"conformance-ed25519-minimal","typ":"curia-post+jws","b64":false,"crit":["b64"]}"#,
        );
        let compact = format!("{header}..");
        let jwks = JwkSet::parse(&load("ed25519-minimal", "jwks.json")).unwrap();
        let err = verify(&compact, b"anything", &jwks).unwrap_err();
        assert_eq!(err.predicate(), "curia/jws/algorithm-not-allowed");
    }

    #[test]
    fn every_hs_star_is_rejected() {
        let jwks = JwkSet::parse(&load("ed25519-minimal", "jwks.json")).unwrap();
        for hs in ["HS256", "HS384", "HS512"] {
            let header = header_b64(&format!(
                r#"{{"alg":"{hs}","kid":"conformance-ed25519-minimal","typ":"curia-post+jws","b64":false,"crit":["b64"]}}"#
            ));
            let compact = format!("{header}..");
            let err = verify(&compact, b"anything", &jwks).unwrap_err();
            assert_eq!(
                err.predicate(),
                "curia/jws/algorithm-not-allowed",
                "alg={hs}"
            );
        }
    }

    /// **Proves the ordering, not just the outcome.** The header's `kid`
    /// names a key that does not exist in this JWKS, and the signature
    /// segment is not valid base64url at all. If the algorithm check ran
    /// *after* the key lookup, this would fail with `key-not-found`; if it
    /// ran after the signature segment was decoded, it would fail with
    /// `signature-not-base64`. Either would mean the implementation reached
    /// code that depends on cryptographic material (a JWKS lookup feeding a
    /// verify call, or decoding bytes destined for one) before rejecting an
    /// algorithm that must never reach that code at all. The only way this
    /// test can observe `algorithm-not-allowed` is if that check runs
    /// strictly first.
    #[test]
    fn algorithm_rejected_before_key_lookup_or_signature_decode() {
        let header = header_b64(
            r#"{"alg":"none","kid":"no-such-kid-in-this-jwks","typ":"curia-post+jws","b64":false,"crit":["b64"]}"#,
        );
        // Not valid base64url (contains a space and a '!').
        let compact = format!("{header}..not valid base64url!!!");
        let jwks = JwkSet::parse(&load("ed25519-minimal", "jwks.json")).unwrap();

        let err = verify(&compact, b"anything", &jwks).unwrap_err();
        assert_eq!(
            err.predicate(),
            "curia/jws/algorithm-not-allowed",
            "a disallowed alg must be rejected before key lookup or signature decoding \
             are ever attempted; got {err:?} instead"
        );
    }

    // -----------------------------------------------------------------
    // Step 2: typ / b64 / crit.
    // -----------------------------------------------------------------

    fn base_header_fields() -> (String, String) {
        // Returns (kid, jwks-relative-case) shared by these tests.
        (
            "conformance-ed25519-minimal".to_string(),
            "ed25519-minimal".to_string(),
        )
    }

    #[test]
    fn wrong_typ_is_rejected() {
        let (kid, case) = base_header_fields();
        let header = header_b64(&format!(
            r#"{{"alg":"EdDSA","kid":"{kid}","typ":"jwt","b64":false,"crit":["b64"]}}"#
        ));
        let compact = format!("{header}..");
        let jwks = JwkSet::parse(&load(&case, "jwks.json")).unwrap();
        let err = verify(&compact, b"anything", &jwks).unwrap_err();
        assert_eq!(err.predicate(), "curia/jws/typ-invalid");
    }

    #[test]
    fn b64_true_is_rejected() {
        let (kid, case) = base_header_fields();
        let header = header_b64(&format!(
            r#"{{"alg":"EdDSA","kid":"{kid}","typ":"curia-post+jws","b64":true,"crit":["b64"]}}"#
        ));
        let compact = format!("{header}..");
        let jwks = JwkSet::parse(&load(&case, "jwks.json")).unwrap();
        let err = verify(&compact, b"anything", &jwks).unwrap_err();
        assert_eq!(err.predicate(), "curia/jws/b64-not-false");
    }

    #[test]
    fn b64_absent_is_rejected() {
        let (kid, case) = base_header_fields();
        let header = header_b64(&format!(
            r#"{{"alg":"EdDSA","kid":"{kid}","typ":"curia-post+jws","crit":["b64"]}}"#
        ));
        let compact = format!("{header}..");
        let jwks = JwkSet::parse(&load(&case, "jwks.json")).unwrap();
        let err = verify(&compact, b"anything", &jwks).unwrap_err();
        assert_eq!(err.predicate(), "curia/jws/b64-not-false");
    }

    #[test]
    fn crit_with_extra_member_is_rejected() {
        let (kid, case) = base_header_fields();
        let header = header_b64(&format!(
            r#"{{"alg":"EdDSA","kid":"{kid}","typ":"curia-post+jws","b64":false,"crit":["b64","exp"]}}"#
        ));
        let compact = format!("{header}..");
        let jwks = JwkSet::parse(&load(&case, "jwks.json")).unwrap();
        let err = verify(&compact, b"anything", &jwks).unwrap_err();
        assert_eq!(err.predicate(), "curia/jws/crit-invalid");
    }

    #[test]
    fn crit_missing_is_rejected() {
        let (kid, case) = base_header_fields();
        let header = header_b64(&format!(
            r#"{{"alg":"EdDSA","kid":"{kid}","typ":"curia-post+jws","b64":false}}"#
        ));
        let compact = format!("{header}..");
        let jwks = JwkSet::parse(&load(&case, "jwks.json")).unwrap();
        let err = verify(&compact, b"anything", &jwks).unwrap_err();
        assert_eq!(err.predicate(), "curia/jws/crit-invalid");
    }

    // -----------------------------------------------------------------
    // Compact-serialization shape.
    // -----------------------------------------------------------------

    #[test]
    fn non_empty_payload_segment_is_rejected() {
        let (kid, case) = base_header_fields();
        let header = header_b64(&format!(
            r#"{{"alg":"EdDSA","kid":"{kid}","typ":"curia-post+jws","b64":false,"crit":["b64"]}}"#
        ));
        let compact = format!("{header}.not-empty.sig");
        let jwks = JwkSet::parse(&load(&case, "jwks.json")).unwrap();
        let err = verify(&compact, b"anything", &jwks).unwrap_err();
        assert_eq!(err.predicate(), "curia/jws/payload-not-detached");
    }

    #[test]
    fn wrong_segment_count_is_rejected() {
        let jwks = JwkSet::parse(&load("ed25519-minimal", "jwks.json")).unwrap();
        for compact in [
            "only-one-segment",
            "two.segments",
            "four.segments.here.oops",
        ] {
            let err = verify(compact, b"anything", &jwks).unwrap_err();
            assert_eq!(
                err.predicate(),
                "curia/jws/malformed-compact-serialization",
                "compact={compact}"
            );
        }
    }

    // -----------------------------------------------------------------
    // Step 4: ES256 must be the fixed-width R||S form, not DER.
    // -----------------------------------------------------------------

    /// Re-encodes a real, valid ES256 fixture signature from its raw
    /// 64-byte `R || S` form into ASN.1 DER `SEQUENCE { INTEGER r, INTEGER
    /// s }`, by hand (no DER-encoding dependency exists in this crate), and
    /// confirms that a signature carrying the *same r, s values*, but in
    /// DER framing, is rejected — proving the fixed-width parser does not
    /// happen to also accept DER.
    #[test]
    fn der_encoded_es256_signature_is_rejected() {
        let canonical = load("es256-minimal", "expected.canonical");
        let jwks = JwkSet::parse(&load("es256-minimal", "jwks.json")).unwrap();
        let compact = signature_of("es256-minimal");
        let protected_b64 = compact.split('.').next().unwrap().to_string();
        let signature_b64 = compact.rsplit('.').next().unwrap();
        let raw = URL_SAFE_NO_PAD.decode(signature_b64).unwrap();
        assert_eq!(
            raw.len(),
            64,
            "fixture signature must be raw R||S to start with"
        );
        let (r, s) = raw.split_at(32);

        let der = der_encode_ecdsa_signature(r, s);
        let der_b64 = URL_SAFE_NO_PAD.encode(&der);
        let der_compact = format!("{protected_b64}..{der_b64}");

        let err = verify(&der_compact, &canonical, &jwks).unwrap_err();
        assert_eq!(
            err.predicate(),
            "curia/jws/signature-malformed",
            "a DER-framed signature over the same r/s must be rejected as malformed \
             before any crypto verify runs, got {err:?}"
        );
    }

    /// Minimal ASN.1 DER `SEQUENCE { INTEGER r, INTEGER s }` encoder, for
    /// the test above only. Each 32-byte coordinate is treated as a
    /// big-endian unsigned integer with a leading `0x00` inserted whenever
    /// its top bit is set (DER INTEGER's sign-avoidance rule) and leading
    /// zero bytes otherwise trimmed (DER's minimal-length rule) — the
    /// standard SEC1/DER encoding real ECDSA DER signatures use.
    fn der_encode_ecdsa_signature(r: &[u8], s: &[u8]) -> Vec<u8> {
        fn der_integer(bytes: &[u8]) -> Vec<u8> {
            let mut trimmed = bytes;
            while trimmed.len() > 1 && trimmed[0] == 0 {
                trimmed = &trimmed[1..];
            }
            let mut content = Vec::new();
            if trimmed[0] & 0x80 != 0 {
                content.push(0x00);
            }
            content.extend_from_slice(trimmed);
            let mut out = vec![0x02, content.len() as u8];
            out.extend_from_slice(&content);
            out
        }
        let r_der = der_integer(r);
        let s_der = der_integer(s);
        let mut body = Vec::new();
        body.extend_from_slice(&r_der);
        body.extend_from_slice(&s_der);
        let mut out = vec![0x30, body.len() as u8];
        out.extend_from_slice(&body);
        out
    }

    // -----------------------------------------------------------------
    // Key-not-found / key-algorithm-mismatch.
    // -----------------------------------------------------------------

    #[test]
    fn unknown_kid_is_key_not_found() {
        let (_, case) = base_header_fields();
        let header = header_b64(
            r#"{"alg":"EdDSA","kid":"nonexistent","typ":"curia-post+jws","b64":false,"crit":["b64"]}"#,
        );
        let compact = format!("{header}..");
        let jwks = JwkSet::parse(&load(&case, "jwks.json")).unwrap();
        let err = verify(&compact, b"anything", &jwks).unwrap_err();
        assert_eq!(err.predicate(), "curia/jws/key-not-found");
    }

    // -----------------------------------------------------------------
    // Platform hazard: embedded whitespace in a base64url segment.
    // -----------------------------------------------------------------

    /// Documents a deliberate choice, per the Task 5 brief: Rust's `base64`
    /// crate rejects embedded whitespace in a base64url string outright —
    /// it is not in the URL-safe alphabet and is not specially skipped —
    /// where some other platforms' decoders strip whitespace before
    /// decoding. The specification does not say which reading is
    /// normative. This crate implements the strict reading (whitespace
    /// makes a segment invalid) by construction, simply by using the
    /// pinned `base64` engine's default behavior rather than building a
    /// permissive decode config; this test exists so that fact is asserted
    /// and visible, not just implicit in a dependency's default.
    #[test]
    fn embedded_whitespace_in_a_base64_segment_is_rejected_not_stripped() {
        let (kid, case) = base_header_fields();
        let header = header_b64(&format!(
            r#"{{"alg":"EdDSA","kid":"{kid}","typ":"curia-post+jws","b64":false,"crit":["b64"]}}"#
        ));
        // A syntactically plausible signature segment with a space spliced
        // into the middle. On a whitespace-stripping platform this might
        // decode to a 64-byte signature (and simply fail to verify); here
        // it must be rejected at the decode step itself.
        let compact = format!("{header}..AAAA AAAA");
        let jwks = JwkSet::parse(&load(&case, "jwks.json")).unwrap();
        let err = verify(&compact, b"anything", &jwks).unwrap_err();
        assert_eq!(err.predicate(), "curia/jws/signature-not-base64");
    }

    #[test]
    fn alg_es256_against_ed25519_key_is_algorithm_mismatch() {
        let header = header_b64(
            r#"{"alg":"ES256","kid":"conformance-ed25519-minimal","typ":"curia-post+jws","b64":false,"crit":["b64"]}"#,
        );
        let compact = format!("{header}..");
        let jwks = JwkSet::parse(&load("ed25519-minimal", "jwks.json")).unwrap();
        let err = verify(&compact, b"anything", &jwks).unwrap_err();
        assert_eq!(err.predicate(), "curia/jws/key-algorithm-mismatch");
    }

    // -----------------------------------------------------------------
    // Fix round 1: duplicate member names in the protected header are
    // rejected, not resolved by first-or-last occurrence.
    // -----------------------------------------------------------------

    /// The scenario the controller's ruling names directly: a header with
    /// `alg` given twice, once as `"none"` and once as a real algorithm.
    /// First-occurrence-wins would read `"none"` and reject (for the wrong
    /// reason — an insecure algorithm, not a structural defect);
    /// last-occurrence-wins would read `"EdDSA"` and proceed to attempt
    /// verification. Neither is acceptable — two conforming verifiers must
    /// not be able to reach different conclusions about the same bytes —
    /// so this must be rejected as a duplicate member before `alg` is read
    /// at all. What follows the header does not need to be a real
    /// signature: the duplicate is caught before any signature or key
    /// material is examined.
    #[test]
    fn duplicated_alg_in_header_is_rejected() {
        let (kid, case) = base_header_fields();
        let header = header_b64(&format!(
            r#"{{"alg":"none","alg":"EdDSA","kid":"{kid}","typ":"curia-post+jws","b64":false,"crit":["b64"]}}"#
        ));
        let compact = format!("{header}..whatever-this-does-not-matter");
        let jwks = JwkSet::parse(&load(&case, "jwks.json")).unwrap();
        let err = verify(&compact, b"anything", &jwks).unwrap_err();
        assert_eq!(
            err.predicate(),
            "curia/jws/protected-header-duplicate-member",
            "a duplicated `alg` must be rejected outright, not resolved to either \
             reading by position; got {err:?} instead"
        );
    }

    #[test]
    fn duplicated_kid_in_header_is_rejected() {
        let header = header_b64(
            r#"{"alg":"EdDSA","kid":"conformance-ed25519-minimal","kid":"someone-else","typ":"curia-post+jws","b64":false,"crit":["b64"]}"#,
        );
        let compact = format!("{header}..");
        let jwks = JwkSet::parse(&load("ed25519-minimal", "jwks.json")).unwrap();
        let err = verify(&compact, b"anything", &jwks).unwrap_err();
        assert_eq!(
            err.predicate(),
            "curia/jws/protected-header-duplicate-member"
        );
    }

    /// `JwsError::Key`/`From<JwkError>` is forward plumbing for Task 6
    /// (which will parse JWKS bytes itself and needs to fold a `JwkError`
    /// into a `JwsError`); `jws::verify` never constructs it internally,
    /// since it always receives an already-parsed `&JwkSet`. This pins the
    /// delegation itself: the wrapped error's predicate passes through
    /// unchanged, not renamed or generalized to something JWKS-agnostic.
    #[test]
    fn key_error_predicate_delegates_through_jws_error() {
        for jwk_err in [
            JwkError::NotJson,
            JwkError::DuplicateMember,
            JwkError::KeyInvalid,
            JwkError::UnsupportedKeyType("RSA".to_string()),
        ] {
            let jws_err: JwsError = jwk_err.clone().into();
            assert!(matches!(jws_err, JwsError::Key(_)));
            assert_eq!(jws_err.predicate(), jwk_err.predicate());
        }
    }

    // -----------------------------------------------------------------
    // Fix round 2: the duplicate-member scan must be linear, not
    // quadratic, in the number of members at a given object level.
    // -----------------------------------------------------------------

    /// Regression guard, identical in shape to `crate::jwk`'s — see that
    /// module's `duplicate_scan_stays_fast_at_scale` for the full
    /// rationale and the round-2 measurement it references. This one pins
    /// `jws`'s own copy of `reject_duplicate_members`, which runs on the
    /// decoded protected header before `alg` (or anything else) is read,
    /// and which nothing upstream bounds the size of either — the CLI's
    /// `--envelope`/`--jwks` reads have no size cap (Task 6's file).
    #[test]
    fn duplicate_scan_stays_fast_at_scale() {
        let members: Vec<(String, Value)> = (0..50_000)
            .map(|i| (format!("member-{i}"), Value::Null))
            .collect();
        let big_header = Value::Object(members);

        let start = std::time::Instant::now();
        let result = reject_duplicate_members(&big_header);
        let elapsed = start.elapsed();

        assert!(
            result.is_ok(),
            "50,000 unique member names must not be flagged as duplicates"
        );
        assert!(
            elapsed < std::time::Duration::from_secs(1),
            "duplicate-member scan over 50,000 unique names took {elapsed:?}; a linear \
             scan should take milliseconds, not approach a second — this is the shape a \
             regression to the old O(n^2) nested-loop scan would produce"
        );
    }

    /// Not a CI check — a manual benchmark reproducing the round-2 report's
    /// timings for this module's copy. `#[ignore]`d because its value is
    /// the printed numbers, not a pass/fail; run it explicitly with:
    /// `cargo test --lib -- --ignored --nocapture jws::tests::duplicate_scan_scaling_benchmark`
    #[test]
    #[ignore]
    fn duplicate_scan_scaling_benchmark() {
        for n in [2_000usize, 10_000, 32_000, 50_000, 100_000] {
            let members: Vec<(String, Value)> = (0..n)
                .map(|i| (format!("member-{i}"), Value::Null))
                .collect();
            let object = Value::Object(members);
            let start = std::time::Instant::now();
            reject_duplicate_members(&object).unwrap();
            println!(
                "jws::reject_duplicate_members n={n:>7} -> {:?}",
                start.elapsed()
            );
        }
    }
}
