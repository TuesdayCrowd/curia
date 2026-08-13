//! A minimal, hand-rolled JSON value model and parser.
//!
//! This is deliberately **not** `serde_json::Value`. Task 1's report records
//! why: `serde_json::Value` is lossy for this project's purposes (see
//! `task/task-1-report.md`), and the loader's own use of `serde_json` is a
//! separate concern (reading fixture files off disk) from the
//! canonicalization business logic this module exists to support.
//!
//! Two things this module is *not* trying to be, on purpose:
//!
//! - `parse`/`Parser` are not the ADMIT phase. They enforce JSON syntax
//!   (RFC 8259) and nothing more — no duplicate-key rejection, no
//!   member-count cap, no safe-integer bound on numbers (design spec §5.4:
//!   "the canonicalizer still implements ECMAScript number serialization in
//!   full ... the envelope schema simply never produces input that reaches
//!   the fractional path" — i.e. `canonicalize` must handle *any* valid
//!   JSON number, not just I-JSON-safe ones). ADMIT's frozen limits (max
//!   size, max depth 32 counting container openings only, max 1024 members
//!   per level, max string length, the `-(2^53-1) <= n <= 2^53-1` integer
//!   bound, duplicate key rejection, Unicode noncharacters, non-finite/
//!   non-integer rejection) are implemented separately, below, by
//!   [`admit`] and its helpers — built *on* this value model (walking the
//!   [`Value`] tree `parse` already produces) rather than woven into the
//!   syntax parser itself, so `parse`'s own behavior (and every vector that
//!   already depends on it) is unchanged by Task 4.
//! - Its recursion-depth guard (`MAX_PARSE_DEPTH`, far above 32) exists
//!   solely so that a maliciously deep input produces a [`ParseError`]
//!   instead of a stack overflow (which would abort the process, not
//!   return a `Result` — a stronger violation of CHARTER §2's "Result, not
//!   panic" than an ordinary panic would be). It is not a stand-in for
//!   ADMIT's depth-32 rule, which is a business limit Task 4 owns.
//!
//! ## Why duplicate keys are out of scope for `parse` specifically
//!
//! No vector `parse`/`canonicalize` are exercised against (the vendored
//! `rfc8785/` pairs, and the `ordering/`/`numbers/` inputs `canonicalize` is
//! tested against directly per the Task 2 controller ruling) contains a
//! duplicate object key. `parse` never deduplicates or rejects them —
//! every occurrence is kept in [`Value::Object`], in input order — so that
//! [`admit`] (below), which *does* reject them per errata D7 /
//! `admit-reject/duplicate-keys`, can see the input exactly as written
//! rather than through a parser that already discarded the evidence.
//! `admit`'s duplicate check compares members' *wire* names — the strings
//! as `parse` decoded them from the input, with no Unicode normalization
//! applied — never a normalized form; see [`admit`]'s doc comment for why
//! that boundary is exact, not incidental.
//!
//! ## Why a non-finite number is rejected here, not silently emitted
//!
//! A JSON number literal can grammatically parse (per RFC 8259) to a value
//! that overflows `f64`'s range, e.g. `1e400`. RFC 8785 has no
//! representation for `Infinity`/`NaN` — emitting one would produce bytes
//! that are not valid JSON. `conformance/admit-reject/non-finite-number/meta.json`
//! independently confirms this is a real, recognized edge case ("1e400
//! overflows a double to +Infinity, which RFC 8785 cannot represent"). That
//! vector is graded through the `admit` profile, not `canonicalize` — but a
//! *pure* `canonicalize` that received such a literal directly (bypassing
//! ADMIT) must still fail safely rather than emit `Infinity` or panic, so
//! `parse_number` rejects it here too, independently of whatever Task 4
//! does. See [`ParseError::NonFiniteNumber`].

use std::collections::HashSet;
use std::fmt;

