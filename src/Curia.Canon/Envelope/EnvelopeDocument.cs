using Curia.Canon.Json;

namespace Curia.Canon.Envelope;

/// <summary>A structurally admitted envelope. Schema conformance per kind is the Domain's job.</summary>
public sealed record EnvelopeDocument(JsonValue.Object Root);

/// <summary>
/// A detached JWS in compact serialization with an empty payload segment.
///
/// Warning: do not use <see cref="Compact"/> as a deduplication, idempotency, or
/// audit-correlation key. The BCL's base64url decoder silently strips embedded ASCII
/// whitespace, so two distinct <see cref="Compact"/> strings (one with a space inserted
/// into a segment) can decode to the same bytes and verify identically -- not forgeable,
/// but it means the string is not a safe exact-match identifier. This is also a
/// documented cross-implementation asymmetry: Rust's <c>base64</c> crate with
/// <c>URL_SAFE_NO_PAD</c> rejects embedded whitespace outright, so a wire JWS this code
/// accepts can be one the Rust verifier refuses -- exactly the kind of divergence the
/// differential harness is built to surface.
/// </summary>
public sealed record JwsSignature(string Compact);

/// <summary>The Appendix C.3 wire object: an envelope and its detached signature.</summary>
public sealed record SubmissionDocument(EnvelopeDocument Envelope, JwsSignature Signature);
