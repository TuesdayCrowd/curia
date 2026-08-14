//! `CanonicalizeWithNfc` — the Cūria profile on top of pure RFC 8785.
//!
//! Errata D1 (`spec/curia-whitepaper-ERRATA-AND-ADDENDUM.md`, revised R6.9)
//! is the entire content of this module, and its normative statement is
//! about **order of operations**, not about which characters get
//! normalized:
//!
//! > Implementations SHALL provide `CanonicalizeWithNfc`, which **first**
//! > normalizes to NFC every string occurring anywhere in the document —
//! > object member names and string values alike, at every level of
//! > nesting — producing a normalized tree, and **then** canonicalizes that
//! > tree with `Canonicalize`. The order is normative and is the entire
//! > content of this correction: because normalization can change a key's
//! > sort position, normalizing after ordering yields different bytes than
//! > normalizing before it.
//!
//! `U+FB33` (`conformance/rfc8785/input-weird.json`) is why the order
//! matters at all: it sits on Unicode's Composition Exclusion list, so NFC
//! decomposes it and never recomposes, changing its leading UTF-16 code
//! unit and therefore where it sorts under RFC 8785 §3.2.3's key-ordering
//! rule. A single-pass implementation that sorted first and normalized
//! second (or normalized only during string rendering, after ordering had
//! already been decided) would silently place that key wrong. This module
//! never sorts anything itself — sorting is [`crate::canonical`]'s job,
//! untouched by this task — so getting the order right here means doing
//! the normalization pass to completion, as a value-to-value tree
//! transform, *before* [`crate::canonical::canonicalize`] is ever called.
//!
//! ## Why this goes through a byte round trip rather than reaching into
//! `canonical.rs`'s internals
//!
//! [`crate::canonical`]'s only public surface is
//! `canonicalize(&[u8]) -> Result<Vec<u8>, ParseError>` — its object-sorting,
//! string-escaping, and number-formatting helpers are private to that
//! module, and this task is explicitly forbidden from modifying
//! `src/canonical.rs` (a parallel fix round owns it). So
//! [`canonicalize_with_nfc`] below does the D1 sequence as two passes over
//! bytes rather than one pass over an in-memory `Value`:
//!
//! 1. Parse the original input with [`crate::json::parse`] (the same parser
//!    [`crate::canonical::canonicalize`] itself uses — no repair, no
//!    tolerance for anything RFC 8259 rejects).
//! 2. Walk the resulting tree once, replacing every object member name and
//!    every string value with its NFC form — [`normalize_value`], the
//!    "normalized tree" D1 requires, built explicitly rather than folded
//!    into a render step. This step is fallible as of Fix round 1: two
//!    distinct raw member names within the same object can normalize to the
//!    same string, and building the tree is where that collision first
//!    exists to detect — see "Normalization can create duplicate object
//!    members" below.
//! 3. Re-encode that normalized tree as *some* syntactically valid JSON
//!    ([`encode`]) — key order, escaping choices, and number spelling are
//!    all irrelevant here, because step 4 immediately re-derives all three
//!    from the parsed tree; this step's only obligation is losslessness
//!    (every string must round-trip through [`crate::json::parse`]
//!    unchanged, and every number must round-trip to the identical `f64`).
//! 4. Hand those bytes to the untouched, pure
//!    [`crate::canonical::canonicalize`], which parses them again and
//!    performs RFC 8785 ordering, escaping, and number formatting exactly
//!    as it does for any other input.
//!
//! Step 3's number encoding uses `f64::to_string()` (`Display`), which is
//! documented core-library behavior to always produce a string that parses
//! back to the identical `f64` — confirmed empirically here (not merely
//! assumed) against magnitudes from `1e-300` to `1e300` and against the
//! known odd/even-tie double `629266065803222.25` (see
//! `task/task-3-report.md`, "Numbers round-trip check"). Which decimal
//! spelling `Display` happens to choose for a value is irrelevant to
//! correctness either way, because [`crate::canonical::canonicalize`]'s own
//! `format_number` re-derives the ECMAScript-form digits from the parsed
//! double, not from this module's spelling of it — so this module cannot
//! introduce, mask, or fix any number-formatting divergence in
//! `canonical.rs`; it only has to deliver the same double back to it.
//!
//! ## Unicode version
//!
//! See [`CRATE_UNICODE_VERSION`] and `task/task-3-report.md` §"Unicode
//! version" for the pinned-vs-actual comparison design spec §5.2 requires.
//!
//! ## Normalization can create duplicate object members — Fix rounds 1–2
//!
//! Two *distinct* raw wire member names can normalize to the *same* NFC
//! string — e.g. `"café"` (precomposed, `U+00E9`) and `"café"`
//! (decomposed, `e` + `U+0301` COMBINING ACUTE ACCENT) both normalize to the
//! same four-character `café`. ADMIT's duplicate-key rule (Task 4,
//! `conformance/admit-reject/duplicate-keys`) cannot see this collision: it
//! runs before normalization, on raw byte-identical keys only. Task 6's
//! `crate::envelope::verify_envelope` now does run ADMIT immediately in
//! front of this function (Ruling #1), which means a *raw* duplicate is
//! caught before `canonicalize_with_nfc` ever sees that particular input —
//! but `canonicalize_with_nfc` is also called directly, with no ADMIT gate
//! in front of it at all, by the `c4`/`ordering`/`unicode`/`numbers` family
//! harness (`conformance/README.md`: "the pure functions stay free of
//! ADMIT"), so this function still cannot assume ADMIT ran, and still needs
//! its own check. Left unchecked, `canonicalize_with_nfc` would silently
//! emit a canonical object with two
//! members sharing one key — not valid I-JSON, and exactly the kind of
//! divergence R6.9 exists to prevent, since which of the two colliding
//! members "wins" a re-parse depends on the reader (some libraries keep the
//! first occurrence, some the last), so two verifiers could each check the
//! *same* signature over the *same* canonical bytes and disagree about what
//! was actually said. Because the collision is created by normalization
//! itself, this function — where the normalized tree is built — is where it
//! must be caught; ADMIT cannot see it, and by the time
//! [`crate::canonical::canonicalize`] runs the input is just bytes with no
//! memory of which raw keys produced them.
//!
//! There are actually **two distinct conditions** that both end in "two
//! members share a normalized name," and Fix round 2 keeps them
//! distinguishable rather than collapsing them into one check with one
//! (sometimes false) message:
//!
//! - **Raw member names byte-identical** (`{"a":1,"a":2}` — no
//!   normalization involved at all). This is exactly what ADMIT's own
//!   `conformance/admit-reject/duplicate-keys` vector pins, just noticed
//!   here instead of at ADMIT because nothing upstream enforces ADMIT
//!   first today. [`normalize_value`] rejects it with
//!   [`NfcError::DuplicateRawKey`], reusing ADMIT's own
//!   `curia/admit/duplicate-key` predicate ([`NfcError::predicate`])
//!   deliberately: the slug names the *condition*, and a verifier should
//!   report the same predicate for the same defect regardless of which
//!   layer noticed it — that property matters for the cross-implementation
//!   differential work later.
//! - **Raw member names differ, but normalize to the same string** (the
//!   `café`/`café` example above — the condition this module actually
//!   exists to catch, per D1). [`normalize_value`] rejects this with
//!   [`NfcError::DuplicateNormalizedKey`], a slug distinct from ADMIT's,
//!   because it is a different cause (created by NFC, invisible on the raw
//!   wire) that a caller needs to be able to tell apart from the first
//!   case.
//!
//! Fix round 1 shipped a version of this check that conflated the two —
//! comparing only *normalized* keys against each other, so `{"a":1,"a":2}`
//! (no normalization difference at all) was reported with the
//! `curia/canon/duplicate-normalized-key` slug and a message claiming the
//! two names were "distinct," which is false for that input. Fix round 2's
//! [`normalize_value`] tracks each normalized key's *originating raw key*
//! (a `HashMap<String, String>`, normalized → raw, per object) precisely so
//! it can tell which of the two conditions actually happened before
//! choosing which error to return.
//!
//! **Precedence when both conditions exist in one object — Fix round 3,
//! normative and deliberate, not incidental.** [`normalize_value`] checks
//! raw-name duplicates in their own first pass over an object's member
//! list, entirely before the second pass computes any normalized name. So
//! an object containing *both* a raw duplicate and a separate NFC
//! collision **always** reports [`NfcError::DuplicateRawKey`], regardless
//! of which pair happens to appear earlier in member order. Fix round 2
//! shipped a single combined pass whose outcome depended on member
//! order — whichever collision was encountered first in the scan is what
//! got reported — and Fix round 3 replaced that with this two-pass
//! structure specifically to remove the order-dependence. This is not
//! merely a style preference:
//!
//! - **Order-independence eliminates a whole class of cross-implementation
//!   divergence.** `curia-testis` exists to be compared against an
//!   independently written verifier of the same specification (Phase 1's
//!   exit criterion), and R14.6 makes a divergence a release blocker.
//!   A second implementation checking raw duplicates before normalization
//!   — an equally natural design, since parsing necessarily precedes
//!   normalizing — would *always* report the raw duplicate for a
//!   dual-defect object. If this implementation's answer instead depended
//!   on member order, the two verifiers could each be individually correct
//!   and still disagree on which slug a dual-defect input produces, and
//!   the differential harness would flag a release blocker with no actual
//!   defect behind it — a false positive that is expensive precisely
//!   because there is nothing to fix, only a tie-break the specification
//!   never stated to align on.
//! - It matches what an independent implementation most plausibly does
//!   anyway: raw-name comparison needs no normalization step and is the
//!   cheaper, more certain check, so checking it first is the natural
//!   order even without this rule being written down.
//! - A raw duplicate is a defect in the document **as transmitted**; an
//!   NFC collision is a defect only relative to a normalization step this
//!   profile applies. Reporting the more primitive, transmission-level
//!   defect first is the more informative diagnostic.
//!
//! See `tests/nfc_profile.rs`'s
//! `raw_duplicate_always_wins_when_raw_duplicate_is_first`,
//! `raw_duplicate_always_wins_when_nfc_collision_is_first`, and
//! `raw_duplicate_always_wins_with_an_unrelated_key_between_them`, which pin
//! all three member orderings the re-review considered — including the
//! "unrelated key in between" permutation — so this is a tested, documented
//! property, not an accident of iteration order.
//!
//! Two equal normalized names in *different* objects (siblings, or
//! unrelated levels of nesting) are unaffected either way — the check is
//! scoped to one object's own member list, per the brief.

