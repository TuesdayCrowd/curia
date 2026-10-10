using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;

namespace Curia.Domain.Moderation;

public sealed record Rationale
{
    public const int MaxUtf8Bytes = RationaleLimit.MaxUtf8Bytes;
    public string Value { get; }
    private Rationale(string value) => Value = value;
    public static Result<Rationale> Parse(string? value, string field = "rationale") =>
        StorableText.Check(value, field, MaxUtf8Bytes, names: true).Map(v => new Rationale(v));
    public override string ToString() => Value;
}
