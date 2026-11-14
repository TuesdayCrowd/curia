//! R6.67 (errata G70): which operator-act rows the Forum acts on.
//!
//! An operator act is an `agent.owner-attested` row (in the stream of the attested agent) or a
//! `moderation.applied` row (in the stream of the moderated post). [`operator_acts`] decides every such
//! row of one stream by R6.67's seven steps, the first that applies deciding, each row against the
//! honoured prefix of the stream and the keys the log's own entries publish (R6.66), and returns the
//! rows with the state the honoured rows alone decide. A skipped row changes nothing a later row is
//! decided against.
//!
//! The key set is derived from the log's own `log.operator-key`, `log.key` and `log.declaration-key`
//! entries by R6.66 and R6.62's closed key form; no key set document enters.
//!
//! This module is written from the requirement text and the conformance vectors. It shares the crate's
//! readers (the log-entry reader, the JSON admission, NFC canonicalization, the detached-JWS verifier)
//! and none of `lifecycle::honoured`'s rules.

use std::collections::BTreeSet;

use base64::Engine;
use serde_json::{Map, Value};

use crate::acta::{self, LogKey};
use crate::jwk::{Jwk, JwkSet};
use crate::jws;
use crate::lifecycle::{LogEntry, Row, Verdict, LOG_DECLARATION_KEY, LOG_KEY};

/// The entry type that publishes an operator-acts key (R6.66).
pub const LOG_OPERATOR_KEY: &str = "log.operator-key";
/// The entry type of an owner attestation (R4.30).
pub const OWNER_ATTESTED: &str = "agent.owner-attested";
/// The entry type of a moderation record (R10.60).
pub const MODERATION_APPLIED: &str = "moderation.applied";

const ENROLLED: &str = "agent.enrolled";
const POST_ACCEPTED: &str = "post.accepted";

/// The current statement `typ` of an attestation.
pub const ATTESTATION_TYP: &str = "curia.owner-attestation/1";
/// The current statement `typ` of a moderation record.
pub const MODERATION_TYP: &str = "curia.moderation/1";
/// The header `typ` of an operator act's signature (R6.66).
pub const ACT_HEADER_TYP: &str = "curia-operator-act+jws";

const ATTESTATION_MEMBERS: [&str; 8] = [
    "actor_id",
    "agent_id",
    "method",
    "owner_id",
    "owner_verified",
    "prev",
    "reason",
    "typ",
];
const MODERATION_MEMBERS: [&str; 10] = [
    "actor_id",
    "adjudicates",
    "category",
    "digest",
    "effect",
    "moderator",
    "post_id",
    "prev",
    "rationale",
    "typ",
];

/// R6.67's predicates, by the names it gives and no others.
pub mod predicate {
    pub const ACT_UNSIGNED: &str = "act-unsigned";
    pub const ACT_STATEMENT_MISMATCH: &str = "act-statement-mismatch";
    pub const ACT_UNKNOWN_KIND: &str = "act-unknown-kind";
    pub const ACT_BROKEN_LINK: &str = "act-broken-link";
    pub const ACT_TYP_MISMATCH: &str = "act-typ-mismatch";
    pub const ACT_KEY_UNPUBLISHED: &str = "act-key-unpublished";
    pub const ACT_KEY_NOT_FIRST: &str = "act-key-not-first";
    pub const ACT_SIGNATURE_INVALID: &str = "act-signature-invalid";

    /// All eight.
    pub const ALL: [&str; 8] = [
        ACT_UNSIGNED,
        ACT_STATEMENT_MISMATCH,
        ACT_UNKNOWN_KIND,
        ACT_BROKEN_LINK,
        ACT_TYP_MISMATCH,
        ACT_KEY_UNPUBLISHED,
        ACT_KEY_NOT_FIRST,
        ACT_SIGNATURE_INVALID,
    ];
}

/// One entry of the log as R6.67 reads it: the crate's log-entry reader's fields, and the event's actor
/// (which R6.67 step 3 compares with the statement's `actor_id` and `LogEntry` does not carry).
#[derive(Debug, Clone)]
pub struct ActEntry {
    pub entry: LogEntry,
    pub actor_id: String,
}

impl ActEntry {
    /// Reads one `entries/<index>.json` document: [`LogEntry::from_document`], and `entry.actor_id`.
    pub fn from_document(document: &[u8]) -> Result<Self, String> {
        let entry = LogEntry::from_document(document)?;
        let value: Value = serde_json::from_slice(document).map_err(|e| e.to_string())?;
        let actor_id = value
            .get("entry")
            .and_then(|e| e.get("actor_id"))
            .and_then(Value::as_str)
            .ok_or("no string `actor_id`")?
            .to_string();
        Ok(ActEntry { entry, actor_id })
    }
}

