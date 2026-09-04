//! The conformance harness: runs every family under `conformance/` and
//! asserts against it.
//!
//! Task 1 status: every function this harness calls into
//! (`curia_testis::canonicalize`, `canonicalize_with_nfc`, `admit`,
//! `sha256_digest`, `verify_envelope`) is a stub returning
//! `NotImplementedError`, so every vector in every family is expected to
//! fail right now. That is the point: each assertion below is the real
//! comparison a later task turns green — produced canonical bytes against
//! `expected.canonical`, produced digest against `expected.digest`, and a
//! produced predicate slug against `expect-reject` / `expect-verify-failure`
//! — not a placeholder that a later task must first go rewrite.
//!
//! Run with `cargo test -- --nocapture` to see the per-vector table for
//! families that are fully or partially passing (Rust's test harness prints
//! captured stdout automatically for *failing* tests, so at today's
//! all-red baseline `--nocapture` is not strictly required to see it — it
//! becomes necessary once some vectors start turning green, because passing
//! tests have their stdout suppressed by default).

use std::sync::OnceLock;

use base64::Engine;

use curia_testis::conformance::{
    Corpus, DirectoryVector, EnvelopeVector, Expectation, Index, MerkleVector, Profile,
    Rfc8785Vector,
};
use curia_testis::merkle;

fn corpus() -> &'static Corpus {
    static CORPUS: OnceLock<Corpus> = OnceLock::new();
    CORPUS.get_or_init(|| {
        let root = curia_testis::conformance::conformance_dir();
        Corpus::load(&root).unwrap_or_else(|err| {
            panic!(
                "failed to load the conformance corpus from {}: {err}\n\
                 (set CURIA_CONFORMANCE_DIR to point at conformance/ if it \
                 has moved)",
                root.display()
            )
        })
    })
}

/// Accumulates per-vector pass/fail for one family, prints a per-vector line
/// as it goes, and prints the family tally in `finish`. `finish` is the
/// point where the harness actually fails loudly: it asserts every vector in
/// the family passed, with every failing vector's reason in the panic
/// message.
struct FamilyReport {
    family: &'static str,
    total: usize,
    passed: usize,
    failures: Vec<String>,
}

impl FamilyReport {
    fn new(family: &'static str) -> Self {
        Self {
            family,
            total: 0,
            passed: 0,
            failures: Vec::new(),
        }
    }

    fn record(&mut self, case: &str, outcome: Result<(), String>) {
        self.total += 1;
        match outcome {
            Ok(()) => {
                self.passed += 1;
                println!("  [pass] {}/{case}", self.family);
            }
            Err(reason) => {
                println!("  [FAIL] {}/{case}: {reason}", self.family);
                self.failures.push(format!("{case}: {reason}"));
            }
        }
    }

    fn finish(self) {
        println!(
            "family {}: {}/{} passed",
            self.family, self.passed, self.total
        );
        assert!(
            self.failures.is_empty(),
            "family {} has {} of {} vectors failing:\n{}",
            self.family,
            self.failures.len(),
            self.total,
            self.failures.join("\n")
        );
    }
}

// ---------------------------------------------------------------------
// rfc8785/ — vendored file pairs, profile implicitly `rfc8785`, no digest
// file. `conformance/README.md`: "carries the rfc8785 profile implicitly".
// ---------------------------------------------------------------------

fn check_rfc8785(v: &Rfc8785Vector) -> Result<(), String> {
    match curia_testis::canonicalize(&v.input) {
        Ok(actual) if actual == v.expected_output => Ok(()),
        Ok(actual) => Err(format!(
            "canonical bytes do not match output-{}.json ({} bytes produced, {} expected)",
            v.name,
            actual.len(),
            v.expected_output.len()
        )),
        Err(e) => Err(e.to_string()),
    }
}

#[test]
fn rfc8785() {
    let mut report = FamilyReport::new("rfc8785");
    for v in &corpus().rfc8785 {
        let outcome = check_rfc8785(v);
        report.record(&v.name, outcome);
    }
    report.finish();
}

// ---------------------------------------------------------------------
// c4/, ordering/, unicode/, numbers/, admit-reject/, admit-accept/ — the
// common directory shape. c4/ordering/unicode/numbers carry profile
// `canonicalize-with-nfc`; admit-reject carries `admit`; admit-accept
// carries `admit-accept`. Routing is by the vector's declared profile, never
// by the directory it sits in (R6.44).
// ---------------------------------------------------------------------