use std::collections::HashSet;
use std::fmt;

use unicode_normalization::UnicodeNormalization;

use crate::canonical::canonicalize;
use crate::json::{self, ParseError, Value};

/// An error from [`canonicalize_with_nfc`]: either the input did not parse
/// as JSON at all (wrapping the same [`ParseError`]
/// [`crate::canonical::canonicalize`] would report for the same bytes,
/// whether that's the original input or — in principle, though it should
/// never happen given [`encode`]'s losslessness guarantee — this module's
/// own re-encoded intermediate bytes), or an object within it contains two
/// members whose names collide — either byte-identical on the wire, or
/// merely equal *after* NFC normalization. See the module doc comment,
/// "Normalization can create duplicate object members," for why these are
/// two different conditions with two different slugs, not one.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum NfcError {
    /// A JSON parse failure — either of the original input, or (should it
    /// ever occur) of this module's own re-encoded intermediate bytes.
    Parse(ParseError),
    /// Two member names within the same object are byte-identical on the
    /// wire — no normalization involved. `key` is that shared raw name.
    /// This is the same condition ADMIT's `curia/admit/duplicate-key`
    /// exists to reject; see [`NfcError::predicate`] for why this variant
    /// reuses that slug rather than minting its own.
    DuplicateRawKey { key: String },
    /// Two member names within the same object are *distinct* on the wire
    /// but normalize to the same NFC string. `key` is that shared
    /// normalized string.
    DuplicateNormalizedKey { key: String },
}

