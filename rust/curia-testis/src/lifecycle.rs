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
//! * a declaration (R6.57) is a statement the operator's declaration key signed (KF1, KF2; errata G67, R6.62: never a log key) whose `kids`, bindings and
//!   `from_index` this verifier recomputes from the lifecycle (KF3); one that holds, names the post's key from a
//!   leaf at or below the post's, and follows the post, makes the post disputed. Check (1) of KF3 is evaluated over
//!   the supplied stream only. A declaration that fails never disputes a post;
//! * the post's key is verified when it was bound before the post and not retired before it.
//!
//! No approval is consulted: a reader folds on the merits (§4.8). Nothing here panics on malformed input.

use std::collections::HashMap;

use base64::Engine;
use serde_json::{Map, Value};

use crate::acta::{
    self, entry_fields, head_covers, post_author_and_kid, str_at, ActaError, LogKey, LogKeys,
    VerifiedHead, ENROLLED, KEY_BOUND, POST_ACCEPTED,
};
use crate::jwk::JwkSet;
use crate::jws;

/// The entry type of a retirement.
pub const KEY_RETIRED: &str = "agent.key-retired";
/// The entry type of a compromise declaration (R6.57).
pub const KEY_COMPROMISED: &str = "agent.key-compromised";

/// A rotation statement's `typ`.
pub const ROTATION_STATEMENT_TYP: &str = "curia.key-rotation/1";
/// A retirement statement's `typ`.
pub const RETIREMENT_STATEMENT_TYP: &str = "curia.key-retirement/1";
/// The header `typ` of a rotation's signatures (KF1).
pub const ROTATION_HEADER_TYP: &str = "curia-key-rotation+jws";
/// The header `typ` of a retirement's signature (KF1).
pub const RETIREMENT_HEADER_TYP: &str = "curia-key-retirement+jws";
/// A compromise statement's `typ`.
pub const COMPROMISE_STATEMENT_TYP: &str = "curia.key-compromise/2";
/// The header `typ` of a declaration's signature (KF1).
pub const COMPROMISE_HEADER_TYP: &str = "curia-key-compromise+jws";
/// The reasons a declaration may state (KF4): a reason outside it is not established.
pub const COMPROMISE_REASONS: [&str; 2] = ["owner-reported", "operator-observed"];

/// Every statement `typ` this verifier knows (KF4). A `typ` outside it is a version or a kind this
/// verifier cannot fold; one inside it, in the other kind's entry, is a statement that is not this
/// entry's.
const KNOWN_STATEMENT_TYPS: [&str; 3] = [
    ROTATION_STATEMENT_TYP,
    RETIREMENT_STATEMENT_TYP,
    COMPROMISE_STATEMENT_TYP,
];

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
const COMPROMISE_ENTRY_MEMBERS: [&str; 7] = [
    "agent_id",
    "declaration",
    "from_index",
    "kids",
    "prev",
    "reason",
    "statement",
];
const COMPROMISE_STATEMENT_MEMBERS: [&str; 6] =
    ["agent_id", "from_index", "kids", "prev", "reason", "typ"];

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
/// them: the raw bodies of `GET /v1/log/head` and `GET /v1/log/jwks`. The same key set is read for each
/// key's position, which a declaration is held to (KF2).
pub fn verify_lifecycle(
    post_entry: &[u8],
    chain: &[(Vec<u8>, Vec<u8>)],
    head: &[u8],
    log_jwks: &[u8],
) -> Result<VerifiedLifecycle, ActaError> {
    // R6.62(a): a key set with an entry of `keys` that is not readable as a key still carries the head's key among the entries that are, and
    // says nothing usable about the declaration keys, which is decided at the declaration.
    let head = match acta::verify_head(head, log_jwks) {
        Err(first @ ActaError::Jwks(_)) => match acta::readable_keys_document(log_jwks) {
            Some(readable) => acta::verify_head(head, &readable).map_err(|_| first)?,
            None => return Err(first),
        },
        other => other?,
    };
    let log_keys = LogKeys::parse(log_jwks)?;
    verify_lifecycle_under(post_entry, chain, &head, &log_keys)
}