// Generic over the error type (bounded only by `Display`), rather than the
// concrete `fn(&[u8]) -> Result<Vec<u8>, NotImplementedError>` pointer Task 1
// used: Task 2 gave `curia_testis::canonicalize` its own real error type
// (`curia_testis::json::ParseError`) rather than leaving it coupled to the
// placeholder `NotImplementedError`, so this helper — shared with
// `curia_testis::canonicalize_with_nfc`, which is still `NotImplementedError`
// pending Task 3 — needs to accept either. This is a type-signature
// generalization only: it changes no assertion, no expected value, and no
// routing decision in `check_directory_vector` below.
fn check_canonicalize<E: std::fmt::Display>(
    canonicalize_fn: impl Fn(&[u8]) -> Result<Vec<u8>, E>,
    input: &[u8],
    expected_canonical: &[u8],
    expected_digest: &str,
) -> Result<(), String> {
    let mut problems = Vec::new();

    match canonicalize_fn(input) {
        Ok(actual) if actual == expected_canonical => {}
        Ok(_) => problems.push("canonical bytes do not match expected.canonical".to_string()),
        Err(e) => problems.push(e.to_string()),
    }

    // Digested from the *expected* canonical bytes, not the (possibly wrong
    // or absent) produced ones, so this check pins Task 5's digest
    // implementation independently of whether Tasks 2/3 have landed yet.
    match curia_testis::sha256_digest(expected_canonical) {
        Ok(actual) if actual == expected_digest => {}
        Ok(actual) => problems.push(format!(
            "digest mismatch: got {actual}, want {expected_digest}"
        )),
        Err(e) => problems.push(e.to_string()),
    }

    if problems.is_empty() {
        Ok(())
    } else {
        Err(problems.join("; "))
    }
}

fn check_admit(input: &[u8], expected_slug: &str) -> Result<(), String> {
    match curia_testis::admit(input) {
        Ok(()) => Err(format!(
            "expected rejection `{expected_slug}`, but admit accepted the input"
        )),
        Err(e) if e.predicate() == expected_slug => Ok(()),
        Err(e) => Err(format!(
            "rejected with predicate `{}`, want `{expected_slug}`",
            e.predicate()
        )),
    }
}

/// The `admit-accept` profile (R6.44): the accepting side of a boundary.
///
/// Two assertions, in this order:
///
/// 1. ADMIT must accept `input` **unmodified** (R6.11, and E6's addendum on
///    harnesses that wrap the published bytes before feeding them in). A
///    rejection fails the vector and names the slug ADMIT produced —
///    otherwise a cap set one byte too tight is indistinguishable in the log
///    from a broken harness.
/// 2. The same bytes must canonicalize to `expected.canonical`, whose
///    SHA-256 is `expected.digest`. **This is what stops the profile being
///    vacuous**: acceptance alone asserts almost nothing, since an ADMIT
///    phase that admits everything passes a bare accept vector, and so does
///    one that admits the document and then canonicalizes it wrongly. The
///    comparison is [`check_canonicalize`] unchanged — the same machinery
///    every canonicalizing vector already uses.
fn check_admit_accept(
    input: &[u8],
    expected_canonical: &[u8],
    expected_digest: &str,
) -> Result<(), String> {
    if let Err(e) = curia_testis::admit(input) {
        return Err(format!(
            "expected ADMIT to accept these bytes, but it rejected them with \
             predicate `{}`",
            e.predicate()
        ));
    }
    check_canonicalize(
        curia_testis::canonicalize_with_nfc,
        input,
        expected_canonical,
        expected_digest,
    )
}