impl NfcError {
    /// A stable predicate slug, in the same spirit as every other
    /// `curia/<layer>/<condition>` slug in this crate and the corpus's
    /// `expect-reject`/`expect-verify-failure` slugs.
    ///
    /// [`NfcError::DuplicateRawKey`] deliberately returns ADMIT's own
    /// `curia/admit/duplicate-key` slug, not a slug of this module's
    /// invention: the raw-identical-keys condition is exactly what
    /// `conformance/admit-reject/duplicate-keys` pins, and a verifier
    /// should report the same predicate for the same defect no matter
    /// which layer happened to notice it. `crate::envelope::verify_envelope`
    /// (Task 6) now does run ADMIT in front of this function's call to
    /// `canonicalize_with_nfc`, so a raw duplicate anywhere in a submission
    /// is caught by ADMIT itself before this module ever sees it on that
    /// path — but `canonicalize_with_nfc` is still called directly
    /// elsewhere (the `c4`/`ordering`/`unicode`/`numbers` family harness,
    /// per `conformance/README.md`'s "the pure functions stay free of
    /// ADMIT"), so this variant, and the slug reuse, both remain live.
    ///
    /// [`NfcError::DuplicateNormalizedKey`] gets its own
    /// `curia/canon/duplicate-normalized-key` slug because it is a
    /// genuinely different condition — a collision NFC itself creates
    /// between two names that were distinct on the wire — that a caller
    /// needs to be able to tell apart from a raw duplicate.
    pub fn predicate(&self) -> &str {
        match self {
            // Task 6 cleanup: this used to be `curia-testis/nfc/parse-error`
            // — wrong root (every other real predicate in this crate is
            // `curia/<layer>/<condition>`, never `curia-testis/...`) and it
            // invented an `nfc` layer where `canon` already exists (see
            // `DuplicateNormalizedKey`'s `curia/canon/duplicate-normalized-key`,
            // right below, in the very same enum). Renamed to match.
            NfcError::Parse(_) => "curia/canon/parse-error",
            NfcError::DuplicateRawKey { .. } => "curia/admit/duplicate-key",
            NfcError::DuplicateNormalizedKey { .. } => "curia/canon/duplicate-normalized-key",
        }
    }
}

