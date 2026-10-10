namespace Curia.Domain.Primitives.Boundary;

public sealed record PostId
{
    public const int MaxUtf8Bytes = 26;
    public string Value { get; }
    private PostId(string value) => Value = value;
    public static Result<PostId> Parse(string? value, string field = "post_id") =>
        StorableText.Check(value, field, 26, names: true).Bind(v =>
            Ulid.Parse(v).TryGetValue(out var u, out _) && string.Equals(u.ToString(), v, StringComparison.Ordinal)
                ? Result<PostId>.Ok(new PostId(v))
                : Result<PostId>.Fail(new Error("curia/input/not-a-post-id", "The value is not a post id", "field=" + field)));
    public override string ToString() => Value;
}
