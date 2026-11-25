//! R4.46 (errata G79): the proof of possession an enrollment carries, read independently.
//!
//! Written from the errata's sentence and the vectors of `conformance/enroll-proof/` alone, sharing nothing with the
//! Forum's validator. It follows the seven steps in their order and answers the first that refuses, or `Ok`. The
//! stored public key is the request's `public_key` as R4.28 stores it: `EdDSA` the 32 raw bytes, `ES256` a
//! SubjectPublicKeyInfo of a P-256 point; the thumbprint is RFC 7638's over the key as R4.34 renders it.
//!
//! Never panics on adversarial input: every refusal is a value.

use base64::engine::general_purpose::{STANDARD, URL_SAFE_NO_PAD};
use base64::Engine;
use p256::ecdsa::signature::Verifier as _;
use serde_json::Value;
use sha2::{Digest, Sha256};

/// The `typ` of the proof's protected header.
pub const PROOF_TYP: &str = "curia-enroll-pop+jwt";

/// The most characters a proof may hold (step 2).
pub const MAX_PROOF_LENGTH: usize = 8192;

/// The permitted distance between the proof's `iat` and the Forum's clock, in seconds (R5.16).
pub const MAX_SKEW: i64 = 30;

/// What the Forum knows when it decides: its route's absolute URL, its clock, the nonces its store reports current
/// and the `jti`s already consumed.
#[derive(Debug, Clone, Default)]
pub struct Context {
    pub audience: String,
    pub now: i64,
    pub current_nonces: Vec<String>,
    pub spent_jtis: Vec<String>,
}

/// The enrollment request's members the proof is about.
#[derive(Debug, Clone, Copy)]
pub struct Request<'a> {
    pub agent_id: &'a str,
    pub alg: &'a str,
    pub kid: &'a str,
    /// Base64 (standard alphabet) of the stored public key.
    pub public_key: &'a str,
    pub proof: Option<&'a str>,
}

/// The verdict: the proof holds, or the first step that refuses, with the type's slug and the member or claim it names.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Verdict {
    Ok,
    Refused {
        type_slug: &'static str,
        detail: Option<String>,
    },
}

fn refused(type_slug: &'static str, detail: Option<&str>) -> Verdict {
    Verdict::Refused {
        type_slug,
        detail: detail.map(str::to_string),
    }
}

/// Decodes one unpadded base64url segment: the alphabet and nothing else.
fn strict_segment(segment: &str) -> Option<Vec<u8>> {
    if segment.is_empty()
        || !segment
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || b == b'-' || b == b'_')
    {
        return None;
    }
    URL_SAFE_NO_PAD.decode(segment).ok()
}

fn text<'a>(object: &'a serde_json::Map<String, Value>, member: &str) -> Option<&'a str> {
    object.get(member).and_then(Value::as_str)
}

/// The stored key's RFC 7638 thumbprint, or `None` when the bytes are not a key of the algorithm.
fn thumbprint(alg: &str, stored: &[u8]) -> Option<String> {
    let members = match alg {
        "EdDSA" => {
            if stored.len() != 32 {
                return None;
            }
            format!(
                r#"{{"crv":"Ed25519","kty":"OKP","x":"{}"}}"#,
                URL_SAFE_NO_PAD.encode(stored)
            )
        }
        "ES256" => {
            let point = p256_point(stored)?;
            format!(
                r#"{{"crv":"P-256","kty":"EC","x":"{}","y":"{}"}}"#,
                URL_SAFE_NO_PAD.encode(&point[1..33]),
                URL_SAFE_NO_PAD.encode(&point[33..65])
            )
        }
        _ => return None,
    };
    Some(URL_SAFE_NO_PAD.encode(Sha256::digest(members.as_bytes())))
}

/// The 65-byte uncompressed point of a P-256 SubjectPublicKeyInfo with nothing after it.
fn p256_point(spki: &[u8]) -> Option<&[u8]> {
    // SEQUENCE { SEQUENCE { id-ecPublicKey, prime256v1 }, BIT STRING { 0, 0x04 || X || Y } }
    const PREFIX: [u8; 26] = [
        0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x02, 0x01, 0x06, 0x08,
        0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00,
    ];
    if spki.len() != 91 || spki[..26] != PREFIX || spki[26] != 0x04 {
        return None;
    }
    Some(&spki[26..])
}

