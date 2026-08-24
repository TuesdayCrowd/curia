//! R6.39's four frozen magnitudes, read out of the white paper at test time
//! and compared against this crate's `ADMIT_MAX_*` constants.
//!
//! # Why the specification is parsed rather than transcribed
//!
//! `tests/admit_boundaries.rs` pins both sides of the depth boundary, and
//! `tests/admit_fuzz.rs` sweeps values around three of the four caps — but
//! every one of those inputs is built from the constant it is checking, or
//! from a literal typed beside it. Narrow `ADMIT_MAX_STRING_BYTES` to 4 KiB
//! and the boundary tests still pass, because each moved with the constant.
//! The C# implementation had the identical blind spot, confirmed by narrowing
//! all four of its caps and watching its suite stay green.
//!
//! So the constants answer to the published sentence instead. Two
//! implementations that each check themselves against their own source agree
//! only by luck; two that check themselves against the same paragraph of the
//! same document agree by construction — which is the posture R14.6 wants,
//! since a divergence between them is a release blocker.
//!
//! The C# twin is `tests/Curia.Canon.Tests/Json/PublishedAdmitLimitsTests.cs`.

use curia_testis::json::{
    ADMIT_MAX_DEPTH, ADMIT_MAX_OBJECT_MEMBERS, ADMIT_MAX_STRING_BYTES, ADMIT_MAX_SUBMISSION_BYTES,
};
use std::path::PathBuf;

/// `CURIA_WHITEPAPER`, if set, wins outright; otherwise the white paper is
/// resolved relative to this crate's manifest directory, exactly as
/// `conformance::conformance_dir` resolves the corpus. Absent, the test
/// **fails** rather than skipping: a conformance check that quietly does not
/// run is indistinguishable from one that passed, which is the whole defect
/// this file exists to close.
fn white_paper_path() -> PathBuf {
    if let Ok(p) = std::env::var("CURIA_WHITEPAPER") {
        return PathBuf::from(p);
    }
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../curia-agent-forum-WHITEPAPER.md")
}

/// One clause of R6.39's enumeration.
#[derive(Debug, Clone)]
struct Limit {
    /// The clause as published, whitespace-normalized.
    text: String,
    /// The number inside the clause's bold span (`"1,024"` -> `1024`).
    magnitude: u64,
    /// The unit word inside the bold span.
    unit: String,
    /// The parenthetical byte count following the bold span, where the clause
    /// gives one. R6.39 glosses both binary-prefix magnitudes and neither
    /// count, which is what lets `bytes()` cross-check prefix against gloss.
    byte_gloss: Option<u64>,
}

impl Limit {
    /// The magnitude expressed in bytes, or `None` where the clause does not
    /// measure bytes at all (depth counts containers, the member cap members).
    ///
    /// Computed from the binary prefix rather than read from the gloss,
    /// deliberately: the two are then compared, so a cap published as
    /// "1 MiB (1,000,000 bytes)" fails here instead of handing two
    /// implementations two defensible readings 4.9 % apart.
    fn bytes(&self) -> Option<u64> {
        match self.unit.as_str() {
            "bytes" => Some(self.magnitude),
            "KiB" => Some(self.magnitude * 1024),
            "MiB" => Some(self.magnitude * 1024 * 1024),
            _ => None,
        }
    }
}

/// The phrase each clause is keyed by — the clause's own subject words, never
/// its position in the sentence. Keying by ordinal would let a reordered
/// enumeration compare the string cap against the member cap and still pass.
const CLAUSE_KEYS: [&str; 4] = [
    "nesting depth",
    "per object",
    "submission size",
    "string length",
];

/// R6.39's paragraph as published, whitespace-normalized to one line.
fn paragraph() -> String {
    let path = white_paper_path();
    let text = std::fs::read_to_string(&path).unwrap_or_else(|e| {
        panic!(
            "R6.39 conformance needs the white paper at {}: {e}. \
             Set CURIA_WHITEPAPER if it lives elsewhere.",
            path.display()
        )
    });

    let start = text
        .find("**R6.39**")
        .expect("the white paper no longer contains '**R6.39**'");
    let rest = &text[start..];
    let end = rest.find("\n\n").unwrap_or(rest.len());

    rest[..end].split_whitespace().collect::<Vec<_>>().join(" ")
}

/// Extracts `(number, unit)` from the first `**<number> <unit>**` span.
fn bold_magnitude(clause: &str) -> Option<(u64, String)> {
    let open = clause.find("**")?;
    let after = &clause[open + 2..];
    let close = after.find("**")?;
    let span = &after[..close];

    let (number, unit) = span.split_once(char::is_whitespace)?;
    let value = number.replace(',', "").parse::<u64>().ok()?;
    Some((value, unit.trim().to_string()))
}

/// Extracts the byte count from a `(<number> bytes)` parenthetical.
fn byte_gloss(clause: &str) -> Option<u64> {
    let open = clause.find('(')?;
    let close = clause[open..].find(')')? + open;
    let inner = clause[open + 1..close].trim();
    let number = inner.strip_suffix("bytes")?.trim();
    number.replace(',', "").parse::<u64>().ok()
}

