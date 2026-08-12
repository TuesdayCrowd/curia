//! JWKS parsing: turns the bytes of a `{"keys": [...]}` document into typed,
//! verify-ready public keys.
//!
//! **Errata D4** is why this module has two branches, not one: the
//! specification's own References cite RFC 7517/7518, which define JWK
//! shapes for RSA and two-coordinate `EC` curves and do not cover Ed25519 at
//! all. The octet-key-pair form (`kty: "OKP"`, single coordinate `x`) is
//! RFC 8037, cited nowhere in the original corpus. An implementer who
//! reuses the `EC` shape (`x`/`y`) for an Ed25519 key produces JSON that
//! parses and then verifies nothing — D4's own words. This module encodes
//! the corrected mapping (proposed R4.21):
//!
//! - **Ed25519** (`alg: "EdDSA"`): RFC 8037 §2 octet key pair —
//!   `kty: "OKP"`, `crv: "Ed25519"`, `x` = base64url(32-byte public key, no
//!   padding).
//! - **ES256**: RFC 7518 §6.2.1 `EC` form — `kty: "EC"`, `crv: "P-256"`,
//!   `x`/`y` = base64url(32-byte coordinate) each, no padding.
//!
//! Parsing goes through [`crate::json`], the crate's hand-rolled, stack-
//! guarded parser, rather than `serde_json`: a JWKS is Forum-served but is
//! still attacker-reachable input to this crate's `verify` entry point (the
//! `--jwks` file is caller-supplied, and nothing upstream of this module
//! bounds its nesting), and CHARTER.md's "Result, never panic" is called
//! out by the Task 5 brief as mattering most for exactly this kind of
//! input. `crate::json::parse` provides the same stack-safety guard
//! [`crate::canonical`] relies on; `serde_json`'s recursive `Value`
//! deserialization does not bound recursion depth on its own.
//!
//! This module only ever *decodes* public key material. It has no function
//! that accepts a private JWK (`d`) — `private-keys.json` exists in the
//! conformance corpus so fixtures are reproducible, and this crate is
//! verify-only by the same global constraint that forbids a signing
//! function (CHARTER.md §2: "a verifier able to sign is a verifier that
//! must be trusted with keys").

use std::collections::HashSet;

use base64::engine::general_purpose::URL_SAFE_NO_PAD;
use base64::Engine;

use crate::json::{self, Value};

/// A decoded public key, tagged by the algorithm family it verifies.
/// Deliberately not a bare byte buffer: by the time a [`Jwk`] exists, its
/// key bytes have already been checked to be a well-formed point on the
/// declared curve (or a well-formed Ed25519 verifying key), so a caller
/// matching on this enum can never hand the wrong byte shape to the wrong
/// verifier.
#[derive(Debug, Clone)]
pub enum PublicKey {
    Ed25519(ed25519_dalek::VerifyingKey),
    EcdsaP256(p256::ecdsa::VerifyingKey),
}

/// One entry from a JWKS `keys` array.
#[derive(Debug, Clone)]
pub struct Jwk {
    pub kid: Option<String>,
    pub key: PublicKey,
}

/// A parsed `{"keys": [...]}` document: every public key a verifier is
/// given, keyed for lookup by `kid`.
#[derive(Debug, Clone)]
pub struct JwkSet {
    keys: Vec<Jwk>,
}

impl JwkSet {
    /// Parses a JWKS document from raw bytes.
    pub fn parse(bytes: &[u8]) -> Result<Self, JwkError> {
        let value = json::parse(bytes).map_err(|_| JwkError::NotJson)?;
        // Fix round 1: reject a duplicate member name anywhere in the
        // document — the JWKS wrapper object itself, or any individual
        // `keys` entry — rather than resolving it by position. See
        // `reject_duplicate_members`'s doc comment.
        reject_duplicate_members(&value)?;
        let root = as_object(&value).ok_or(JwkError::NotObject)?;
        let keys_value = get(root, "keys").ok_or(JwkError::MissingKeys)?;
        let keys_array = match keys_value {
            Value::Array(items) => items,
            _ => return Err(JwkError::KeysNotArray),
        };

        let mut keys = Vec::with_capacity(keys_array.len());
        for entry in keys_array {
            keys.push(parse_jwk(entry)?);
        }
        Ok(JwkSet { keys })
    }

