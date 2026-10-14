//! R6.54 (add.): the author's lifecycle, folded.
//!
//! [`verify_author`](crate::acta::verify_author) shows that the key which signed a post was bound to its
//! author before it. It cannot show that the key was still the author's when the post was accepted: a key
//! can be retired, and the Forum can omit the entry that says so. [`verify_lifecycle`] folds every
//! lifecycle entry of the author, in log order, from the author's `agent.enrolled` through the first entry
//! at or after the post, and answers for the key that signed the post.
//!
//! The rules are spec §4.5, §4.6 and §4.8, and `Curia.Client`'s `LifecycleCheck` writes the same ones; the
//! two are compared on `conformance/lifecycle/`. In brief:
//!
//! * each entry is read for its type, then its statement's `typ`, before anything else (KF4): an unknown
//!   type or version is *not established*, whatever it carries and whoever signed it;
//! * each entry is proven under the one signed head, belongs to the author, and links from the entry
//!   before it in the author's own chain; the only entry without a `prev` is the enrolment's first
//!   binding, as the one entry after `agent.enrolled`, under the enrolment's `kid`, with exactly
//!   `{agent_id, jwk, kid}` (R4.38);
//! * a rotation or a retirement is a canonical statement of a closed member set (KF4, KF5), whose
//!   signatures carry its kind's header `typ` (KF1) and name their roles' members as `kid` (KF2), and
//!   verify under a key that was bound and not retired at that point;
//! * the post's key is verified when it was bound before the post and not retired before it.
//!
//! No approval is consulted: a reader folds on the merits (§4.8). Nothing here panics on malformed input.

use std::collections::HashMap;

use base64::Engine;
use serde_json::{Map, Value};

use crate::acta::{
    self, entry_fields, head_covers, post_author_and_kid, str_at, ActaError, VerifiedHead,
    ENROLLED, KEY_BOUND, POST_ACCEPTED,
};
use crate::jwk::JwkSet;
use crate::jws;

/// The entry type of a retirement.
pub const KEY_RETIRED: &str = "agent.key-retired";

/// A rotation statement's `typ`.
pub const ROTATION_STATEMENT_TYP: &str = "curia.key-rotation/1";
/// A retirement statement's `typ`.
pub const RETIREMENT_STATEMENT_TYP: &str = "curia.key-retirement/1";
/// The header `typ` of a rotation's signatures (KF1).
pub const ROTATION_HEADER_TYP: &str = "curia-key-rotation+jws";
/// The header `typ` of a retirement's signature (KF1).
pub const RETIREMENT_HEADER_TYP: &str = "curia-key-retirement+jws";

/// Every statement `typ` this verifier knows (KF4). A `typ` outside it is a version or a kind this
/// verifier cannot fold; one inside it, in the other kind's entry, is a statement that is not this
/// entry's.
const KNOWN_STATEMENT_TYPS: [&str; 2] = [ROTATION_STATEMENT_TYP, RETIREMENT_STATEMENT_TYP];

/// How many lifecycle entries before a post a reader will fold (spec §4.8).
pub const MAX_ENTRIES_BEFORE_POST: usize = 1024;

const ENROLMENT_BINDING_MEMBERS: [&str; 3] = ["agent_id", "jwk", "kid"];
const ROTATION_ENTRY_MEMBERS: [&str; 8] = [
    "agent_id",
    "authorization",
    "authorized_by",
    "jwk",
    "kid",
    "possession",
    "prev",
    "statement",
];
const RETIREMENT_ENTRY_MEMBERS: [&str; 5] =
    ["agent_id", "authorization", "kid", "prev", "statement"];
const ROTATION_STATEMENT_MEMBERS: [&str; 6] =
    ["agent_id", "authorized_by", "jwk", "kid", "prev", "typ"];
const RETIREMENT_STATEMENT_MEMBERS: [&str; 4] = ["agent_id", "kid", "prev", "typ"];

const MAX_SAFE_INTEGER: u64 = 9_007_199_254_740_991;