/// R6.39's clauses, keyed by `CLAUSE_KEYS`.
fn limits() -> Vec<(&'static str, Limit)> {
    let paragraph = paragraph();

    let list_start = paragraph
        .find("SHALL be exactly:")
        .expect("R6.39 no longer introduces its limits with 'SHALL be exactly:'")
        + "SHALL be exactly:".len();

    // The enumeration runs to the first sentence end. "R6.15's" and "UTF-8"
    // contain no period-then-space, so the first ". " closes the list.
    let body = &paragraph[list_start..];
    let list_end = body
        .find(". ")
        .expect("R6.39's enumeration does not end in a sentence");

    let mut out: Vec<(&'static str, Limit)> = Vec::new();

    for raw in body[..list_end].split(';') {
        let clause = raw.trim();
        if clause.is_empty() {
            continue;
        }

        let (magnitude, unit) = bold_magnitude(clause)
            .unwrap_or_else(|| panic!("R6.39 clause states no bold magnitude: '{clause}'"));

        let matches: Vec<&'static str> = CLAUSE_KEYS
            .iter()
            .copied()
            .filter(|k| clause.contains(k))
            .collect();
        assert_eq!(
            matches.len(),
            1,
            "R6.39 clause names {} recognised limits, expected exactly 1: '{clause}'. \
             If the wording changed, re-read R6.39 before changing CLAUSE_KEYS.",
            matches.len()
        );

        let key = matches[0];
        assert!(
            !out.iter().any(|(k, _)| *k == key),
            "R6.39 states '{key}' more than once"
        );

        out.push((
            key,
            Limit {
                text: clause.to_string(),
                magnitude,
                unit,
                byte_gloss: byte_gloss(clause),
            },
        ));
    }

    out
}

fn limit(key: &str) -> Limit {
    limits()
        .into_iter()
        .find(|(k, _)| *k == key)
        .unwrap_or_else(|| panic!("R6.39 states no '{key}' limit"))
        .1
}

/// The vacuity guard, and it comes first: a parser that silently returns
/// nothing makes every comparison below pass by having nothing to compare.
/// R6.39 publishes the count itself — "ADMIT's *four* size-shaped limits" —
/// so the count is checked against the sentence, not against this file's own
/// idea of how many there ought to be.
#[test]
fn published_sentence_enumerates_exactly_the_four_limits_it_claims_to() {
    let paragraph = paragraph();
    assert!(
        paragraph.contains("four size-shaped limits"),
        "R6.39 no longer claims to state four limits: {paragraph}"
    );

    let mut keys: Vec<&str> = limits().iter().map(|(k, _)| *k).collect();
    keys.sort_unstable();
    assert_eq!(
        keys,
        [
            "nesting depth",
            "per object",
            "string length",
            "submission size"
        ]
    );
}

#[test]
fn depth_cap_matches_the_published_number() {
    let published = limit("nesting depth");
    assert_eq!(
        published.magnitude as usize, ADMIT_MAX_DEPTH,
        "ADMIT_MAX_DEPTH disagrees with R6.39: '{}'",
        published.text
    );
    assert_eq!(published.unit, "containers");
}

#[test]
fn member_cap_matches_the_published_number() {
    let published = limit("per object");
    assert_eq!(
        published.magnitude as usize, ADMIT_MAX_OBJECT_MEMBERS,
        "ADMIT_MAX_OBJECT_MEMBERS disagrees with R6.39: '{}'",
        published.text
    );
    assert_eq!(published.unit, "members");
}

#[test]
fn submission_size_cap_matches_the_published_number() {
    let published = limit("submission size");
    assert_eq!(
        published.bytes().map(|b| b as usize),
        Some(ADMIT_MAX_SUBMISSION_BYTES),
        "ADMIT_MAX_SUBMISSION_BYTES disagrees with R6.39: '{}'",
        published.text
    );
}

#[test]
fn string_length_cap_matches_the_published_number() {
    let published = limit("string length");
    assert_eq!(
        published.bytes().map(|b| b as usize),
        Some(ADMIT_MAX_STRING_BYTES),
        "ADMIT_MAX_STRING_BYTES disagrees with R6.39: '{}'",
        published.text
    );
}

/// Both byte-valued caps are published twice — once as a binary prefix, once
/// as an exact count — and the two must agree.
#[test]
fn binary_prefix_and_parenthetical_byte_count_agree() {
    for key in ["submission size", "string length"] {
        let published = limit(key);
        assert_eq!(
            published.byte_gloss,
            published.bytes(),
            "R6.39's '{key}' clause glosses its own magnitude inconsistently: '{}'",
            published.text
        );
        assert!(published.byte_gloss.is_some());
    }
}

/// The unit of measurement is as load-bearing as the magnitude: 256 KiB of
/// UTF-8 bytes and 256 KiB of UTF-16 code units are different caps for every
/// non-ASCII document, and the difference is invisible to a suite whose
/// fixtures are all ASCII.
#[test]
fn published_caps_are_measured_in_utf8_bytes() {
    assert!(paragraph().contains("measured in UTF-8 bytes"));
}

/// R15.1's freeze is what makes this file worth having: an unfrozen constant
/// may legitimately drift and needs no conformance check.
#[test]
fn published_caps_are_still_claimed_frozen_under_r15_1() {
    assert!(paragraph().contains("frozen values under R15.1"));
}
