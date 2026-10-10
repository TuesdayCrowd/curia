namespace Curia.Domain.Primitives.Boundary;

public sealed record AgentId
{
    public const int MaxUtf8Bytes = 1_024;
    public string Value { get; }
    private AgentId(string value) => Value = value;
    public static Result<AgentId> Parse(string? value, string field = "agent_id") =>
        StorableText.Check(value, field, MaxUtf8Bytes, names: true).Map(v => new AgentId(v));
    public override string ToString() => Value;
}
