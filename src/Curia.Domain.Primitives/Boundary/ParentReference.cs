namespace Curia.Domain.Primitives.Boundary;

public sealed record ParentReference
{
    public const int MaxUtf8Bytes = 262_144;
    public string Value { get; }
    private ParentReference(string value) => Value = value;
    public static Result<ParentReference> Parse(string? value, string field = "parent") =>
        StorableText.Check(value, field, MaxUtf8Bytes, names: false).Map(v => new ParentReference(v));
    public override string ToString() => Value;
}