/// A canonicalization vector that must *fail*.
///
/// `expect-reject` was originally defined only for the `admit` profile, on the
/// assumption that canonicalization either succeeds or is never reached. The
/// NFC-collision finding disproved that: normalizing two distinct member names
/// can make them equal, and the only correct response is for
/// `CanonicalizeWithNfc` itself to reject — ADMIT cannot see the collision,
/// because the input genuinely has no duplicate. So the corpus format needs to
/// express "this canonicalization must fail with this predicate", and this is
/// the check that reads it.
fn check_canonicalize_rejects<E: std::fmt::Display>(
    canonicalize_fn: impl Fn(&[u8]) -> Result<Vec<u8>, E>,
    input: &[u8],
    expected_slug: &str,
) -> Result<(), String> {
    match canonicalize_fn(input) {
        Ok(bytes) => Err(format!(
            "expected rejection `{expected_slug}`, but canonicalization \
             succeeded and produced {} bytes",
            bytes.len()
        )),
        // The error's Display begins with its predicate slug followed by ": ",
        // which is the shape every slug-bearing error in this crate uses.
        Err(e) => {
            let rendered = e.to_string();
            let predicate = rendered.split(':').next().unwrap_or("").trim();
            if predicate == expected_slug {
                Ok(())
            } else {
                Err(format!(
                    "rejected with predicate `{predicate}`, want `{expected_slug}`"
                ))
            }
        }
    }
}

fn check_directory_vector(v: &DirectoryVector) -> Result<(), String> {
    match (v.profile, &v.expectation) {
        (Profile::Rfc8785, Expectation::Canonicalize { canonical, digest }) => {
            check_canonicalize(curia_testis::canonicalize, &v.input, canonical, digest)
        }
        (Profile::CanonicalizeWithNfc, Expectation::Canonicalize { canonical, digest }) => {
            check_canonicalize(
                curia_testis::canonicalize_with_nfc,
                &v.input,
                canonical,
                digest,
            )
        }
        (Profile::Rfc8785, Expectation::Reject { slug }) => {
            check_canonicalize_rejects(curia_testis::canonicalize, &v.input, slug)
        }
        (Profile::CanonicalizeWithNfc, Expectation::Reject { slug }) => {
            check_canonicalize_rejects(curia_testis::canonicalize_with_nfc, &v.input, slug)
        }
        (Profile::Admit, Expectation::Reject { slug }) => check_admit(&v.input, slug),
        (Profile::AdmitAccept, Expectation::Canonicalize { canonical, digest }) => {
            check_admit_accept(&v.input, canonical, digest)
        }
        // R6.46: the pure profile, then the leaf prefix. The NFC profile is
        // deliberately not an option here -- `nfd-payload-stays-nfd` is the
        // vector that fails if it were.
        (Profile::ActaLeaf, Expectation::Canonicalize { canonical, digest }) => {
            check_canonicalize(curia_testis::canonicalize, &v.input, canonical, digest)?;
            let expected = v
                .expected_leaf
                .as_deref()
                .ok_or_else(|| "acta-leaf vector has no expected.leaf".to_string())?;
            let actual = hex(&merkle::leaf_hash(canonical));
            if actual != expected {
                return Err(format!("leaf hash: expected {expected}, got {actual}"));
            }
            Ok(())
        }
        // Deliberately no `(Profile::AdmitAccept, Expectation::Reject)` arm.
        // "The profile declares acceptance, never the absence of a file"
        // (R6.44): an `admit-accept` vector carrying `expect-reject` is a
        // contradiction, and it must fall through to the catch-all and fail
        // rather than be quietly run as a rejection vector.
        (profile, expectation) => Err(format!(
            "unexpected profile/expectation pairing: {profile:?} / \
             {expectation:?} (either a loader bug or a vector whose profile \
             contradicts its expectation files — never a missing \
             implementation)"
        )),
    }
}

fn directory_family_test(family: &'static str, vectors: &[DirectoryVector]) {
    let mut report = FamilyReport::new(family);
    for v in vectors {
        let outcome = check_directory_vector(v);
        report.record(&v.case, outcome);
    }
    report.finish();
}

#[test]
fn c4() {
    directory_family_test("c4", &corpus().c4);
}

#[test]
fn ordering() {
    directory_family_test("ordering", &corpus().ordering);
}

#[test]
fn unicode() {
    directory_family_test("unicode", &corpus().unicode);
}

#[test]
fn numbers() {
    directory_family_test("numbers", &corpus().numbers);
}

#[test]
fn admit_reject() {
    directory_family_test("admit-reject", &corpus().admit_reject);
}

#[test]
fn admit_accept() {
    directory_family_test("admit-accept", &corpus().admit_accept);
}

#[test]
fn acta() {
    directory_family_test("acta", &corpus().acta);
}

