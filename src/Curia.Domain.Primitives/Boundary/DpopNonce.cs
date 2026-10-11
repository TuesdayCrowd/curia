namespace Curia.Domain.Primitives.Boundary;

public sealed record DpopNonce
{
    public const int MaxUtf8Bytes = 4_096;
    public string Value { get; }
    private DpopNonce(string value) => Value = value;
    public static Result<DpopNonce> Parse(string? value, string field = "nonce") =>
        StorableText.Check(value, field, MaxUtf8Bytes, names: true).Map(v => new DpopNonce(v));
    public override string ToString() => Value;
}
