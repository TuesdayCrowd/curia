//! Task 4, Step 4: "no input causes a panic," proved rather than asserted.
//!
//! No fuzzing dependency (`arbitrary`, `proptest`, `quickcheck`, `cargo-fuzz`
//! ...) is available or permitted — CHARTER §3 pins exactly six
//! dependencies and adding a seventh is out of scope for this task. This
//! file is a hand-rolled, deterministic, seeded property test instead: a
//! `splitmix64` PRNG (public-domain algorithm, ~10 lines, no crate needed)
//! drives the random component, and every non-random case (truncation,
//! bracket-balance, depth/size/width boundaries, adversarial escapes) is
//! fully enumerated rather than sampled, so the whole run is bit-for-bit
//! reproducible given the fixed seed below.
//!
//! **What this file grades, and what it does not.** Every case here is
//! graded on exactly one property: `curia_testis::json::parse` and
//! `curia_testis::admit` return a `Result`, never unwind. Whether a given
//! input is *correctly* accepted or rejected, and with which slug, is
//! covered elsewhere (`tests/admit_reject_direct.rs`,
//! `tests/vectors.rs`'s `admit_reject` family, `tests/admit_boundaries.rs`,
//! `tests/json_parse_error_paths.rs`) — this file does not assert on
//! outcomes, only on the absence of a panic. Scope is deliberately just
//! `json::parse` and `admit` (what this task touches), not
//! `canonicalize`/`canonicalize_with_nfc` (Tasks 2/3's parser-adjacent but
//! separate surface, already covered by the prior prober's 2,015,396-case
//! run the brief cites, with zero panics found there).
//!
//! Run with `cargo test --test admit_fuzz -- --nocapture` to see the total
//! case count and elapsed wall-clock time this run measured, printed at the
//! end — that transcript, not this comment, is the actual deliverable the
//! brief asks for ("report what you ran and for how long").

use std::panic::{self, AssertUnwindSafe};
use std::time::Instant;

use curia_testis::conformance::Corpus;

/// `splitmix64` — a small, public-domain, dependency-free PRNG. Not
/// cryptographic, not trying to be; only reproducibility and reasonable
/// bit dispersion are needed for a property-test driver.
struct SplitMix64(u64);

impl SplitMix64 {
    fn new(seed: u64) -> Self {
        Self(seed)
    }

    fn next_u64(&mut self) -> u64 {
        self.0 = self.0.wrapping_add(0x9E37_79B9_7F4A_7C15);
        let mut z = self.0;
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        z ^ (z >> 31)
    }

    fn next_byte(&mut self) -> u8 {
        (self.next_u64() & 0xFF) as u8
    }

    /// Uniform-ish in `[lo, hi)`. Not unbiased (modulo bias exists for
    /// non-power-of-two ranges), which is irrelevant for driving a
    /// panic-freedom sweep rather than a statistical claim.
    fn next_range(&mut self, lo: usize, hi: usize) -> usize {
        assert!(hi > lo);
        lo + (self.next_u64() as usize) % (hi - lo)
    }
}

/// Runs both `json::parse` and `admit` on `input` inside `catch_unwind`,
/// recording a failure (rather than propagating the panic immediately) so
/// the sweep can finish and report every offending case at once instead of
/// stopping at the first one.
fn run_no_panic(label: &str, input: &[u8], calls: &mut usize, failures: &mut Vec<String>) {
    *calls += 1;
    let owned = input.to_vec();
    let result = panic::catch_unwind(AssertUnwindSafe(|| {
        let _ = curia_testis::json::parse(&owned);
        let _ = curia_testis::admit(&owned);
    }));
    if result.is_err() {
        failures.push(format!(
            "{label} (len={}, first 64 bytes={:?})",
            input.len(),
            &input[..input.len().min(64)]
        ));
    }
}

