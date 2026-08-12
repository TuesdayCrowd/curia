using Curia.Canon.Json;
using Curia.Domain.Primitives;

namespace Curia.Canon.Envelope;

/// <summary>ADMIT phase ① for the submission wire format (§6.4, R6.15, R6.33).</summary>
public static class EnvelopeParser
{
    private const long SafeMax = 9_007_199_254_740_991;   // 2^53 - 1

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

        var numeric = CheckNumerics(envelopeObject);
        if (numeric is not null)
            return Result<SubmissionDocument>.Fail(numeric);

        return Result<SubmissionDocument>.Ok(
            new SubmissionDocument(new EnvelopeDocument(envelopeObject), new JwsSignature(signatureString.Value)));
    }

    /// <summary>R6.33: envelope numerics are I-JSON-exact integers within the safe range.</summary>
    private static Error? CheckNumerics(JsonValue value) => value switch
    {
        JsonValue.Number n when !double.IsInteger(n.Value) => CanonErrors.NonIntegerNumber(),
        JsonValue.Number n when Math.Abs(n.Value) > SafeMax => CanonErrors.UnsafeInteger(),
        JsonValue.Number => null,
        JsonValue.Object o => o.Members.Select(m => CheckNumerics(m.Value)).FirstOrDefault(e => e is not null),
        JsonValue.Array a => a.Items.Select(CheckNumerics).FirstOrDefault(e => e is not null),
        _ => null,
    };
}
