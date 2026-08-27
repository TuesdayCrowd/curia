//! Task 4, Step 4: "no input causes a panic," proved rather than asserted.
//!
//! No fuzzing dependency (`arbitrary`, `proptest`, `quickcheck`, `cargo-fuzz`
//! ...) is available or permitted — CHARTER §3 pins exactly six
//! dependencies and adding a seventh is out of scope for this task. This
//! file is a hand-rolled, deterministic, seeded property test instead: a
//! `splitmix64` PRNG (public-domain algorithm, ~10 lines, no crate needed)
//! drives the random component, and every non-random case (truncation,
//! bracket-balance, depth/size/width boundaries, adversarial escapes) is
//! fully enumerated rather than sampled -- with one bounded exception
//! documented at `EXHAUSTIVE_OFFSET_SWEEP_MAX_BYTES` below, which the run
//! prints -- so the whole run is bit-for-bit reproducible given the fixed
//! seed below.
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

use std::collections::BTreeSet;
use std::panic::{self, AssertUnwindSafe};
use std::time::Instant;

use curia_testis::conformance::Corpus;
use curia_testis::json::{ADMIT_MAX_STRING_BYTES, ADMIT_MAX_SUBMISSION_BYTES};

/// Above this seed size, the two offset sweeps sample rather than exhaust.
///
/// Both are quadratic in the seed: truncation parses a prefix at every offset,
/// and the UTF-8 corruption sweep parses at every offset seven times over. That
/// was free while the largest corpus document was a few hundred bytes. It stopped
/// being free when errata G2 published R6.39's boundary vectors, whose rejecting
/// sides are 1,048,577 and 262,153 bytes *by construction* -- the 1 MiB seed alone
/// is on the order of 10^12 byte-operations across the two sweeps, which is hours.
///
/// A seed at or below this size still gets every offset, exactly as before. A
/// larger one gets a bounded, deterministic set: both edges, every ADMIT cap
/// boundary it straddles, and a fixed stride through the interior. The edges and
/// the cap boundaries are where a truncation or corruption bug actually lives; a
/// uniform sweep of the interior of a 1 MiB run of `a` bytes re-tests one code
/// path a million times.
///
/// **The run prints which seeds were sampled and how many offsets each
/// contributed**, and a test below fails if sampling ever silently becomes the
/// common case. A coverage bound nobody can see reads exactly like coverage --
/// which is the defect this suite has already been caught by once, when the
/// submission-size sweep never straddled its boundary and looked green doing it.
const EXHAUSTIVE_OFFSET_SWEEP_MAX_BYTES: usize = 8 * 1024;

/// Interior stride budget for a sampled seed. With the edges and cap boundaries
/// added, a large seed contributes a few hundred offsets rather than a million.
const SAMPLED_INTERIOR_OFFSETS: usize = 96;

/// Offsets to probe in a seed of `len` bytes, and whether they are exhaustive.
///
/// Deterministic: no PRNG, so this run stays bit-for-bit reproducible.
fn sweep_offsets(len: usize) -> (Vec<usize>, bool) {
    if len <= EXHAUSTIVE_OFFSET_SWEEP_MAX_BYTES {
        return ((0..=len).collect(), true);
    }

    let mut offsets: BTreeSet<usize> = BTreeSet::new();

    // Both edges in full: a truncation bug that exists at all almost certainly
    // exists in the first or last few dozen bytes, where the container and string
    // framing is.
    let edge = 48.min(len);
    offsets.extend(0..edge);
    offsets.extend(len.saturating_sub(edge)..=len);

    // Every ADMIT cap boundary the seed straddles, and its immediate neighbours --
    // the offsets where truncating or corrupting flips which rule decides the verdict.
    for boundary in [ADMIT_MAX_STRING_BYTES, ADMIT_MAX_SUBMISSION_BYTES] {
        for delta in -2i64..=2 {
            let at = boundary as i64 + delta;
            if at >= 0 && (at as usize) <= len {
                offsets.insert(at as usize);
            }
        }
    }

    // A fixed stride through the interior, so no large region is wholly unprobed.
    let stride = (len / SAMPLED_INTERIOR_OFFSETS).max(1);
    offsets.extend((0..=len).step_by(stride));

    (offsets.into_iter().collect(), false)
}

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