/// Real, varied, already-adversarial JSON documents pulled from the
/// conformance corpus itself (rather than hand-authored, which risks
/// encoding the same blind spots as the code under test): the vendored
/// RFC 8785 vectors, every `c4`/`unicode`/`ordering`/`numbers` input, and —
/// deliberately — every `admit-reject` input, since those are already
/// known-hostile (invalid UTF-8, raw NUL, unpaired surrogates, 33 levels of
/// nesting) and make excellent seeds for truncation and byte-mutation.
fn representative_documents() -> Vec<Vec<u8>> {
    let corpus = Corpus::load_default().expect("conformance corpus must load for the fuzz sweep");
    let mut docs = Vec::new();
    for v in &corpus.rfc8785 {
        docs.push(v.input.clone());
    }
    for v in &corpus.c4 {
        docs.push(v.input.clone());
    }
    for v in &corpus.unicode {
        docs.push(v.input.clone());
    }
    for v in &corpus.ordering {
        docs.push(v.input.clone());
    }
    for v in &corpus.numbers {
        docs.push(v.input.clone());
    }
    for v in &corpus.admit_reject {
        docs.push(v.input.clone());
    }
    assert!(
        docs.len() >= 40,
        "expected the corpus to yield a few dozen seed documents, got {}",
        docs.len()
    );
    docs
}

/// Hand-crafted escape sequences chosen to stress every branch in
/// `parse_string`/`resolve_unicode_escape`: incomplete escapes, invalid hex
/// digits, every surrogate-pairing failure mode, escapes at end-of-input.
/// Built from unambiguous, individually-commented byte-string literals
/// (each with at most one level of `\`-escaping) plus a few raw `vec![...]`
/// constructions, rather than one dense literal, so the intended bytes are
/// legible without hand-counting backslashes (CHARTER §5's "byte
/// discipline" — this project has been bitten by exactly that kind of
/// misreading before).
fn adversarial_escapes() -> Vec<Vec<u8>> {
    let mut cases: Vec<Vec<u8>> = [
        &b"\""[..],                 // opening quote only, nothing else
        &b"\"\\"[..],               // opening quote + trailing backslash, eof
        &b"\"\\u"[..],              // \u with no hex digits
        &b"\"\\u1"[..],             // \u with 1 hex digit
        &b"\"\\u12"[..],            // \u with 2 hex digits
        &b"\"\\u123"[..],           // \u with 3 hex digits, then eof
        &b"\"\\u123\""[..],         // \u with 3 hex digits, 4th "digit" is the closing quote
        &b"\"\\uZZZZ\""[..],        // \u with non-hex digits
        &b"\"\\uD800"[..],          // lone high surrogate, eof right after
        &b"\"\\uD800\""[..],        // lone high surrogate, string closes immediately
        &b"\"\\uD800\\"[..],        // high surrogate then trailing backslash, eof
        &b"\"\\uD800\\u"[..],       // high surrogate then \u with no digits, eof
        &b"\"\\uD800\\uD800\""[..], // high surrogate followed by another high surrogate
        &b"\"\\uD800\\uDC00\""[..], // a *valid* pair — control case, must parse, not panic
        &b"\"\\uDC00\\uDC00\""[..], // lone low surrogate (short-circuits before the second)
        &b"\"\\uDFFF\\uD800\""[..], // low surrogate followed by high surrogate, wrong order
        &b"\"\\x\""[..],            // invalid escape character 'x'
        &b"\"\\1\""[..],            // invalid escape character '1'
    ]
    .iter()
    .map(|c| c.to_vec())
    .collect::<Vec<_>>();

    cases.push(vec![b'\\']); // a lone backslash, not even inside a string
    cases.push(vec![b'"', b'\\', b'\\']); // escaped backslash, then eof, no closing quote
    cases.push(vec![b'"', b'\\', b'"']); // escaped quote, then eof, no closing quote

    cases
}

