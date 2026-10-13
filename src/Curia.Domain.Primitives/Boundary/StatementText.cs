namespace Curia.Domain.Primitives.Boundary;

/// <summary>
/// A key statement as a caller sent it (R11.34, spec section 4.5): the exact text a lifecycle signature
/// covers. Parsed by the one rule, with a cap well above any statement's size; whether it is the
/// canonical form of a statement is the statement reader's question, not this type's.
/// </summary>
public sealed record StatementText
{
    public const int MaxUtf8Bytes = 8_192;
    public string Value { get; }
    private StatementText(string value) => Value = value;
    public static Result<StatementText> Parse(string? value, string field = "statement") =>
        StorableText.Check(value, field, MaxUtf8Bytes, names: true).Map(v => new StatementText(v));
    public override string ToString() => Value;
}