impl fmt::Display for NfcError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            NfcError::Parse(e) => write!(f, "{e}"),
            NfcError::DuplicateRawKey { key } => write!(
                f,
                "{}: the object contains two members with the same name \
                 {key:?}",
                self.predicate()
            ),
            NfcError::DuplicateNormalizedKey { key } => write!(
                f,
                "{}: two distinct member names normalize to the same string \
                 {key:?} within one object; rejected rather than emitting a \
                 canonical form with duplicate members",
                self.predicate()
            ),
        }
    }
}

impl std::error::Error for NfcError {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        match self {
            NfcError::Parse(e) => Some(e),
            NfcError::DuplicateRawKey { .. } | NfcError::DuplicateNormalizedKey { .. } => None,
        }
    }
}

impl From<ParseError> for NfcError {
    fn from(e: ParseError) -> Self {
        match e {
            // `json::parse` now rejects duplicate member names itself, as a
            // well-definedness violation, so it fires before this module's own
            // pass-1 raw-duplicate scan can. Route it to the variant that
            // already names that exact condition rather than letting it
            // degrade to a generic parse failure: the caller-visible predicate
            // must not depend on which layer happened to notice first, which
            // is the same rule the raw/normalized precedence ruling rests on.
            ParseError::DuplicateMember { name, .. } => NfcError::DuplicateRawKey { key: name },
            other => NfcError::Parse(other),
        }
    }
}

/// The Unicode version design spec §5.2 pins for NFC normalization
/// (`CanonicalJson.UnicodeVersion` on the C# side; errata R6.34). This is a
/// *declaration*, not a runtime check — see `task/task-3-report.md` for the
/// comparison against [`CRATE_UNICODE_VERSION`] and the residual risk
/// design spec §5.2 already describes (Unicode's Normalization Stability
/// Policy bounds a version mismatch to characters *newly assigned* in the
/// higher version that also carry a canonical decomposition; already-assigned
/// characters' decompositions never change).
pub const PINNED_UNICODE_VERSION: (u32, u32, u32) = (16, 0, 0);

/// The Unicode version the `unicode-normalization` crate, as pinned in
/// `Cargo.lock`, actually normalizes with — read directly from the crate's
/// own published constant, not from documentation, so this value tracks
/// whatever `cargo` actually resolves and builds. Compare against
/// [`PINNED_UNICODE_VERSION`].
pub const CRATE_UNICODE_VERSION: (u8, u8, u8) = unicode_normalization::UNICODE_VERSION;

