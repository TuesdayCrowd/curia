//! `Digests.Sha256` — SHA-256 over canonical bytes, hex-encoded.
//!
//! This is the simplest of the three Task 5 modules: hashing an arbitrary
//! byte slice with SHA-256 has no rejection condition. Every input,
//! well-formed or not, has a defined digest. The function still returns a
//! [`Result`] rather than a bare `String`, for two reasons: it preserves the
//! call shape `curia_testis::sha256_digest`'s existing callers (notably
//! `tests/vectors.rs`, which this task must not modify — see
//! `check_canonicalize`'s `match curia_testis::sha256_digest(...) { Ok(..)
//! => .., Err(e) => .. }`) already depend on, and it is honest about *why*:
//! the error type is [`std::convert::Infallible`], which documents at the
//! type level — not just in prose — that this function cannot fail.

use sha2::{Digest, Sha256};

/// The lowercase-hex SHA-256 digest of `canonical`, in the 64-character,
/// no-prefix form `conformance/*/expected.digest` files use (errata D9.6:
/// "Encoded as 64 lowercase hex characters with no prefix; stated nowhere,
/// discoverable only by opening a file").
///
/// `Err` is unreachable — [`std::convert::Infallible`] cannot be
/// constructed — but the `Result` shape is kept so this slots into the same
/// call sites as every other check in this crate.
pub fn sha256_digest(canonical: &[u8]) -> Result<String, std::convert::Infallible> {
    let mut hasher = Sha256::new();
    hasher.update(canonical);
    let digest = hasher.finalize();
    Ok(hex_lower(&digest))
}

/// Lowercase hex encoding, written by hand rather than pulled in from a
/// crate: the alphabet is four lines, and every dependency in this crate is
/// pinned deliberately (`CHARTER.md` §2 — "no new dependencies").
fn hex_lower(bytes: &[u8]) -> String {
    const HEX_DIGITS: &[u8; 16] = b"0123456789abcdef";
    let mut s = String::with_capacity(bytes.len() * 2);
    for &b in bytes {
        s.push(HEX_DIGITS[(b >> 4) as usize] as char);
        s.push(HEX_DIGITS[(b & 0x0f) as usize] as char);
    }
    s
}

#[cfg(test)]
mod tests {
    use super::*;

    /// NIST's own SHA-256 test vector for the empty string, independent of
    /// the conformance corpus.
    #[test]
    fn empty_input_digest_is_the_known_constant() {
        let digest = sha256_digest(b"").unwrap();
        assert_eq!(
            digest,
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
        );
    }

    #[test]
    fn output_is_lowercase_hex_no_prefix() {
        let digest = sha256_digest(b"curia").unwrap();
        assert_eq!(digest.len(), 64);
        assert!(digest
            .chars()
            .all(|c| c.is_ascii_hexdigit() && !c.is_ascii_uppercase()));
        assert!(!digest.starts_with("0x"));
        assert!(!digest.starts_with("sha256:"));
    }

    #[test]
    fn digest_matches_every_published_expected_digest() {
        // Cross-checks this module against every expected.digest in the
        // corpus that pairs with an expected.canonical, independent of
        // whatever canonicalize/canonicalize_with_nfc produce — this is the
        // same check tests/vectors.rs performs per-family, gathered here so
        // `cargo test -p curia-testis digest::` alone proves the digest
        // function against the whole corpus.
        let corpus = crate::conformance::Corpus::load_default()
            .expect("conformance corpus must load for this test");

        let mut checked = 0;
        for v in corpus
            .c4
            .iter()
            .chain(corpus.ordering.iter())
            .chain(corpus.unicode.iter())
            .chain(corpus.numbers.iter())
        {
            if let crate::conformance::Expectation::Canonicalize { canonical, digest } =
                &v.expectation
            {
                let actual = sha256_digest(canonical).unwrap();
                assert_eq!(
                    &actual, digest,
                    "digest mismatch for {}/{}",
                    v.family, v.case
                );
                checked += 1;
            }
        }
        for v in &corpus.envelope {
            let actual = sha256_digest(&v.expected_canonical).unwrap();
            assert_eq!(
                actual, v.expected_digest,
                "digest mismatch for envelope/{}",
                v.case
            );
            checked += 1;
        }
        assert!(checked > 0, "expected at least one digest vector to check");
    }
}
