using System.Collections.Immutable;
using Curia.Application.Projections;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Acta;
using Curia.Domain.Keys;

namespace Curia.Application.Credentials;

/// <summary>
/// The keys the log publishes (errata G68, R6.62, ruling 34; errata G70, R6.66), each class in a list of its own so that a further class is an addition and not a rewrite: the log keys,
/// which sign heads under <see cref="DetachedJws.HeadTyp"/>, the declaration keys, which sign compromise declarations under <see cref="KeyStatements.CompromiseHeaderTyp"/>, and the operator
/// keys, which sign the operator's out-of-band acts under <c>curia-operator-act+jws</c>. A key of one class is never presented as another.
///
/// <para>Which entries hold a position in which list is decided by one function (<see cref="Decide"/>, Amendment 2 ruling 40), which <see cref="OfLog"/> and <see cref="ActaLog"/> both read, so that
/// the verifier and the Acta cannot disagree about it. The structural reads stay different on purpose: the Acta fails closed on an entry that is not shaped as a key, and this reader leaves
/// it out (ruling 20: the verifier does not depend on the Acta's fold).</para>
/// </summary>
public sealed class PublishedKeys
{
    /// <summary>The classes, each with the header <c>typ</c> its signatures carry.</summary>
    public static readonly ImmutableArray<(string Class, string HeaderTyp)> Classes =
    [
        (LogEntries.KeyType, DetachedJws.HeadTyp),
        (LogEntries.DeclarationKeyType, KeyStatements.CompromiseHeaderTyp),
        (LogEntries.OperatorKeyType, "curia-operator-act+jws"),
    ];

    private PublishedKeys(ImmutableArray<LogKey> logKeys, ImmutableArray<DeclarationKey> declarationKeys, ImmutableArray<OperatorKey> operatorKeys)
    {
        LogKeys = logKeys;
        DeclarationKeys = declarationKeys;
        OperatorKeys = operatorKeys;
    }

    /// <summary>The key set a Forum serves as <c>GET /v1/log/jwks</c>: its <c>keys</c> are the log keys, its <c>declaration_keys</c> the declaration keys and its <c>operator_keys</c> the operator keys (a document without the member lists none), each entry read as a key and one that does not read left out.</summary>
    public PublishedKeys(JsonValue jwks)
    {
        ArgumentNullException.ThrowIfNull(jwks);

        LogKeys = jwks is JsonValue.Object document ? Read(document, "keys", (index, kid, jwk, from) => new LogKey(index, kid, jwk, from)) : [];
        DeclarationKeys = jwks is JsonValue.Object declared ? Read(declared, "declaration_keys", (index, kid, jwk, from) => new DeclarationKey(index, kid, jwk, from)) : [];
        OperatorKeys = jwks is JsonValue.Object operators ? Read(operators, "operator_keys", (index, kid, jwk, from) => new OperatorKey(index, kid, jwk, from)) : [];
    }

    /// <summary>The keys of the first class, in log order.</summary>
    public ImmutableArray<LogKey> LogKeys { get; }

    /// <summary>The keys of the second class, in log order.</summary>
    public ImmutableArray<DeclarationKey> DeclarationKeys { get; }

    /// <summary>
    /// The keys of the third class, in log order: every entry that holds a position, as <see cref="DeclarationKeys"/> does. "The" operator key is the first of them, and deciding that is the
    /// fold's, not this list's (R6.66).
    /// </summary>
    public ImmutableArray<OperatorKey> OperatorKeys { get; }

    /// <summary>
    /// The class the log first published this key under, or null when it published it under none (R6.66: none of the three classes may be published in a class it was not first published in): the class
    /// constant (<see cref="LogEntries.KeyType"/>, <see cref="LogEntries.DeclarationKeyType"/> or <see cref="LogEntries.OperatorKeyType"/>) of the earliest listed key, in any class, that has
    /// <paramref name="kid"/> or <paramref name="publicJwk"/>'s public key. Every publisher asks this before it appends.
    /// </summary>
    public string? FirstPublishedClass(string kid, JsonValue.Object publicJwk)
    {
        ArgumentNullException.ThrowIfNull(kid);
        ArgumentNullException.ThrowIfNull(publicJwk);

        var named = ClosedJwk.Read(publicJwk, kid);
        bool Matches(string listedKid, JsonValue.Object listedJwk) =>
            string.Equals(listedKid, kid, StringComparison.Ordinal)
            || (named is not null && ClosedJwk.Read(listedJwk, listedKid) is { } listed && listed.SamePublicKey(named));

        var candidates = new List<(long Index, string Class)>();
        candidates.AddRange(LogKeys.Where(k => Matches(k.Kid, k.Jwk)).Select(k => (k.LogIndex, LogEntries.KeyType)));
        candidates.AddRange(DeclarationKeys.Where(k => Matches(k.Kid, k.Jwk)).Select(k => (k.LogIndex, LogEntries.DeclarationKeyType)));
        candidates.AddRange(OperatorKeys.Where(k => Matches(k.Kid, k.Jwk)).Select(k => (k.LogIndex, LogEntries.OperatorKeyType)));
        return candidates.Count == 0 ? null : candidates.MinBy(c => c.Index).Class;
    }

