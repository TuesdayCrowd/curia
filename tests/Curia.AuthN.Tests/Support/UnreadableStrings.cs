using System.Text;
using Curia.AuthN.Ports;
using Curia.Canon.Jws;
using Curia.Domain.Primitives;

namespace Curia.AuthN.Tests.Support;

/// <summary>
/// R11.33's strings a store reads (the stage's final gate, second round): the values of a signed
/// claim or header member that no store can be asked about, named so a theory row reads as what it
/// sends. <see cref="Absent"/> removes the member; every other name is the value written in its place.
/// </summary>
internal static class UnreadableStrings
{
    public const string Absent = "absent";

    /// <summary>The value each row name stands for. <see cref="Absent"/> has none: the member is removed.</summary>
    public static object Value(string name) => name switch
    {
        "empty" => "",
        "spaces" => "   ",
        "space" => " ",
        "newline" => "\n",
        "tab" => "\t",
        "number" => 1,
        "object" => new Dictionary<string, object> { ["a"] = 1 },
        "nul-inside" => "a\0b",
        "nul" => "\0",
        "ascii-256" => new string('a', 256),
        "ascii-257" => new string('a', 257),
        "ascii-4097" => new string('a', 4097),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "no such row"),
    };

    /// <summary>The payload with <paramref name="claim"/> set to the row's value, or removed for <see cref="Absent"/>.</summary>
    public static Dictionary<string, object?> WithRow(this Dictionary<string, object?> payload, string claim, string name) =>
        name == Absent ? payload.WithoutClaim(claim) : payload.WithClaim(claim, Value(name));

    /// <summary>The header with <paramref name="member"/> set to the row's value, or removed for <see cref="Absent"/>.</summary>
    public static Dictionary<string, object> WithHeaderRow(this Dictionary<string, object> header, string member, string name)
    {
        if (name != Absent) return header.With(member, Value(name));
        var copy = new Dictionary<string, object>(header, StringComparer.Ordinal);
        copy.Remove(member);
        return copy;
    }
}

/// <summary>
/// A replay cache that throws, as <c>PostgresReplayCache</c> and Postgres do, for a <c>jti</c> no
/// store can hold: empty or white space (<c>ArgumentException</c>), holding U+0000 (22021), or past
/// what an index row stores (54000; here, past 256 UTF-8 bytes). Reaching it with one is the defect.
/// </summary>
internal sealed class RefusingReplayCache : IReplayCache
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    public Task<Result<bool>> TryInsertAsync(string jti, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jti) || jti.Contains('\0', StringComparison.Ordinal) || Encoding.UTF8.GetByteCount(jti) > 256)
            throw new ArgumentException("a jti no store can hold reached the replay cache", nameof(jti));
        return Task.FromResult(Result<bool>.Ok(_seen.Add(jti)));
    }
}

/// <summary>An agent key resolver that records every question it is asked, then answers as the in-memory one does.</summary>
internal sealed class RecordingAgentKeyResolver(IAgentKeyResolver inner) : IAgentKeyResolver
{
    public List<string> Asked { get; } = [];

    public Task<Result<PublicKeyMaterial>> ResolveAsync(string agentId, string kid, ServerTimestamp at, CancellationToken cancellationToken = default)
    {
        Asked.Add(kid);
        return inner.ResolveAsync(agentId, kid, at, cancellationToken);
    }
}

/// <summary>A nonce store that must not be asked: a nonce the Forum could not have issued is stale before any store hears of it.</summary>
internal sealed class UncallableNonceStore : IDpopNonceStore
{
    public int Calls { get; private set; }

    public Task<Result<DpopNonce>> IssueAsync(CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("the nonce store was asked to issue");

    public Task<Result<bool>> IsCurrentAsync(string nonce, CancellationToken cancellationToken = default)
    {
        Calls++;
        throw new InvalidOperationException("the nonce store was asked about a nonce the Forum could not have issued");
    }
}