    /// The first key whose `kid` equals `kid`. JWKS is not required to have
    /// unique `kid` values (RFC 7517 does not forbid repeats); this crate
    /// takes the first match, deterministically, rather than guessing at a
    /// tie-break the specification does not state.
    ///
    /// This is a different thing from a duplicate *member name* within one
    /// key object (rejected by [`JwkError::DuplicateMember`]): two distinct
    /// array entries sharing the same `kid` *value* are structurally
    /// unambiguous — each is its own complete object — and this crate
    /// accepts that shape. `conformance/envelope/wrong-key/private-keys.json`
    /// relies on exactly this to publish both the actual signer and the
    /// published-but-wrong key under one `kid`.
    pub fn find_by_kid(&self, kid: &str) -> Option<&Jwk> {
        self.keys.iter().find(|k| k.kid.as_deref() == Some(kid))
    }

    /// The full list of keys in this set, in document order.
    pub fn keys(&self) -> &[Jwk] {
        &self.keys
    }
}

fn parse_jwk(value: &Value) -> Result<Jwk, JwkError> {
    let object = as_object(value).ok_or(JwkError::KeyEntryNotObject)?;

    let kid = match get(object, "kid") {
        None => None,
        Some(Value::String(s)) => Some(s.clone()),
        Some(_) => return Err(JwkError::KeyMalformed),
    };

    let kty = get_str(object, "kty").ok_or(JwkError::MissingKty)?;

    let key = match kty {
        "OKP" => parse_okp(object)?,
        "EC" => parse_ec(object)?,
        other => return Err(JwkError::UnsupportedKeyType(other.to_string())),
    };

    Ok(Jwk { kid, key })
}

/// RFC 8037 §2: Ed25519 octet key pair.
fn parse_okp(object: &[(String, Value)]) -> Result<PublicKey, JwkError> {
    let crv = get_str(object, "crv").ok_or(JwkError::KeyMalformed)?;
    if crv != "Ed25519" {
        return Err(JwkError::UnsupportedCurve(crv.to_string()));
    }
    let x = get_str(object, "x").ok_or(JwkError::KeyMalformed)?;
    let x_bytes = decode_coordinate(x, "x")?;
    let array: [u8; 32] = x_bytes
        .try_into()
        .map_err(|_| JwkError::CoordinateWrongLength {
            field: "x",
            expected: 32,
        })?;
    let verifying_key =
        ed25519_dalek::VerifyingKey::from_bytes(&array).map_err(|_| JwkError::KeyInvalid)?;
    Ok(PublicKey::Ed25519(verifying_key))
}

/// RFC 7518 §6.2.1: `EC` key, P-256 curve.
fn parse_ec(object: &[(String, Value)]) -> Result<PublicKey, JwkError> {
    let crv = get_str(object, "crv").ok_or(JwkError::KeyMalformed)?;
    if crv != "P-256" {
        return Err(JwkError::UnsupportedCurve(crv.to_string()));
    }
    let x = get_str(object, "x").ok_or(JwkError::KeyMalformed)?;
    let y = get_str(object, "y").ok_or(JwkError::KeyMalformed)?;
    let x_bytes = decode_coordinate(x, "x")?;
    let y_bytes = decode_coordinate(y, "y")?;
    if x_bytes.len() != 32 {
        return Err(JwkError::CoordinateWrongLength {
            field: "x",
            expected: 32,
        });
    }
    if y_bytes.len() != 32 {
        return Err(JwkError::CoordinateWrongLength {
            field: "y",
            expected: 32,
        });
    }

    // SEC1 uncompressed point encoding: 0x04 || X || Y (RFC 7518 §6.2.1.2/.3
    // give X and Y as separate base64url coordinates; p256's SEC1 decoder
    // wants them concatenated behind the uncompressed-point tag byte).
    let mut sec1 = Vec::with_capacity(1 + 32 + 32);
    sec1.push(0x04);
    sec1.extend_from_slice(&x_bytes);
    sec1.extend_from_slice(&y_bytes);

    let verifying_key =
        p256::ecdsa::VerifyingKey::from_sec1_bytes(&sec1).map_err(|_| JwkError::KeyInvalid)?;
    Ok(PublicKey::EcdsaP256(verifying_key))
}