/// A syntactically valid document of exactly `total` bytes whose verdict only
/// the submission-size cap can decide: sixteen members (far under the
/// 1,024-member cap), each holding a string far under the 256 KiB string cap.
fn document_of_exact_size(total: usize) -> Vec<u8> {
    const SLOTS: usize = 16;
    let skeleton = 2 + SLOTS * 6 + (SLOTS - 1); // {"a":"", ... ,"p":""}
    let payload = total - skeleton;
    let base = payload / SLOTS;
    let extra = payload % SLOTS;

    let mut doc = String::with_capacity(total);
    doc.push('{');
    for i in 0..SLOTS {
        if i > 0 {
            doc.push(',');
        }
        doc.push('"');
        doc.push((b'a' + i as u8) as char);
        doc.push_str("\":\"");
        for _ in 0..base + if i == 0 { extra } else { 0 } {
            doc.push('a');
        }
        doc.push('"');
    }
    doc.push('}');

    assert_eq!(doc.len(), total, "exact-size builder is off");
    doc.into_bytes()
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
    let mut exhaustive_seeds = 0usize;
    let mut sampled_seeds: Vec<(usize, usize)> = Vec::new();
    for doc in &seeds {
        let (offsets, exhaustive) = sweep_offsets(doc.len());
        if exhaustive {
            exhaustive_seeds += 1;
        } else {
            sampled_seeds.push((doc.len(), offsets.len()));
        }
        for &end in &offsets {
            run_no_panic("truncation", &doc[..end], &mut calls, &mut failures);
        }
    }

    // 2. Single-byte UTF-8 corruption at every offset of every seed
    //    document, with several different invalid lead/continuation bytes.
    for doc in &seeds {
        // One buffer per seed, restored after each offset, rather than a fresh clone
        // per case: at these sizes the clone was costing more than the parse.
        let mut mutated = doc.clone();
        let (offsets, _) = sweep_offsets(doc.len());
        for &pos in offsets.iter().filter(|&&p| p < doc.len()) {
            let original = mutated[pos];
            for &bad in &[0xFFu8, 0xFEu8, 0x80u8, 0xC0u8, 0xEDu8, 0xA0u8, 0xF5u8] {
                mutated[pos] = bad;
                run_no_panic("bad-utf8-byte", &mutated, &mut calls, &mut failures);
            }
            mutated[pos] = original;
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

    // 7a. Very large single-string documents. These were labelled
    //     "submission-size-boundary" and are not: `{"s":"<n-10 a's>"}` is a
    //     document of n-2 bytes, so at n = 1_048_577 it is still 1,048,575
    //     bytes -- under the 1 MiB cap -- and all three of the sub-2 MB cases
    //     are decided by the 256 KiB *string* cap long before the size cap is
    //     consulted. Verified by running both differential endpoints, which
    //     answer curia/admit/string-too-long for the first three.
    //
    //     Kept, under an honest label: a ~1 MiB single string is still a
    //     worthwhile panic-freedom case for the string path.
    for n in [1_048_575usize, 1_048_576, 1_048_577, 2_000_000] {
        let filler = "a".repeat(n.saturating_sub(10));
        run_no_panic(
            "oversize-single-string",
            format!("{{\"s\":\"{filler}\"}}").as_bytes(),
            &mut calls,
            &mut failures,
        );
    }

    // 7b. The submission-size boundary the label above promised, actually
    //     straddled. Reaching the 1 MiB cap means spreading the bytes so that
    //     no single string approaches the 256 KiB string cap and no object
    //     approaches the 1,024-member cap -- sixteen members of ~64 KiB each.
    //     Both sides of this boundary are graded in
    //     `tests/admit_boundaries.rs`; here they only have to not panic, but a
    //     maximal admitted document is the interesting panic case, and before
    //     this block no such document was ever built.
    for n in [
        ADMIT_MAX_SUBMISSION_BYTES - 1,
        ADMIT_MAX_SUBMISSION_BYTES,
        ADMIT_MAX_SUBMISSION_BYTES + 1,
        2_000_000,
    ] {
        run_no_panic(
            "submission-size-boundary",
            &document_of_exact_size(n),
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

    println!(
        "  offset sweeps: {exhaustive_seeds} seed(s) exhaustive (<= {EXHAUSTIVE_OFFSET_SWEEP_MAX_BYTES} bytes), \
         {} sampled: {}",
        sampled_seeds.len(),
        if sampled_seeds.is_empty() {
            "none".to_string()
        } else {
            sampled_seeds
                .iter()
                .map(|(len, n)| format!("{len} bytes -> {n} offsets"))
                .collect::<Vec<_>>()
                .join(", ")
        }
    );

    // Sampling is a bounded exception, not the arrangement. If most seeds ever became
    // large enough to sample, this file would still print a large case count and would
    // be grading far less than it appears to -- the exact shape it was already caught
    // by once.
    assert!(
        exhaustive_seeds > sampled_seeds.len() * 4,
        "offset sweeps are mostly sampled ({exhaustive_seeds} exhaustive vs {} sampled); \
         the corpus has grown past what this sweep's bound assumes, and the coverage claim \
         in this file's header no longer holds",
        sampled_seeds.len()
    );

    assert!(
        failures.is_empty(),
        "{} of {calls} cases panicked:\n{}",
        failures.len(),
        failures.join("\n")
    );
}