/// Which stream is under test.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Stream {
    /// An agent's stream: `agent.owner-attested` rows, rooted at `agent.enrolled`.
    Agent,
    /// A post's stream: `moderation.applied` rows, rooted at `post.accepted`.
    Post,
}

/// What the honoured rows decide.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum State {
    /// An agent's owner binding (R4.1) and whether that owner is verified.
    Agent {
        owner: Option<String>,
        verified: bool,
    },
    /// Whether a post is served (R10.61).
    Post { served: bool },
}

/// The decision for one stream.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Acts {
    pub rows: Vec<Row>,
    pub state: State,
}

/// Decides every operator-act row of `subject`'s stream of kind `stream`, R6.67's procedure, against the keys
/// `log` publishes.
pub fn operator_acts(log: &[ActEntry], stream: Stream, subject: &str) -> Acts {
    let mut ordered: Vec<&ActEntry> = log.iter().collect();
    ordered.sort_by_key(|e| e.entry.log_index);
    let keys = OperatorKeys::read(&ordered);

    let (act_type, root_type) = match stream {
        Stream::Agent => (OWNER_ATTESTED, ENROLLED),
        Stream::Post => (MODERATION_APPLIED, POST_ACCEPTED),
    };
    let events: Vec<&&ActEntry> = ordered
        .iter()
        .filter(|e| e.entry.aggregate_id == subject)
        .collect();
    // R6.67: "the stream's root is its first event". A stream whose first event is not of the root type has no root.
    let root = events
        .first()
        .filter(|e| e.entry.event_type == root_type)
        .map(|e| e.entry.log_index);

    let mut rows = Vec::new();
    let mut prev = root;
    let mut owner: Option<String> = None;
    let mut verified = false;
    let mut held: BTreeSet<String> = BTreeSet::new();
    for event in events.iter().filter(|e| e.entry.event_type == act_type) {
        let verdict = decide(event, stream, subject, root, prev, &keys);
        match verdict {
            Ok(act) => {
                prev = Some(event.entry.log_index);
                match act {
                    Honoured::Attestation {
                        owner_id,
                        owner_verified,
                    } => match &owner {
                        None => {
                            owner = Some(owner_id);
                            verified = owner_verified;
                        }
                        Some(bound) if *bound == owner_id => verified = owner_verified,
                        // R6.67: honoured as an act, binds nothing and sets nothing.
                        Some(_) => {}
                    },
                    Honoured::Moderation { effect, category } => match effect.as_str() {
                        "quarantine" | "withhold" => {
                            held.insert(category);
                        }
                        "restore" => {
                            held.remove(&category);
                        }
                        // A dismissal holds and releases nothing (R10.61).
                        _ => {}
                    },
                }
                rows.push(Row {
                    log_index: event.entry.log_index,
                    event_type: event.entry.event_type.clone(),
                    verdict: Verdict::Honoured,
                });
            }
            Err(predicate) => rows.push(Row {
                log_index: event.entry.log_index,
                event_type: event.entry.event_type.clone(),
                verdict: Verdict::Skipped(predicate),
            }),
        }
    }
    let state = match stream {
        Stream::Agent => State::Agent { owner, verified },
        Stream::Post => State::Post {
            served: held.is_empty(),
        },
    };
    Acts { rows, state }
}

/// What an honoured row says, once decided.
enum Honoured {
    Attestation {
        owner_id: String,
        owner_verified: bool,
    },
    Moderation {
        effect: String,
        category: String,
    },
}

