//! Offline verification of the Acta (§6.6): signed tree heads (R6.49), audit
//! paths (R6.48) and consistency proofs (R6.23), from the JSON the Forum
//! serves at `/v1/log/*`.
//!
//! The boundary that matters: [`verify_inclusion`] takes the *entry* -- the
//! six-member object from `/v1/log/entries/{index}` -- and recomputes the
//! leaf itself (R6.46). It never takes a leaf digest the Forum computed,
//! because a verifier that checks the Forum's arithmetic against the Forum's
//! own input passes a leaf that corresponds to nothing. Where the Forum also
//! states a `leaf_hash`, it is compared and a disagreement is a failure in
//! its own right.
//!
//! Every rejection is a typed [`ActaError`] with a predicate slug; nothing
//! here panics on malformed input.

use std::collections::HashMap;
use std::fmt;

use serde_json::value::RawValue;
use serde_json::Value;

use crate::jwk::{JwkError, JwkSet};
use crate::jws::{self, JwsError};
use crate::merkle::{self, Hash};

/// The `typ` of a signed tree head; a head is not a post and must not verify as one.
pub const HEAD_TYP: &str = "curia-head+jws";

#[derive(Debug)]
pub enum ActaError {
    /// A document is not the JSON shape the Forum publishes.
    Malformed { what: &'static str, detail: String },
    /// A required member is absent or of the wrong type.
    MissingField {
        what: &'static str,
        field: &'static str,
    },
    /// A digest member is not `sha256:` followed by 64 lowercase hex digits.
    Digest {
        what: &'static str,
        field: &'static str,
    },
    /// The signed object or the entry could not be canonicalized.
    Canonical(String),
    /// The log JWKS could not be parsed.
    Jwks(JwkError),
    /// The head's signature did not verify (the inner predicate says why).
    Jws(JwsError),
    /// The head names one `kid` beside the signature and another inside it.
    KidMismatch { stated: String, signed: String },
    /// The leaf recomputed from the entry is not the leaf the proof states.
    LeafMismatch,
    /// The audit path does not lead from the leaf to the root.
    InclusionInvalid,
    /// The proof does not connect the two roots.
    ConsistencyInvalid,
    /// The head covers a different size than the proof is against.
    HeadSizeMismatch { head: u64, proof: u64 },
    /// The head's root is not the proof's root at that size.
    HeadRootMismatch,
}

impl ActaError {
    pub fn predicate(&self) -> &str {
        match self {
            ActaError::Malformed { .. } => "curia/acta/malformed",
            ActaError::MissingField { .. } => "curia/acta/missing-field",
            ActaError::Digest { .. } => "curia/acta/digest-form",
            ActaError::Canonical(_) => "curia/acta/not-canonicalizable",
            ActaError::Jwks(_) => "curia/acta/jwks-invalid",
            ActaError::Jws(err) => err.predicate(),
            ActaError::KidMismatch { .. } => "curia/acta/kid-mismatch",
            ActaError::LeafMismatch => "curia/acta/leaf-mismatch",
            ActaError::InclusionInvalid => "curia/acta/inclusion-invalid",
            ActaError::ConsistencyInvalid => "curia/acta/consistency-invalid",
            ActaError::HeadSizeMismatch { .. } => "curia/acta/head-size-mismatch",
            ActaError::HeadRootMismatch => "curia/acta/head-root-mismatch",
        }
    }
}

impl fmt::Display for ActaError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            ActaError::Malformed { what, detail } => write!(f, "{what} is malformed: {detail}"),
            ActaError::MissingField { what, field } => write!(f, "{what} has no usable `{field}`"),
            ActaError::Digest { what, field } => {
                write!(
                    f,
                    "{what}.{field} is not `sha256:` + 64 lowercase hex digits"
                )
            }
            ActaError::Canonical(detail) => write!(f, "cannot canonicalize: {detail}"),
            ActaError::Jwks(err) => write!(f, "log jwks: {err}"),
            ActaError::Jws(err) => write!(f, "head signature: {err}"),
            ActaError::KidMismatch { stated, signed } => {
                write!(
                    f,
                    "head names kid `{stated}` but its signature was made under `{signed}`"
                )
            }
            ActaError::LeafMismatch => write!(
                f,
                "the leaf recomputed from the entry is not the leaf the proof states"
            ),
            ActaError::InclusionInvalid => {
                write!(
                    f,
                    "the audit path does not lead from this leaf to the stated root"
                )
            }
            ActaError::ConsistencyInvalid => {
                write!(f, "the consistency proof does not connect the two roots")
            }
            ActaError::HeadSizeMismatch { head, proof } => write!(
                f,
                "the head covers {head} leaves but the proof is against a tree of {proof}"
            ),
            ActaError::HeadRootMismatch => {
                write!(
                    f,
                    "the head's root is not the root the proof verifies against"
                )
            }
        }
        .and_then(|()| write!(f, " [{}]", self.predicate()))
    }
}