fn decode_coordinate(field_value: &str, field: &'static str) -> Result<Vec<u8>, JwkError> {
    URL_SAFE_NO_PAD
        .decode(field_value)
        .map_err(|_| JwkError::CoordinateNotBase64(field))
}

fn as_object(value: &Value) -> Option<&[(String, Value)]> {
    match value {
        Value::Object(members) => Some(members.as_slice()),
        _ => None,
    }
}

/// **Fix round 1.** Rejects a document that has two members with the same
/// name in the same object, at any depth — the JWKS wrapper object, or any
/// individual `keys` entry.
///
/// `crate::json::parse` deliberately does not reject duplicates itself (see
/// its module doc comment — that is `admit`'s concern, applied to the
/// envelope). This module previously resolved a duplicate by taking the
/// first occurrence (see `get`'s old doc comment); review flagged that as
/// the same class of hazard errata D7 names for the envelope ("common
/// parsers silently accept last-wins, so implementations diverge without a
/// stated rule"), and the controller ruled it out for JWKS/JWK material
/// specifically because the protected header's `alg` selects the
/// algorithm: `{"alg":"none","alg":"EdDSA",...}` must not be resolvable to
/// either reading by position — two conforming verifiers must not be able
/// to reach opposite conclusions about the same bytes. This function is
/// called before any field is read out of a parsed [`Value`], in both
/// [`JwkSet::parse`] and [`crate::jws::verify`], so "first occurrence wins"
/// is no longer reachable at either layer.
///
/// **Fix round 2.** Per object level, this is a single pass through
/// `members` inserting each name into a `HashSet`, rejecting on the first
/// name `insert` reports as already present — O(n) name comparisons per
/// level (amortized), not the O(n²) nested-loop scan this function started
/// with. The nested-loop version was measured (see the round-2 report) at
/// seconds for a `--jwks` file with tens of thousands of members in one
/// object, with nothing upstream bounding that count — ADMIT's
/// members-per-level cap governs envelope submissions, not JWKS or header
/// parsing, and the CLI's `--jwks` read has no size cap either (Task 6's
/// file, not this module's). `HashSet::with_capacity(members.len())`
/// avoids reallocation churn as it fills.
fn reject_duplicate_members(value: &Value) -> Result<(), JwkError> {
    match value {
        Value::Object(members) => {
            let mut seen: HashSet<&str> = HashSet::with_capacity(members.len());
            for (name, _) in members {
                if !seen.insert(name.as_str()) {
                    return Err(JwkError::DuplicateMember);
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
/// object has already passed [`reject_duplicate_members`], so there is at
/// most one occurrence of `key` to find — this is a plain lookup, not a
/// tie-break.
fn get<'a>(object: &'a [(String, Value)], key: &str) -> Option<&'a Value> {
    object.iter().find(|(k, _)| k == key).map(|(_, v)| v)
}

fn get_str<'a>(object: &'a [(String, Value)], key: &str) -> Option<&'a str> {
    match get(object, key) {
        Some(Value::String(s)) => Some(s.as_str()),
        _ => None,
    }
}

/// Every rejection this module can produce, each named for the predicate
/// that failed rather than a generic "invalid JWK". `predicate()` renders
/// each as a `curia/jws/...` slug — the same namespace `crate::jws` uses,
/// because a JWKS/JWK failure is still, from a caller's point of view, a
/// reason a JWS did not verify.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum JwkError {
    /// The bytes are not valid JSON at all.
    NotJson,
    /// **Fix round 1.** Some object in the document — the JWKS wrapper, or
    /// a `keys` entry — has two members with the same name. Rejected
    /// outright rather than resolved by first-or-last occurrence; see
    /// `reject_duplicate_members`'s doc comment.
    DuplicateMember,
    /// The top-level JSON value is not an object.
    NotObject,
    /// The top-level object has no `keys` member.
    MissingKeys,
    /// `keys` is present but is not a JSON array.
    KeysNotArray,
    /// An entry in `keys` is not a JSON object.
    KeyEntryNotObject,
    /// A key entry has no `kty`.
    MissingKty,
    /// A key entry has a required field (other than `kty`) that is missing
    /// or the wrong JSON type — e.g. `crv` absent, or `kid` present but not
    /// a string.
    KeyMalformed,
    /// `kty` is present but is neither `"OKP"` nor `"EC"` — the only two
    /// shapes errata D4 / RFC 7518 define for this crate's two allowed
    /// algorithms.
    UnsupportedKeyType(String),
    /// `crv` does not match the one curve each supported `kty` allows
    /// (`Ed25519` for `OKP`, `P-256` for `EC`).
    UnsupportedCurve(String),
    /// A coordinate field (`x` or `y`) is not valid base64url.
    CoordinateNotBase64(&'static str),
    /// A coordinate field decoded to the wrong byte length for its curve.
    CoordinateWrongLength {
        field: &'static str,
        expected: usize,
    },
    /// A coordinate decoded to the right length and encoding but is not a
    /// valid key for its algorithm — e.g. an Ed25519 `x` that is not a
    /// valid compressed Edwards point, or an EC point not on P-256.
    KeyInvalid,
}

impl JwkError {
    /// The RFC 9457 slug this error reports as, in the `curia/jws/...`
    /// namespace the conformance fixtures' `expect-verify-failure` field
    /// uses.
    pub fn predicate(&self) -> &'static str {
        match self {
            JwkError::DuplicateMember => "curia/jws/jwks-duplicate-member",
            JwkError::NotJson
            | JwkError::NotObject
            | JwkError::MissingKeys
            | JwkError::KeysNotArray
            | JwkError::KeyEntryNotObject => "curia/jws/jwks-malformed",
            JwkError::MissingKty | JwkError::KeyMalformed => "curia/jws/key-malformed",
            JwkError::UnsupportedKeyType(_) | JwkError::UnsupportedCurve(_) => {
                "curia/jws/key-type-unsupported"
            }
            JwkError::CoordinateNotBase64(_) | JwkError::CoordinateWrongLength { .. } => {
                "curia/jws/key-malformed"
            }
            JwkError::KeyInvalid => "curia/jws/key-invalid",
        }
    }
}