/// R6.44 (addendum): an accepting-side vector names its rejecting-side twin
/// as `"pairs-with": "<family>/<case>"`, and a runner SHALL fail when the
/// named vector is absent from the corpus.
///
/// This is the assertion that makes the pair, rather than either half, the
/// thing that locates a boundary: an accepting-side vector cannot detect a
/// cap that is too generous and a rejecting-side vector cannot detect one
/// that is too strict. Without it, a corpus that shipped only the easy half
/// reports exactly what a complete one reports.
///
/// Checked for every directory family, not just `admit-accept/`, so a
/// `pairs-with` added elsewhere is resolved rather than ignored.
#[test]
fn pairs_with_targets_resolve() {
    let c = corpus();
    let mut problems = Vec::new();

    for family in curia_testis::conformance::LOADED_FAMILIES {
        let Some(vectors) = c.directory_family(family) else {
            // rfc8785/ and envelope/ are not directory-shaped families;
            // neither shape carries `pairs-with` today.
            continue;
        };
        for v in vectors {
            let Some(reference) = v.pairs_with.as_deref() else {
                if v.profile == Profile::AdmitAccept {
                    problems.push(format!(
                        "{family}/{}: profile `admit-accept` but meta.json has no \
                         `pairs-with` naming its rejecting-side twin (R6.44 addendum)",
                        v.case
                    ));
                }
                continue;
            };
            let Some((target_family, target_case)) = reference.split_once('/') else {
                problems.push(format!(
                    "{family}/{}: `pairs-with` is `{reference}`, which is not of the \
                     form <family>/<case>",
                    v.case
                ));
                continue;
            };
            match c.contains_case(target_family, target_case) {
                Some(true) => {}
                Some(false) => problems.push(format!(
                    "{family}/{}: `pairs-with` names `{reference}`, which is not in \
                     the corpus",
                    v.case
                )),
                None => problems.push(format!(
                    "{family}/{}: `pairs-with` names `{reference}`, whose family this \
                     runner does not load, so the twin cannot be resolved",
                    v.case
                )),
            }
        }
    }

    assert!(
        problems.is_empty(),
        "{} unresolved `pairs-with` reference(s):\n{}",
        problems.len(),
        problems.join("\n")
    );
}

// ---------------------------------------------------------------------
// envelope/ — the six-file shape. Every vector pins three things at once:
// canonicalization of the envelope sub-object, its digest, and the outcome
// of curia_testis::verify_envelope over the whole submission.
// ---------------------------------------------------------------------

fn expected_author(envelope_bytes: &[u8]) -> String {
    // A plain serde_json::Value read is fine here: this is test-harness code
    // extracting one scalar field from a trusted, committed fixture to build
    // an independent oracle for the assertion below. It is not the
    // production canonicalization path, and it never feeds bytes back into
    // one.
    let value: serde_json::Value =
        serde_json::from_slice(envelope_bytes).expect("fixture envelope bytes are valid JSON");
    value
        .get("author")
        .and_then(serde_json::Value::as_str)
        .expect("fixture envelope has an author field")
        .to_string()
}

fn decode_protected_header(compact_jws: &str) -> serde_json::Value {
    let protected_b64 = compact_jws
        .split('.')
        .next()
        .expect("compact JWS has at least a protected-header segment");
    let bytes = base64::engine::general_purpose::URL_SAFE_NO_PAD
        .decode(protected_b64)
        .expect("fixture protected header is valid base64url");
    serde_json::from_slice(&bytes).expect("fixture protected header is valid JSON")
}