/// A head whose signature verified under the log's published keys.
#[derive(Debug, Clone)]
pub struct VerifiedHead {
    pub root: Hash,
    pub tree_size: u64,
    pub timestamp: String,
    pub kid: String,
    pub alg: String,
}

/// Verifies `GET /v1/log/head`'s body against `GET /v1/log/jwks`'s.
///
/// The signed object is the `head` member's *raw bytes*, canonicalized here;
/// the members beside it (`kid`, `signature`, and whatever unsigned context
/// the Forum adds) are not under the signature and are not treated as if
/// they were.
pub fn verify_head(head_json: &[u8], log_jwks: &[u8]) -> Result<VerifiedHead, ActaError> {
    let map = raw_members(head_json, "head document")?;
    let head_raw = map.get("head").ok_or(ActaError::MissingField {
        what: "head document",
        field: "head",
    })?;
    let kid = string_member(&map, "kid", "head document")?;
    let signature = string_member(&map, "signature", "head document")?;

    let canonical = crate::canonicalize(head_raw.get().as_bytes())
        .map_err(|e| ActaError::Canonical(e.to_string()))?;
    let jwks = JwkSet::parse(log_jwks).map_err(ActaError::Jwks)?;
    let verified =
        jws::verify_typed(&signature, &canonical, &jwks, HEAD_TYP).map_err(ActaError::Jws)?;
    if verified.kid != kid {
        return Err(ActaError::KidMismatch {
            stated: kid,
            signed: verified.kid,
        });
    }

    let fields: Value = serde_json::from_str(head_raw.get()).map_err(|e| ActaError::Malformed {
        what: "head",
        detail: e.to_string(),
    })?;
    Ok(VerifiedHead {
        root: digest_member(&fields, "root_hash", "head")?,
        tree_size: u64_member(&fields, "tree_size", "head")?,
        timestamp: fields
            .get("timestamp")
            .and_then(Value::as_str)
            .ok_or(ActaError::MissingField {
                what: "head",
                field: "timestamp",
            })?
            .to_string(),
        kid,
        alg: verified.alg,
    })
}

/// An audit path that verified, and what it verified against.
#[derive(Debug, Clone)]
pub struct VerifiedInclusion {
    pub log_index: u64,
    pub tree_size: u64,
    pub leaf: Hash,
    pub root: Hash,
}

/// Verifies `GET /v1/log/proof/{index}`'s body for the entry in
/// `GET /v1/log/entries/{index}`'s body. The leaf is recomputed from the
/// entry (R6.46); the proof's own `leaf_hash`, when present, must agree.
pub fn verify_inclusion(
    entry_json: &[u8],
    proof_json: &[u8],
) -> Result<VerifiedInclusion, ActaError> {
    let map = raw_members(entry_json, "entry document")?;
    let entry_raw = map.get("entry").ok_or(ActaError::MissingField {
        what: "entry document",
        field: "entry",
    })?;
    let canonical = crate::canonicalize(entry_raw.get().as_bytes())
        .map_err(|e| ActaError::Canonical(e.to_string()))?;
    let leaf = merkle::leaf_hash(&canonical);

    let proof: Value = serde_json::from_slice(proof_json).map_err(|e| ActaError::Malformed {
        what: "proof",
        detail: e.to_string(),
    })?;
    let log_index = u64_member(&proof, "log_index", "proof")?;
    let tree_size = u64_member(&proof, "tree_size", "proof")?;
    let root = digest_member(&proof, "root_hash", "proof")?;
    if proof.get("leaf_hash").is_some() && digest_member(&proof, "leaf_hash", "proof")? != leaf {
        return Err(ActaError::LeafMismatch);
    }
    let path = digest_array(&proof, "audit_path", "proof")?;

    if !merkle::verify_inclusion(&leaf, log_index, tree_size, &path, &root) {
        return Err(ActaError::InclusionInvalid);
    }
    Ok(VerifiedInclusion {
        log_index,
        tree_size,
        leaf,
        root,
    })
}

/// A consistency proof that verified, and the two heads it connects.
#[derive(Debug, Clone)]
pub struct VerifiedConsistency {
    pub from_size: u64,
    pub to_size: u64,
    pub from_root: Hash,
    pub to_root: Hash,
}

