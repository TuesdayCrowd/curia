using Curia.Domain.Primitives;

namespace Curia.Domain;

/// <summary>
/// The half-open interval <c>[ValidFrom, ValidUntil)</c> during which one key was authoritative
/// for its agent, expressed entirely in <see cref="ServerTimestamp"/> -- both bounds are, like
/// the instant they are compared against, moments on the Forum's own clock (when the enrollment
/// or rotation that introduced the key was recorded; when a revocation closed it), never a value
/// read out of an envelope. <see cref="ValidUntil"/> is <see langword="null"/> while the key is
/// still active; R4.19 requires it be retained, never deleted, once a revocation or expiry sets
/// it, so a closed window is still a fact about the key's history, not a reason to drop the
/// record.
/// </summary>
public readonly record struct KeyValidityWindow
{
    public ServerTimestamp ValidFrom { get; }
    public ServerTimestamp? ValidUntil { get; }

    private KeyValidityWindow(ServerTimestamp validFrom, ServerTimestamp? validUntil)
    {
        ValidFrom = validFrom;
        ValidUntil = validUntil;
    }

    /// <summary>
    /// Fails when <paramref name="validUntil"/> does not leave the window at least one instant
    /// wide -- a key cannot have been valid at no instant at all; that is not a history worth
    /// recording, it is a key that never should have been added.
    /// </summary>
    public static Result<KeyValidityWindow> Create(ServerTimestamp validFrom, ServerTimestamp? validUntil) =>
        validUntil is { } end && end <= validFrom
            ? Result<KeyValidityWindow>.Fail(KeyErrors.InvalidWindow(validFrom, end))
            : Result<KeyValidityWindow>.Ok(new KeyValidityWindow(validFrom, validUntil));

    /// <summary>A freshly issued or rotated-in key: valid from this instant, with no retirement yet on record.</summary>
    public static KeyValidityWindow OpenEndedFrom(ServerTimestamp validFrom) => new(validFrom, null);

    public bool IsOpenEnded => ValidUntil is null;

    /// <summary>Whether <paramref name="at"/> falls inside <c>[ValidFrom, ValidUntil)</c> -- the
    /// single predicate R6.31 asks every key to answer, always against a <see cref="ServerTimestamp"/>.</summary>
    public bool Contains(ServerTimestamp at) => at >= ValidFrom && (ValidUntil is null || at < ValidUntil.Value);
}
