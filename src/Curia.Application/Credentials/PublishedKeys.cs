using System.Collections.Immutable;
using Curia.Application.Projections;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Acta;
using Curia.Domain.Keys;

namespace Curia.Application.Credentials;

/// <summary>
/// The keys the log publishes (errata G68, R6.62, ruling 34), each class in a list of its own so that a further class is an addition and not a rewrite: the log keys,
/// which sign heads under <see cref="DetachedJws.HeadTyp"/>, and the declaration keys, which sign compromise declarations under <see cref="KeyStatements.CompromiseHeaderTyp"/>.
/// A key of one class is never presented as the other.
///
/// <para>Read from the log's own <c>log.key</c> and <c>log.declaration-key</c> entries through <see cref="ActaLog"/>'s two readers, so that the Acta and the verifier cannot read a
/// key entry differently, and an entry that does not read is left out (ruling 20: the verifier does not depend on the Acta's fold, which fails closed on it).</para>
/// </summary>
public sealed class PublishedKeys
{
    /// <summary>The classes, each with the header <c>typ</c> its signatures carry.</summary>
    public static readonly ImmutableArray<(string Class, string HeaderTyp)> Classes =
    [
        (LogEntries.KeyType, DetachedJws.HeadTyp),
        (LogEntries.DeclarationKeyType, KeyStatements.CompromiseHeaderTyp),
    ];

    private PublishedKeys(ImmutableArray<LogKey> logKeys, ImmutableArray<DeclarationKey> declarationKeys)
    {
        LogKeys = logKeys;
        DeclarationKeys = declarationKeys;
    }

    /// <summary>The key set a Forum serves as <c>GET /v1/log/jwks</c>: its <c>keys</c> are the log keys and its <c>declaration_keys</c> the declaration keys, each entry read as a key and one that does not read left out.</summary>
    public PublishedKeys(JsonValue jwks)
    {
        ArgumentNullException.ThrowIfNull(jwks);

        LogKeys = jwks is JsonValue.Object document ? Read(document, "keys", (index, kid, jwk, from) => new LogKey(index, kid, jwk, from)) : [];
        DeclarationKeys = jwks is JsonValue.Object declared ? Read(declared, "declaration_keys", (index, kid, jwk, from) => new DeclarationKey(index, kid, jwk, from)) : [];
    }

    /// <summary>The keys of the first class, in log order.</summary>
    public ImmutableArray<LogKey> LogKeys { get; }

    /// <summary>The keys of the second class, in log order.</summary>
    public ImmutableArray<DeclarationKey> DeclarationKeys { get; }

    /// <summary>
    /// The published keys in <paramref name="log"/>, in order, by log index (R6.64): an entry is a key only when its payload is an object whose <c>kid</c> is a string and whose <c>jwk</c> is a key
    /// in R6.62's closed form carrying that same <c>kid</c>; an entry that is not a key holds no position, and a <c>kid</c> that an earlier entry <i>that is a key</i> of the same class lists is not a
    /// key again (the earliest listing), so a malformed entry naming a genuine key's <c>kid</c> cannot displace it.
    /// </summary>
    public static PublishedKeys OfLog(IReadOnlyList<AppendedEvent> log)
    {
        ArgumentNullException.ThrowIfNull(log);

        var logKeys = ImmutableArray.CreateBuilder<LogKey>();
        var declarationKeys = ImmutableArray.CreateBuilder<DeclarationKey>();
        for (var i = 0; i < log.Count; i++)
        {
            var appended = log[i];
            switch (appended.Event.Type.Value)
            {
                case LogEntries.KeyType:
                    if (ReadEntry(appended.Event.Payload) is { } key && !logKeys.Any(k => string.Equals(k.Kid, key.Kid, StringComparison.Ordinal)))
                        logKeys.Add(new LogKey(i, key.Kid, key.Jwk, key.ValidFrom));
                    break;

                case LogEntries.DeclarationKeyType:
                    if (ReadEntry(appended.Event.Payload) is { } declared && !declarationKeys.Any(k => string.Equals(k.Kid, declared.Kid, StringComparison.Ordinal)))
                        declarationKeys.Add(new DeclarationKey(i, declared.Kid, declared.Jwk, declared.ValidFrom));
                    break;

                default:
                    break;
            }
        }

        return new PublishedKeys(logKeys.ToImmutable(), declarationKeys.ToImmutable());
    }

    /// <summary>The key a <c>log.key</c> or <c>log.declaration-key</c> payload states, or null when the entry is not a key (R6.64).</summary>
    private static (string Kid, JsonValue.Object Jwk, string ValidFrom)? ReadEntry(JsonValue payload)
    {
        if (payload is not JsonValue.Object o
            || Member(o, LogEntries.KidMember) is not JsonValue.String kid
            || Member(o, LogEntries.JwkMember) is not JsonValue.Object jwk
            || ClosedJwk.Read(jwk, kid.Value) is null)
            return null;

        return (kid.Value, jwk, Member(o, LogEntries.ValidFromMember) is JsonValue.String validFrom ? validFrom.Value : string.Empty);
    }

    private static ImmutableArray<T> Read<T>(JsonValue.Object document, string member, Func<long, string, JsonValue.Object, string, T> make)
    {
        if (Member(document, member) is not JsonValue.Array entries) return [];

        var read = ImmutableArray.CreateBuilder<T>();
        foreach (var entry in entries.Items)
        {
            if (entry is not JsonValue.Object key
                || Member(key, "kid") is not JsonValue.String kid
                || Member(key, "log_index") is not JsonValue.Number index
                || Member(key, "valid_from") is not JsonValue.String validFrom)
                continue;

            // The key as the entry carries it, without the two members the document adds to it.
            var jwk = new JsonValue.Object([.. key.Members.Where(m => m.Key is not "log_index" and not "valid_from")]);
            read.Add(make((long)index.Value, kid.Value, jwk, validFrom.Value));
        }

        return read.ToImmutable();
    }

    private static JsonValue? Member(JsonValue.Object o, string name)
    {
        foreach (var m in o.Members)
            if (string.Equals(m.Key, name, StringComparison.Ordinal))
                return m.Value;
        return null;
    }
}
