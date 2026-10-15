//! Offline verification of the Acta (§6.6): signed tree heads (R6.49), audit
//! paths (R6.48) and consistency proofs (R6.23), from the JSON the Forum
//! serves at `/v1/log/*`.
//!
//! The boundary that matters: [`verify_inclusion`] takes the *entry* -- the
//! six-member object from `/v1/log/entries/{index}` -- and recomputes the
//! leaf itself (R6.46). It never takes a leaf digest the Forum computed,
//! because a verifier that checks the Forum's arithmetic against the Forum's
//! own input passes a leaf that corresponds to nothing. Where the Forum also
//! states a `leaf_hash` -- on the proof, or on the entry route beside the
//! entry -- it is compared, as is the index the entry route states, and a
//! disagreement is a failure in its own right.
//!
//! [`verify_author`] is R6.54 (errata G16): authorship from the log alone.
//! A post's entry carries its canonical envelope and signature; its author's
//! key is carried by an `agent.key-bound` entry (R4.34). Both proven under one
//! signed head, the binding first, and the signature verifying under the key
//! the binding carries: no key set the Forum serves enters the check.
//! [`check_author_unanchored`] makes every one of those checks that needs no
//! head, for a reader that holds none.
//!
//! Every rejection is a typed [`ActaError`] with a predicate slug; nothing
//! here panics on malformed input.

use std::collections::HashMap;
use std::fmt;

use serde_json::error::Category;
use serde_json::value::RawValue;
use serde_json::Value;

use crate::display;
use crate::envelope::VerifyEnvelopeError;
use crate::jwk::{JwkError, JwkSet};
use crate::jws::{self, JwsError};
use crate::merkle::{self, Hash};

/// The `typ` of a signed tree head; a head is not a post and must not verify as one.
pub const HEAD_TYP: &str = "curia-head+jws";

/// The entry a post's acceptance is (R6.46): its payload carries the
/// canonical envelope and the detached signature.
pub const POST_ACCEPTED: &str = "post.accepted";

/// The entry that binds a key to an identity (R4.34): its payload carries
/// `agent_id`, `kid` and the public `jwk`.
pub const KEY_BOUND: &str = "agent.key-bound";

