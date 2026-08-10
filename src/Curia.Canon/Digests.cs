using System.Security.Cryptography;
using Curia.Canon.Canonical;
using Curia.Domain.Primitives;

namespace Curia.Canon;

/// <summary>
/// The envelope digest: SHA-256 over the canonical bytes. This is the value `prev`,
/// `refs`, and deduplication use. It is NOT the transparency-log leaf digest, which
/// is SHA-256(leaf_prefix ‖ canonical_envelope ‖ signature) and belongs to the log.
/// </summary>
public static class Digests
{
    public static EnvelopeDigest Sha256(CanonicalBytes canonical) =>
        new(SHA256.HashData(canonical.Span));
}