/// Verifies `GET /v1/log/consistency?from=&to=`'s body.
pub fn verify_consistency(proof_json: &[u8]) -> Result<VerifiedConsistency, ActaError> {
    let proof: Value = serde_json::from_slice(proof_json).map_err(|e| ActaError::Malformed {
        what: "consistency proof",
        detail: e.to_string(),
    })?;
    let from_size = u64_member(&proof, "from_size", "consistency proof")?;
    let to_size = u64_member(&proof, "to_size", "consistency proof")?;
    let from_root = digest_member(&proof, "from_root", "consistency proof")?;
    let to_root = digest_member(&proof, "to_root", "consistency proof")?;
    let path = digest_array(&proof, "path", "consistency proof")?;

    if !merkle::verify_consistency(from_size, to_size, &from_root, &to_root, &path) {
        return Err(ActaError::ConsistencyInvalid);
    }
    Ok(VerifiedConsistency {
        from_size,
        to_size,
        from_root,
        to_root,
    })
}

/// Ties a proof to a signed head: same size, same root. Without this a
/// proof verifies against whatever root it names, which for a reader
/// holding no head is no root at all (R6.48).
pub fn head_covers(head: &VerifiedHead, tree_size: u64, root: &Hash) -> Result<(), ActaError> {
    if head.tree_size != tree_size {
        return Err(ActaError::HeadSizeMismatch {
            head: head.tree_size,
            proof: tree_size,
        });
    }
    if head.root != *root {
        return Err(ActaError::HeadRootMismatch);
    }
    Ok(())
}

/// `sha256:` followed by exactly 64 lowercase hex digits -- the form every
/// digest on this wire takes. Uppercase and bare hex are refused: a verifier
/// that accepted several spellings would compare less strictly than the
/// Forum promises.
pub fn parse_digest(text: &str) -> Option<Hash> {
    let hex = text.strip_prefix("sha256:")?;
    if hex.len() != 64 {
        return None;
    }
    let mut out = [0u8; 32];
    for (i, pair) in hex.as_bytes().chunks(2).enumerate() {
        let digit = |c: u8| match c {
            b'0'..=b'9' => Some(c - b'0'),
            b'a'..=b'f' => Some(c - b'a' + 10),
            _ => None,
        };
        out[i] = digit(pair[0])? << 4 | digit(pair[1])?;
    }
    Some(out)
}

pub fn format_digest(hash: &Hash) -> String {
    let mut s = String::with_capacity(7 + 64);
    s.push_str("sha256:");
    for b in hash {
        s.push_str(&format!("{b:02x}"));
    }
    s
}

fn raw_members(
    json: &[u8],
    what: &'static str,
) -> Result<HashMap<String, Box<RawValue>>, ActaError> {
    serde_json::from_slice(json).map_err(|e| ActaError::Malformed {
        what,
        detail: e.to_string(),
    })
}

fn string_member(
    map: &HashMap<String, Box<RawValue>>,
    field: &'static str,
    what: &'static str,
) -> Result<String, ActaError> {
    let raw = map
        .get(field)
        .ok_or(ActaError::MissingField { what, field })?;
    serde_json::from_str::<String>(raw.get()).map_err(|_| ActaError::MissingField { what, field })
}

fn u64_member(value: &Value, field: &'static str, what: &'static str) -> Result<u64, ActaError> {
    value
        .get(field)
        .and_then(Value::as_u64)
        .ok_or(ActaError::MissingField { what, field })
}

fn digest_member(
    value: &Value,
    field: &'static str,
    what: &'static str,
) -> Result<Hash, ActaError> {
    let text = value
        .get(field)
        .and_then(Value::as_str)
        .ok_or(ActaError::MissingField { what, field })?;
    parse_digest(text).ok_or(ActaError::Digest { what, field })
}