/// The enrollment record. Before R4.34 it was the only entry naming a key,
/// and it names the `kid` alone.
pub const ENROLLED: &str = "agent.enrolled";

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
    /// The leaf recomputed from the entry is not the leaf the proof, or the
    /// entry route, states.
    LeafMismatch,
    /// The entry route names one leaf index and the proof another.
    IndexMismatch { entry: u64, proof: u64 },
    /// The audit path does not lead from the leaf to the root.
    InclusionInvalid,
    /// The proof does not connect the two roots.
    ConsistencyInvalid,
    /// The head covers a different size than the proof is against.
    HeadSizeMismatch { head: u64, proof: u64 },
    /// The head's root is not the proof's root at that size.
    HeadRootMismatch,
    /// The entry offered as a post is not a `post.accepted` entry.
    NotAPost { event_type: String },
    /// The entry offered as the key's binding is not an `agent.key-bound`
    /// entry (nor an `agent.enrolled` one).
    NotAKeyBinding { event_type: String },
    /// The binding does not name the post's author and the post's `kid`.
    BindingMismatch(String),
    /// The author's binding of the post's `kid` is not earlier in the log
    /// than the post, so the log says nothing about which key was the
    /// author's when the post was accepted. Not a failure and not a pass
    /// (exit 3): a forger binds first at no cost, and what lands here is
    /// history older than its binding.
    BoundAfterPost { key: u64, post: u64 },
    /// The post's signature does not verify under the key the binding
    /// carries (the inner predicate says why).
    Author(VerifyEnvelopeError),
    /// The binding is the author's own `agent.enrolled`, earlier than the
    /// post and naming its `kid` (all an identity enrolled before R4.34 has;
    /// a reader cannot tell when one was made): the log names the `kid` and
    /// carries no key, so which key signed cannot be established from the
    /// log. Not a failure and not a pass (exit 3).
    KeyNotCarried { kid: String },
    /// With no head, the post and its key's entry are proven against two
    /// trees: no one head covers both, so they fail with no head as they
    /// fail with one.
    TreeMismatch { post: u64, key: u64 },
    /// R6.54 (add.): the post's key was retired at a leaf before the post.
    RetiredBeforePost {
        kid: String,
        retired: u64,
        post: u64,
    },
    /// R6.54 (add.): a lifecycle entry names, as the entry before it, one that is not the entry
    /// before it in the author's chain; or is a binding without a `prev` that is not the
    /// enrolment's own first binding (R4.38); or is out of order.
    BrokenLink {
        entry: u64,
        stated: Option<u64>,
        actual: Option<u64>,
    },
    /// R6.54 (add.): a rotation or retirement is not signed by a key that was live there, in the role
    /// it names (KF2): its signature does not verify, its header `kid` is not its role's member, or the
    /// key it names was never bound or had been retired.
    RotationUnauthorized { at: u64 },
    /// R6.54 (add.): a statement or an entry disagrees with its set or with itself (KF4, KF5), or an
    /// entry is not the author's, or names no author.
    StatementMismatch { at: u64, what: &'static str },
    /// R6.54 (add.): a lifecycle signature's header `typ` is not its kind's (KF1).
    LifecycleTypMismatch { at: u64 },
    /// R6.54 (add.), not established: an entry of a type none of the four, or a statement of a version
    /// this verifier does not know (KF4). Never verified, never skipped.
    LifecycleUnknownKind { what: String },
    /// R6.54 (add.), not established: no lifecycle was supplied, so an offline verifier cannot say the
    /// key was not retired.
    LifecycleNotSupplied,
    /// R6.54 (add.), not established: more than 1,024 lifecycle entries before the post.
    LifecycleTooLong,
    /// R6.57, not established: a declaration states a `reason` outside the closed set (KF4). Never verified, never skipped.
    LifecycleUnknownReason { reason: String },
    /// R6.57, not established: the log's key set gives the declaring key no usable `log_index` (absent, negative or not
    /// an I-JSON integer), so it cannot be said the key was published before the declaration (KF2, KF4's rule).
    LogKeyPositionUnknown { kid: String },
    /// R6.57: the declaration's header `kid` is not a key the log's key set publishes, or was published at or after the
    /// declaration (KF2).
    DeclarationKeyUnpublished { at: u64, kid: String },
    /// R6.57: the declaration does not verify under the log key its header names.
    DeclarationSignatureInvalid { at: u64 },
    /// R6.57, KF3 (1): a key the declaration names is bound to no entry of the author's stream as supplied.
    KidNotBoundToAgent { at: u64, kid: String },
    /// R6.57, KF3 (2) and (3): `from_index` is below the first key's binding or not below the declaration's index, or
    /// `kids` are not, in order, the cascade this verifier recomputes from the lifecycle.
    DeclarationMismatch { at: u64, what: &'static str },
    /// R6.57: a declaration that follows the post's key's use at a leaf before the post: a compromised key is honoured for
    /// nothing at or after the declaration, so the post could not have been accepted under it as the author's.
    CompromisedBeforePost {
        kid: String,
        declaration: u64,
        post: u64,
    },
    /// R6.57, disputed: the signature holds, and the post's key was declared compromised from a leaf at or below the post's
    /// by a declaration that follows it. Neither verified nor failed; the exit is 4.
    Disputed {
        kid: String,
        from_index: u64,
        declaration: u64,
        post: u64,
    },
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
            ActaError::IndexMismatch { .. } => "curia/acta/index-mismatch",
            ActaError::InclusionInvalid => "curia/acta/inclusion-invalid",
            ActaError::ConsistencyInvalid => "curia/acta/consistency-invalid",
            ActaError::HeadSizeMismatch { .. } => "curia/acta/head-size-mismatch",
            ActaError::HeadRootMismatch => "curia/acta/head-root-mismatch",
            ActaError::NotAPost { .. } => "curia/acta/not-a-post",
            ActaError::NotAKeyBinding { .. } => "curia/acta/not-a-key-binding",
            ActaError::BindingMismatch(_) => "curia/acta/binding-mismatch",
            ActaError::BoundAfterPost { .. } => "curia/acta/bound-after-post",
            ActaError::Author(err) => err.predicate(),
            ActaError::KeyNotCarried { .. } => "curia/acta/key-not-carried",
            ActaError::TreeMismatch { .. } => "curia/acta/tree-mismatch",
            ActaError::RetiredBeforePost { .. } => "curia/acta/retired-before-post",
            ActaError::BrokenLink { .. } => "curia/acta/broken-link",
            ActaError::RotationUnauthorized { .. } => "curia/acta/rotation-unauthorized",
            ActaError::StatementMismatch { .. } => "curia/acta/statement-mismatch",
            ActaError::LifecycleTypMismatch { .. } => "curia/acta/lifecycle-typ-mismatch",
            ActaError::LifecycleUnknownKind { .. } => "curia/acta/unknown-lifecycle-kind",
            ActaError::LifecycleNotSupplied => "curia/acta/lifecycle-not-supplied",
            ActaError::LifecycleTooLong => "curia/acta/lifecycle-too-long",
            ActaError::LifecycleUnknownReason { .. } => "curia/acta/unknown-reason",
            ActaError::LogKeyPositionUnknown { .. } => "curia/acta/log-key-position-unknown",
            ActaError::DeclarationKeyUnpublished { .. } => "curia/acta/declaration-key-unpublished",
            ActaError::DeclarationSignatureInvalid { .. } => {
                "curia/acta/declaration-signature-invalid"
            }
            ActaError::KidNotBoundToAgent { .. } => "curia/acta/kid-not-bound-to-agent",
            ActaError::DeclarationMismatch { .. } => "curia/acta/declaration-mismatch",
            ActaError::CompromisedBeforePost { .. } => "curia/acta/compromised-before-post",
            ActaError::Disputed { .. } => "curia/acta/disputed",
        }
    }

    /// R6.54's third outcome: the log carries no key for the post's `kid`
    /// from before the post. Neither a pass nor a failure, and `log author`
    /// exits 3 for exactly these.
    pub fn not_established(&self) -> bool {
        matches!(
            self,
            ActaError::KeyNotCarried { .. }
                | ActaError::BoundAfterPost { .. }
                | ActaError::LifecycleUnknownKind { .. }
                | ActaError::LifecycleNotSupplied
                | ActaError::LifecycleTooLong
                | ActaError::LifecycleUnknownReason { .. }
                | ActaError::LogKeyPositionUnknown { .. }
        )
    }

    /// R6.57's fourth outcome: the signature holds and who made it is in question. Distinct from not
    /// established (exit 3) and from a failure (exit 1): `log author` exits 4.
    pub fn disputed(&self) -> bool {
        matches!(self, ActaError::Disputed { .. })
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
                    "head names kid {} but its signature was made under {}",
                    display::literal(stated),
                    display::literal(signed)
                )
            }
            ActaError::LeafMismatch => write!(
                f,
                "the leaf recomputed from the entry is not the leaf the proof, or the entry route, states"
            ),
            ActaError::IndexMismatch { entry, proof } => write!(
                f,
                "the entry route names leaf {entry} and the proof is about leaf {proof}"
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
            ActaError::NotAPost { event_type } => {
                write!(
                    f,
                    "the post entry is {}, not `{POST_ACCEPTED}`",
                    display::literal(event_type)
                )
            }
            ActaError::NotAKeyBinding { event_type } => {
                write!(
                    f,
                    "the key entry is {}, not `{KEY_BOUND}`",
                    display::literal(event_type)
                )
            }
            ActaError::BindingMismatch(detail) => {
                write!(f, "the key entry does not bind the post's key: {detail}")
            }
            ActaError::BoundAfterPost { key, post } => write!(
                f,
                "the key is bound at leaf {key}, which is not before the post at leaf {post}"
            ),
            ActaError::Author(err) => {
                write!(f, "the post does not verify under the bound key: {err}")
            }
            ActaError::KeyNotCarried { kid } => write!(
                f,
                "the log names kid {} only in the author's enrollment, which carries no key",
                display::literal(kid)
            ),
            ActaError::TreeMismatch { post, key } => write!(
                f,
                "the post is proven in a tree of {post} leaves and the key's entry in a tree of {key}, or under another root: no one head covers both"
            ),
            ActaError::RetiredBeforePost {
                kid,
                retired,
                post,
            } => write!(
                f,
                "the author's key {} was retired at leaf {retired}, before the post at leaf {post}",
                display::literal(kid)
            ),
            ActaError::BrokenLink {
                entry,
                stated,
                actual,
            } => match (stated, actual) {
                (Some(stated), Some(actual)) => write!(
                    f,
                    "lifecycle entry {entry} names {stated} as the entry before it, and the entry before it is {actual}"
                ),
                (None, Some(actual)) => write!(
                    f,
                    "lifecycle entry {entry} is not after the entry before it, {actual}, or is a binding without a prev that is not the enrolment's first"
                ),
                _ => write!(
                    f,
                    "lifecycle entry {entry} has no entry before it to link from, or is a binding without a prev that is not the enrolment's first"
                ),
            },
            ActaError::RotationUnauthorized { at } => write!(
                f,
                "the lifecycle entry at leaf {at} is not signed by a key that was live there, in the role its statement names"
            ),
            ActaError::StatementMismatch { at, what } => write!(
                f,
                "the lifecycle entry at leaf {at} disagrees with its statement or its set: {what}"
            ),
            ActaError::LifecycleTypMismatch { at } => write!(
                f,
                "the signature of the lifecycle entry at leaf {at} does not carry its kind's header typ"
            ),
            ActaError::LifecycleUnknownKind { what } => write!(
                f,
                "the lifecycle holds an entry or a statement of a kind this verifier does not know: {}",
                display::literal(what)
            ),
            ActaError::LifecycleNotSupplied => write!(
                f,
                "no lifecycle was supplied for the author, so it cannot be said the key was not retired"
            ),
            ActaError::LifecycleTooLong => write!(
                f,
                "the author's lifecycle has more than 1,024 entries before the post"
            ),
            ActaError::LifecycleUnknownReason { reason } => write!(
                f,
                "the declaration states the reason {}, outside the closed set this verifier knows",
                display::literal(reason)
            ),
            ActaError::LogKeyPositionUnknown { kid } => write!(
                f,
                "the log's key set gives kid {} no log index, so it cannot be said it was published before the declaration",
                display::literal(kid)
            ),
            ActaError::DeclarationKeyUnpublished { at, kid } => write!(
                f,
                "the declaration at leaf {at} is signed under kid {}, which the log's key set does not publish before it",
                display::literal(kid)
            ),
            ActaError::DeclarationSignatureInvalid { at } => write!(
                f,
                "the declaration at leaf {at} does not verify under the log key its header names"
            ),
            ActaError::KidNotBoundToAgent { at, kid } => write!(
                f,
                "the declaration at leaf {at} names kid {}, which no entry of the author's stream binds",
                display::literal(kid)
            ),
            ActaError::DeclarationMismatch { at, what } => write!(
                f,
                "the declaration at leaf {at} disagrees with the lifecycle: {what}"
            ),
            ActaError::CompromisedBeforePost {
                kid,
                declaration,
                post,
            } => write!(
                f,
                "the author's key {} was declared compromised by the declaration at leaf {declaration}, before the post at leaf {post}",
                display::literal(kid)
            ),
            ActaError::Disputed {
                kid,
                from_index,
                declaration,
                post,
            } => write!(
                f,
                "the signature holds, and the author's key {} was declared compromised from leaf {from_index} by the declaration at leaf {declaration}, which follows the post at leaf {post}: do not cite it as the author's",
                display::literal(kid)
            ),
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
        detail: json_error(&e),
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

/// One key of the log's key set (R6.50) with where the log published it: the `log_index` member of its
/// `GET /v1/log/jwks` entry, which names the `log.key` entry that publishes it (KF2).
#[derive(Debug, Clone)]
pub struct LogKey {
    pub kid: String,
    pub key: crate::jwk::Jwk,
    /// `None` when the member is absent, negative, or not an I-JSON integer: the position is not given, and a
    /// declaration under this key cannot be said to follow its publication (KF2, KF4's rule).
    pub log_index: Option<u64>,
}

/// The log's key set with each key's position, read from `GET /v1/log/jwks`'s body.
///
/// The key material is parsed by [`JwkSet::parse`], the one JWK parser, so a key set this reads is a key set
/// [`verify_head`] reads, byte for byte; the position of each key is read beside it from the same document. A key
/// without a `kid` carries no position a declaration could name and is left out.
#[derive(Debug, Clone, Default)]
pub struct LogKeys {
    keys: Vec<LogKey>,
}

impl LogKeys {
    /// Parses the key set document. The same errors as `JwkSet::parse`.
    pub fn parse(log_jwks: &[u8]) -> Result<Self, ActaError> {
        let set = JwkSet::parse(log_jwks).map_err(ActaError::Jwks)?;
        // `JwkSet::parse` has admitted this document, so it parses here and `keys` is an array of as many entries.
        let positions: Vec<Option<u64>> = crate::json::parse(log_jwks)
            .ok()
            .and_then(|root| match root {
                crate::json::Value::Object(members) => members
                    .into_iter()
                    .find(|(name, _)| name == "keys")
                    .map(|(_, value)| value),
                _ => None,
            })
            .and_then(|keys| match keys {
                crate::json::Value::Array(items) => Some(items),
                _ => None,
            })
            .map(|items| items.iter().map(log_index_of).collect())
            .unwrap_or_default();

        let keys = set
            .keys()
            .iter()
            .enumerate()
            .filter_map(|(i, jwk)| {
                jwk.kid.clone().map(|kid| LogKey {
                    kid,
                    key: jwk.clone(),
                    log_index: positions.get(i).copied().flatten(),
                })
            })
            .collect();
        Ok(LogKeys { keys })
    }

    /// The key published under `kid`: the last one, as the head's own lookup takes the key set's last match.
    pub fn find(&self, kid: &str) -> Option<&LogKey> {
        self.keys.iter().rev().find(|k| k.kid == kid)
    }
}

/// A key entry's `log_index` as a non-negative I-JSON integer, else `None`.
fn log_index_of(entry: &crate::json::Value) -> Option<u64> {
    let crate::json::Value::Object(members) = entry else {
        return None;
    };
    match members.iter().find(|(name, _)| name == "log_index") {
        Some((_, crate::json::Value::Number(n)))
            if n.fract() == 0.0 && *n >= 0.0 && *n <= MAX_SAFE_INTEGER_F64 =>
        {
            Some(*n as u64)
        }
        _ => None,
    }
}

const MAX_SAFE_INTEGER_F64: f64 = 9_007_199_254_740_991.0;

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
/// entry (R6.46). What the Forum states beside it is compared with it, never
/// used in its place: the proof's own `leaf_hash` and the entry route's, when
/// present, must be that leaf, and the entry route's `log_index`, when
/// present, must be the proof's.
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
        detail: json_error(&e),
    })?;
    let log_index = u64_member(&proof, "log_index", "proof")?;
    let tree_size = u64_member(&proof, "tree_size", "proof")?;
    let root = digest_member(&proof, "root_hash", "proof")?;
    if proof.get("leaf_hash").is_some() && digest_member(&proof, "leaf_hash", "proof")? != leaf {
        return Err(ActaError::LeafMismatch);
    }
    // The entry route's own statements about the leaf it serves: the Forum's
    // word, like the proof's `leaf_hash`, and a disagreement is the served
    // material contradicting itself.
    let stated: Value = serde_json::from_slice(entry_json).map_err(|e| ActaError::Malformed {
        what: "entry document",
        detail: json_error(&e),
    })?;
    if stated.get("leaf_hash").is_some()
        && digest_member(&stated, "leaf_hash", "entry document")? != leaf
    {
        return Err(ActaError::LeafMismatch);
    }
    if stated.get("log_index").is_some() {
        let entry_index = u64_member(&stated, "log_index", "entry document")?;
        if entry_index != log_index {
            return Err(ActaError::IndexMismatch {
                entry: entry_index,
                proof: log_index,
            });
        }
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
        detail: json_error(&e),
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

/// Authorship established from the log alone (R6.54).
#[derive(Debug, Clone)]
pub struct VerifiedAuthor {
    pub author: String,
    pub kid: String,
    pub alg: String,
    pub key_index: u64,
    pub post_index: u64,
}

/// R6.54 (errata G16): the post in `post_entry` was signed by the key its
/// author's `agent.key-bound` entry (`key_entry`) carries, and the log bound
/// that key before it accepted the post, all under `head`.
///
/// Both entries' leaves are recomputed and proven (R6.46, R6.48) and tied to
/// the one head, so their order is the log's order. The post's envelope is
/// ADMITted and its protected header parsed before anything is read from
/// either. The key entry must bind the post's own author and `kid`, in the
/// author's own stream, or it is [`ActaError::BindingMismatch`], whatever its
/// type. The author's binding at a leaf not before the post is
/// [`ActaError::BoundAfterPost`], and the author's own `agent.enrolled`
/// naming it is [`ActaError::KeyNotCarried`]: each says the log holds
/// no key for the post from before it, and neither is a failure
/// ([`ActaError::not_established`]). Only then is the signature checked, over
/// the envelope the post's own entry carries, under a key set holding only
/// the key the binding carries: nothing a key set endpoint serves is read.
pub fn verify_author(
    post_entry: &[u8],
    post_proof: &[u8],
    key_entry: &[u8],
    key_proof: &[u8],
    head: &VerifiedHead,
) -> Result<VerifiedAuthor, ActaError> {
    author_under(post_entry, post_proof, key_entry, key_proof, Some(head))
}

/// R6.54 for a reader holding no head: every check [`verify_author`] makes
/// that needs none, in the same order, so a document that fails one fails here
/// exactly as it fails there. The two proofs must still be against one tree
/// ([`ActaError::TreeMismatch`]), since a head covering both could cover no
/// other. `Ok` is not a verdict: it says those checks held over a tree no
/// signed head anchors, whose root, and so the order of its leaves, is the
/// Forum's word.
pub fn check_author_unanchored(
    post_entry: &[u8],
    post_proof: &[u8],
    key_entry: &[u8],
    key_proof: &[u8],
) -> Result<(), ActaError> {
    author_under(post_entry, post_proof, key_entry, key_proof, None).map(|_| ())
}

/// [`verify_author`]'s checks, under `head` when there is one.
fn author_under(
    post_entry: &[u8],
    post_proof: &[u8],
    key_entry: &[u8],
    key_proof: &[u8],
    head: Option<&VerifiedHead>,
) -> Result<VerifiedAuthor, ActaError> {
    let post = verify_inclusion(post_entry, post_proof)?;
    let key = verify_inclusion(key_entry, key_proof)?;
    match head {
        Some(head) => {
            head_covers(head, post.tree_size, &post.root)?;
            head_covers(head, key.tree_size, &key.root)?;
        }
        None => {
            if key.tree_size != post.tree_size || key.root != post.root {
                return Err(ActaError::TreeMismatch {
                    post: post.tree_size,
                    key: key.tree_size,
                });
            }
        }
    }

    let post_fields = entry_fields(post_entry)?;
    let post_type = str_at(&post_fields, &["event_type"], "post entry")?;
    if post_type != POST_ACCEPTED {
        return Err(ActaError::NotAPost {
            event_type: post_type.to_string(),
        });
    }
    let canonical = str_at(&post_fields, &["payload", "canonical"], "post entry")?;
    let signature = str_at(&post_fields, &["payload", "signature"], "post entry")?;

    // The post's own entry carries the envelope as the Forum persisted it;
    // the submission is rebuilt around it verbatim, never re-encoded.
    let signature_json = serde_json::to_string(signature).map_err(|e| ActaError::Malformed {
        what: "post entry",
        detail: json_error(&e),
    })?;
    let submission = format!("{{\"envelope\":{canonical},\"signature\":{signature_json}}}");
    // ADMIT, as the signature check does: an envelope no Forum could have
    // accepted fails here, before the binding's type or order is read.
    crate::json::admit(submission.as_bytes()).map_err(|e| ActaError::Malformed {
        what: "post entry",
        detail: e.to_string(),
    })?;
    let (post_author, post_kid) = post_author_and_kid(canonical, signature)?;

    let key_fields = entry_fields(key_entry)?;
    let key_type = str_at(&key_fields, &["event_type"], "key entry")?;
    if key_type != KEY_BOUND && key_type != ENROLLED {
        return Err(ActaError::NotAKeyBinding {
            event_type: key_type.to_string(),
        });
    }
    let bound_kid = str_at(&key_fields, &["payload", "kid"], "key entry")?;
    let bound_agent = str_at(&key_fields, &["payload", "agent_id"], "key entry")?;
    let aggregate = str_at(&key_fields, &["aggregate_id"], "key entry")?;

    // Whose binding, of which kid, before anything about its key: an entry
    // for another identity or another kid binds nothing the post names,
    // whichever type it is.
    if aggregate != bound_agent || bound_agent != post_author || bound_kid != post_kid {
        return Err(ActaError::BindingMismatch(format!(
            "the entry binds kid {} to {} in stream {}, and the post is {}'s under kid {}",
            display::literal(bound_kid),
            display::literal(bound_agent),
            display::literal(aggregate),
            display::literal(&post_author),
            display::literal(&post_kid)
        )));
    }

    // R6.54: a binding after the post is the log's silence about the key
    // the post was accepted under, not a contradiction of it.
    if key.log_index >= post.log_index {
        return Err(ActaError::BoundAfterPost {
            key: key.log_index,
            post: post.log_index,
        });
    }
    if key_type == ENROLLED {
        return Err(ActaError::KeyNotCarried {
            kid: bound_kid.to_string(),
        });
    }
    let jwk = key_fields
        .get("payload")
        .and_then(|p| p.get("jwk"))
        .filter(|j| j.is_object())
        .ok_or(ActaError::MissingField {
            what: "key entry",
            field: "jwk",
        })?;

    let jwks = serde_json::to_vec(&serde_json::json!({ "keys": [jwk] })).map_err(|e| {
        ActaError::Malformed {
            what: "key entry",
            detail: json_error(&e),
        }
    })?;
    let provenance =
        crate::verify_envelope(submission.as_bytes(), &jwks).map_err(ActaError::Author)?;

    Ok(VerifiedAuthor {
        author: provenance.author,
        kid: provenance.kid,
        alg: provenance.alg,
        key_index: key.log_index,
        post_index: post.log_index,
    })
}

/// The post's author, from its canonical envelope, and the `kid` its detached
/// signature's protected header names; read before any key is chosen, so the
/// binding can be compared with the post before it is trusted for anything.
/// The envelope has been ADMITted by the caller; the header is parsed here
/// by the parser the signature check uses, which refuses a member named twice,
/// so neither is read from a document with two answers. The signature itself
/// is verified afterwards, over the same bytes.
pub(crate) fn post_author_and_kid(
    canonical: &str,
    signature: &str,
) -> Result<(String, String), ActaError> {
    use base64::Engine;
    let malformed = |detail: String| ActaError::Malformed {
        what: "post entry",
        detail,
    };
    let envelope: Value = serde_json::from_str(canonical).map_err(|e| malformed(json_error(&e)))?;
    let author = envelope
        .get("author")
        .and_then(Value::as_str)
        .ok_or_else(|| malformed("the envelope names no author".to_string()))?;
    let header_bytes = signature
        .split('.')
        .next()
        .and_then(|h| {
            base64::engine::general_purpose::URL_SAFE_NO_PAD
                .decode(h)
                .ok()
        })
        .ok_or_else(|| malformed("the signature has no readable protected header".to_string()))?;
    crate::json::parse(&header_bytes)
        .map_err(|e| malformed(format!("the signature's protected header: {e}")))?;
    let header: Value =
        serde_json::from_slice(&header_bytes).map_err(|e| malformed(json_error(&e)))?;
    let kid = header
        .get("kid")
        .and_then(Value::as_str)
        .ok_or_else(|| malformed("the signature's header names no kid".to_string()))?;
    Ok((author.to_string(), kid.to_string()))
}

/// The `entry` member of an entry document, parsed.
pub(crate) fn entry_fields(entry_json: &[u8]) -> Result<Value, ActaError> {
    let document: Value = serde_json::from_slice(entry_json).map_err(|e| ActaError::Malformed {
        what: "entry document",
        detail: json_error(&e),
    })?;
    document
        .get("entry")
        .cloned()
        .ok_or(ActaError::MissingField {
            what: "entry document",
            field: "entry",
        })
}

/// The string at `path` inside `value`, or which member is missing.
pub(crate) fn str_at<'a>(
    value: &'a Value,
    path: &[&'static str],
    what: &'static str,
) -> Result<&'a str, ActaError> {
    let mut at = value;
    for field in path {
        at = at
            .get(*field)
            .ok_or(ActaError::MissingField { what, field })?;
    }
    at.as_str().ok_or(ActaError::MissingField {
        what,
        field: path.last().copied().unwrap_or("value"),
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

/// R10.63: what a serde_json error says, in this verifier's own words.
/// serde_json's message can quote the document it refused -- `invalid type:
/// string "..."`, in Rust's debug form, where a look-alike letter stands
/// for itself -- so it is never written: the refusal names the kind of error
/// and where it is, and no value.
fn json_error(e: &serde_json::Error) -> String {
    let kind = match e.classify() {
        Category::Io => "unreadable",
        Category::Syntax => "not well-formed JSON",
        Category::Data => "JSON of the wrong shape",
        Category::Eof => "JSON that ends before its value does",
    };
    format!("{kind}, at line {}, column {}", e.line(), e.column())
}

fn raw_members(
    json: &[u8],
    what: &'static str,
) -> Result<HashMap<String, Box<RawValue>>, ActaError> {
    serde_json::from_slice(json).map_err(|e| ActaError::Malformed {
        what,
        detail: json_error(&e),
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

        let wire_entry = format!(
            r#"{{ "log_index": 5, "leaf_hash": "{}", "entry": {{ "server_ts": "2026-09-04T16:00:05.000000Z", "payload": {{}}, "event_type": "t", "event_id": "e5", "aggregate_id": "a5", "actor_id": null }} }}"#,
            format_digest(&leaves[5])
        );
        let proof = proof_json(5, &leaves, &leaves[5]);

        let verified = verify_inclusion(wire_entry.as_bytes(), &proof).expect("verifies");
        assert_eq!(verified.log_index, 5);
        assert_eq!(verified.root, merkle::root(&leaves));

        // What the entry route states beside the entry is compared, not taken:
        // another leaf hash, or another index, fails an entry that is itself
        // intact.
        let other_leaf = wire_entry.replace(&format_digest(&leaves[5]), &format_digest(&leaves[4]));
        let err = verify_inclusion(other_leaf.as_bytes(), &proof).unwrap_err();
        assert_eq!(err.predicate(), "curia/acta/leaf-mismatch");
        let other_index = wire_entry.replace("\"log_index\": 5", "\"log_index\": 4");
        let err = verify_inclusion(other_index.as_bytes(), &proof).unwrap_err();
        assert_eq!(err.predicate(), "curia/acta/index-mismatch");

        // A tampered entry recomputes to a different leaf: the proof's stated
        // leaf disagrees first, and without that member, and without the entry
        // route's, the path itself fails.
        let tampered = wire_entry.replace("\"a5\"", "\"a9\"");
        let err = verify_inclusion(tampered.as_bytes(), &proof).unwrap_err();
        assert_eq!(err.predicate(), "curia/acta/leaf-mismatch");
        let without_leaf: Value = serde_json::from_slice(&proof).unwrap();
        let mut without_leaf = without_leaf.as_object().unwrap().clone();
        without_leaf.remove("leaf_hash");
        let bare: Value = serde_json::from_str(&tampered).unwrap();
        let mut bare = bare.as_object().unwrap().clone();
        bare.remove("leaf_hash");
        let err = verify_inclusion(
            &serde_json::to_vec(&bare).unwrap(),
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