/// A parsed JSON value.
///
/// `Object` is a `Vec` of key/value pairs, not a map: order is preserved
/// exactly as encountered, and no deduplication happens here (see the
/// module doc comment). [`crate::canonical`] is what imposes RFC 8785's
/// UTF-16-code-unit member ordering, and it does so at render time without
/// mutating a parsed `Value`.
///
/// `Number` stores the parsed `f64` only, not the original literal text.
/// This is sufficient for `canonicalize` (RFC 8785 §3.2.2.3 canonicalizes
/// through the double, not the source spelling) but is flagged here for
/// whoever builds Task 4's ADMIT integer-safety checks on top of this
/// model: detecting an unsafe integer like `9007199254740993` (which loses
/// precision when parsed to `f64`) from the *value* alone requires
/// re-deriving the safe-range test from the double (e.g. `n.abs() <=
/// 9007199254740991.0 && n.fract() == 0.0`), not from a preserved literal,
/// because none is kept here.
#[derive(Debug, Clone, PartialEq)]
pub enum Value {
    Null,
    Bool(bool),
    Number(f64),
    String(String),
    Array(Vec<Value>),
    Object(Vec<(String, Value)>),
}

/// A JSON syntax error. `parse` never panics; every rejection — malformed
/// syntax, invalid UTF-8, an unpaired surrogate escape, a raw control byte
/// inside a string, a number that overflows to a non-finite `f64`, or
/// input nested deeper than the stack-safety guard allows — surfaces here.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ParseError {
    /// The input ended while a value, string, or literal was still open.
    UnexpectedEof { pos: usize },
    /// A byte (well, character — see `pos`, a `char`-boundary byte offset)
    /// appeared where JSON grammar did not allow it.
    UnexpectedChar { pos: usize },
    /// The input is not valid UTF-8. JSON text is UTF-8 by definition
    /// (RFC 8259 §8.1); this is checked once, up front.
    InvalidUtf8,
    /// A `\` inside a string was followed by something other than one of
    /// the RFC 8259 §7 escape characters.
    InvalidEscape { pos: usize },
    /// A `\uXXXX` escape used a high surrogate with no following low
    /// surrogate, a low surrogate with no preceding high surrogate, or a
    /// high surrogate followed by a non-surrogate `\uXXXX`.
    UnpairedSurrogate { pos: usize },
    /// A raw byte below `U+0020` appeared inside a string literal,
    /// unescaped. RFC 8259 §7 requires such characters to be escaped; a
    /// literal one is a syntax error, not (at this layer) an ADMIT rule.
    RawControlInString { pos: usize },
    /// A number token did not match RFC 8259 §6's grammar.
    InvalidNumber { pos: usize },
    /// A number token matched the grammar but its value overflows `f64` to
    /// `Infinity`/`-Infinity` (e.g. `1e400`). See the module doc comment.
    NonFiniteNumber { pos: usize },
    /// Non-whitespace bytes remained after the single top-level JSON value.
    TrailingData { pos: usize },
    /// Nesting exceeded the parser's stack-safety guard. Not the ADMIT
    /// depth-32 rule — see the module doc comment.
    DepthLimitExceeded { pos: usize },
}

impl fmt::Display for ParseError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            ParseError::UnexpectedEof { pos } => {
                write!(f, "unexpected end of input at byte {pos}")
            }
            ParseError::UnexpectedChar { pos } => {
                write!(f, "unexpected character at byte {pos}")
            }
            ParseError::InvalidUtf8 => write!(f, "input is not valid UTF-8"),
            ParseError::InvalidEscape { pos } => {
                write!(f, "invalid \\ escape at byte {pos}")
            }
            ParseError::UnpairedSurrogate { pos } => {
                write!(f, "unpaired UTF-16 surrogate in \\u escape at byte {pos}")
            }
            ParseError::RawControlInString { pos } => {
                write!(
                    f,
                    "unescaped control character (< U+0020) in string at byte {pos}"
                )
            }
            ParseError::InvalidNumber { pos } => {
                write!(f, "malformed number literal at byte {pos}")
            }
            ParseError::NonFiniteNumber { pos } => {
                write!(
                    f,
                    "number literal at byte {pos} overflows to a non-finite value; \
                     RFC 8785 cannot represent Infinity or NaN"
                )
            }
            ParseError::TrailingData { pos } => {
                write!(f, "trailing data after the JSON value at byte {pos}")
            }
            ParseError::DepthLimitExceeded { pos } => {
                write!(
                    f,
                    "input nested too deeply (parser stack-safety guard) at byte {pos}"
                )
            }
        }
    }
}