fn check_envelope_vector(v: &EnvelopeVector) -> Result<(), String> {
    let mut problems = Vec::new();

    match curia_testis::canonicalize_with_nfc(&v.envelope) {
        Ok(actual) if actual == v.expected_canonical => {}
        Ok(_) => problems.push("canonical bytes do not match expected.canonical".to_string()),
        Err(e) => problems.push(format!("canonicalize_with_nfc: {e}")),
    }

    match curia_testis::sha256_digest(&v.expected_canonical) {
        Ok(actual) if actual == v.expected_digest => {}
        Ok(actual) => problems.push(format!(
            "digest mismatch: got {actual}, want {}",
            v.expected_digest
        )),
        Err(e) => problems.push(format!("sha256_digest: {e}")),
    }

    match &v.expect_verify_failure {
        Some(slug) => match curia_testis::verify_envelope(&v.submission, &v.jwks) {
            Ok(_) => problems.push(format!(
                "expected verification to fail with `{slug}`, but it succeeded"
            )),
            Err(e) if e.predicate() == slug => {}
            Err(e) => problems.push(format!(
                "verify_envelope failed with predicate `{}`, want `{slug}`",
                e.predicate()
            )),
        },
        None => match curia_testis::verify_envelope(&v.submission, &v.jwks) {
            Ok(provenance) => {
                let expected_author = expected_author(&v.envelope);
                let header = decode_protected_header(&v.signature);
                let expected_kid = header.get("kid").and_then(serde_json::Value::as_str);
                let expected_alg = header.get("alg").and_then(serde_json::Value::as_str);
                let expected_digest = format!("sha256:{}", v.expected_digest);

                if provenance.author != expected_author {
                    problems.push(format!(
                        "author mismatch: got `{}`, want `{expected_author}`",
                        provenance.author
                    ));
                }
                if Some(provenance.kid.as_str()) != expected_kid {
                    problems.push(format!(
                        "kid mismatch: got `{}`, want `{:?}`",
                        provenance.kid, expected_kid
                    ));
                }
                if Some(provenance.alg.as_str()) != expected_alg {
                    problems.push(format!(
                        "alg mismatch: got `{}`, want `{:?}`",
                        provenance.alg, expected_alg
                    ));
                }
                if provenance.digest != expected_digest {
                    problems.push(format!(
                        "digest mismatch: got `{}`, want `{expected_digest}`",
                        provenance.digest
                    ));
                }
            }
            Err(e) => problems.push(format!("verify_envelope: {e}")),
        },
    }

    if problems.is_empty() {
        Ok(())
    } else {
        Err(problems.join("; "))
    }
}

#[test]
fn envelope() {
    let mut report = FamilyReport::new("envelope");
    for v in &corpus().envelope {
        let outcome = check_envelope_vector(v);
        report.record(&v.case, outcome);
    }
    report.finish();
}

// ---------------------------------------------------------------------
// merkle/ — RFC 9162 §2.1 over the Certificate Transparency reference
// leaves (R6.23). Every vector pins the leaf hashes, the root, and every
// audit path and consistency proof *node for node*, then verifies each with
// the RFC's own procedures. Matching the path and not only its verdict is
// the point: a prover with a differently shaped but self-consistent proof
// would pass its own verifier and interoperate with nobody.
// ---------------------------------------------------------------------

fn hex(bytes: &[u8]) -> String {
    bytes.iter().map(|b| format!("{b:02x}")).collect()
}

fn check_merkle_vector(v: &MerkleVector) -> Result<(), String> {
    let n = v.leaves.len();
    let leaves: Vec<merkle::Hash> = v.leaves.iter().map(|l| merkle::leaf_hash(l)).collect();

    let actual_leaf_hashes: Vec<String> = leaves.iter().map(|h| hex(h)).collect();
    if actual_leaf_hashes != v.leaf_hashes {
        return Err(format!(
            "leaf hashes differ: expected {:?}, got {:?}",
            v.leaf_hashes, actual_leaf_hashes
        ));
    }

    let root = merkle::root(&leaves);
    if hex(&root) != v.root {
        return Err(format!("root: expected {}, got {}", v.root, hex(&root)));
    }

    if v.inclusion.len() != n {
        return Err(format!(
            "a size-{n} vector must carry {n} audit paths, one per leaf; it carries {}",
            v.inclusion.len()
        ));
    }
    for case in &v.inclusion {
        let path = merkle::inclusion_path(&leaves, case.index);
        let spelled: Vec<String> = path.iter().map(|h| hex(h)).collect();
        if spelled != case.path {
            return Err(format!(
                "audit path for leaf {}: expected {:?}, got {:?}",
                case.index, case.path, spelled
            ));
        }
        if !merkle::verify_inclusion(
            &leaves[case.index],
            case.index as u64,
            n as u64,
            &path,
            &root,
        ) {
            return Err(format!(
                "audit path for leaf {} does not verify",
                case.index
            ));
        }
    }

    if v.consistency.len() != n {
        return Err(format!(
            "a size-{n} vector must carry {n} consistency proofs, one per earlier size 1..={n}; \
             it carries {}",
            v.consistency.len()
        ));
    }
    for case in &v.consistency {
        let path = merkle::consistency_path(&leaves, case.from);
        let spelled: Vec<String> = path.iter().map(|h| hex(h)).collect();
        if spelled != case.path {
            return Err(format!(
                "consistency {} -> {n}: expected {:?}, got {:?}",
                case.from, case.path, spelled
            ));
        }
        let first_root = merkle::root(&leaves[..case.from]);
        if !merkle::verify_consistency(case.from as u64, n as u64, &first_root, &root, &path) {
            return Err(format!("consistency {} -> {n} does not verify", case.from));
        }
    }
    Ok(())
}

