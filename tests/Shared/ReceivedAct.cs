using System.Security.Cryptography;
using System.Text;
using Curia.Canon.Canonical;
using Curia.Canon.Json;

namespace Curia.Tests.Shared;

/// <summary>
/// The digest of an act as the Forum would compute it from what it received (errata G52, R11.72, ULG55 and ULG56): the <c>envelope</c> member of the body a stub
/// was sent, canonicalized by <c>Curia.Canon</c>, hashed with SHA-256, in the <c>sha256:</c> and 64 lowercase hex spelling <c>recheck</c> takes. It never reads
/// the value <c>SubmissionBuilder</c> returned: that is the value under test.
/// </summary>
internal static class ReceivedAct
{
    /// <summary>True where <paramref name="body"/> is a submission (an object with an <c>envelope</c> member) and not another write, such as the token request.</summary>
    internal static bool IsSubmission(string body) => body.StartsWith("{\"envelope\"", StringComparison.Ordinal);

    /// <summary>The prefixed digest of the envelope in <paramref name="wire"/>.</summary>
    internal static string DigestOf(string wire)
    {
        ArgumentNullException.ThrowIfNull(wire);
        if (!JsonReader.ParseUnrestricted(Encoding.UTF8.GetBytes(wire)).TryGetValue(out var parsed, out var parseError))
            throw new InvalidOperationException($"the received body is not JSON: {parseError!.Type}");

        var envelope = parsed is JsonValue.Object o ? o.Members.FirstOrDefault(m => m.Key == "envelope").Value : null;
        if (envelope is null) throw new InvalidOperationException("the received body has no envelope member");

        if (!CanonicalJson.CanonicalizeWithNfc(envelope).TryGetValue(out var canonical, out var canonError))
            throw new InvalidOperationException($"the received envelope does not canonicalize: {canonError!.Type}");

        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(canonical.Span));
    }
}