impl std::error::Error for ParseError {}

/// Purely a stack-safety guard against a maliciously/accidentally deep
/// input causing a stack overflow in this recursive-descent parser. Chosen
/// far above ADMIT's real depth-32 rule (Task 4) so that it never fires on
/// any legitimately admitted document; it exists only so deep input fails
/// as a `ParseError`, not a process abort.
const MAX_PARSE_DEPTH: usize = 512;

/// Parses exactly one JSON value from `input`, per RFC 8259, with no
/// repair of malformed input — every rejection is a typed [`ParseError`].
pub fn parse(input: &[u8]) -> Result<Value, ParseError> {
    let text = std::str::from_utf8(input).map_err(|_| ParseError::InvalidUtf8)?;
    let mut parser = Parser {
        input: text,
        pos: 0,
        depth: 0,
    };
    parser.skip_whitespace();
    let value = parser.parse_value()?;
    parser.skip_whitespace();
    if parser.pos != text.len() {
        return Err(ParseError::TrailingData { pos: parser.pos });
    }
    Ok(value)
}

struct Parser<'a> {
    input: &'a str,
    pos: usize,
    depth: usize,
}

impl<'a> Parser<'a> {
    fn peek(&self) -> Option<char> {
        self.input[self.pos..].chars().next()
    }

    fn bump(&mut self) -> Option<char> {
        let c = self.peek()?;
        self.pos += c.len_utf8();
        Some(c)
    }

    fn skip_whitespace(&mut self) {
        // RFC 8259 §2: exactly these four characters are JSON whitespace.
        while matches!(self.peek(), Some(' ' | '\t' | '\n' | '\r')) {
            self.bump();
        }
    }

    fn enter_depth(&mut self) -> Result<(), ParseError> {
        if self.depth >= MAX_PARSE_DEPTH {
            return Err(ParseError::DepthLimitExceeded { pos: self.pos });
        }
        self.depth += 1;
        Ok(())
    }

    fn parse_value(&mut self) -> Result<Value, ParseError> {
        match self.peek() {
            None => Err(ParseError::UnexpectedEof { pos: self.pos }),
            Some('"') => self.parse_string().map(Value::String),
            Some('{') => self.parse_object(),
            Some('[') => self.parse_array(),
            Some('t') => self.parse_literal("true", Value::Bool(true)),
            Some('f') => self.parse_literal("false", Value::Bool(false)),
            Some('n') => self.parse_literal("null", Value::Null),
            Some(c) if c == '-' || c.is_ascii_digit() => self.parse_number(),
            Some(_) => Err(ParseError::UnexpectedChar { pos: self.pos }),
        }
    }

    fn parse_literal(&mut self, literal: &str, value: Value) -> Result<Value, ParseError> {
        let start = self.pos;
        if self.input[self.pos..].starts_with(literal) {
            self.pos += literal.len();
            Ok(value)
        } else {
            Err(ParseError::UnexpectedChar { pos: start })
        }
    }

    fn parse_object(&mut self) -> Result<Value, ParseError> {
        self.enter_depth()?;
        self.bump(); // '{'
        let mut members = Vec::new();
        self.skip_whitespace();
        if self.peek() == Some('}') {
            self.bump();
            self.depth -= 1;
            return Ok(Value::Object(members));
        }
        loop {
            self.skip_whitespace();
            if self.peek() != Some('"') {
                return Err(ParseError::UnexpectedChar { pos: self.pos });
            }
            let key = self.parse_string()?;
            self.skip_whitespace();
            if self.peek() != Some(':') {
                return Err(ParseError::UnexpectedChar { pos: self.pos });
            }
            self.bump();
            self.skip_whitespace();
            let value = self.parse_value()?;
            members.push((key, value));
            self.skip_whitespace();
            match self.peek() {
                Some(',') => {
                    self.bump();
                }
                Some('}') => {
                    self.bump();
                    break;
                }
                _ => return Err(ParseError::UnexpectedChar { pos: self.pos }),
            }
        }
        self.depth -= 1;
        Ok(Value::Object(members))
    }

