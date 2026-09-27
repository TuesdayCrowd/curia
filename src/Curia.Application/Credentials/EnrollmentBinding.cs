using System.Collections.Immutable;
using Curia.Application.Projections;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain;

namespace Curia.Application.Credentials;

/// <summary>
/// One key the log binds to an identity (R4.34, errata G16): its <c>kid</c>, the public JWK when the
/// binding carries one, when the log recorded it, and the event that did.
/// </summary>
/// <param name="Kid">The bound <c>kid</c>.</param>
/// <param name="Jwk">
/// The key as <see cref="PublicJwk.Of"/> rendered it, from an <c>agent.key-bound</c> entry; or
/// <see langword="null"/> for an identity enrolled before R4.34, whose <c>agent.enrolled</c> names the
/// <c>kid</c> and no key. Such a binding cannot tell two keys under one <c>kid</c> apart, and
/// <see cref="Holds"/> says so by answering on the <c>kid</c> alone.
/// </param>
/// <param name="BoundAt">The instant the log recorded the binding. R4.31 dates a key re-registered after a lost row from here (R6.31).</param>
/// <param name="EventId">The entry that binds it: where a reader finds the binding in the log (R6.54).</param>
public sealed record KeyBinding(string Kid, JsonValue.Object? Jwk, DateTimeOffset BoundAt, string EventId)
{
    /// <summary>
    /// Whether <paramref name="key"/> is the key this binding names: the same <c>kid</c>, and, where
    /// the binding carries the key, the same public key, so material with no public JWK is not it. A
    /// kid-only binding (<see cref="Jwk"/> null) answers on the <c>kid</c> alone, whatever the material.
    /// </summary>
    public bool Holds(PublicKeyMaterial key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (!string.Equals(Kid, key.Kid, StringComparison.Ordinal)) return false;
        if (Jwk is null) return true;

        return PublicJwk.Of(key).TryGetValue(out var rendered, out _) && PublicJwk.SameKey(Jwk, rendered!);
    }
}

/// <summary>
/// What an identifier's enrollment bound, as the log records it (R4.31 rev., R4.34; errata G14, G16):
/// when the identity was enrolled, and every key the log binds to it.
///
/// <para><b>Two kinds of entry bind a key.</b> Since R4.34 an enrollment appends
/// <c>agent.key-bound</c> beside <c>agent.enrolled</c>, in the same append, carrying the key's public
/// JWK; that entry binds the key itself. An identity enrolled before R4.34 has only
/// <c>agent.enrolled</c>, which names the <c>kid</c>; that binds the <c>kid</c> alone, and only while
/// no <c>agent.key-bound</c> names the same <c>kid</c>.</para>
///
/// <para><b>Why the log and not the key store.</b> The store can lose rows, and a store written before
/// errata G14 can hold keys no enrollment bound. The log is append-only under R11.6's grant and
/// signed into heads, so it is the one record of which keys an identity holds that neither failure can
/// change.</para>
/// </summary>
/// <param name="EnrolledAt">The instant the log recorded the enrollment.</param>
/// <param name="Keys">
/// Every binding, in log order. An enrollment produces one; R4.18's rotation will append more, and
/// <see cref="For"/> already answers for each, so a re-announcement of a rotated key is decided as a
/// re-announcement of the first (R4.31 rev.).
/// </param>
public sealed record EnrollmentBinding(DateTimeOffset EnrolledAt, ImmutableArray<KeyBinding> Keys)
{
    /// <summary>The binding the log holds for <paramref name="agentId"/>, or <see langword="null"/> when it holds no enrollment.</summary>
    public static EnrollmentBinding? Find(IReadOnlyList<AppendedEvent> history, string agentId)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        DateTimeOffset? enrolledAt = null;
        KeyBinding? legacy = null;
        var bound = ImmutableArray.CreateBuilder<KeyBinding>();

        foreach (var appended in history)
        {
            if (appended.Event.Payload is not JsonValue.Object payload) continue;
            if (!string.Equals(Text(payload, AgentStandingProjector.AgentIdField), agentId, StringComparison.Ordinal)) continue;

            var type = appended.Event.Type.Value;
            if (string.Equals(type, AgentStandingProjector.EnrolledType, StringComparison.Ordinal) && enrolledAt is null)
            {
                enrolledAt = appended.ServerTimestamp.Value;
                if (Text(payload, AgentStandingProjector.KeyIdField) is { } kid)
                    legacy = new KeyBinding(kid, null, appended.ServerTimestamp.Value, appended.Event.Id.Value);
            }
            else if (string.Equals(type, AgentStandingProjector.KeyBoundType, StringComparison.Ordinal)
                && Text(payload, AgentStandingProjector.KeyIdField) is { } kid
                && Member(payload, AgentStandingProjector.JwkField) is JsonValue.Object jwk)
            {
                bound.Add(new KeyBinding(kid, jwk, appended.ServerTimestamp.Value, appended.Event.Id.Value));
            }
        }

        if (enrolledAt is not { } at) return null;

        // A kid-only binding stands only for a kid no key-binding entry names: once the log carries the
        // key, the kid alone binds nothing.
        if (legacy is not null && !bound.Any(b => string.Equals(b.Kid, legacy.Kid, StringComparison.Ordinal)))
            bound.Insert(0, legacy);

        return new EnrollmentBinding(at, bound.ToImmutable());
    }

    /// <summary>The binding for <paramref name="kid"/>, or <see langword="null"/> when the log binds no key under it to this identity.</summary>
    public KeyBinding? For(string kid) =>
        Keys.FirstOrDefault(k => string.Equals(k.Kid, kid, StringComparison.Ordinal));

    private static string? Text(JsonValue.Object payload, string name) =>
        Member(payload, name) is JsonValue.String text ? text.Value : null;

    private static JsonValue? Member(JsonValue.Object payload, string name)
    {
        foreach (var member in payload.Members)
            if (string.Equals(member.Key, name, StringComparison.Ordinal))
                return member.Value;
        return null;
    }
}