/// One row, R6.67's steps in order, the first that applies deciding.
fn decide(
    event: &ActEntry,
    stream: Stream,
    subject: &str,
    root: Option<u64>,
    prev: Option<u64>,
    keys: &OperatorKeys,
) -> Result<Honoured, &'static str> {
    let (current_typ, other_typ, members, subject_member) = match stream {
        Stream::Agent => (
            ATTESTATION_TYP,
            MODERATION_TYP,
            &ATTESTATION_MEMBERS[..],
            "agent_id",
        ),
        Stream::Post => (
            MODERATION_TYP,
            ATTESTATION_TYP,
            &MODERATION_MEMBERS[..],
            "post_id",
        ),
    };

    // 1.
    let Some(payload) = event.entry.payload.as_object() else {
        return Err(predicate::ACT_STATEMENT_MISMATCH);
    };
    let Some(statement) = payload.get("statement").and_then(Value::as_str) else {
        return Err(predicate::ACT_UNSIGNED);
    };
    let parsed = statement_object(statement);

    // 2.
    if let Some(typ) = parsed
        .as_ref()
        .and_then(|o| o.get("typ"))
        .and_then(Value::as_str)
    {
        if typ != current_typ {
            return Err(if typ == other_typ {
                predicate::ACT_STATEMENT_MISMATCH
            } else {
                predicate::ACT_UNKNOWN_KIND
            });
        }
    }

    // 3.
    let Some(fields) = parsed else {
        return Err(predicate::ACT_STATEMENT_MISMATCH);
    };
    if !is_closed_form(statement, &fields, members)
        || !row_agrees_with_statement(payload, &fields)
        || fields.get(subject_member).and_then(Value::as_str) != Some(subject)
        || fields.get("actor_id").and_then(Value::as_str) != Some(event.actor_id.as_str())
        || (stream == Stream::Post
            && fields.get("moderator").and_then(Value::as_str) != Some("human"))
    {
        return Err(predicate::ACT_STATEMENT_MISMATCH);
    }

    // 4.
    if root.is_none() || fields.get("prev").and_then(Value::as_u64) != prev {
        return Err(predicate::ACT_BROKEN_LINK);
    }

    // 5.
    let header = payload
        .get("authorization")
        .and_then(Value::as_str)
        .and_then(detached_header)
        .filter(|h| {
            h.get("typ").and_then(Value::as_str) == Some(ACT_HEADER_TYP)
                && h.get("alg").and_then(Value::as_str) == Some("ES256")
        })
        .ok_or(predicate::ACT_TYP_MISMATCH)?;
    let authorization = payload
        .get("authorization")
        .and_then(Value::as_str)
        .ok_or(predicate::ACT_TYP_MISMATCH)?;

    // 6.
    let kid = header.get("kid").and_then(Value::as_str);
    let key = kid
        .and_then(|k| keys.published_before(k, event.entry.log_index))
        .ok_or(predicate::ACT_KEY_UNPUBLISHED)?;
    if !keys.is_first(key) {
        return Err(predicate::ACT_KEY_NOT_FIRST);
    }

    // 7.
    let jwk = key.jwk.clone().ok_or(predicate::ACT_SIGNATURE_INVALID)?;
    jws::verify_typed(
        authorization,
        statement.as_bytes(),
        &JwkSet::from_keys(vec![jwk]),
        ACT_HEADER_TYP,
    )
    .map_err(|_| predicate::ACT_SIGNATURE_INVALID)?;

    let text = |name: &str| {
        fields
            .get(name)
            .and_then(Value::as_str)
            .unwrap_or_default()
            .to_string()
    };
    Ok(match stream {
        Stream::Agent => Honoured::Attestation {
            owner_id: text("owner_id"),
            owner_verified: fields.get("owner_verified").and_then(Value::as_bool) == Some(true),
        },
        Stream::Post => Honoured::Moderation {
            effect: text("effect"),
            category: text("category"),
        },
    })
}

/// A statement that is a string holding a JSON object a reader can admit (a repeated member is no object).
fn statement_object(text: &str) -> Option<Map<String, Value>> {
    crate::json::parse(text.as_bytes()).ok()?;
    match serde_json::from_str::<Value>(text) {
        Ok(Value::Object(object)) => Some(object),
        _ => None,
    }
}

/// R6.67 step 3's first clause, the statement alone: its own canonical form (JCS and NFC), exactly its kind's
/// members, each of its type.
fn is_closed_form(statement: &str, fields: &Map<String, Value>, members: &[&str]) -> bool {
    crate::canonicalize_with_nfc(statement.as_bytes())
        .ok()
        .as_deref()
        == Some(statement.as_bytes())
        && fields.len() == members.len()
        && members.iter().all(|m| fields.contains_key(*m))
        && members.iter().all(|m| member_type_ok(fields, m))
        && members.iter().all(|m| member_value_ok(fields, m))
}

/// R4.24's four proofs.
const METHODS: [&str; 4] = ["domain", "email", "attestation", "manual"];
/// The four effects of a moderation record (R10.60).
const EFFECTS: [&str; 4] = ["quarantine", "withhold", "restore", "dismiss"];
/// R10.35's seven flag types, as the wire spells them.
const CATEGORIES: [&str; 7] = [
    "injection",
    "credential_leak",
    "incorrect",
    "spam",
    "duplicate",
    "license_violation",
    "malicious_code",
];

/// R6.67 step 3: the closed form closes the values of `method`, `effect` and `category`, so that no honoured row carries
/// a value a consumer cannot read.
fn member_value_ok(fields: &Map<String, Value>, name: &str) -> bool {
    let allowed: &[&str] = match name {
        "method" => &METHODS,
        "effect" => &EFFECTS,
        "category" => &CATEGORIES,
        _ => return true,
    };
    fields[name]
        .as_str()
        .is_some_and(|value| allowed.contains(&value))
}

fn member_type_ok(fields: &Map<String, Value>, name: &str) -> bool {
    match (name, &fields[name]) {
        ("owner_verified", v) => v.is_boolean(),
        ("prev", v) => v.as_u64().is_some(),
        ("adjudicates", Value::Array(items)) => items.iter().all(Value::is_string),
        ("adjudicates", _) => false,
        (_, v) => v.is_string(),
    }
}

