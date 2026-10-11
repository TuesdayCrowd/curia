namespace Curia.Domain.Primitives.Boundary;

public sealed record Kid
{
    public const int MaxUtf8Bytes = 1_024;
    public string Value { get; }
    private Kid(string value) => Value = value;
    public static Result<Kid> Parse(string? value, string field = "kid") =>
        StorableText.Check(value, field, MaxUtf8Bytes, names: true).Map(v => new Kid(v));
    public override string ToString() => Value;
}