    fn parse_array(&mut self) -> Result<Value, ParseError> {
        self.enter_depth()?;
        self.bump(); // '['
        let mut items = Vec::new();
        self.skip_whitespace();
        if self.peek() == Some(']') {
            self.bump();
            self.depth -= 1;
            return Ok(Value::Array(items));
        }
        loop {
            self.skip_whitespace();
            let value = self.parse_value()?;
            items.push(value);
            self.skip_whitespace();
            match self.peek() {
                Some(',') => {
                    self.bump();
                }
                Some(']') => {
                    self.bump();
                    break;
                }
                _ => return Err(ParseError::UnexpectedChar { pos: self.pos }),
            }
        }
        self.depth -= 1;
        Ok(Value::Array(items))
    }

    fn parse_string(&mut self) -> Result<String, ParseError> {
        let start = self.pos;
        self.bump(); // opening '"'
        let mut s = String::new();
        loop {
            match self.bump() {
                None => return Err(ParseError::UnexpectedEof { pos: start }),
                Some('"') => return Ok(s),
                Some('\\') => {
                    let escape_pos = self.pos - 1;
                    match self.bump() {
                        Some('"') => s.push('"'),
                        Some('\\') => s.push('\\'),
                        Some('/') => s.push('/'),
                        Some('b') => s.push('\u{0008}'),
                        Some('f') => s.push('\u{000C}'),
                        Some('n') => s.push('\n'),
                        Some('r') => s.push('\r'),
                        Some('t') => s.push('\t'),
                        Some('u') => {
                            let high = self.parse_hex4(escape_pos)?;
                            let scalar = self.resolve_unicode_escape(high, escape_pos)?;
                            s.push(scalar);
                        }
                        _ => return Err(ParseError::InvalidEscape { pos: escape_pos }),
                    }
                }
                Some(c) if (c as u32) < 0x20 => {
                    return Err(ParseError::RawControlInString {
                        pos: self.pos - c.len_utf8(),
                    })
                }
                Some(c) => s.push(c),
            }
        }
    }

    /// Reads the 4 hex digits of a `\uXXXX` escape (the `\u` itself already
    /// consumed) and returns the UTF-16 code unit they encode.
    fn parse_hex4(&mut self, escape_pos: usize) -> Result<u16, ParseError> {
        let mut value: u16 = 0;
        for _ in 0..4 {
            let c = self
                .bump()
                .ok_or(ParseError::UnexpectedEof { pos: escape_pos })?;
            let digit = c
                .to_digit(16)
                .ok_or(ParseError::InvalidEscape { pos: escape_pos })?;
            value = value * 16 + digit as u16;
        }
        Ok(value)
    }

    /// Given the code unit of a just-parsed `\uXXXX` escape, resolves it to
    /// a Unicode scalar value: a lone BMP code unit stands for itself; a
    /// high surrogate must be immediately followed by another `\uXXXX` low
    /// surrogate, combined per the standard surrogate-pair formula; a low
    /// surrogate on its own, or a high surrogate not followed by a low
    /// one, is rejected as [`ParseError::UnpairedSurrogate`].
    fn resolve_unicode_escape(&mut self, unit: u16, escape_pos: usize) -> Result<char, ParseError> {
        if (0xD800..=0xDBFF).contains(&unit) {
            // High surrogate: require an immediately following \uXXXX low
            // surrogate.
            if self.peek() != Some('\\') {
                return Err(ParseError::UnpairedSurrogate { pos: escape_pos });
            }
            self.bump(); // '\'
            if self.bump() != Some('u') {
                return Err(ParseError::UnpairedSurrogate { pos: escape_pos });
            }
            let low = self.parse_hex4(escape_pos)?;
            if !(0xDC00..=0xDFFF).contains(&low) {
                return Err(ParseError::UnpairedSurrogate { pos: escape_pos });
            }
            let high = unit as u32;
            let low = low as u32;
            let scalar = 0x10000 + ((high - 0xD800) << 10) + (low - 0xDC00);
            char::from_u32(scalar).ok_or(ParseError::UnpairedSurrogate { pos: escape_pos })
        } else if (0xDC00..=0xDFFF).contains(&unit) {
            Err(ParseError::UnpairedSurrogate { pos: escape_pos })
        } else {
            // Every u16 outside the surrogate range is a valid Unicode
            // scalar value on its own.
            char::from_u32(unit as u32).ok_or(ParseError::UnpairedSurrogate { pos: escape_pos })
        }
    }