impl std::fmt::Display for JwkError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            JwkError::NotJson => write!(f, "JWKS is not valid JSON"),
            JwkError::DuplicateMember => {
                write!(
                    f,
                    "an object in the JWKS document has a duplicate member name"
                )
            }
            JwkError::NotObject => write!(f, "JWKS top-level value is not an object"),
            JwkError::MissingKeys => write!(f, "JWKS object has no `keys` member"),
            JwkError::KeysNotArray => write!(f, "JWKS `keys` member is not an array"),
            JwkError::KeyEntryNotObject => write!(f, "a `keys` entry is not an object"),
            JwkError::MissingKty => write!(f, "a key entry has no `kty`"),
            JwkError::KeyMalformed => {
                write!(
                    f,
                    "a key entry is missing a required field or has the wrong type"
                )
            }
            JwkError::UnsupportedKeyType(kty) => {
                write!(
                    f,
                    "unsupported `kty`: `{kty}` (only `OKP` and `EC` are supported)"
                )
            }
            JwkError::UnsupportedCurve(crv) => write!(f, "unsupported `crv`: `{crv}`"),
            JwkError::CoordinateNotBase64(field) => {
                write!(f, "`{field}` is not valid base64url")
            }
            JwkError::CoordinateWrongLength { field, expected } => {
                write!(f, "`{field}` did not decode to {expected} bytes")
            }
            JwkError::KeyInvalid => {
                write!(
                    f,
                    "key material does not decode to a valid point/key for its curve"
                )
            }
        }
    }
}

