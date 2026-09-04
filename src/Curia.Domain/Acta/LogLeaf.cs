using System.Collections.Immutable;
using System.Globalization;
using Curia.Canon.Acta;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Domain.Primitives;

namespace Curia.Domain.Acta;

/// <summary>
/// R6.46: what a leaf of the Acta is made of, byte for byte.
///
/// <para><b>This computation is frozen (R15.1).</b> A leaf is one event of the append-only store,
/// rendered as the six-member object below, canonicalized under pure RFC 8785 (R6.8, no Unicode
/// normalization), and hashed as <c>SHA-256(0x00 ‖ leaf_input)</c> per RFC 9162 §2.1.1. Every
/// signed tree head ever published commits to this exact rendering, so it cannot change without a
/// version bump and a migration -- which is the reason it is small, has no options, and is pinned
/// by <c>conformance/acta/</c> in both implementations.</para>
///
/// <para><b>Why an event and not a content item.</b> Figure 7 hashed the envelope and its
/// signature, and had no answer for the entries R6.25 and R6.30 require the log to hold --
/// moderation records and compromise declarations carry no envelope. One encoding for every
/// entry class, distinguished by <c>event_type</c> inside the hashed bytes, is what makes a new
/// class of entry a payload decision rather than a second leaf format (errata G9).</para>
///
/// <para><b>Why pure canonicalization.</b> The same reason the event store renders payloads with
/// it: hashing is not signing, R11.24 already refuses any payload without a Cūria-profile form,
/// and normalizing content on its way into a digest would touch bytes §6.4 fixed at PERSIST.</para>
///
/// <para><b>Why <c>seq</c> is not a member.</b> The leaf's position is proven by the shape of its
/// audit path, not asserted inside it (RFC 9162 §4.7 makes the same choice), and Appendix D's
/// identity column is not gapless -- R6.47 derives the index by counting, so a value that is not
/// the index has no business in the leaf.</para>
/// </summary>
public static class LogLeaf
{
    public const string ActorIdMember = "actor_id";
    public const string AggregateIdMember = "aggregate_id";
    public const string EventIdMember = "event_id";
    public const string EventTypeMember = "event_type";
    public const string PayloadMember = "payload";
    public const string ServerTimestampMember = "server_ts";

    /// <summary>
    /// The entry object: the <c>events</c> row minus <c>seq</c>, with <c>actor_id</c> as JSON
    /// <c>null</c> when the event has no actor, and <c>server_ts</c> rendered by
    /// <see cref="RenderServerTimestamp"/>.
    /// </summary>
    public static JsonValue.Object Entry(AppendedEvent appended)
    {
        ArgumentNullException.ThrowIfNull(appended);

        return new JsonValue.Object(
        [
            new(ActorIdMember, appended.Event.Actor is { } actor ? new JsonValue.String(actor.Value) : JsonValue.Null.Instance),
            new(AggregateIdMember, new JsonValue.String(appended.AggregateId.Value)),
            new(EventIdMember, new JsonValue.String(appended.Event.Id.Value)),
            new(EventTypeMember, new JsonValue.String(appended.Event.Type.Value)),
            new(PayloadMember, appended.Event.Payload),
            new(ServerTimestampMember, new JsonValue.String(RenderServerTimestamp(appended.ServerTimestamp))),
        ]);
    }

    /// <summary>
    /// RFC 3339, UTC, exactly six fractional digits, <c>Z</c>: <c>2026-09-04T16:00:00.000000Z</c>.
    /// Six because the system of record stores microseconds (Appendix D's <c>timestamptz</c>);
    /// a rendering that carried .NET's seventh digit would encode precision the store cannot
    /// hold, and a leaf computed before and after a round trip would differ.
    /// </summary>
    public static string RenderServerTimestamp(ServerTimestamp timestamp) =>
        timestamp.Value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);

    /// <summary>The leaf input: <see cref="Entry"/> under pure RFC 8785.</summary>
    public static Result<CanonicalBytes> Input(AppendedEvent appended) =>
        CanonicalJson.Canonicalize(Entry(appended));

    /// <summary><c>SHA-256(0x00 ‖ input)</c>: the leaf hash the tree is built from.</summary>
    public static Result<ImmutableArray<byte>> Hash(AppendedEvent appended) =>
        Input(appended).Map(input => MerkleTree.LeafHash(input.Span));
}

/// <summary>
/// The Acta's own entries: signed tree heads (R6.49) and the log keys that sign them (R6.50),
/// both appended to the log by the operator tool and never by the Forum, which holds no log key
/// (R11.7).
/// </summary>
public static class LogEntries
{
    /// <summary>A signed tree head. Payload: <c>{ head: { root_hash, timestamp, tree_size }, kid, signature }</c>.</summary>
    public const string HeadType = "log.head";

    /// <summary>A log signing key's publication. Payload: <c>{ kid, jwk, valid_from }</c>.</summary>
    public const string KeyType = "log.key";

    /// <summary>The one aggregate every head lands in, so concurrent signers conflict rather than interleave.</summary>
    public const string HeadsAggregate = "log:heads";

    /// <summary>The one aggregate every key publication lands in.</summary>
    public const string KeysAggregate = "log:keys";

    public const string HeadMember = "head";
    public const string RootHashMember = "root_hash";
    public const string TimestampMember = "timestamp";
    public const string TreeSizeMember = "tree_size";
    public const string KidMember = "kid";
    public const string SignatureMember = "signature";
    public const string JwkMember = "jwk";
    public const string ValidFromMember = "valid_from";

    /// <summary>The digest form the wire uses everywhere else: <c>sha256:</c> and 64 lowercase hex digits.</summary>
    public static string Prefixed(ImmutableArray<byte> hash) => "sha256:" + Convert.ToHexStringLower([.. hash]);

    /// <summary>The inverse of <see cref="Prefixed"/>; <see langword="null"/> for anything that is not that form.</summary>
    public static ImmutableArray<byte>? Unprefixed(string? text)
    {
        if (text is null || !text.StartsWith("sha256:", StringComparison.Ordinal) || text.Length != 7 + 64)
            return null;

        var hex = text.AsSpan(7);
        foreach (var c in hex)
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                return null;

        return [.. Convert.FromHexString(hex)];
    }

    /// <summary>
    /// The object a tree head signature covers: <c>{ root_hash, timestamp, tree_size }</c>, and
    /// nothing else. Unsigned information served beside a head (the current tree size, the
    /// head's own log index) stays outside this object, because a reader's trust anchor is the
    /// signature and everything inside it is what the signer vouched for.
    /// </summary>
    public static JsonValue.Object HeadDocument(ImmutableArray<byte> root, string timestamp, long treeSize) => new(
    [
        new(RootHashMember, new JsonValue.String(Prefixed(root))),
        new(TimestampMember, new JsonValue.String(timestamp)),
        new(TreeSizeMember, new JsonValue.Number(treeSize)),
    ]);

    /// <summary>The signing input of a head: <see cref="HeadDocument"/> under pure RFC 8785.</summary>
    public static Result<CanonicalBytes> HeadCanonical(JsonValue.Object head) => CanonicalJson.Canonicalize(head);
}