/// What a lifecycle that folds establishes about the post's key.
#[derive(Debug, Clone)]
pub struct VerifiedLifecycle {
    /// The `kid` the post's signature names.
    pub kid: String,
    /// The log index at which that key was bound.
    pub bound_at: u64,
    pub post_index: u64,
    /// How many lifecycle entries were folded.
    pub entries_checked: usize,
    /// The log index of the last one.
    pub last_index: u64,
}

/// [`verify_lifecycle_under`] with the head verified here, against `log_jwks` (R6.49), as the CLI holds
/// them: the raw bodies of `GET /v1/log/head` and `GET /v1/log/jwks`.
pub fn verify_lifecycle(
    post_entry: &[u8],
    chain: &[(Vec<u8>, Vec<u8>)],
    head: &[u8],
    log_jwks: &[u8],
) -> Result<VerifiedLifecycle, ActaError> {
    let head = acta::verify_head(head, log_jwks)?;
    verify_lifecycle_under(post_entry, chain, &head)
}

/// The fold itself. `post_entry` is the body of `GET /v1/log/entries/{index}` for the post; `chain`
/// holds, in log order, each lifecycle entry's body and the body of its proof against the tree
/// `head` covers: every entry of the author up to the first at or after the post.
pub fn verify_lifecycle_under(
    post_entry: &[u8],
    chain: &[(Vec<u8>, Vec<u8>)],
    head: &VerifiedHead,
) -> Result<VerifiedLifecycle, ActaError> {
    let post = read_post(post_entry)?;

    if chain.is_empty() {
        return Err(ActaError::LifecycleNotSupplied);
    }

    // Counted from the entries' own indices before any is folded: a lifecycle too long to fold is
    // not established, whatever its first entries hold.
    let before_post = chain
        .iter()
        .filter_map(|(entry, _)| entry_log_index(entry).ok())
        .filter(|index| *index < post.index)
        .count();
    if before_post > MAX_ENTRIES_BEFORE_POST {
        return Err(ActaError::LifecycleTooLong);
    }

    let mut fold = Fold::new(post.author.clone());
    for (entry_json, proof_json) in chain {
        let index = fold.fold_entry(entry_json, proof_json, head)?;
        if index >= post.index {
            break;
        }
    }

    // The post's key. Retired before the post is a failure whatever else holds.
    if let Some(retired) = fold.retired.get(&post.kid) {
        if *retired < post.index {
            return Err(ActaError::RetiredBeforePost {
                kid: post.kid,
                retired: *retired,
                post: post.index,
            });
        }
    }
    match fold.bound.get(&post.kid) {
        Some((_, bound_at)) if *bound_at < post.index => Ok(VerifiedLifecycle {
            kid: post.kid,
            bound_at: *bound_at,
            post_index: post.index,
            entries_checked: fold.count,
            last_index: fold.prev_index.unwrap_or(0),
        }),
        // Bound at or after the post, or by no entry the lifecycle holds: the log holds no key for the
        // post from before it, which is its silence and not a contradiction (R6.54).
        bound => Err(ActaError::BoundAfterPost {
            key: bound
                .map(|(_, at)| *at)
                .or(fold.prev_index)
                .unwrap_or(post.index),
            post: post.index,
        }),
    }
}

struct Post {
    author: String,
    kid: String,
    index: u64,
}

/// The post as the log records it: its author and signature `kid`, and its leaf's index, from the
/// post's own entry (the entry route states the index; the inclusion check proved it).
fn read_post(post_entry: &[u8]) -> Result<Post, ActaError> {
    let fields = entry_fields(post_entry)?;
    let post_type = str_at(&fields, &["event_type"], "post entry")?;
    if post_type != POST_ACCEPTED {
        return Err(ActaError::NotAPost {
            event_type: post_type.to_string(),
        });
    }
    let canonical = str_at(&fields, &["payload", "canonical"], "post entry")?;
    let signature = str_at(&fields, &["payload", "signature"], "post entry")?;
    let (author, kid) = post_author_and_kid(canonical, signature)?;
    Ok(Post {
        author,
        kid,
        index: entry_log_index(post_entry)?,
    })
}