/// `CanonicalizeWithNfc` (errata D1, revised R6.9): normalizes every object
/// member name and string value to NFC, recursively, building a normalized
/// tree — then canonicalizes that tree per RFC 8785. See the module doc
/// comment for the exact sequencing and why it is expressed as a byte round
/// trip through [`crate::canonical::canonicalize`].
///
/// `input` is taken by reference for the same reason
/// [`crate::canonical::canonicalize`] is: a rejecting canonicalizer must
/// never need to have admitted, repaired, or otherwise mutated its input
/// first.
pub fn canonicalize_with_nfc(input: &[u8]) -> Result<Vec<u8>, NfcError> {
    let value = json::parse(input)?;
    let normalized = normalize_value(&value)?;
    let mut reencoded = Vec::new();
    encode(&normalized, &mut reencoded);
    Ok(canonicalize(&reencoded)?)
}

/// Builds the "normalized tree" D1 requires: a new [`Value`] with every
/// object member name and every string value replaced by its NFC form,
/// recursively. Numbers, booleans, `null`, and array/object *structure* are
/// carried over unchanged — NFC has nothing to say about them.
///
/// Also enforces the Fix rounds 1–3 rule (see the module doc comment): within
/// one object's own member list, two members whose names collide — either
/// byte-identical on the wire, or merely equal after NFC — are rejected
/// with [`NfcError::DuplicateRawKey`] or [`NfcError::DuplicateNormalizedKey`]
/// respectively, instead of silently building an object with two equal
/// keys. The check is per-object, not global — two equal normalized names
/// in different objects (siblings or otherwise) are fine, since RFC 8785
/// §3.2.3 ordering and duplicate-freedom are properties of one member list,
/// not of the whole document. **A raw duplicate always wins over an NFC
/// collision in the same object, regardless of member order** — see the
/// module doc comment's "Precedence" section for why this is normative, not
/// incidental, and is implemented as two separate linear passes (raw names
/// first, in full, before any normalized name is even computed) rather than
/// one combined pass that would make the outcome depend on which collision
/// happens to appear earlier.
fn normalize_value(value: &Value) -> Result<Value, NfcError> {
    match value {
        Value::Null => Ok(Value::Null),
        Value::Bool(b) => Ok(Value::Bool(*b)),
        Value::Number(n) => Ok(Value::Number(*n)),
        Value::String(s) => Ok(Value::String(nfc(s))),
        Value::Array(items) => {
            let normalized = items
                .iter()
                .map(normalize_value)
                .collect::<Result<Vec<_>, _>>()?;
            Ok(Value::Array(normalized))
        }
        Value::Object(members) => {
            // Pass 1 (Fix round 3): raw member names only, in full, before
            // any normalization happens — this is what makes a raw
            // duplicate win over an NFC collision *regardless of member
            // order*, rather than whichever collision the scan happens to
            // reach first (Fix round 2's behavior). O(n) via a `HashSet` of
            // borrowed `&str`s; no cloning needed here since nothing is
            // kept past this loop.
            let mut raw_seen: HashSet<&str> = HashSet::with_capacity(members.len());
            for (raw_key, _) in members {
                if !raw_seen.insert(raw_key.as_str()) {
                    return Err(NfcError::DuplicateRawKey {
                        key: raw_key.clone(),
                    });
                }
            }

            // Pass 2: normalize, and check for collisions among the
            // *normalized* names. Reached only once pass 1 has confirmed
            // every raw name in this object is unique, so any collision
            // found here is necessarily between two genuinely distinct raw
            // names — a true NFC collision, never a raw duplicate — and a
            // plain `HashSet<String>` of normalized names already seen is
            // enough (no need to track which raw name produced each entry,
            // unlike Fix round 2's combined pass). Still O(n) per object,
            // same bound as pass 1: two linear passes remain linear overall,
            // not quadratic, and this function still cannot assume ADMIT's
            // member-count cap gated what reached it — `verify_envelope`
            // (Task 6) does run ADMIT first on that path, but
            // `canonicalize_with_nfc` is also reachable directly, with no
            // ADMIT gate at all, from the family harness.
            let mut normalized: Vec<(String, Value)> = Vec::with_capacity(members.len());
            let mut normalized_seen: HashSet<String> = HashSet::with_capacity(members.len());
            for (raw_key, val) in members {
                let normalized_key = nfc(raw_key);
                if !normalized_seen.insert(normalized_key.clone()) {
                    return Err(NfcError::DuplicateNormalizedKey {
                        key: normalized_key,
                    });
                }
                let normalized_val = normalize_value(val)?;
                normalized.push((normalized_key, normalized_val));
            }
            Ok(Value::Object(normalized))
        }
    }
}

