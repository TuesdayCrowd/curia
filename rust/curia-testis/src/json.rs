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
//! - It is not the ADMIT phase. ADMIT's frozen limits (max size, max depth
//!   32 counting container openings only, max 1024 members per level, max
//!   string length, the `-(2^53-1) <= n <= 2^53-1` integer bound, duplicate
//!   key rejection) belong to Task 4, which is expected to build on this
//!   value model rather than duplicate it. This parser enforces JSON syntax
//!   (RFC 8259) and nothing more — it does not reject duplicate object
//!   keys, does not enforce a member-count cap, and does not enforce the
//!   safe-integer bound on numbers (design spec §5.4: "the canonicalizer
//!   still implements ECMAScript number serialization in full ... the
//!   envelope schema simply never produces input that reaches the
//!   fractional path" — i.e. `canonicalize` must handle *any* valid JSON
//!   number, not just I-JSON-safe ones).
//! - Its recursion-depth guard (`MAX_PARSE_DEPTH`, far above 32) exists
//!   solely so that a maliciously deep input produces a [`ParseError`]
//!   instead of a stack overflow (which would abort the process, not
//!   return a `Result` — a stronger violation of CHARTER §2's "Result, not
//!   panic" than an ordinary panic would be). It is not a stand-in for
//!   ADMIT's depth-32 rule, which is a business limit Task 4 owns.
//!
//! ## Why duplicate keys are out of scope here
//!
//! No vector this module is exercised against (the vendored `rfc8785/`
//! pairs, and the `ordering/`/`numbers/` inputs `canonicalize` is tested
//! against directly per the Task 2 controller ruling) contains a duplicate
//! object key. Rejecting duplicates is errata D7 / `admit-reject/duplicate-keys`'s
//! concern, exercised through the `admit` profile against `crate::admit`
//! (Task 4), not through `canonicalize`. If `parse` is ever handed an input
//! with duplicate keys, every occurrence is kept in [`Value::Object`], in
//! input order; nothing here deduplicates or rejects them.
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
