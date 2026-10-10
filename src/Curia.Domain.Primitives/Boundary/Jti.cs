namespace Curia.Domain.Primitives.Boundary;

public sealed record Jti
{
    public const int MaxUtf8Bytes = 256;
    public string Value { get; }
    private Jti(string value) => Value = value;
    public static Result<Jti> Parse(string? value, string field = "jti") =>
        StorableText.Check(value, field, MaxUtf8Bytes, names: true).Map(v => new Jti(v));
    public override string ToString() => Value;
}
