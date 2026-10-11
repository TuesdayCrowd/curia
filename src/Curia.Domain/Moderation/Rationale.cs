using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;

namespace Curia.Domain.Moderation;

public sealed record Rationale
{
    public const int MaxUtf8Bytes = RationaleLimit.MaxUtf8Bytes;
    public string Value { get; }
    private Rationale(string value) => Value = value;
    public static Result<Rationale> Parse(string? value, string field = "rationale")
    {
        var checkedText = StorableText.Check(value, field, MaxUtf8Bytes, names: true);

        // R10.68's order on both paths: absent and blank, then the cap, then what no store holds.
        // Before R11.34 the cap was checked first, and a rationale over it was too long whatever else
        // it held; `RationaleLimit.Over` counts a lone surrogate as U+FFFD, as it did then.
        if (!checkedText.TryGetValue(out _, out var error)
            && error!.Type is StorableText.NulType or StorableText.UnpairedSurrogateType
            && RationaleLimit.Over(value!) is { } bytes)
            return Result<Rationale>.Fail(StorableText.TooLong(field, bytes, MaxUtf8Bytes));

        return checkedText.Map(v => new Rationale(v));
    }
    public override string ToString() => Value;
}
