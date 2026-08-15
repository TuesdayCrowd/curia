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
    Corpus, DirectoryVector, EnvelopeVector, Expectation, Profile, Rfc8785Vector,
};

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
// c4/, ordering/, unicode/, numbers/, admit-reject/ — the common directory
// shape. c4/ordering/unicode/numbers carry profile `canonicalize-with-nfc`;
// admit-reject carries profile `admit`.
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
        (profile, expectation) => Err(format!(
            "loader produced an unexpected profile/expectation pairing: \
             {profile:?} / {expectation:?} (this indicates a loader bug, \
             not a missing implementation)"
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
// A whole-corpus sanity check, independent of any canonicalization logic:
// the loader itself must find every vector the controller counted by hand
// (50 vector directories, per CHARTER.md, plus the 6 vendored rfc8785 file
// pairs). This is not one of Step 3's per-family assertions; it exists so a
// future change to the loader that silently drops a family (e.g. an empty
// Vec from a typo'd directory name) fails here instead of just quietly
// shrinking every family's count.
// ---------------------------------------------------------------------

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
            + c.envelope.len(),
        44,
        "conformance/ vector directories (c4 + ordering + unicode + numbers \
         + admit-reject + envelope)"
    );
}
