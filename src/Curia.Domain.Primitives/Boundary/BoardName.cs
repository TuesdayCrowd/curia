namespace Curia.Domain.Primitives.Boundary;

public sealed record BoardName
{
    public const int MaxUtf8Bytes = 262_144;
    public string Value { get; }
    private BoardName(string value) => Value = value;
    public static Result<BoardName> Parse(string? value, string field = "board") =>
        StorableText.Check(value, field, MaxUtf8Bytes, names: false).Map(v => new BoardName(v));
    public override string ToString() => Value;
}
