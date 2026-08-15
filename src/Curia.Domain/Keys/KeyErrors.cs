using System.Globalization;
using Curia.Domain.Primitives;

namespace Curia.Domain;

/// <summary>
/// RFC 9457 problem-type slugs for the agent key store (Increment 4/Stage B), mirroring
/// <see cref="DomainErrors"/>'s one-factory-per-condition shape.
/// </summary>
public static class KeyErrors
{
    public static Error UnsupportedKeyShape(string kty, string crv) => new(
        "curia/domain/keys/unsupported-key-shape",
        "R4.15/R4.28 recognize only OKP/Ed25519 and EC/P-256; every other kty/crv combination is rejected",
        $"kty={kty} crv={crv}");

    public static Error MissingCoordinate(string coordinateName) => new(
        "curia/domain/keys/missing-coordinate",
        $"An EC/P-256 key requires both x and y; {coordinateName} was not supplied",
        coordinateName);

    public static Error WrongCoordinateLength(string shape, string coordinateName, int expected, int actual) => new(
        "curia/domain/keys/wrong-coordinate-length",
        $"{shape}'s {coordinateName} must be exactly {expected.ToString(CultureInfo.InvariantCulture)} bytes",
        $"shape={shape} coordinate={coordinateName} expected={expected.ToString(CultureInfo.InvariantCulture)} actual={actual.ToString(CultureInfo.InvariantCulture)}");

    public static Error InvalidWindow(ServerTimestamp validFrom, ServerTimestamp validUntil) => new(
        "curia/domain/keys/invalid-window",
        "A key validity window's end must be strictly after its start",
        $"valid_from={validFrom} valid_until={validUntil}");

    public static Error EmptyKeySet(AggregateId agentId) => new(
        "curia/domain/keys/empty-key-set",
        "An agent key set must contain at least one key",
        $"agent={agentId.Value}");

    public static Error DuplicateKid(AggregateId agentId, KeyId kid) => new(
        "curia/domain/keys/duplicate-kid",
        "Every key in an agent's key history must have a unique kid",
        $"agent={agentId.Value} kid={kid.Value}");

    public static Error KeyNotFound(AggregateId agentId, KeyId kid) => new(
        "curia/domain/keys/key-not-found",
        "No key with this kid exists in the agent's key history",
        $"agent={agentId.Value} kid={kid.Value}");

    /// <summary>
    /// Errata A12/R6.31: the rejection that matters. The detail names the exact instant that
    /// decided the outcome -- <c>server_ts</c>, never <c>created_at</c> or any other clock --
    /// alongside the key's recorded interval, so a caller (or a test) can see precisely why "valid
    /// when the author signed" did not mean "valid when the Forum received it."
    /// </summary>
    public static Error KeyNotValidAt(AggregateId agentId, AgentKey key, ServerTimestamp evaluatedAt)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new(
            "curia/domain/keys/not-valid-at-server-ts",
            "The key was not valid for this agent at server_ts (R6.31); validity is never evaluated at created_at or submission time",
            $"agent={agentId.Value} kid={key.Kid.Value} server_ts={evaluatedAt} " +
            $"valid_from={key.Validity.ValidFrom} valid_until={(key.Validity.ValidUntil is { } end ? end.ToString() : "(open)")}");
    }

    public static Error AlreadyClosed(AggregateId agentId, KeyId kid) => new(
        "curia/domain/keys/already-closed",
        "This key's validity window is already closed and cannot be revoked a second time",
        $"agent={agentId.Value} kid={kid.Value}");
}