/// R6.67 step 3: every member the row states beside `statement` and `authorization` is the statement's member of
/// that name, and equal to it.
fn row_agrees_with_statement(payload: &Map<String, Value>, fields: &Map<String, Value>) -> bool {
    payload
        .iter()
        .filter(|(name, _)| *name != "statement" && *name != "authorization")
        .all(|(name, value)| fields.get(name) == Some(value))
}

/// A detached compact JWS (`<header>..<signature>`, RFC 7515 Appendix F) and its protected header as an object.
fn detached_header(compact: &str) -> Option<Map<String, Value>> {
    let mut parts = compact.split('.');
    let (header, payload, _signature) = (parts.next()?, parts.next()?, parts.next()?);
    if parts.next().is_some() || !payload.is_empty() {
        return None;
    }
    let bytes = base64::engine::general_purpose::URL_SAFE_NO_PAD
        .decode(header)
        .ok()?;
    crate::json::parse(&bytes).ok()?;
    match serde_json::from_slice::<Value>(&bytes) {
        Ok(Value::Object(object)) => Some(object),
        _ => None,
    }
}

/// One operator key the log publishes and holds a position for.
struct OperatorKey {
    kid: String,
    log_index: u64,
    /// `None` where the entry is a readable key whose point is not on its curve: it cannot verify anything.
    jwk: Option<Jwk>,
}

/// The operator keys R6.66 reads from the log's own entries, in log order.
struct OperatorKeys {
    keys: Vec<OperatorKey>,
}

impl OperatorKeys {
    /// R6.66: a `log.operator-key` entry that reads as a key holds a position unless an earlier operator-class entry that
    /// reads as a key lists its `kid` (whether or not that entry holds a position), or the log published its key (by
    /// `kid` or by public key) earlier as a log key or a declaration key that holds a position; a `log.key` or
    /// `log.declaration-key` entry that lists a `kid` an earlier entry of its own class that reads as a key lists, or
    /// that publishes a key (by `kid` or by public key) an earlier operator entry that holds a position lists, holds none.
    /// An entry that holds no position publishes no key.
    fn read(ordered: &[&ActEntry]) -> Self {
        let mut operator: Vec<(LogKey, OperatorKey)> = Vec::new();
        let mut other: Vec<LogKey> = Vec::new();
        // The `kid`s each class has listed so far in entries that read as a key, position or not.
        let mut listed: [BTreeSet<String>; 3] = Default::default();
        for event in ordered {
            let entry = &event.entry;
            let (class, class_is_operator) = match entry.event_type.as_str() {
                LOG_OPERATOR_KEY => (0, true),
                LOG_KEY => (1, false),
                LOG_DECLARATION_KEY => (2, false),
                _ => continue,
            };
            let Some((key, jwk)) = Self::read_key(entry) else {
                continue;
            };
            let repeated = !listed[class].insert(key.kid.clone());
            let clashes = |other_key: &LogKey| other_key.kid == key.kid || other_key.same_key(&key);
            if class_is_operator {
                if repeated || other.iter().any(clashes) {
                    continue;
                }
                operator.push((
                    key.clone(),
                    OperatorKey {
                        kid: key.kid.clone(),
                        log_index: entry.log_index,
                        jwk,
                    },
                ));
            } else {
                if repeated || operator.iter().any(|(k, _)| clashes(k)) {
                    continue;
                }
                other.push(key);
            }
        }
        OperatorKeys {
            keys: operator.into_iter().map(|(_, k)| k).collect(),
        }
    }

    /// R6.62's closed key form decides what reads as a key: an object `jwk` the reader can read, under a payload `kid`
    /// that is the key's own.
    fn read_key(entry: &LogEntry) -> Option<(LogKey, Option<Jwk>)> {
        let jwk = entry.payload.as_object()?.get("jwk")?;
        let parsed = crate::json::parse(&serde_json::to_vec(jwk).ok()?).ok()?;
        if !acta::is_readable_key_entry(&parsed) {
            return None;
        }
        let key = acta::read_entry(&parsed);
        if entry.payload.get("kid").and_then(Value::as_str) != Some(key.kid.as_str()) {
            return None;
        }
        let jwk = key.key.clone();
        Some((key, jwk))
    }

    /// The operator key `kid` names, if it was published at an earlier log index than `row_index`.
    fn published_before(&self, kid: &str, row_index: u64) -> Option<&OperatorKey> {
        self.keys
            .iter()
            .find(|k| k.kid == kid)
            .filter(|k| k.log_index < row_index)
    }

    fn is_first(&self, key: &OperatorKey) -> bool {
        self.keys
            .first()
            .is_some_and(|first| first.log_index == key.log_index)
    }
}
