using Curia.Domain.Primitives;

namespace Curia.Domain;

/// <summary>
/// The <c>kid</c> a JWS protected header names (<see cref="Curia.Canon.Jws.JwsProtectedHeader.Kid"/>)
/// and Appendix E's key history keys off of -- opaque here for the same reason
/// <see cref="EventId"/>/<see cref="ActorId"/> are: R4.16 (revised) gives the Registrar sole
/// authority over the key store and its wire format, so this layer only needs "a stable,
/// non-empty label," never an opinion on how the Registrar mints one.
/// </summary>
public readonly record struct KeyId
{
    public string Value { get; }

    private KeyId(string value) => Value = value;

    public static Result<KeyId> Create(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? Result<KeyId>.Fail(DomainErrors.EmptyIdentifier(nameof(KeyId)))
            : Result<KeyId>.Ok(new KeyId(value));

    public override string ToString() => Value;
}