/// NFC-normalizes one string. `unicode_normalization::UnicodeNormalization::nfc`
/// is a `char`-iterator adaptor; `s.chars()` already came from a validated
/// Rust `String` (well-formed scalar values only — `json::parse` cannot
/// produce an unpaired surrogate, see its `UnpairedSurrogate` rejection), so
/// there is nothing here that can fail.
fn nfc(s: &str) -> String {
    s.nfc().collect()
}

/// Re-encodes a [`Value`] as syntactically valid JSON (RFC 8259) — *not*
/// RFC 8785 canonical form. Object member order is emitted as stored (not
/// sorted) and numbers are emitted via `f64::to_string()` rather than
/// ECMAScript `Number::toString`; both are fine because
/// [`crate::canonical::canonicalize`] immediately re-parses this output and
/// re-derives ordering, escaping, and number formatting itself. This
/// function's only obligation is losslessness — see the module doc comment.
///
/// `pub(crate)`, not private: Task 6's `crate::envelope` reuses this exact
/// lossless re-encoder to turn the `envelope` sub-object of an
/// already-ADMITted submission `Value` back into bytes for
/// [`canonicalize_with_nfc`], rather than writing a second copy of the same
/// "any valid, round-trippable JSON" encoding logic. Sharing one
/// implementation means there is exactly one place this crate ever answers
/// "how do I turn a parsed `Value` back into bytes losslessly," the same
/// reasoning the module doc comment gives for reusing
/// [`crate::canonical::canonicalize`] itself rather than forking it.
pub(crate) fn encode(value: &Value, out: &mut Vec<u8>) {
    match value {
        Value::Null => out.extend_from_slice(b"null"),
        Value::Bool(true) => out.extend_from_slice(b"true"),
        Value::Bool(false) => out.extend_from_slice(b"false"),
        // f64::to_string() (Display) is documented to produce a string that
        // parses back to the identical f64 — see the module doc comment for
        // the empirical check backing this. It never emits `NaN`/`Infinity`
        // because json::parse already rejects any literal that overflows to
        // a non-finite f64 (ParseError::NonFiniteNumber), so every `n` here
        // is finite.
        Value::Number(n) => out.extend_from_slice(n.to_string().as_bytes()),
        Value::String(s) => encode_string(s, out),
        Value::Array(items) => {
            out.push(b'[');
            for (i, item) in items.iter().enumerate() {
                if i > 0 {
                    out.push(b',');
                }
                encode(item, out);
            }
            out.push(b']');
        }
        Value::Object(members) => {
            out.push(b'{');
            for (i, (key, val)) in members.iter().enumerate() {
                if i > 0 {
                    out.push(b',');
                }
                encode_string(key, out);
                out.push(b':');
                encode(val, out);
            }
            out.push(b'}');
        }
    }
}

/// Escapes a string just enough for [`crate::json::parse`] to accept the
/// re-encoded bytes and read back exactly the same `char` sequence: quote
/// and backslash are escaped, and any control character below `U+0020` is
/// escaped as `\u00XX` (`json::parse`'s `RawControlInString` rejects such a
/// byte if left literal — see `src/json.rs`). Everything else, including
/// every character above `U+007F`, is written as literal UTF-8; `json::parse`
/// accepts raw non-ASCII bytes directly, and this function does not need to
/// match RFC 8785's *canonical* escaping choices (short forms like `\n`),
/// only to be valid, round-trippable JSON — canonical escaping is
/// [`crate::canonical`]'s job, applied on the second pass.
fn encode_string(s: &str, out: &mut Vec<u8>) {
    out.push(b'"');
    for c in s.chars() {
        match c {
            '"' => out.extend_from_slice(b"\\\""),
            '\\' => out.extend_from_slice(b"\\\\"),
            c if (c as u32) < 0x20 => {
                out.extend_from_slice(format!("\\u{:04x}", c as u32).as_bytes());
            }
            c => {
                let mut buf = [0u8; 4];
                out.extend_from_slice(c.encode_utf8(&mut buf).as_bytes());
            }
        }
    }
    out.push(b'"');
}