    fn parse_number(&mut self) -> Result<Value, ParseError> {
        let start = self.pos;

        if self.peek() == Some('-') {
            self.bump();
        }

        match self.peek() {
            Some('0') => {
                self.bump();
                // A leading zero is not followed by another digit (RFC 8259
                // §6: int = "0" / ( digit1-9 *DIGIT )). If it is, we simply
                // stop consuming here; the leftover digit becomes a syntax
                // error at the caller (unexpected character where a `,`,
                // `}`, `]`, or end of input was expected), which correctly
                // rejects "01" without special-casing it here.
            }
            Some(c) if c.is_ascii_digit() => {
                while matches!(self.peek(), Some(c) if c.is_ascii_digit()) {
                    self.bump();
                }
            }
            _ => return Err(ParseError::InvalidNumber { pos: start }),
        }

        if self.peek() == Some('.') {
            self.bump();
            if !matches!(self.peek(), Some(c) if c.is_ascii_digit()) {
                return Err(ParseError::InvalidNumber { pos: start });
            }
            while matches!(self.peek(), Some(c) if c.is_ascii_digit()) {
                self.bump();
            }
        }

        if matches!(self.peek(), Some('e' | 'E')) {
            self.bump();
            if matches!(self.peek(), Some('+' | '-')) {
                self.bump();
            }
            if !matches!(self.peek(), Some(c) if c.is_ascii_digit()) {
                return Err(ParseError::InvalidNumber { pos: start });
            }
            while matches!(self.peek(), Some(c) if c.is_ascii_digit()) {
                self.bump();
            }
        }

        let text = &self.input[start..self.pos];
        let value: f64 = text
            .parse()
            .map_err(|_| ParseError::InvalidNumber { pos: start })?;
        if !value.is_finite() {
            return Err(ParseError::NonFiniteNumber { pos: start });
        }
        Ok(Value::Number(value))
    }
}

// ============================================================================
// ADMIT — §6.4 phase ①: reject-or-pass, no repair. Errata D5 (numeric
// bounds), D6 (depth-counting convention), D7 (four rejection classes
// R6.15's original enumeration omits). Built on top of `parse`/`Value`
// above rather than woven into the syntax parser, per this module's
// top-of-file note.
// ============================================================================

/// Maximum submission size, in raw bytes, checked before any parsing is
/// attempted (design spec §5.1).
pub const ADMIT_MAX_SUBMISSION_BYTES: usize = 1_048_576;

/// Maximum nesting depth, counting container (`{`/`[`) *openings* only —
/// never the innermost scalar (errata D6, R6.15 addendum). A document whose
/// innermost value sits inside exactly this many containers is accepted;
/// one nested a further level is rejected.
pub const ADMIT_MAX_DEPTH: usize = 32;

/// Maximum object members at any one nesting level (design spec §5.1:
/// "bounds the sort in canonicalization"). Checked independently at every
/// level, not cumulatively across the document.
pub const ADMIT_MAX_OBJECT_MEMBERS: usize = 1_024;

/// Maximum string length, in UTF-8 bytes of the *decoded* value (design
/// spec §5.1: "256 KiB ... bounds NFC normalization cost on a single
/// field"). Applied to object member names as well as string values —
/// R6.15's revised enumeration states each rejection class as a property of
/// the input, not scoped to one JSON position, and an oversize key is the
/// same normalization-cost and interop hazard as an oversize value.
pub const ADMIT_MAX_STRING_BYTES: usize = 262_144;

/// `2^53 - 1`, the largest safe integer (RFC 7493 §2.2; errata D5).
pub const ADMIT_MAX_SAFE_INTEGER: f64 = 9_007_199_254_740_991.0;