#[test]
fn merkle() {
    let vectors = &corpus().merkle;
    assert_eq!(
        vectors.len(),
        9,
        "conformance/merkle/ holds one vector per tree size 0..=8"
    );
    let mut report = FamilyReport::new("merkle");
    for v in vectors {
        report.record(&v.case, check_merkle_vector(v));
    }
    report.finish();
}

// ---------------------------------------------------------------------
// Whole-corpus checks, independent of any canonicalization logic: the
// loader itself must find every vector that is on disk, and
// `conformance/index.json` must agree with what is there (R6.45). These are
// not per-family assertions; they exist so a change that silently drops a
// family (an empty Vec from a typo'd directory name, or a family nobody
// taught this runner to load) fails here instead of quietly shrinking a
// count nobody is watching.
// ---------------------------------------------------------------------

/// The three-way agreement: this hand count, the vectors the loader found on
/// disk, and the counts `conformance/index.json` publishes.
///
/// The literals below are counted from the corpus directory, family by
/// family: admit-accept 5, admit-reject 14, c4 10, numbers 9, ordering 3,
/// unicode 6, envelope 8, merkle 9, acta 5 — 69 vector directories — plus
/// the 6 vendored `rfc8785/` file pairs, 75 in all. (Envelope grew from 6 to
/// 8 with errata G8's `vote-minimal` and `verification-contradicted`; merkle
/// and acta arrived with Phase 3 Stage 4 -- one merkle vector per tree size
/// 0–8, and five acta vectors pinning R6.46's leaf input.) (An earlier version of this comment
/// cited "50 vector directories, per CHARTER.md": a count that contradicted
/// the assertion beneath it, and a file that does not exist in this
/// repository. Both are corrected here.)
///
/// The literal is deliberately not derived from the index: the index is the
/// thing being checked. Asserting disk against index alone would pass when
/// both are wrong in the same direction — which is exactly what happens when
/// a family is added to `conformance/` and to `index.json` in one commit
/// while no runner learns to load it.
#[test]
fn corpus_size_matches_charter() {
    let c = corpus();
    assert_eq!(c.rfc8785.len(), 6, "conformance/rfc8785/ file pairs");
    assert_eq!(
        c.c4.len()
            + c.ordering.len()
            + c.unicode.len()
            + c.numbers.len()
            + c.admit_reject.len()
            + c.admit_accept.len()
            + c.envelope.len()
            + c.merkle.len()
            + c.acta.len(),
        69,
        "conformance/ vector directories (c4 + ordering + unicode + numbers \
         + admit-reject + admit-accept + envelope + merkle + acta)"
    );
    assert_eq!(c.total_len(), 75, "every vector in conformance/");

    // `Index::load` already refuses a family entry with no `count`, so
    // `filter_map` here drops only the non-family entries (`red-team/`).
    let index = Index::load_default().expect("conformance/index.json loads");
    let declared: usize = index
        .directories
        .iter()
        .filter(|e| e.family)
        .filter_map(|e| e.count)
        .sum();
    assert_eq!(
        declared, 75,
        "conformance/index.json's declared family counts"
    );
}

/// R6.45: every runner loads `conformance/index.json` and fails when it
/// disagrees with what is on disk.
///
/// The disagreement this exists to catch is not a miscount. It is a family
/// directory that no runner enumerates: it contributes no assurance while
/// looking, in a passing test-run log, exactly like a family that ran.
/// `Corpus::load` hard-codes its family list in source, so nothing else in
/// this crate can notice.
#[test]
fn index_agrees_with_the_corpus_on_disk() {
    let root = curia_testis::conformance::conformance_dir();
    let index = Index::load(&root).unwrap_or_else(|err| {
        panic!(
            "failed to load {}: {err}",
            root.join("index.json").display()
        )
    });
    if let Err(err) = index.check_against_disk(&root) {
        panic!("{err}");
    }
}
