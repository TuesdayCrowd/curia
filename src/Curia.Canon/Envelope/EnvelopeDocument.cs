using Curia.Canon.Json;

namespace Curia.Canon.Envelope;

/// <summary>A structurally admitted envelope. Schema conformance per kind is the Domain's job.</summary>
public sealed record EnvelopeDocument(JsonValue.Object Root);

/// <summary>A detached JWS in compact serialization with an empty payload segment.</summary>
public sealed record JwsSignature(string Compact);

/// <summary>The Appendix C.3 wire object: an envelope and its detached signature.</summary>
public sealed record SubmissionDocument(EnvelopeDocument Envelope, JwsSignature Signature);