fn entry_log_index(entry_json: &[u8]) -> Result<u64, ActaError> {
    let document: Value = serde_json::from_slice(entry_json).map_err(|_| ActaError::Malformed {
        what: "entry document",
        detail: "not well-formed JSON".to_string(),
    })?;
    document
        .get("log_index")
        .and_then(Value::as_u64)
        .ok_or(ActaError::MissingField {
            what: "entry document",
            field: "log_index",
        })
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum Kind {
    Enrolled,
    Bound,
    Retired,
}

struct Fold {
    author: String,
    /// The log index of the entry before the next one in the author's chain.
    prev_index: Option<u64>,
    /// How many entries of the author's chain have been folded.
    count: usize,
    first_was_enrolment: bool,
    enrolment_kid: Option<String>,
    /// Each bound key's set, for verifying under it, and where it was bound.
    bound: HashMap<String, (JwkSet, u64)>,
    retired: HashMap<String, u64>,
}

/// A rotation or a retirement, read from its statement: the exact bytes it was signed over, and the
/// members both kinds share.
struct Statement {
    bytes: Vec<u8>,
    fields: Map<String, Value>,
}

impl Fold {
    fn new(author: String) -> Self {
        Fold {
            author,
            prev_index: None,
            count: 0,
            first_was_enrolment: false,
            enrolment_kid: None,
            bound: HashMap::new(),
            retired: HashMap::new(),
        }
    }

    /// One entry, in KF4's order: its type, then its statement's `typ`, then everything else. Returns
    /// the entry's log index.
    fn fold_entry(
        &mut self,
        entry_json: &[u8],
        proof_json: &[u8],
        head: &VerifiedHead,
    ) -> Result<u64, ActaError> {
        let fields = entry_fields(entry_json)?;
        let event_type = str_at(&fields, &["event_type"], "lifecycle entry")?.to_string();
        let kind = match event_type.as_str() {
            ENROLLED => Kind::Enrolled,
            KEY_BOUND => Kind::Bound,
            KEY_RETIRED => Kind::Retired,
            _ => return Err(ActaError::LifecycleUnknownKind { what: event_type }),
        };

        let payload =
            fields
                .get("payload")
                .and_then(Value::as_object)
                .ok_or(ActaError::MissingField {
                    what: "lifecycle entry",
                    field: "payload",
                })?;
        let statement_text = payload.get("statement").and_then(Value::as_str);
        let at = entry_log_index(entry_json).unwrap_or(0);

        // The statement's `typ`, read from the still-unverified statement only to choose which rules
        // apply: another version of this kind is not one this verifier knows (KF4), and a `typ` of
        // another kind is a statement that is not this entry's.
        if let (Some(text), Kind::Bound | Kind::Retired) = (statement_text, kind) {
            let expected = if kind == Kind::Bound {
                ROTATION_STATEMENT_TYP
            } else {
                RETIREMENT_STATEMENT_TYP
            };
            // A statement that does not parse under the ADMIT profile (a repeated member, say) has no
            // readable `typ`: it is malformed, not a future version, and the checks after this one
            // refuse it.
            if let Some(typ) = crate::json::parse(text.as_bytes())
                .ok()
                .and_then(|_| serde_json::from_str::<Value>(text).ok())
                .and_then(|v| v.get("typ").and_then(Value::as_str).map(str::to_string))
            {
                if typ != expected {
                    return if KNOWN_STATEMENT_TYPS.contains(&typ.as_str()) {
                        Err(mismatch(at, "the statement is not this entry's kind"))
                    } else {
                        Err(ActaError::LifecycleUnknownKind { what: typ })
                    };
                }
            }
        }

        let proven = acta::verify_inclusion(entry_json, proof_json)?;
        head_covers(head, proven.tree_size, &proven.root)?;
        let index = proven.log_index;

        let aggregate = str_at(&fields, &["aggregate_id"], "lifecycle entry")?;
        let agent = payload.get("agent_id").and_then(Value::as_str);
        if aggregate != self.author || agent != Some(self.author.as_str()) {
            return Err(mismatch(index, "the entry is not the author's"));
        }
        if self.prev_index.is_some_and(|prev| index <= prev) {
            return Err(ActaError::BrokenLink {
                entry: index,
                stated: None,
                actual: self.prev_index,
            });
        }

        match kind {
            Kind::Enrolled => self.enrolled(payload, index)?,
            Kind::Bound if statement_text.is_none() => self.enrolment_binding(payload, index)?,
            Kind::Bound => self.rotation(payload, index)?,
            Kind::Retired => self.retirement(payload, index)?,
        }
        self.count += 1;
        self.prev_index = Some(index);
        Ok(index)
    }

    /// `agent.enrolled` opens the author's chain, and only opens it.
    fn enrolled(&mut self, payload: &Map<String, Value>, index: u64) -> Result<(), ActaError> {
        if self.count != 0 {
            return Err(ActaError::BrokenLink {
                entry: index,
                stated: None,
                actual: self.prev_index,
            });
        }
        self.first_was_enrolment = true;
        self.enrolment_kid = payload
            .get("kid")
            .and_then(Value::as_str)
            .map(str::to_string);
        Ok(())
    }

    /// R4.38: a binding without a `prev` is the enrolment's first binding only as the one entry after
    /// `agent.enrolled`, under its `kid`, with exactly `{agent_id, jwk, kid}`; anywhere else it is a
    /// broken link, and a wrong member set is a mismatch.
    fn enrolment_binding(
        &mut self,
        payload: &Map<String, Value>,
        index: u64,
    ) -> Result<(), ActaError> {
        let kid = payload.get("kid").and_then(Value::as_str);
        let enrolment_only = self.count == 1 && self.first_was_enrolment;
        if !enrolment_only || kid.is_none() || kid != self.enrolment_kid.as_deref() {
            return Err(ActaError::BrokenLink {
                entry: index,
                stated: None,
                actual: self.prev_index,
            });
        }
        if !has_exactly(payload, &ENROLMENT_BINDING_MEMBERS) {
            return Err(mismatch(
                index,
                "the binding's member set is not {agent_id, jwk, kid}",
            ));
        }
        let kid = kid.unwrap_or_default();
        let jwk = payload
            .get("jwk")
            .filter(|j| j.is_object())
            .ok_or_else(|| mismatch(index, "the binding carries no jwk"))?;
        let set =
            key_set(jwk).ok_or_else(|| mismatch(index, "the binding's jwk is not a usable key"))?;
        if jwk.get("kid").and_then(Value::as_str) != Some(kid) {
            return Err(mismatch(index, "the binding's jwk names another kid"));
        }
        self.bound.insert(kid.to_string(), (set, index));
        Ok(())
    }

    fn rotation(&mut self, payload: &Map<String, Value>, index: u64) -> Result<(), ActaError> {
        if !has_exactly(payload, &ROTATION_ENTRY_MEMBERS) {
            return Err(mismatch(
                index,
                "the entry's member set is not a rotation's",
            ));
        }
        let statement = read_statement(
            payload,
            index,
            &ROTATION_STATEMENT_MEMBERS,
            ROTATION_STATEMENT_TYP,
        )?;
        let s = &statement.fields;

        let authorized_by = text(s, "authorized_by", index)?;
        let kid = text(s, "kid", index)?;
        let jwk = s
            .get("jwk")
            .filter(|j| j.is_object())
            .ok_or_else(|| mismatch(index, "the statement's jwk is not an object"))?;

        // KF5: the alg is the one the key type determines, and a key cannot authorize its own binding.
        if alg_of(jwk).is_none() || jwk.get("alg").and_then(Value::as_str) != alg_of(jwk) {
            return Err(mismatch(
                index,
                "the jwk's alg is not the one its key type determines",
            ));
        }
        if authorized_by == kid {
            return Err(mismatch(index, "a key cannot authorize its own binding"));
        }
        // The jwk is the key's public rendering and nothing more, under the statement's kid.
        let members = if alg_of(jwk) == Some("ES256") {
            vec!["alg", "crv", "kid", "kty", "x", "y"]
        } else {
            vec!["alg", "crv", "kid", "kty", "x"]
        };
        if !jwk.as_object().is_some_and(|o| has_exactly(o, &members))
            || jwk.get("kid").and_then(Value::as_str) != Some(kid)
        {
            return Err(mismatch(
                index,
                "the jwk is not the key's public rendering under the statement's kid",
            ));
        }
        let new_key = key_set(jwk).ok_or_else(|| mismatch(index, "the jwk is not a usable key"))?;

        // Every member the entry states beside the statement equals the statement's own.
        for member in ["agent_id", "authorized_by", "kid", "prev", "jwk"] {
            if payload.get(member) != s.get(member) {
                return Err(mismatch(
                    index,
                    "an entry member differs from its statement's",
                ));
            }
        }

        self.link(s, index)?;

        // KF1, then KF2: the header typ of each signature is a rotation's, and each names its
        // role's member as kid.
        let authorization = signature(payload, "authorization", index)?;
        let possession = signature(payload, "possession", index)?;
        for compact in [authorization, possession] {
            if header_of(compact).0.as_deref() != Some(ROTATION_HEADER_TYP) {
                return Err(ActaError::LifecycleTypMismatch { at: index });
            }
        }
        if header_of(authorization).1.as_deref() != Some(authorized_by)
            || header_of(possession).1.as_deref() != Some(kid)
        {
            return Err(ActaError::RotationUnauthorized { at: index });
        }

        // The authorizing key was bound and not retired before this entry, and both signatures verify.
        let Some((authorizer, _)) = self.bound.get(authorized_by) else {
            return Err(ActaError::RotationUnauthorized { at: index });
        };
        if self.retired.contains_key(authorized_by) {
            return Err(ActaError::RotationUnauthorized { at: index });
        }
        jws::verify_typed(
            authorization,
            &statement.bytes,
            authorizer,
            ROTATION_HEADER_TYP,
        )
        .map_err(|_| ActaError::RotationUnauthorized { at: index })?;
        jws::verify_typed(possession, &statement.bytes, &new_key, ROTATION_HEADER_TYP)
            .map_err(|_| ActaError::RotationUnauthorized { at: index })?;

        if self.bound.contains_key(kid) {
            return Err(mismatch(index, "the kid is already bound"));
        }
        self.bound.insert(kid.to_string(), (new_key, index));
        Ok(())
    }

    fn retirement(&mut self, payload: &Map<String, Value>, index: u64) -> Result<(), ActaError> {
        if !has_exactly(payload, &RETIREMENT_ENTRY_MEMBERS) {
            return Err(mismatch(
                index,
                "the entry's member set is not a retirement's",
            ));
        }
        let statement = read_statement(
            payload,
            index,
            &RETIREMENT_STATEMENT_MEMBERS,
            RETIREMENT_STATEMENT_TYP,
        )?;
        let s = &statement.fields;
        let kid = text(s, "kid", index)?;

        for member in ["agent_id", "kid", "prev"] {
            if payload.get(member) != s.get(member) {
                return Err(mismatch(
                    index,
                    "an entry member differs from its statement's",
                ));
            }
        }

        self.link(s, index)?;

        // KF1, KF2: the retirement is made by the key it retires, under a retirement's typ.
        let authorization = signature(payload, "authorization", index)?;
        if header_of(authorization).0.as_deref() != Some(RETIREMENT_HEADER_TYP) {
            return Err(ActaError::LifecycleTypMismatch { at: index });
        }
        if header_of(authorization).1.as_deref() != Some(kid) {
            return Err(ActaError::RotationUnauthorized { at: index });
        }

        let Some((key, _)) = self.bound.get(kid) else {
            return Err(ActaError::RotationUnauthorized { at: index });
        };
        if self.retired.contains_key(kid) {
            return Err(ActaError::RotationUnauthorized { at: index });
        }
        jws::verify_typed(authorization, &statement.bytes, key, RETIREMENT_HEADER_TYP)
            .map_err(|_| ActaError::RotationUnauthorized { at: index })?;

        self.retired.insert(kid.to_string(), index);
        Ok(())
    }

    /// The statement's `prev` is the index of the entry before it in the author's chain.
    fn link(&self, statement: &Map<String, Value>, index: u64) -> Result<(), ActaError> {
        let stated = statement.get("prev").and_then(Value::as_u64);
        if stated.is_none() || stated != self.prev_index {
            return Err(ActaError::BrokenLink {
                entry: index,
                stated,
                actual: self.prev_index,
            });
        }
        Ok(())
    }
}

fn mismatch(at: u64, what: &'static str) -> ActaError {
    ActaError::StatementMismatch { at, what }
}

/// Whether an object's members are exactly `names`, no more and no fewer.
fn has_exactly(object: &Map<String, Value>, names: &[&str]) -> bool {
    object.len() == names.len() && names.iter().all(|n| object.contains_key(*n))
}

fn text<'a>(object: &'a Map<String, Value>, name: &str, at: u64) -> Result<&'a str, ActaError> {
    object
        .get(name)
        .and_then(Value::as_str)
        .ok_or_else(|| mismatch(at, "a statement member is not text"))
}