/// `-(2^53 - 1)`, the smallest safe integer. The bound is symmetric —
/// errata D5 is explicit that `2^53` and `-2^53` are *both* rejected, not
/// just the positive side the single pre-D5 vector pinned.
pub const ADMIT_MIN_SAFE_INTEGER: f64 = -9_007_199_254_740_991.0;

/// A rejection from the ADMIT phase.
///
/// Every rejection carries a stable slug in the `curia/admit/...`
/// namespace — the vocabulary `conformance/admit-reject/*/expect-reject`
/// files name (see [`AdmitError::predicate`]) — plus a human-readable
/// `detail` that is diagnostic only and never part of the stable contract.
/// ADMIT is reject-or-pass with no repair: there is no variant here that
/// carries a "corrected" value, on purpose.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AdmitError {
    slug: &'static str,
    detail: String,
}

impl AdmitError {
    fn new(slug: &'static str, detail: impl Into<String>) -> Self {
        Self {
            slug,
            detail: detail.into(),
        }
    }

    /// The stable rejection slug, e.g. `curia/admit/duplicate-key`.
    pub fn predicate(&self) -> &str {
        self.slug
    }

    /// A human-readable explanation of the rejection. Not part of the
    /// stable contract — only [`AdmitError::predicate`] is.
    pub fn detail(&self) -> &str {
        &self.detail
    }
}

impl fmt::Display for AdmitError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}: {}", self.slug, self.detail)
    }
}

impl std::error::Error for AdmitError {}

/// The ADMIT phase: reject-or-pass, no repair (CLAUDE.md's "no mutation
/// between verify and persist" — ADMIT never returns a corrected value,
/// only the original input or a typed rejection). Runs, in order:
///
/// 1. **Submission size** ([`ADMIT_MAX_SUBMISSION_BYTES`]) against the raw
///    byte slice — the cheapest possible check, so absurdly oversized
///    input never reaches any of the more expensive steps below.
/// 2. **A raw-byte scan for embedded NUL (`0x00`)**, before UTF-8
///    validation or JSON parsing. R6.15's "embedded NUL bytes" class (D7)
///    covers *any* NUL anywhere in the wire stream — including outside a
///    string, where RFC 8259 syntax would reject it only as a generic
///    unexpected character, not by name. Doing this first also means every
///    NUL byte is accounted for before [`parse`] runs, so a later
///    [`ParseError::RawControlInString`] (see [`map_parse_error`]) can
///    never be a NUL in disguise.
/// 3. **[`parse`]** — RFC 8259 syntax, UTF-8 validity, unpaired-surrogate
///    and non-finite-number rejection, all inherited unchanged from Task
///    2's parser. A [`ParseError`] here is remapped onto the matching
///    `curia/admit/...` slug by [`map_parse_error`].
/// 4. **A tree walk** ([`check_node`]) enforcing every business rule the
///    syntax parser cannot express on its own: depth (counting container
///    openings only — errata D6), object member count, string length,
///    Unicode noncharacters, duplicate member names, and the symmetric
///    safe-integer bound (errata D5), including that `2^53` and `-2^53`
///    are rejected exactly at the boundary while underflow to `0` is
///    accepted.
///
/// **Duplicate-member scope, by ruling.** The duplicate-key check compares
/// members' *wire* names — the strings [`parse`] decoded from the input,
/// exactly as written, with **no** Unicode normalization applied. A
/// controller ruling scoped this deliberately: NFC normalization (R6.9,
/// applied later, only on the path to a signature) can *manufacture* a
/// duplicate from two wire-distinct keys that happen to normalize to the
/// same NFC form (e.g. precomposed vs. combining-sequence "café"). That
/// post-normalization case is a different task's concern; this function
/// does not import or invoke any NFC helper, and does not attempt to
/// detect it.
///
/// On success, returns the parsed [`Value`] — admitted, but not yet
/// canonicalized or NFC-normalized. No `admit-reject/` vector expects
/// acceptance, so every vector in that family exercises the `Err` path.
pub fn admit(input: &[u8]) -> Result<Value, AdmitError> {
    if input.len() > ADMIT_MAX_SUBMISSION_BYTES {
        return Err(AdmitError::new(
            "curia/admit/too-large",
            format!(
                "submission is {} bytes, exceeds the {}-byte cap",
                input.len(),
                ADMIT_MAX_SUBMISSION_BYTES
            ),
        ));
    }

    if let Some(pos) = input.iter().position(|&b| b == 0x00) {
        return Err(AdmitError::new(
            "curia/admit/nul-byte",
            format!("raw NUL (0x00) byte at input offset {pos}"),
        ));
    }

    let value = parse(input).map_err(map_parse_error)?;
    check_node(&value, 0)?;
    Ok(value)
}

