namespace Curia.Domain.Primitives.Boundary;

/// <summary>
/// A detached JWS as a caller sent it (R11.34, spec section 4.5): the compact serialization of a
/// lifecycle signature. Parsed by the one rule; whether it is a well-formed signature is the verifier's
/// question, not this type's.
/// </summary>
public sealed record DetachedSignature
{
    public const int MaxUtf8Bytes = 4_096;
    public string Value { get; }
    private DetachedSignature(string value) => Value = value;
    public static Result<DetachedSignature> Parse(string? value, string field = "signature") =>
        StorableText.Check(value, field, MaxUtf8Bytes, names: true).Map(v => new DetachedSignature(v));
    public override string ToString() => Value;
}