fn signature<'a>(
    payload: &'a Map<String, Value>,
    name: &str,
    at: u64,
) -> Result<&'a str, ActaError> {
    text(payload, name, at)
}

/// The `alg` a public JWK's `kty` and `crv` determine (R4.15).
fn alg_of(jwk: &Value) -> Option<&'static str> {
    match (
        jwk.get("kty").and_then(Value::as_str),
        jwk.get("crv").and_then(Value::as_str),
    ) {
        (Some("OKP"), Some("Ed25519")) => Some("EdDSA"),
        (Some("EC"), Some("P-256")) => Some("ES256"),
        _ => None,
    }
}

/// A key set holding the one JWK, for verifying under it.
fn key_set(jwk: &Value) -> Option<JwkSet> {
    let document = serde_json::to_vec(&serde_json::json!({ "keys": [jwk] })).ok()?;
    JwkSet::parse(&document).ok()
}

/// A compact signature's protected header `typ` and `kid`, as it states them.
fn header_of(compact: &str) -> (Option<String>, Option<String>) {
    let Some(header) = compact.split('.').next().and_then(|h| {
        base64::engine::general_purpose::URL_SAFE_NO_PAD
            .decode(h)
            .ok()
    }) else {
        return (None, None);
    };
    if crate::json::parse(&header).is_err() {
        return (None, None);
    }
    let Ok(value) = serde_json::from_slice::<Value>(&header) else {
        return (None, None);
    };
    let get = |name: &str| value.get(name).and_then(Value::as_str).map(str::to_string);
    (get("typ"), get("kid"))
}