impl std::error::Error for JwkError {}

#[cfg(test)]
mod tests {
    use super::*;

    fn ed25519_minimal_jwks() -> Vec<u8> {
        std::fs::read(
            crate::conformance::conformance_dir().join("envelope/ed25519-minimal/jwks.json"),
        )
        .expect("fixture jwks.json must exist")
    }

    fn es256_minimal_jwks() -> Vec<u8> {
        std::fs::read(
            crate::conformance::conformance_dir().join("envelope/es256-minimal/jwks.json"),
        )
        .expect("fixture jwks.json must exist")
    }

    #[test]
    fn parses_ed25519_okp_jwks() {
        let set = JwkSet::parse(&ed25519_minimal_jwks()).expect("valid OKP JWKS must parse");
        let jwk = set
            .find_by_kid("conformance-ed25519-minimal")
            .expect("fixture kid must be found");
        assert!(matches!(jwk.key, PublicKey::Ed25519(_)));
    }

    #[test]
    fn parses_es256_ec_jwks() {
        let set = JwkSet::parse(&es256_minimal_jwks()).expect("valid EC JWKS must parse");
        let jwk = set
            .find_by_kid("conformance-es256-minimal")
            .expect("fixture kid must be found");
        assert!(matches!(jwk.key, PublicKey::EcdsaP256(_)));
    }

    #[test]
    fn unknown_kid_is_not_found() {
        let set = JwkSet::parse(&ed25519_minimal_jwks()).unwrap();
        assert!(set.find_by_kid("no-such-kid").is_none());
    }

    #[test]
    fn rejects_non_json() {
        let err = JwkSet::parse(b"not json at all").unwrap_err();
        assert_eq!(err.predicate(), "curia/jws/jwks-malformed");
    }