/// Maps a [`ParseError`] from [`parse`] onto the ADMIT slug naming the same
/// rejection class where the corpus names one (invalid UTF-8, unpaired
/// surrogate, non-finite number, excessive nesting), and a generic
/// `curia/admit/malformed-json` slug for every other syntax error, since
/// neither R6.15 nor the corpus names those individually.
fn map_parse_error(err: ParseError) -> AdmitError {
    match err {
        ParseError::InvalidUtf8 => AdmitError::new("curia/admit/invalid-utf8", err.to_string()),
        ParseError::UnpairedSurrogate { .. } => {
            AdmitError::new("curia/admit/unpaired-surrogate", err.to_string())
        }
        ParseError::NonFiniteNumber { .. } => {
            AdmitError::new("curia/admit/non-finite-number", err.to_string())
        }
        // `parse`'s own stack-safety guard (`MAX_PARSE_DEPTH`, far above
        // `ADMIT_MAX_DEPTH` — see its doc comment) and this phase's own
        // precise depth-32 rule (`check_node`, below) are both, to a
        // caller, "nested too deeply". Reporting the same slug regardless
        // of which guard actually fired keeps that caller-visible contract
        // single-valued; a document nested beyond `MAX_PARSE_DEPTH` is
        // necessarily also nested beyond `ADMIT_MAX_DEPTH`; the two never
        // disagree about whether to reject, only about which line of code
        // notices first.
        ParseError::DepthLimitExceeded { .. } => {
            AdmitError::new("curia/admit/depth-exceeded", err.to_string())
        }
        // Every raw NUL byte was already rejected before `parse` ran (see
        // `admit`), so a `RawControlInString` reaching here is a
        // *different* unescaped control character (e.g. a literal tab or
        // 0x01) — not a class R6.15 names, so it gets a generic slug
        // rather than `nul-byte`.
        ParseError::RawControlInString { .. } => {
            AdmitError::new("curia/admit/raw-control-character", err.to_string())
        }
        ParseError::UnexpectedEof { .. }
        | ParseError::UnexpectedChar { .. }
        | ParseError::InvalidEscape { .. }
        | ParseError::InvalidNumber { .. }
        | ParseError::TrailingData { .. } => {
            AdmitError::new("curia/admit/malformed-json", err.to_string())
        }
    }
}

/// Walks one node of an already-parsed tree, enforcing every ADMIT business
/// rule [`parse`] cannot express on its own: depth, member count, string
/// length, Unicode noncharacters, duplicate member names (wire-name
/// comparison only — see [`admit`]'s doc comment), and the symmetric
/// safe-integer bound.
///
/// `depth` is the number of container openings already consumed to reach
/// `value`; recursion here can never exceed [`MAX_PARSE_DEPTH`], because
/// `value` was itself produced by [`parse`], which enforces that bound
/// while building the tree — so this function needs no stack-safety guard
/// of its own.
fn check_node(value: &Value, depth: usize) -> Result<(), AdmitError> {
    match value {
        Value::Null | Value::Bool(_) => Ok(()),
        Value::Number(n) => check_number(*n),
        Value::String(s) => check_string(s),
        Value::Array(items) => {
            let depth = enter_container(depth)?;
            for item in items {
                check_node(item, depth)?;
            }
            Ok(())
        }
        Value::Object(members) => {
            let depth = enter_container(depth)?;
            if members.len() > ADMIT_MAX_OBJECT_MEMBERS {
                return Err(AdmitError::new(
                    "curia/admit/too-many-members",
                    format!(
                        "object has {} members, exceeds the {}-member cap",
                        members.len(),
                        ADMIT_MAX_OBJECT_MEMBERS
                    ),
                ));
            }
            let mut seen: HashSet<&str> = HashSet::with_capacity(members.len());
            for (key, _) in members {
                if !seen.insert(key.as_str()) {
                    return Err(AdmitError::new(
                        "curia/admit/duplicate-key",
                        format!("duplicate object member name `{key}`"),
                    ));
                }
            }
            for (key, val) in members {
                check_string(key)?;
                check_node(val, depth)?;
            }
            Ok(())
        }
    }
}