/// The fold itself. `post_entry` is the body of `GET /v1/log/entries/{index}` for the post; `chain`
/// holds, in log order, each lifecycle entry's body and the body of its proof against the tree
/// `head` covers: every entry of the author up to the first at or after the post, and, when the key
/// set names a declaration for the post's key after the post, every entry up to and including it
/// (R6.57). `log_keys` is the log's key set, which a declaration is verified under (KF2).
pub fn verify_lifecycle_under(
    post_entry: &[u8],
    chain: &[(Vec<u8>, Vec<u8>)],
    head: &VerifiedHead,
    log_keys: &LogKeys,
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

    // Every supplied entry is folded, in log order: the entries from the first at or after the post to a
    // declaration that names the post's key are supplied so that the declaration's link and cascade are held to
    // the chain (R6.57).
    let mut fold = Fold::new(post.author.clone());
    for (entry_json, proof_json) in chain {
        fold.fold_entry(entry_json, proof_json, head, log_keys)?;
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
    // A declaration naming the post's key that precedes the post: a compromised key is honoured for nothing
    // at or after the declaration's position (R6.57).
    if let Some(declared) = fold
        .declarations
        .iter()
        .find(|d| d.kids.contains(&post.kid) && d.index < post.index)
    {
        return Err(ActaError::CompromisedBeforePost {
            kid: post.kid,
            declaration: declared.index,
            post: post.index,
        });
    }
    match fold.bound.get(&post.kid) {
        Some((_, bound_at)) if *bound_at < post.index => {
            // R6.57: disputed is by log position, [from_index, the declaration's index): the signature holds and
            // who made it is in question.
            if let Some(disputing) = fold.declarations.iter().find(|d| {
                d.kids.contains(&post.kid) && d.from_index <= post.index && post.index < d.index
            }) {
                return Err(ActaError::Disputed {
                    kid: post.kid,
                    from_index: disputing.from_index,
                    declaration: disputing.index,
                    post: post.index,
                });
            }
            Ok(VerifiedLifecycle {
                kid: post.kid,
                bound_at: *bound_at,
                post_index: post.index,
                entries_checked: fold.count,
                last_index: fold.prev_index.unwrap_or(0),
            })
        }
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
    Compromised,
}

/// A declaration that held under KF1 to KF4 and KF3: where it sits, where it states the compromise from, and
/// whom it names.
struct Declared {
    index: u64,
    from_index: u64,
    kids: Vec<String>,
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
    /// Every rotation folded so far, in log order, as (index, authorized_by, kid): the material R6.57's cascade is
    /// recomputed from.
    rotations: Vec<(u64, String, String)>,
    declarations: Vec<Declared>,
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
            rotations: Vec::new(),
            declarations: Vec::new(),
        }
    }

    /// One entry, in KF4's order: its type, then its statement's `typ`, then everything else. Returns
    /// the entry's log index.
    fn fold_entry(
        &mut self,
        entry_json: &[u8],
        proof_json: &[u8],
        head: &VerifiedHead,
        log_keys: &LogKeys,
    ) -> Result<u64, ActaError> {
        let fields = entry_fields(entry_json)?;
        let event_type = str_at(&fields, &["event_type"], "lifecycle entry")?.to_string();
        let kind = match event_type.as_str() {
            ENROLLED => Kind::Enrolled,
            KEY_BOUND => Kind::Bound,
            KEY_RETIRED => Kind::Retired,
            KEY_COMPROMISED => Kind::Compromised,
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
        if let (Some(text), Kind::Bound | Kind::Retired | Kind::Compromised) =
            (statement_text, kind)
        {
            let expected = match kind {
                Kind::Bound => ROTATION_STATEMENT_TYP,
                Kind::Retired => RETIREMENT_STATEMENT_TYP,
                _ => COMPROMISE_STATEMENT_TYP,
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

        // KF4: a reason outside the closed set is a reason this verifier does not know, never verified and never
        // skipped.
        if let (Some(text), Kind::Compromised) = (statement_text, kind) {
            if let Some(reason) = serde_json::from_str::<Value>(text)
                .ok()
                .and_then(|v| v.get("reason").and_then(Value::as_str).map(str::to_string))
            {
                if !COMPROMISE_REASONS.contains(&reason.as_str()) {
                    return Err(ActaError::LifecycleUnknownReason { reason });
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
            Kind::Compromised => self.declaration(payload, index, log_keys)?,
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
        self.rotations
            .push((index, authorized_by.to_string(), kid.to_string()));
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

    /// A declaration (R6.57, spec §4.5, §4.6), in KF4's order after its type, statement `typ` and reason: the
    /// closed member sets, the link, KF1 (header `typ`), KF2 (header `kid` in the log's key set, published
    /// before the declaration), the signature, then KF3: (1) every kid is bound to the author in the supplied
    /// stream, (2) the binding of `kids[0]` is at or below `from_index`, which is below the declaration's
    /// index, (3) `kids` equal, in order, the cascade recomputed from the lifecycle. A declaration that fails
    /// any of them never disputes a post.
    fn declaration(
        &mut self,
        payload: &Map<String, Value>,
        index: u64,
        log_keys: &LogKeys,
    ) -> Result<(), ActaError> {
        if !has_exactly(payload, &COMPROMISE_ENTRY_MEMBERS) {
            return Err(mismatch(
                index,
                "the entry's member set is not a declaration's",
            ));
        }
        let statement = read_statement(
            payload,
            index,
            &COMPROMISE_STATEMENT_MEMBERS,
            COMPROMISE_STATEMENT_TYP,
        )?;
        let s = &statement.fields;

        let from_index = match s.get("from_index").and_then(Value::as_u64) {
            Some(n) if n <= MAX_SAFE_INTEGER => n,
            _ => {
                return Err(mismatch(
                    index,
                    "the statement's from_index is not an integer",
                ))
            }
        };
        let kids: Vec<String> = match s.get("kids").and_then(Value::as_array) {
            Some(items) if !items.is_empty() => items
                .iter()
                .map(|k| k.as_str().map(str::to_string))
                .collect::<Option<Vec<_>>>()
                .ok_or_else(|| mismatch(index, "the statement's kids are not text"))?,
            _ => return Err(mismatch(index, "the statement's kids are not a list")),
        };
        if s.get("reason")
            .and_then(Value::as_str)
            .is_none_or(|r| !COMPROMISE_REASONS.contains(&r))
        {
            return Err(mismatch(index, "the statement's reason is not text"));
        }

        // Every member the entry states beside the statement equals the statement's own.
        for member in ["agent_id", "from_index", "kids", "prev", "reason"] {
            if payload.get(member) != s.get(member) {
                return Err(mismatch(
                    index,
                    "an entry member differs from its statement's",
                ));
            }
        }

        self.link(s, index)?;

        // KF1, then KF2: the declaration carries a declaration's header typ, and names a key the log
        // published before it.
        let declaration = signature(payload, "declaration", index)?;
        let (header_typ, header_kid) = header_of(declaration);
        if header_typ.as_deref() != Some(COMPROMISE_HEADER_TYP) {
            return Err(ActaError::LifecycleTypMismatch { at: index });
        }
        let Some(header_kid) = header_kid else {
            return Err(ActaError::DeclarationKeyUnpublished {
                at: index,
                kid: String::new(),
            });
        };
        // R6.62, in order, the first that fails deciding. (a) A key set with no `declaration_keys` member says nothing.
        let Some(declaration_keys) = log_keys.declaration_keys() else {
            return Err(ActaError::DeclarationKeySetNotSupplied);
        };
        // (b) The header's kid names a declaration key (a served empty array names none). A kid listed more than once is the key of its
        // earliest listing, and a later entry under it is not a key: each kid below stands for its earliest listing, whose position is
        // not given if any listing of the kid gives none (the earliest cannot then be said).
        let mut keys: Vec<(&LogKey, Option<u64>)> = Vec::new();
        for listed in declaration_keys {
            match keys.iter_mut().find(|(k, _)| k.kid == listed.kid) {
                None => keys.push((listed, listed.log_index)),
                Some((earliest, at)) => {
                    let before = *at;
                    // R6.62: where two listings of a kid share a position and are not the same key, its earliest cannot be said.
                    *at = match (before, listed.log_index) {
                        (Some(x), Some(y)) if x == y && !earliest.same_key(listed) => None,
                        (Some(x), Some(y)) => Some(x.min(y)),
                        _ => None,
                    };
                    if listed.log_index.is_some() && listed.log_index == *at && before != *at {
                        *earliest = listed;
                    }
                }
            }
        }
        let Some(&(declaration_key, position)) = keys.iter().find(|(k, _)| k.kid == header_kid)
        else {
            return Err(ActaError::DeclarationKeyUnpublished {
                at: index,
                kid: header_kid,
            });
        };
        // (c) A position not given: the declaring key's or any other declaration key's.
        if keys.iter().any(|(_, at)| at.is_none()) {
            return Err(ActaError::DeclarationKeyPositionUnknown { kid: header_kid });
        }
        let published_at = position.unwrap_or(u64::MAX);
        // (d) A log key never declares: by kid, or by public key under another kid.
        if log_keys.is_log_key(declaration_key) {
            return Err(ActaError::DeclarationKeyIsLogKey {
                at: index,
                kid: header_kid,
            });
        }
        // (e) The first declaration key published is the one, by `log_index` and never by the order the key set is served in: no
        // other key is at or before it.
        if keys
            .iter()
            .any(|(k, at)| k.kid != header_kid && at.is_some_and(|at| at <= published_at))
        {
            return Err(ActaError::DeclarationKeyNotFirst {
                at: index,
                kid: header_kid,
            });
        }
        // (f) Published before the declaration it signs.
        if published_at >= index {
            return Err(ActaError::DeclarationKeyUnpublished {
                at: index,
                kid: header_kid,
            });
        }
        // (g) The signature verifies under it.
        // A key whose point is not on its curve verifies nothing: asked of the declaring key alone, here (R6.62).
        let Some(declaring_jwk) = declaration_key.key.clone() else {
            return Err(ActaError::DeclarationSignatureInvalid { at: index });
        };
        let signer = JwkSet::from_keys(vec![declaring_jwk]);
        jws::verify_typed(
            declaration,
            &statement.bytes,
            &signer,
            COMPROMISE_HEADER_TYP,
        )
        .map_err(|_| ActaError::DeclarationSignatureInvalid { at: index })?;

        // KF3 (1): every kid is bound to the author in the stream this verifier holds. A kid with no binding
        // there, and a kid whose only binding is not supplied, are alike unbound.
        if let Some(unbound) = kids.iter().find(|k| !self.bound.contains_key(*k)) {
            return Err(ActaError::KidNotBoundToAgent {
                at: index,
                kid: unbound.clone(),
            });
        }

        // KF3 (2): binding(kids[0]) <= from_index < the declaration's own index, strictly.
        let first_bound = self.bound.get(&kids[0]).map_or(u64::MAX, |(_, at)| *at);
        if first_bound > from_index {
            return Err(ActaError::DeclarationMismatch {
                at: index,
                what: "from_index is below the binding of the first key",
            });
        }
        if from_index >= index {
            return Err(ActaError::DeclarationMismatch {
                at: index,
                what: "from_index is not below the declaration's own index",
            });
        }

        // KF3 (3): kids equal, in order, the cascade this verifier recomputes. Its root is the earliest-bound
        // key in kids (never the post's key, never kids[0] by position); each rotation at or after from_index
        // authorized by a key already in it adds its key, in log order, and a rotation's authorizer was bound
        // before it, so one pass is the closure.
        let root = kids
            .iter()
            .min_by_key(|k| self.bound.get(*k).map_or(u64::MAX, |(_, at)| *at))
            .cloned()
            .unwrap_or_default();
        let mut cascade = vec![root];
        for (at, authorized_by, kid) in &self.rotations {
            if *at >= from_index && cascade.contains(authorized_by) && !cascade.contains(kid) {
                cascade.push(kid.clone());
            }
        }
        if kids != cascade {
            return Err(ActaError::DeclarationMismatch {
                at: index,
                what: "kids are not, in order, the cascade recomputed from the lifecycle",
            });
        }

        self.declarations.push(Declared {
            index,
            from_index,
            kids,
        });
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

// ---------------------------------------------------------------------------------------------------------------------
// R6.64: which lifecycle rows the Forum acts on.
//
// `verify_lifecycle` answers for a post, from the rows a Forum lists and the key set it serves. This is the step before
// it: given the log's own events, decide every lifecycle row of one identity's stream (honoured, or skipped with the
// predicate that decided it), so a row that verifies under no published key ends, binds and marks nothing. The keys the
// log publishes are its own `log.key` and `log.declaration-key` entries; no key set document enters.
// ---------------------------------------------------------------------------------------------------------------------

/// The entry type that publishes a log key (R6.50).
pub const LOG_KEY: &str = "log.key";
/// The entry type that publishes a declaration key (R6.62).
pub const LOG_DECLARATION_KEY: &str = "log.declaration-key";

/// One entry of the log as the Forum reads it from its event store: the members of `entry` that R6.64 reads.
#[derive(Debug, Clone)]
pub struct LogEntry {
    pub log_index: u64,
    pub event_type: String,
    pub aggregate_id: String,
    pub payload: Value,
}

impl LogEntry {
    /// Reads one `entries/<index>.json` document: `log_index` and `entry` (`aggregate_id`, `event_type`, `payload`).
    pub fn from_document(document: &[u8]) -> Result<Self, String> {
        let value: Value = serde_json::from_slice(document).map_err(|e| e.to_string())?;
        let entry = value.get("entry").ok_or("no `entry` member")?;
        let text = |name: &str| {
            entry
                .get(name)
                .and_then(Value::as_str)
                .map(str::to_string)
                .ok_or(format!("no string `{name}`"))
        };
        Ok(LogEntry {
            log_index: value
                .get("log_index")
                .and_then(Value::as_u64)
                .ok_or("no integer `log_index`")?,
            event_type: text("event_type")?,
            aggregate_id: text("aggregate_id")?,
            payload: entry.get("payload").cloned().unwrap_or(Value::Null),
        })
    }
}

/// The decision for one lifecycle row.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Verdict {
    Honoured,
    /// Skipped, with R6.64's predicate.
    Skipped(&'static str),
}

/// One lifecycle row of the stream (never the enrolment, its pair, or a later `agent.enrolled`).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Row {
    pub log_index: u64,
    pub event_type: String,
    pub verdict: Verdict,
}

/// A key's status after the honoured prefix.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum KeyStatus {
    Active,
    Retired,
    Compromised,
}

impl KeyStatus {
    pub fn as_str(self) -> &'static str {
        match self {
            KeyStatus::Active => "active",
            KeyStatus::Retired => "retired",
            KeyStatus::Compromised => "compromised",
        }
    }
}

/// What the procedure decides for one identity.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Honoured {
    pub rows: Vec<Row>,
    /// Every `kid` an honoured entry names, with its status.
    pub keys: std::collections::BTreeMap<String, KeyStatus>,
    /// The kids the honoured prefix binds, in log order.
    pub bound: Vec<String>,
}

/// R6.64's predicates, by the names it gives and no others.
pub mod predicate {
    pub const STATEMENT_MISMATCH: &str = "statement-mismatch";
    pub const UNKNOWN_LIFECYCLE_KIND: &str = "unknown-lifecycle-kind";
    pub const UNKNOWN_REASON: &str = "unknown-reason";
    pub const BROKEN_LINK: &str = "broken-link";
    pub const LIFECYCLE_TYP_MISMATCH: &str = "lifecycle-typ-mismatch";
    pub const ROTATION_UNAUTHORIZED: &str = "rotation-unauthorized";
    pub const DECLARATION_KEY_UNPUBLISHED: &str = "declaration-key-unpublished";
    pub const DECLARATION_KEY_IS_LOG_KEY: &str = "declaration-key-is-log-key";
    pub const DECLARATION_KEY_NOT_FIRST: &str = "declaration-key-not-first";
    pub const DECLARATION_SIGNATURE_INVALID: &str = "declaration-signature-invalid";
    pub const KID_NOT_BOUND_TO_AGENT: &str = "kid-not-bound-to-agent";
    pub const DECLARATION_MISMATCH: &str = "declaration-mismatch";

    /// All twelve.
    pub const ALL: [&str; 12] = [
        STATEMENT_MISMATCH,
        UNKNOWN_LIFECYCLE_KIND,
        UNKNOWN_REASON,
        BROKEN_LINK,
        LIFECYCLE_TYP_MISMATCH,
        ROTATION_UNAUTHORIZED,
        DECLARATION_KEY_UNPUBLISHED,
        DECLARATION_KEY_IS_LOG_KEY,
        DECLARATION_KEY_NOT_FIRST,
        DECLARATION_SIGNATURE_INVALID,
        KID_NOT_BOUND_TO_AGENT,
        DECLARATION_MISMATCH,
    ];
}

/// The log keys and declaration keys the log's own entries publish, in log order, each `kid` at its earliest listing.
struct Published {
    log_keys: Vec<LogKey>,
    declaration_keys: Vec<LogKey>,
}

impl Published {
    /// R6.62's closed form decides what is a key: an entry whose `jwk` is not a key the reader can read, or whose `kid` is not the key's, is
    /// no key and holds no position. A `kid` listed again after its earliest listing is not a key.
    fn read(ordered: &[&LogEntry]) -> Self {
        let mut published = Published {
            log_keys: Vec::new(),
            declaration_keys: Vec::new(),
        };
        for entry in ordered {
            let list = match entry.event_type.as_str() {
                LOG_KEY => &mut published.log_keys,
                LOG_DECLARATION_KEY => &mut published.declaration_keys,
                _ => continue,
            };
            let Some(mut key) = Self::key_of(entry) else {
                continue;
            };
            if list.iter().any(|k| k.kid == key.kid) {
                continue;
            }
            key.log_index = Some(entry.log_index);
            list.push(key);
        }
        published
    }

    fn key_of(entry: &LogEntry) -> Option<LogKey> {
        let jwk = entry.payload.as_object()?.get("jwk")?;
        let parsed = crate::json::parse(&serde_json::to_vec(jwk).ok()?).ok()?;
        if !acta::is_readable_key_entry(&parsed) {
            return None;
        }
        let key = acta::read_entry(&parsed);
        (entry.payload.get("kid").and_then(Value::as_str) == Some(key.kid.as_str())).then_some(key)
    }

    fn is_log_key(&self, declaration_key: &LogKey) -> bool {
        self.log_keys
            .iter()
            .any(|k| k.kid == declaration_key.kid || k.same_key(declaration_key))
    }
}

/// The honoured prefix of one identity, and the rows decided against it.
struct Prefix<'a> {
    agent: &'a str,
    published: &'a Published,
    /// The log index of the previous honoured lifecycle entry.
    prev: Option<u64>,
    bound: Vec<String>,
    keys_bound: HashMap<String, (JwkSet, u64)>,
    status: std::collections::BTreeMap<String, KeyStatus>,
    /// Honoured rotations, in log order, as (index, authorized_by, kid): the material R6.57's cascade is recomputed from.
    rotations: Vec<(u64, String, String)>,
}

type Decision = Result<(), &'static str>;

/// Decides every lifecycle row of `agent`'s stream, R6.64's procedure, against the keys `log` publishes.
pub fn honoured(log: &[LogEntry], agent: &str) -> Honoured {
    let mut ordered: Vec<&LogEntry> = log.iter().collect();
    ordered.sort_by_key(|e| e.log_index);
    let published = Published::read(&ordered);
    let mut prefix = Prefix {
        agent,
        published: &published,
        prev: None,
        bound: Vec::new(),
        keys_bound: HashMap::new(),
        status: std::collections::BTreeMap::new(),
        rotations: Vec::new(),
    };

    let mut rows = Vec::new();
    // Lifecycle entries of the stream before the one being decided, a later `agent.enrolled` among them.
    let mut before = 0usize;
    let mut enrolment_kid: Option<String> = None;
    let mut enrolled = false;
    for entry in ordered.iter().filter(|e| e.aggregate_id == agent) {
        let kind = match entry.event_type.as_str() {
            ENROLLED => {
                if !enrolled {
                    enrolled = true;
                    // The enrolment is honoured and is the previous honoured entry of the next row; a pair
                    // after it moves `prev` on to the pair's index and binds the key.
                    prefix.prev = Some(entry.log_index);
                    enrolment_kid = entry
                        .payload
                        .get("kid")
                        .and_then(Value::as_str)
                        .map(str::to_string);
                }
                before += 1;
                continue;
            }
            KEY_BOUND => Kind::Bound,
            KEY_RETIRED => Kind::Retired,
            KEY_COMPROMISED => Kind::Compromised,
            _ => continue,
        };
        // The pair's position: the first lifecycle entry after the enrolment.
        let after_enrolment = enrolled && before == 1;
        let decision = if kind == Kind::Bound
            && after_enrolment
            && prefix.is_enrolment_pair(&entry.payload, enrolment_kid.as_deref())
        {
            prefix.bind_root(&entry.payload, entry.log_index);
            before += 1;
            continue;
        } else {
            prefix.decide(kind, entry, after_enrolment, enrolment_kid.as_deref())
        };
        before += 1;
        rows.push(Row {
            log_index: entry.log_index,
            event_type: entry.event_type.clone(),
            verdict: match decision {
                Ok(()) => Verdict::Honoured,
                Err(predicate) => Verdict::Skipped(predicate),
            },
        });
    }
    Honoured {
        rows,
        keys: prefix.status,
        bound: prefix.bound,
    }
}

impl Prefix<'_> {
    /// R4.38: no `statement`, the enrolment's `kid`, exactly `agent_id`, `jwk` and `kid`, and R4.34's key.
    fn is_enrolment_pair(&self, payload: &Value, enrolment_kid: Option<&str>) -> bool {
        let Some(object) = payload.as_object() else {
            return false;
        };
        object.get("statement").is_none()
            && enrolment_kid.is_some()
            && object.get("kid").and_then(Value::as_str) == enrolment_kid
            && has_exactly(object, &ENROLMENT_BINDING_MEMBERS)
            && object.get("agent_id").and_then(Value::as_str) == Some(self.agent)
            && Self::pair_key(object).is_some()
    }

    fn pair_key(object: &Map<String, Value>) -> Option<JwkSet> {
        let jwk = object.get("jwk").filter(|j| j.is_object())?;
        let kid = object.get("kid").and_then(Value::as_str)?;
        if jwk.get("kid").and_then(Value::as_str) != Some(kid) {
            return None;
        }
        key_set(jwk)
    }

    fn bind_root(&mut self, payload: &Value, index: u64) {
        let object = payload.as_object().expect("a pair is an object");
        let kid = object["kid"]
            .as_str()
            .expect("a pair has a kid")
            .to_string();
        let set = Self::pair_key(object).expect("a pair has a key");
        self.bind(kid, set, index);
    }

    fn bind(&mut self, kid: String, set: JwkSet, index: u64) {
        self.status.insert(kid.clone(), KeyStatus::Active);
        self.keys_bound.insert(kid.clone(), (set, index));
        self.bound.push(kid);
        self.prev = Some(index);
    }

    /// One row, R6.64's steps in order, the first that applies deciding. On honour the prefix takes the row.
    fn decide(
        &mut self,
        kind: Kind,
        entry: &LogEntry,
        in_pair_position: bool,
        enrolment_kid: Option<&str>,
    ) -> Decision {
        // 1.
        let Some(payload) = entry.payload.as_object() else {
            return Err(predicate::STATEMENT_MISMATCH);
        };
        let statement = payload.get("statement").and_then(Value::as_str);
        let parsed = statement.and_then(statement_object);

        // 2.
        let (current_typ, statement_members) = match kind {
            Kind::Bound => (ROTATION_STATEMENT_TYP, &ROTATION_STATEMENT_MEMBERS[..]),
            Kind::Retired => (RETIREMENT_STATEMENT_TYP, &RETIREMENT_STATEMENT_MEMBERS[..]),
            _ => (COMPROMISE_STATEMENT_TYP, &COMPROMISE_STATEMENT_MEMBERS[..]),
        };
        if let Some(typ) = parsed
            .as_ref()
            .and_then(|o| o.get("typ"))
            .and_then(Value::as_str)
        {
            if typ != current_typ {
                return Err(if KNOWN_STATEMENT_TYPS.contains(&typ) {
                    predicate::STATEMENT_MISMATCH
                } else {
                    predicate::UNKNOWN_LIFECYCLE_KIND
                });
            }
        }
        // 3.
        if kind == Kind::Compromised {
            if let Some(reason) = parsed
                .as_ref()
                .and_then(|o| o.get("reason"))
                .and_then(Value::as_str)
            {
                if !COMPROMISE_REASONS.contains(&reason) {
                    return Err(predicate::UNKNOWN_REASON);
                }
            }
        }
        // 4.
        if payload.get("agent_id").and_then(Value::as_str) != Some(self.agent) {
            return Err(predicate::STATEMENT_MISMATCH);
        }
        // 5.
        // "Without a `statement`": R6.64 step 2 speaks of a statement that is a string, and a statement that is not text is no statement a
        // row can be read by, so it is treated as absent here (the reader vectors read it so).
        if kind == Kind::Bound && statement.is_none() {
            let names_enrolment = enrolment_kid.is_some()
                && payload.get("kid").and_then(Value::as_str) == enrolment_kid;
            return Err(if in_pair_position && names_enrolment {
                predicate::STATEMENT_MISMATCH
            } else {
                predicate::BROKEN_LINK
            });
        }
        // 6.
        let index = entry.log_index;
        let statement = read_statement(payload, index, statement_members, current_typ)
            .map_err(|_| predicate::STATEMENT_MISMATCH)?;
        match kind {
            Kind::Bound => self.rotation(payload, &statement, index),
            Kind::Retired => self.retirement(payload, &statement, index),
            _ => self.declaration(payload, &statement, index),
        }
    }

    fn link(&self, statement: &Statement) -> Decision {
        let stated = statement.fields.get("prev").and_then(Value::as_u64);
        if stated.is_some() && stated == self.prev {
            Ok(())
        } else {
            Err(predicate::BROKEN_LINK)
        }
    }

    fn rotation(
        &mut self,
        payload: &Map<String, Value>,
        statement: &Statement,
        index: u64,
    ) -> Decision {
        let mismatch = predicate::STATEMENT_MISMATCH;
        if !has_exactly(payload, &ROTATION_ENTRY_MEMBERS) {
            return Err(mismatch);
        }
        let s = &statement.fields;
        let (Some(authorized_by), Some(kid), Some(jwk)) = (
            s.get("authorized_by").and_then(Value::as_str),
            s.get("kid").and_then(Value::as_str),
            s.get("jwk").filter(|j| j.is_object()),
        ) else {
            return Err(mismatch);
        };
        // The jwk is the key's public rendering and nothing more, under the statement's kid.
        let members = match alg_of(jwk) {
            Some("ES256") => vec!["alg", "crv", "kid", "kty", "x", "y"],
            Some(_) => vec!["alg", "crv", "kid", "kty", "x"],
            None => return Err(mismatch),
        };
        if jwk.get("alg").and_then(Value::as_str) != alg_of(jwk)
            || !jwk.as_object().is_some_and(|o| has_exactly(o, &members))
            || jwk.get("kid").and_then(Value::as_str) != Some(kid)
        {
            return Err(mismatch);
        }
        // KF5: a key does not authorize its own binding.
        if authorized_by == kid {
            return Err(mismatch);
        }
        let new_key = key_set(jwk).ok_or(mismatch)?;
        for member in ["agent_id", "authorized_by", "jwk", "kid", "prev"] {
            if payload.get(member) != s.get(member) {
                return Err(mismatch);
            }
        }
        self.link(statement)?;

        let authorization = payload.get("authorization").and_then(Value::as_str);
        let possession = payload.get("possession").and_then(Value::as_str);
        let header = |c: Option<&str>| c.map_or((None, None), header_of);
        let (authorization_header, possession_header) = (header(authorization), header(possession));
        if authorization_header.0.as_deref() != Some(ROTATION_HEADER_TYP)
            || possession_header.0.as_deref() != Some(ROTATION_HEADER_TYP)
        {
            return Err(predicate::LIFECYCLE_TYP_MISMATCH);
        }
        let unauthorized = predicate::ROTATION_UNAUTHORIZED;
        if authorization_header.1.as_deref() != Some(authorized_by)
            || possession_header.1.as_deref() != Some(kid)
        {
            return Err(unauthorized);
        }
        let Some((authorizer, _)) = self.keys_bound.get(authorized_by) else {
            return Err(unauthorized);
        };
        if matches!(
            self.status.get(authorized_by),
            Some(KeyStatus::Retired | KeyStatus::Compromised)
        ) {
            return Err(unauthorized);
        }
        let (Some(authorization), Some(possession)) = (authorization, possession) else {
            return Err(unauthorized);
        };
        jws::verify_typed(
            authorization,
            &statement.bytes,
            authorizer,
            ROTATION_HEADER_TYP,
        )
        .map_err(|_| unauthorized)?;
        jws::verify_typed(possession, &statement.bytes, &new_key, ROTATION_HEADER_TYP)
            .map_err(|_| unauthorized)?;

        if self.keys_bound.contains_key(kid) {
            return Err(mismatch);
        }
        self.bind(kid.to_string(), new_key, index);
        self.rotations
            .push((index, authorized_by.to_string(), kid.to_string()));
        Ok(())
    }

    fn retirement(
        &mut self,
        payload: &Map<String, Value>,
        statement: &Statement,
        index: u64,
    ) -> Decision {
        let mismatch = predicate::STATEMENT_MISMATCH;
        if !has_exactly(payload, &RETIREMENT_ENTRY_MEMBERS) {
            return Err(mismatch);
        }
        let s = &statement.fields;
        let kid = s.get("kid").and_then(Value::as_str).ok_or(mismatch)?;
        for member in ["agent_id", "kid", "prev"] {
            if payload.get(member) != s.get(member) {
                return Err(mismatch);
            }
        }
        self.link(statement)?;

        let authorization = payload.get("authorization").and_then(Value::as_str);
        let (typ, header_kid) = authorization.map_or((None, None), header_of);
        if typ.as_deref() != Some(RETIREMENT_HEADER_TYP) {
            return Err(predicate::LIFECYCLE_TYP_MISMATCH);
        }
        let unauthorized = predicate::ROTATION_UNAUTHORIZED;
        if header_kid.as_deref() != Some(kid) {
            return Err(unauthorized);
        }
        let Some((key, _)) = self.keys_bound.get(kid) else {
            return Err(unauthorized);
        };
        if matches!(
            self.status.get(kid),
            Some(KeyStatus::Retired | KeyStatus::Compromised)
        ) {
            return Err(unauthorized);
        }
        jws::verify_typed(
            authorization.unwrap_or_default(),
            &statement.bytes,
            key,
            RETIREMENT_HEADER_TYP,
        )
        .map_err(|_| unauthorized)?;

        // A compromised key stays compromised: the declaration is the stronger statement.
        self.status.entry(kid.to_string()).and_modify(|s| {
            if *s == KeyStatus::Active {
                *s = KeyStatus::Retired;
            }
        });
        self.prev = Some(index);
        Ok(())
    }

    fn declaration(
        &mut self,
        payload: &Map<String, Value>,
        statement: &Statement,
        index: u64,
    ) -> Decision {
        let mismatch = predicate::STATEMENT_MISMATCH;
        if !has_exactly(payload, &COMPROMISE_ENTRY_MEMBERS) {
            return Err(mismatch);
        }
        let s = &statement.fields;
        let from_index = match s.get("from_index").and_then(Value::as_u64) {
            Some(n) if n <= MAX_SAFE_INTEGER => n,
            _ => return Err(mismatch),
        };
        let kids: Vec<String> = match s.get("kids").and_then(Value::as_array) {
            Some(items) if !items.is_empty() => items
                .iter()
                .map(|k| k.as_str().map(str::to_string))
                .collect::<Option<Vec<_>>>()
                .ok_or(mismatch)?,
            _ => return Err(mismatch),
        };
        // Step 3 has refused a reason that is a string outside the set; one that is not a string is no closed form.
        if s.get("reason")
            .and_then(Value::as_str)
            .is_none_or(|r| !COMPROMISE_REASONS.contains(&r))
        {
            return Err(mismatch);
        }
        for member in ["agent_id", "from_index", "kids", "prev", "reason"] {
            if payload.get(member) != s.get(member) {
                return Err(mismatch);
            }
        }
        self.link(statement)?;

        let declaration = payload.get("declaration").and_then(Value::as_str);
        let (typ, header_kid) = declaration.map_or((None, None), header_of);
        if typ.as_deref() != Some(COMPROMISE_HEADER_TYP) {
            return Err(predicate::LIFECYCLE_TYP_MISMATCH);
        }
        let Some(header_kid) = header_kid else {
            return Err(predicate::DECLARATION_KEY_UNPUBLISHED);
        };

        // R6.62, in its order, with the log's own entries as the key set.
        let Some(declaring) = self
            .published
            .declaration_keys
            .iter()
            .find(|k| k.kid == header_kid)
        else {
            return Err(predicate::DECLARATION_KEY_UNPUBLISHED);
        };
        let position = declaring.log_index.unwrap_or(u64::MAX);
        if self.published.is_log_key(declaring) {
            return Err(predicate::DECLARATION_KEY_IS_LOG_KEY);
        }
        if self
            .published
            .declaration_keys
            .iter()
            .any(|k| k.kid != header_kid && k.log_index.is_some_and(|at| at <= position))
        {
            return Err(predicate::DECLARATION_KEY_NOT_FIRST);
        }
        if position >= index {
            return Err(predicate::DECLARATION_KEY_UNPUBLISHED);
        }
        let invalid = predicate::DECLARATION_SIGNATURE_INVALID;
        let jwk = declaring.key.clone().ok_or(invalid)?;
        jws::verify_typed(
            declaration.unwrap_or_default(),
            &statement.bytes,
            &JwkSet::from_keys(vec![jwk]),
            COMPROMISE_HEADER_TYP,
        )
        .map_err(|_| invalid)?;

        if kids.iter().any(|k| !self.keys_bound.contains_key(k)) {
            return Err(predicate::KID_NOT_BOUND_TO_AGENT);
        }
        let first_bound = self.keys_bound[&kids[0]].1;
        if first_bound > from_index || from_index >= index {
            return Err(predicate::DECLARATION_MISMATCH);
        }
        // R6.57: the compromised key first, then each key it compromises, in the log order of their bindings.
        let mut cascade = vec![kids[0].clone()];
        for (at, authorized_by, kid) in &self.rotations {
            if *at >= from_index && cascade.contains(authorized_by) && !cascade.contains(kid) {
                cascade.push(kid.clone());
            }
        }
        if kids != cascade {
            return Err(predicate::DECLARATION_MISMATCH);
        }

        for kid in &kids {
            self.status.insert(kid.clone(), KeyStatus::Compromised);
        }
        self.prev = Some(index);
        Ok(())
    }
}

/// A statement that is a string holding a JSON object a reader can admit (a repeated member is no object).
fn statement_object(text: &str) -> Option<Map<String, Value>> {
    crate::json::parse(text.as_bytes()).ok()?;
    match serde_json::from_str::<Value>(text) {
        Ok(Value::Object(object)) => Some(object),
        _ => None,
    }
}