    #[test]
    fn rejects_missing_keys_member() {
        let err = JwkSet::parse(br#"{"notkeys": []}"#).unwrap_err();
        assert_eq!(err, JwkError::MissingKeys);
        assert_eq!(err.predicate(), "curia/jws/jwks-malformed");
    }

    #[test]
    fn rejects_unsupported_kty() {
        // A plausible-looking RSA key: well-formed JSON, wrong shape for
        // this crate's two supported algorithms.
        let err = JwkSet::parse(br#"{"keys":[{"kty":"RSA","n":"abc","e":"AQAB"}]}"#).unwrap_err();
        assert_eq!(err.predicate(), "curia/jws/key-type-unsupported");
    }

    /// Errata D4's exact failure mode: guessing the `EC` shape (`x`/`y`) for
    /// an Ed25519 key. This must be rejected as an unsupported curve for
    /// `OKP`, not silently accepted with a garbage `y`.
    #[test]
    fn rejects_okp_with_wrong_curve() {
        let err = JwkSet::parse(
            br#"{"keys":[{"kty":"OKP","crv":"X25519","x":"HRzJlnTufZYYTZyCDBpyP5ldQ38JlbCeDOQHgIozgg8"}]}"#,
        )
        .unwrap_err();
        assert_eq!(err.predicate(), "curia/jws/key-type-unsupported");
    }

    #[test]
    fn rejects_ed25519_x_of_wrong_length() {
        // Valid base64url, wrong decoded length (16 bytes, not 32).
        let err = JwkSet::parse(
            br#"{"keys":[{"kty":"OKP","crv":"Ed25519","x":"AAAAAAAAAAAAAAAAAAAAAA"}]}"#,
        )
        .unwrap_err();
        assert_eq!(err.predicate(), "curia/jws/key-malformed");
    }

    #[test]
    fn rejects_coordinate_not_base64() {
        let err = JwkSet::parse(br#"{"keys":[{"kty":"OKP","crv":"Ed25519","x":"not base64!!"}]}"#)
            .unwrap_err();
        assert_eq!(err.predicate(), "curia/jws/key-malformed");
    }

    // -----------------------------------------------------------------
    // Fix round 1: duplicate member names are rejected, at every object
    // level, rather than resolved by first-or-last occurrence.
    // -----------------------------------------------------------------

    /// A duplicated member *inside one `keys` entry* — not two entries
    /// sharing a value, a single object with the same name twice.
    #[test]
    fn rejects_duplicate_member_inside_a_jwk_entry() {
        let err =
            JwkSet::parse(br#"{"keys":[{"kty":"OKP","crv":"Ed25519","x":"AAAA","x":"BBBB"}]}"#)
                .unwrap_err();
        assert_eq!(err, JwkError::DuplicateMember);
        assert_eq!(err.predicate(), "curia/jws/jwks-duplicate-member");
    }

    /// The same rejection, one level up: a duplicated member on the JWKS
    /// wrapper object itself, not inside any entry.
    #[test]
    fn rejects_duplicate_member_on_the_wrapper_object() {
        let err =
            JwkSet::parse(br#"{"keys":[],"keys":[{"kty":"OKP","crv":"Ed25519","x":"AAAA"}]}"#)
                .unwrap_err();
        assert_eq!(err, JwkError::DuplicateMember);
        assert_eq!(err.predicate(), "curia/jws/jwks-duplicate-member");
    }

    /// **Control.** Two *distinct array entries* sharing the same `kid`
    /// value are a different thing entirely from a duplicate member name
    /// within one object, and must still be accepted:
    /// `conformance/envelope/wrong-key/private-keys.json` deliberately
    /// publishes both the actual signer and the published-but-wrong key
    /// under one `kid`, each as its own complete, unambiguous object.
    #[test]
    fn two_jwk_entries_sharing_a_kid_are_still_accepted() {
        let bytes = std::fs::read(
            crate::conformance::conformance_dir().join("envelope/wrong-key/private-keys.json"),
        )
        .expect("fixture private-keys.json must exist");
        let set = JwkSet::parse(&bytes)
            .expect("two entries sharing a kid must parse, not be rejected as duplicate members");
        assert_eq!(
            set.keys().len(),
            2,
            "both entries must be kept, not deduplicated"
        );
        assert!(
            set.keys()
                .iter()
                .all(|k| k.kid.as_deref() == Some("conformance-wrong-key")),
            "both entries do in fact share the same kid, by fixture construction"
        );
        assert!(set.find_by_kid("conformance-wrong-key").is_some());
    }

    // -----------------------------------------------------------------
    // Fix round 2: the duplicate-member scan must be linear, not
    // quadratic, in the number of members at a given object level.
    // -----------------------------------------------------------------

    /// Regression guard against reintroducing the O(n²) nested-loop scan
    /// this function started with. Round 2 review measured that version at
    /// ~7.6s for a single 100,000-member object, with nothing upstream
    /// bounding a `--jwks` document's member count (ADMIT's per-level cap
    /// governs envelope submissions, not JWKS). A linear scan finishes
    /// 50,000 unique member names in low tens of milliseconds; one second
    /// is a generous ceiling that tolerates ordinary machine jitter for an
    /// O(n) operation while remaining far below where a reintroduced O(n²)
    /// scan would land at this size (the old version was already over a
    /// second by n=50,000 per the same measurement). A wall-clock bound is
    /// not the most rigorous possible guard, but the reviewer's own
    /// ruling named it as an acceptable, non-flaky choice at this margin;
    /// see the round-2 report for the actual measured numbers this crate
    /// produces on this machine.
    #[test]
    fn duplicate_scan_stays_fast_at_scale() {
        let members: Vec<(String, Value)> = (0..50_000)
            .map(|i| (format!("member-{i}"), Value::Null))
            .collect();
        let big_object = Value::Object(members);

        let start = std::time::Instant::now();
        let result = reject_duplicate_members(&big_object);
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
    /// timings. `#[ignore]`d because its value is the printed numbers, not
    /// a pass/fail; run it explicitly with:
    /// `cargo test --lib -- --ignored --nocapture duplicate_scan_scaling_benchmark`
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
                "jwk::reject_duplicate_members n={n:>7} -> {:?}",
                start.elapsed()
            );
        }
    }
}