fn signature_holds(alg: &str, stored: &[u8], input: &[u8], signature: &[u8]) -> bool {
    match alg {
        "EdDSA" => {
            let Ok(array) = <[u8; 32]>::try_from(stored) else {
                return false;
            };
            let Ok(key) = ed25519_dalek::VerifyingKey::from_bytes(&array) else {
                return false;
            };
            let Ok(sig) = <[u8; 64]>::try_from(signature) else {
                return false;
            };
            key.verify_strict(input, &ed25519_dalek::Signature::from_bytes(&sig))
                .is_ok()
        }
        "ES256" => {
            let Some(point) = p256_point(stored) else {
                return false;
            };
            let Ok(key) = p256::ecdsa::VerifyingKey::from_sec1_bytes(point) else {
                return false;
            };
            let Ok(sig) = p256::ecdsa::Signature::from_slice(signature) else {
                return false;
            };
            key.verify(input, &sig).is_ok()
        }
        _ => false,
    }
}

/// R4.46's seven steps, in order. A proof that holds is not recorded here: the caller owns the `jti` set.
pub fn verify(request: &Request<'_>, context: &Context) -> Verdict {
    // Step 1.
    let Some(proof) = request.proof.filter(|p| !p.is_empty()) else {
        return refused("curia/enroll/proof-required", Some("member=proof"));
    };

    // Step 2: the cap, three unpadded base64url segments, a header that reads as an object, its typ, alg and kid.
    let malformed = |member: &str| {
        refused(
            "curia/enroll/proof-malformed",
            Some(&format!("member={member}")),
        )
    };
    if proof.chars().count() > MAX_PROOF_LENGTH {
        return malformed("proof");
    }
    let segments: Vec<&str> = proof.split('.').collect();
    if segments.len() != 3 {
        return malformed("proof");
    }
    let Some(decoded) = segments
        .iter()
        .map(|s| strict_segment(s))
        .collect::<Option<Vec<_>>>()
    else {
        return malformed("proof");
    };
    let Ok(Value::Object(header)) = serde_json::from_slice::<Value>(&decoded[0]) else {
        return malformed("header");
    };
    if text(&header, "typ") != Some(PROOF_TYP) {
        return malformed("typ");
    }
    let header_alg = text(&header, "alg").filter(|a| matches!(*a, "EdDSA" | "ES256"));
    let Some(header_alg) = header_alg else {
        return malformed("alg");
    };
    let Some(header_kid) = text(&header, "kid").filter(|k| !k.is_empty()) else {
        return malformed("kid");
    };

    // Step 3.
    let mismatch = |claim: &str| {
        refused(
            "curia/enroll/proof-mismatch",
            Some(&format!("claim={claim}")),
        )
    };
    if header_alg != request.alg {
        return mismatch("alg");
    }
    if header_kid != request.kid {
        return mismatch("kid");
    }

    // Step 4: the verifier is chosen by the request's alg, under the request's key.
    let stored = STANDARD.decode(request.public_key).unwrap_or_default();
    let signing_input = format!("{}.{}", segments[0], segments[1]);
    if !signature_holds(request.alg, &stored, signing_input.as_bytes(), &decoded[2]) {
        return refused("curia/enroll/proof-not-possessed", None);
    }

    // Step 5: the payload, now that it is signed.
    let Ok(Value::Object(claims)) = serde_json::from_slice::<Value>(&decoded[1]) else {
        return malformed("payload");
    };
    let agent_id = text(&claims, "agent_id");
    let jkt = text(&claims, "jkt");
    let aud = text(&claims, "aud");
    let iat = claims.get("iat").and_then(Value::as_i64);
    let jti = text(&claims, "jti").filter(|j| !j.is_empty());
    for (member, present) in [
        ("agent_id", agent_id.is_some()),
        ("jkt", jkt.is_some()),
        ("aud", aud.is_some()),
        ("iat", iat.is_some()),
        ("jti", jti.is_some()),
    ] {
        if !present {
            return malformed(member);
        }
    }
    let (agent_id, jkt, aud, iat, jti) = (
        agent_id.unwrap_or_default(),
        jkt.unwrap_or_default(),
        aud.unwrap_or_default(),
        iat.unwrap_or_default(),
        jti.unwrap_or_default(),
    );
    if agent_id != request.agent_id {
        return mismatch("agent_id");
    }
    match thumbprint(request.alg, &stored) {
        Some(expected) if expected == jkt => {}
        Some(_) => return mismatch("jkt"),
        None => return malformed("public_key"),
    }
    if aud != context.audience {
        return mismatch("aud");
    }
    if (iat - context.now).abs() > MAX_SKEW {
        return refused("curia/enroll/proof-window", Some("claim=iat"));
    }

    // Step 6: the nonce, before the jti is consumed.
    let current =
        text(&claims, "nonce").is_some_and(|n| context.current_nonces.iter().any(|c| c == n));
    if !current {
        return refused("curia/enroll/proof-nonce-stale", Some("claim=nonce"));
    }

    // Step 7.
    if context.spent_jtis.iter().any(|s| s == jti) {
        return refused("curia/enroll/proof-replayed", Some("claim=jti"));
    }

    Verdict::Ok
}