    /// <summary>Where an entry that reads as a key stands (Amendment 2 ruling 40).</summary>
    internal enum Standing
    {
        /// <summary>It holds a position in its class's list.</summary>
        Holds,

        /// <summary>(a) An earlier entry of its class that reads as a key lists the same <c>kid</c>.</summary>
        RepeatsAKid,

        /// <summary>(b) An operator entry whose key an earlier log key or declaration key already is, by <c>kid</c> or public key: the key was first published in another class.</summary>
        FirstPublishedElsewhere,

        /// <summary>(c) A log key or declaration key entry whose key an earlier operator key already is, by <c>kid</c> or public key: the operator key's class is fixed.</summary>
        OperatorKeyClass,
    }

    /// <summary>One entry of a key class that reads as a key, with the decision about it.</summary>
    internal sealed record Decided(long Index, string Class, string Kid, JsonValue.Object Jwk, string ValidFrom, Standing Standing);

    /// <summary>
    /// The one function that says which entries hold a position (Amendment 2 ruling 40, R6.66). An entry of <c>log.key</c>, <c>log.declaration-key</c> or <c>log.operator-key</c> <i>reads as a key</i>
    /// when its payload is an object whose <c>kid</c> is a string and whose <c>jwk</c> is a key in R6.62's closed form carrying that same <c>kid</c>; one that does not holds no position and is not
    /// returned. Entries are taken in log order, and one that reads holds a position in its class's list unless (a) an earlier entry of its class that reads as a key lists the same <c>kid</c>; (b) it is
    /// an operator entry and an earlier log key or declaration key that holds a position has the same <c>kid</c> or public key; or (c) it is a log key or declaration key entry and an earlier operator key that
    /// holds a position has the same <c>kid</c> or public key. The relation between a log key and a declaration key is not changed (PR N's vector 29; register item 296).
    /// </summary>
    internal static ImmutableArray<Decided> Decide(IReadOnlyList<AppendedEvent> log)
    {
        ArgumentNullException.ThrowIfNull(log);

        var decided = ImmutableArray.CreateBuilder<Decided>();
        var seen = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            [LogEntries.KeyType] = new(StringComparer.Ordinal),
            [LogEntries.DeclarationKeyType] = new(StringComparer.Ordinal),
            [LogEntries.OperatorKeyType] = new(StringComparer.Ordinal),
        };
        var heldLogOrDeclaration = new List<(string Kid, ClosedJwk Key)>();
        var heldOperator = new List<(string Kid, ClosedJwk Key)>();

        for (var i = 0; i < log.Count; i++)
        {
            var type = log[i].Event.Type.Value;
            if (!seen.TryGetValue(type, out var kids) || ReadEntry(log[i].Event.Payload) is not { } entry)
                continue;

            var key = ClosedJwk.Read(entry.Jwk, entry.Kid)!;
            bool Same((string Kid, ClosedJwk Key) held) => string.Equals(held.Kid, entry.Kid, StringComparison.Ordinal) || held.Key.SamePublicKey(key);

            var standing = !kids.Add(entry.Kid) ? Standing.RepeatsAKid
                : type == LogEntries.OperatorKeyType
                    ? (heldLogOrDeclaration.Any(Same) ? Standing.FirstPublishedElsewhere : Standing.Holds)
                    : (heldOperator.Any(Same) ? Standing.OperatorKeyClass : Standing.Holds);
            if (standing == Standing.Holds)
                (type == LogEntries.OperatorKeyType ? heldOperator : heldLogOrDeclaration).Add((entry.Kid, key));

            decided.Add(new Decided(i, type, entry.Kid, entry.Jwk, entry.ValidFrom, standing));
        }

        return decided.ToImmutable();
    }

    /// <summary>The published keys in <paramref name="log"/>, in order, by log index (R6.64): what <see cref="Decide"/> says holds a position, each in its class's list.</summary>
    public static PublishedKeys OfLog(IReadOnlyList<AppendedEvent> log) => From(Decide(log));

    internal static PublishedKeys From(ImmutableArray<Decided> decided)
    {
        var held = decided.Where(d => d.Standing == Standing.Holds).ToArray();
        return new PublishedKeys(
            [.. held.Where(d => d.Class == LogEntries.KeyType).Select(d => new LogKey(d.Index, d.Kid, d.Jwk, d.ValidFrom))],
            [.. held.Where(d => d.Class == LogEntries.DeclarationKeyType).Select(d => new DeclarationKey(d.Index, d.Kid, d.Jwk, d.ValidFrom))],
            [.. held.Where(d => d.Class == LogEntries.OperatorKeyType).Select(d => new OperatorKey(d.Index, d.Kid, d.Jwk, d.ValidFrom))]);
    }

    /// <summary>The key a key entry's payload states, or null when the entry is not a key (R6.64).</summary>
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