fn digest_array(
    value: &Value,
    field: &'static str,
    what: &'static str,
) -> Result<Vec<Hash>, ActaError> {
    value
        .get(field)
        .and_then(Value::as_array)
        .ok_or(ActaError::MissingField { what, field })?
        .iter()
        .map(|item| {
            item.as_str()
                .and_then(parse_digest)
                .ok_or(ActaError::Digest { what, field })
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn leaves(n: usize) -> Vec<Hash> {
        const LEAVES: [&str; 8] = [
            "",
            "00",
            "10",
            "2021",
            "3031",
            "40414243",
            "5051525354555657",
            "606162636465666768696a6b6c6d6e6f",
        ];
        LEAVES[..n]
            .iter()
            .map(|h| {
                let bytes: Vec<u8> = (0..h.len())
                    .step_by(2)
                    .map(|i| u8::from_str_radix(&h[i..i + 2], 16).unwrap())
                    .collect();
                merkle::leaf_hash(&bytes)
            })
            .collect()
    }

    fn proof_json(index: usize, leaves: &[Hash], leaf: &Hash) -> Vec<u8> {
        let path: Vec<String> = merkle::inclusion_path(leaves, index)
            .iter()
            .map(format_digest)
            .collect();
        serde_json::to_vec(&serde_json::json!({
            "log_index": index,
            "tree_size": leaves.len(),
            "leaf_hash": format_digest(leaf),
            "audit_path": path,
            "root_hash": format_digest(&merkle::root(leaves)),
            "head_signed": false,
        }))
        .unwrap()
    }

    #[test]
    fn an_entry_is_recomputed_into_its_leaf_and_the_path_verifies() {
        // An entry whose canonical form is the CT reference leaf "2021" is not
        // constructible (a leaf input is JSON), so build the tree from real
        // entries instead: eight tiny entry documents, hashed the R6.46 way.
        let entries: Vec<String> = (0..8)
            .map(|i| format!(r#"{{"actor_id":null,"aggregate_id":"a{i}","event_id":"e{i}","event_type":"t","payload":{{}},"server_ts":"2026-09-04T16:00:0{i}.000000Z"}}"#))
            .collect();
        let leaves: Vec<Hash> = entries
            .iter()
            .map(|e| merkle::leaf_hash(&crate::canonicalize(e.as_bytes()).unwrap()))
            .collect();

        let wire_entry = r#"{ "log_index": 5, "leaf_hash": "x", "entry": { "server_ts": "2026-09-04T16:00:05.000000Z", "payload": {}, "event_type": "t", "event_id": "e5", "aggregate_id": "a5", "actor_id": null } }"#.to_string();
        let proof = proof_json(5, &leaves, &leaves[5]);

        let verified = verify_inclusion(wire_entry.as_bytes(), &proof).expect("verifies");
        assert_eq!(verified.log_index, 5);
        assert_eq!(verified.root, merkle::root(&leaves));

        // A tampered entry recomputes to a different leaf: the proof's stated
        // leaf disagrees first, and without that member the path itself fails.
        let tampered = wire_entry.replace("\"a5\"", "\"a9\"");
        let err = verify_inclusion(tampered.as_bytes(), &proof).unwrap_err();
        assert_eq!(err.predicate(), "curia/acta/leaf-mismatch");
        let without_leaf: Value = serde_json::from_slice(&proof).unwrap();
        let mut without_leaf = without_leaf.as_object().unwrap().clone();
        without_leaf.remove("leaf_hash");
        let err = verify_inclusion(
            tampered.as_bytes(),
            &serde_json::to_vec(&without_leaf).unwrap(),
        )
        .unwrap_err();
        assert_eq!(err.predicate(), "curia/acta/inclusion-invalid");
    }

    #[test]
    fn a_consistency_proof_on_the_wire_verifies_and_a_swapped_one_does_not() {
        let seven = leaves(7);
        let path: Vec<String> = merkle::consistency_path(&seven, 3)
            .iter()
            .map(format_digest)
            .collect();
        let body = serde_json::json!({
            "from_size": 3, "to_size": 7,
            "from_root": format_digest(&merkle::root(&seven[..3])),
            "to_root": format_digest(&merkle::root(&seven)),
            "path": path,
        });
        let verified = verify_consistency(&serde_json::to_vec(&body).unwrap()).expect("verifies");
        assert_eq!((verified.from_size, verified.to_size), (3, 7));

        let mut swapped = body.clone();
        swapped["from_root"] = body["to_root"].clone();
        let err = verify_consistency(&serde_json::to_vec(&swapped).unwrap()).unwrap_err();
        assert_eq!(err.predicate(), "curia/acta/consistency-invalid");
    }

    #[test]
    fn digest_form_is_strict() {
        assert!(parse_digest(&format!("sha256:{}", "0".repeat(64))).is_some());
        assert!(parse_digest(&"0".repeat(64)).is_none());
        assert!(parse_digest(&format!("sha256:{}", "A".repeat(64))).is_none());
        assert!(parse_digest(&format!("sha256:{}", "0".repeat(63))).is_none());
    }

    #[test]
    fn a_head_that_names_a_different_size_does_not_cover_a_proof() {
        let head = VerifiedHead {
            root: [1; 32],
            tree_size: 8,
            timestamp: String::new(),
            kid: String::new(),
            alg: String::new(),
        };
        assert_eq!(
            head_covers(&head, 7, &[1; 32]).unwrap_err().predicate(),
            "curia/acta/head-size-mismatch"
        );
        assert_eq!(
            head_covers(&head, 8, &[2; 32]).unwrap_err().predicate(),
            "curia/acta/head-root-mismatch"
        );
        assert!(head_covers(&head, 8, &[1; 32]).is_ok());
    }
}