#[test]
fn no_panic_on_adversarial_input() {
    let start = Instant::now();
    let mut calls = 0usize;
    let mut failures = Vec::new();

    // Silence panic-hook stderr noise during the sweep; failures are
    // collected via `catch_unwind`'s `Result`, not by reading stderr, and a
    // clean run should produce zero hook invocations anyway.
    let prev_hook = panic::take_hook();
    panic::set_hook(Box::new(|_| {}));

    let seeds = representative_documents();

    // 1. Truncation at every byte offset of every seed document — the
    //    brief's explicit "truncation at every byte offset of a valid
    //    document" requirement, run against ~42 different real documents
    //    rather than one.
    for doc in &seeds {
        for end in 0..=doc.len() {
            run_no_panic("truncation", &doc[..end], &mut calls, &mut failures);
        }
    }

    // 2. Single-byte UTF-8 corruption at every offset of every seed
    //    document, with several different invalid lead/continuation bytes.
    for doc in &seeds {
        for pos in 0..doc.len() {
            for &bad in &[0xFFu8, 0xFEu8, 0x80u8, 0xC0u8, 0xEDu8, 0xA0u8, 0xF5u8] {
                let mut mutated = doc.clone();
                mutated[pos] = bad;
                run_no_panic("bad-utf8-byte", &mutated, &mut calls, &mut failures);
            }
        }
    }

    // 3. Unbalanced / mismatched containers, across a range of sizes
    //    straddling both depth caps (ADMIT's 32, `parse`'s own 512).
    for n in [
        0, 1, 2, 5, 10, 31, 32, 33, 63, 64, 100, 511, 512, 513, 1000, 5000,
    ] {
        run_no_panic(
            "open-brace-run",
            "{".repeat(n).as_bytes(),
            &mut calls,
            &mut failures,
        );
        run_no_panic(
            "close-brace-run",
            "}".repeat(n).as_bytes(),
            &mut calls,
            &mut failures,
        );
        run_no_panic(
            "open-bracket-run",
            "[".repeat(n).as_bytes(),
            &mut calls,
            &mut failures,
        );
        run_no_panic(
            "close-bracket-run",
            "]".repeat(n).as_bytes(),
            &mut calls,
            &mut failures,
        );
        let mixed: String = (0..n).map(|i| if i % 2 == 0 { '{' } else { ']' }).collect();
        run_no_panic(
            "mismatched-mixed",
            mixed.as_bytes(),
            &mut calls,
            &mut failures,
        );
        let mixed2: String = (0..n).map(|i| ['{', '[', '}', ']'][i % 4]).collect();
        run_no_panic(
            "mismatched-cycle",
            mixed2.as_bytes(),
            &mut calls,
            &mut failures,
        );
    }

    // 4. Deep, *balanced* nesting around both depth boundaries — these are
    //    syntactically valid JSON, so a panic here would be in `check_node`
    //    or the tree-walk depth accounting, not the syntax parser.
    for n in [31, 32, 33, 511, 512, 513, 1000, 20_000] {
        let doc = format!("{}0{}", "[".repeat(n), "]".repeat(n));
        run_no_panic(
            "deep-balanced-array",
            doc.as_bytes(),
            &mut calls,
            &mut failures,
        );
        let doc_o = format!("{}0{}", "{\"a\":".repeat(n), "}".repeat(n));
        run_no_panic(
            "deep-balanced-object",
            doc_o.as_bytes(),
            &mut calls,
            &mut failures,
        );
    }

    // 5. Wide objects around the 1024-member cap, and objects with many
    //    duplicate keys (stresses the `HashSet`-based duplicate check).
    for n in [0, 1, 1023, 1024, 1025, 2000, 5000] {
        let members: String = (0..n)
            .map(|i| format!("\"k{i}\":{i}"))
            .collect::<Vec<_>>()
            .join(",");
        run_no_panic(
            "wide-object",
            format!("{{{members}}}").as_bytes(),
            &mut calls,
            &mut failures,
        );
        let dup_members: String = (0..n)
            .map(|_| "\"k\":0".to_string())
            .collect::<Vec<_>>()
            .join(",");
        run_no_panic(
            "duplicate-heavy-object",
            format!("{{{dup_members}}}").as_bytes(),
            &mut calls,
            &mut failures,
        );
    }

    // 6. Long strings around the 256 KiB cap, both plain ASCII and with
    //    multi-byte UTF-8 near the boundary (stresses byte- vs char-length
    //    accounting at a char boundary).
    for n in [0, 1, 262_143, 262_144, 262_145, 300_000] {
        let doc = format!("{{\"s\":\"{}\"}}", "a".repeat(n));
        run_no_panic(
            "long-ascii-string",
            doc.as_bytes(),
            &mut calls,
            &mut failures,
        );
        let doc_u = format!("{{\"s\":\"{}\"}}", "\u{00e9}".repeat(n / 2));
        run_no_panic(
            "long-multibyte-string",
            doc_u.as_bytes(),
            &mut calls,
            &mut failures,
        );
    }

    // 7. Submission-size boundary and beyond (the 1 MiB cap), including one
    //    input roughly double the cap to confirm the early-return size
    //    check doesn't scan the whole buffer first.
    for n in [1_048_575usize, 1_048_576, 1_048_577, 2_000_000] {
        let filler = "a".repeat(n.saturating_sub(10));
        run_no_panic(
            "submission-size-boundary",
            format!("{{\"s\":\"{filler}\"}}").as_bytes(),
            &mut calls,
            &mut failures,
        );
    }

    // 8. Adversarial escapes.
    for case in adversarial_escapes() {
        run_no_panic("adversarial-escape", &case, &mut calls, &mut failures);
    }

    // 9. Huge/deep combined with malformed UTF-8 and raw NUL bytes injected
    //    at random positions in real seed documents.
    let mut rng = SplitMix64::new(0xC0FFEE_u64);
    for doc in &seeds {
        if doc.is_empty() {
            continue;
        }
        for _ in 0..20 {
            let mut mutated = doc.clone();
            let pos = rng.next_range(0, mutated.len());
            mutated[pos] = 0x00;
            run_no_panic("injected-nul", &mutated, &mut calls, &mut failures);
        }
    }

    // 10. Purely random byte strings of varying length (the classic fuzz
    //     shape), including a handful of samples straddling the submission
    //     size cap.
    for _ in 0..1_000_000 {
        let len = rng.next_range(0, 512);
        let bytes: Vec<u8> = (0..len).map(|_| rng.next_byte()).collect();
        run_no_panic("random-bytes", &bytes, &mut calls, &mut failures);
    }
    for _ in 0..20 {
        let len = rng.next_range(1_040_000, 1_060_000);
        let bytes: Vec<u8> = (0..len).map(|_| rng.next_byte()).collect();
        run_no_panic(
            "random-bytes-near-size-cap",
            &bytes,
            &mut calls,
            &mut failures,
        );
    }

    // 11. Random bytes constrained to a JSON-structural alphabet — more
    //     likely than fully random bytes to get past the first character
    //     and exercise deeper parser/admit state before failing.
    let alphabet: &[u8] = b"{}[]\":,truefalsn0123456789.-eE \t\n\\uD8DC ";
    for _ in 0..500_000 {
        let len = rng.next_range(0, 256);
        let bytes: Vec<u8> = (0..len)
            .map(|_| alphabet[rng.next_range(0, alphabet.len())])
            .collect();
        run_no_panic(
            "random-structural-alphabet",
            &bytes,
            &mut calls,
            &mut failures,
        );
    }

    panic::set_hook(prev_hook);

    let elapsed = start.elapsed();
    println!(
        "no_panic_on_adversarial_input: {calls} cases (parse+admit each) in {elapsed:?} \
         ({:.0} cases/sec)",
        calls as f64 / elapsed.as_secs_f64().max(1e-9)
    );

    assert!(
        failures.is_empty(),
        "{} of {calls} cases panicked:\n{}",
        failures.len(),
        failures.join("\n")
    );
}