/// Accounts for one container opening and rejects if the resulting depth
/// exceeds [`ADMIT_MAX_DEPTH`]. Errata D6: depth counts container openings
/// only, never the innermost scalar, so a document whose innermost value
/// sits inside exactly `ADMIT_MAX_DEPTH` containers is accepted, and one
/// nested a further level is rejected — both sides of that boundary are
/// pinned by `tests/admit_boundaries.rs`, since the published
/// `admit-reject/over-nested` vector pins only the reject side.
fn enter_container(depth: usize) -> Result<usize, AdmitError> {
    let depth = depth + 1;
    if depth > ADMIT_MAX_DEPTH {
        return Err(AdmitError::new(
            "curia/admit/depth-exceeded",
            format!("nesting exceeds the {ADMIT_MAX_DEPTH}-container cap"),
        ));
    }
    Ok(depth)
}

/// Checks one decoded JSON string (an object member name or a string
/// value) against the string-length cap and the Unicode-noncharacter rule.
fn check_string(s: &str) -> Result<(), AdmitError> {
    if s.len() > ADMIT_MAX_STRING_BYTES {
        return Err(AdmitError::new(
            "curia/admit/string-too-long",
            format!(
                "string is {} bytes, exceeds the {}-byte cap",
                s.len(),
                ADMIT_MAX_STRING_BYTES
            ),
        ));
    }
    if let Some(c) = s.chars().find(|&c| is_noncharacter(c)) {
        return Err(AdmitError::new(
            "curia/admit/noncharacter",
            format!("string contains Unicode noncharacter U+{:04X}", c as u32),
        ));
    }
    Ok(())
}

/// Unicode §23.7 "Noncharacters, not recommended for use in open
/// interchange": `U+FDD0..=U+FDEF` (32 code points), plus `U+xFFFE` and
/// `U+xFFFF` in every one of the 17 planes (34 code points) — 66 total.
/// Errata D7 / design spec §5.1's `curia/admit/noncharacter` rule states
/// this as a property of the code point itself, deliberately not as
/// "whatever one platform's NFC implementation happens to throw on" (the
/// design spec's own rationale for why this class exists at all).
fn is_noncharacter(c: char) -> bool {
    let cp = c as u32;
    (0xFDD0..=0xFDEF).contains(&cp) || matches!(cp & 0xFFFF, 0xFFFE | 0xFFFF)
}

/// Checks one decoded JSON number against R6.33/errata D5's I-JSON-exact
/// numerics: an integer, and within the symmetric safe range.
///
/// `n` is always finite here: [`parse`] rejects a non-finite literal as
/// [`ParseError::NonFiniteNumber`] before a `Value::Number` can exist (see
/// [`map_parse_error`]), so finiteness is not re-checked.
fn check_number(n: f64) -> Result<(), AdmitError> {
    if n.fract() != 0.0 {
        return Err(AdmitError::new(
            "curia/admit/non-integer-number",
            format!("{n} is not an integer; envelope numerics are I-JSON-exact (R6.33)"),
        ));
    }
    if !(ADMIT_MIN_SAFE_INTEGER..=ADMIT_MAX_SAFE_INTEGER).contains(&n) {
        return Err(AdmitError::new(
            "curia/admit/unsafe-integer",
            format!(
                "{n} is outside the safe integer range \
                 [{ADMIT_MIN_SAFE_INTEGER}, {ADMIT_MAX_SAFE_INTEGER}] (errata D5)"
            ),
        ));
    }
    Ok(())
}
