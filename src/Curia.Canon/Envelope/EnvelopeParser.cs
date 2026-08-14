using Curia.Canon.Json;
using Curia.Domain.Primitives;

namespace Curia.Canon.Envelope;

/// <summary>
/// ADMIT phase ① for the submission wire format (§6.4, R6.15). R6.33 (rev. 2)'s numeric
/// bound is no longer checked here: it applies to every number ADMIT parses, in any
/// document, at any depth (errata E4) -- not only fields inside "envelope" -- so it is
/// enforced once, generically, inside <see cref="JsonReader.Parse"/> itself. The call below
/// already rejects a submission carrying an out-of-bound number anywhere in the wire
/// object, envelope-shaped or not, before this method's own envelope/signature-presence
/// checks ever run.
/// </summary>
public static class EnvelopeParser
{
    public static Result<SubmissionDocument> Parse(ReadOnlySpan<byte> utf8, AdmitLimits limits)
    {
        var parsed = JsonReader.Parse(utf8, limits);
        if (!parsed.IsOk)
            return parsed.ToFailure<SubmissionDocument>();

        var root = parsed.Match(v => v, _ => JsonValue.Null.Instance);
        if (root is not JsonValue.Object wire)
            return Result<SubmissionDocument>.Fail(CanonErrors.Malformed("submission must be a JSON object"));

        var envelope = wire.Members.FirstOrDefault(m => m.Key == "envelope").Value;
        if (envelope is not JsonValue.Object envelopeObject)
            return Result<SubmissionDocument>.Fail(CanonErrors.MissingEnvelope());

        var signature = wire.Members.FirstOrDefault(m => m.Key == "signature").Value;
        if (signature is not JsonValue.String signatureString)
            return Result<SubmissionDocument>.Fail(CanonErrors.MissingSignature());

        return Result<SubmissionDocument>.Ok(
            new SubmissionDocument(new EnvelopeDocument(envelopeObject), new JwsSignature(signatureString.Value)));
    }
}