/// A statement read from its exact bytes (spec §4.5): admitted without a repeated member, its own
/// canonical form, of a closed member set, and of the entry's kind (its `typ` is checked first by the
/// caller, so a version this verifier does not know never reaches here).
fn read_statement(
    payload: &Map<String, Value>,
    at: u64,
    members: &[&str],
    typ: &str,
) -> Result<Statement, ActaError> {
    let text = payload
        .get("statement")
        .and_then(Value::as_str)
        .ok_or_else(|| mismatch(at, "the entry carries no statement"))?;
    let bytes = text.as_bytes().to_vec();

    if crate::json::parse(&bytes).is_err() {
        return Err(mismatch(at, "the statement is not JSON a reader can admit"));
    }
    if crate::canonicalize_with_nfc(&bytes).ok().as_deref() != Some(bytes.as_slice()) {
        return Err(mismatch(at, "the statement is not its own canonical form"));
    }
    let Ok(Value::Object(fields)) = serde_json::from_slice::<Value>(&bytes) else {
        return Err(mismatch(at, "the statement is not an object"));
    };
    if fields.get("typ").and_then(Value::as_str) != Some(typ) {
        return Err(mismatch(at, "the statement is not this entry's kind"));
    }
    if !has_exactly(&fields, members) {
        return Err(mismatch(at, "the statement's member set is not its kind's"));
    }
    match fields.get("prev").and_then(Value::as_u64) {
        Some(prev) if prev <= MAX_SAFE_INTEGER => {}
        _ => return Err(mismatch(at, "the statement's prev is not an integer")),
    }
    Ok(Statement { bytes, fields })
}
