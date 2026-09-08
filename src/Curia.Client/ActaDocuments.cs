using System.Collections.Immutable;
using Curia.Canon.Json;
using Curia.Domain.Primitives;

namespace Curia.Client;

/// <summary>
/// R6.48's audit path as served on every post: the index and size it is against, the leaf hash the
/// Forum computed, the path, the root it climbs to, and whether a signed head exists for exactly
/// that size.
///
/// <para><b><see cref="LeafHash"/> is here to be <i>compared</i>, never to be used.</b> R6.52 is
/// explicit: the leaf is recomputed from R6.46's entry and the served value checked against the
/// recomputation rather than substituted for it. The value sits outside the object whose hash is
/// being proved, so trusting it does not shorten the check -- it skips it, and what remains is the
/// Forum checking its own arithmetic against its own input. <see cref="ActaCheck"/> therefore reads
/// this member only to disagree with it.</para>
/// </summary>
public sealed record InclusionProofDocument(
    long LogIndex,
    long TreeSize,
    string LeafHash,
    ImmutableArray<string> AuditPath,
    string RootHash,
    bool HeadSigned);

/// <summary>
/// R6.49's signed tree head: the signed object exactly as the log holds it, and the unsigned
/// material served beside it.
/// </summary>
/// <param name="Head">
/// The three-member object <c>{root_hash, timestamp, tree_size}</c> the signature covers, kept as a
/// parsed tree rather than flattened. The signature is verified over this re-canonicalized, so
/// flattening it into three fields and rebuilding the object later would put this client's own
/// rendering between the signer and the check.
/// </param>
/// <param name="LogIndex">
/// The head's own leaf index. Unsigned, and outside <paramref name="Head"/> on purpose: a reader's
/// trust anchor is the signature, and everything inside it is what the signer vouched for.
/// </param>
/// <param name="ForumClaimsSignatureValid">
/// The Forum's own re-verification. A convenience, never the basis of trust -- this client verifies
/// the head itself against the keys the log publishes, and a Forum that wanted to lie about its log
/// could set this member freely.
/// </param>
public sealed record SignedHeadDocument(
    JsonValue.Object Head,
    string Kid,
    string Signature,
    long LogIndex,
    bool ForumClaimsSignatureValid,
    long CurrentTreeSize)
{
    /// <summary>The root the head commits to, in the <c>sha256:</c> form the wire uses.</summary>
    public string? RootHash => ClientJson.String(Head, "root_hash");

    /// <summary>
    /// How many leaves the head covers, or <c>-1</c> when the signed object carries no whole-number
    /// <c>tree_size</c>. Negative rather than zero: zero is a real size (the empty tree has a root),
    /// so a malformed head that read as zero would compare equal to a legitimate one.
    /// </summary>
    public long TreeSize => ClientJson.WholeNumber(Head, "tree_size") ?? -1;

    /// <summary>When the operator signed it.</summary>
    public string? Timestamp => ClientJson.String(Head, "timestamp");
}

/// <summary>
/// R6.23's consistency proof between two sizes, and the roots it connects.
/// </summary>
public sealed record ConsistencyProofDocument(
    long FromSize, long ToSize, string FromRoot, string ToRoot, ImmutableArray<string> Path);

/// <summary>
/// R6.46's leaf input, as <c>GET /v1/log/entries/{index}</c> serves it -- the one route in the
/// system exempt from property P22 (R6.51).
///
/// <para><b>This is proof material and never a passage.</b> R6.51 is unambiguous: an entry carries
/// an author's body with no provenance envelope, no delimiters, no marking and no moderation
/// filter, so a tool that returned one would deliver the single representation P22 does not cover
/// straight into a model's context, and would re-serve withheld content while doing it. The type
/// exists so a leaf can be recomputed; nothing in this client renders <see cref="Entry"/>, and
/// R11.29 forbids <c>curia_verify</c> returning it.</para>
/// </summary>
/// <param name="LeafHash">
/// What the Forum says the leaf hashes to. Present for the same reason as
/// <see cref="InclusionProofDocument.LeafHash"/> and used the same way: compared against a
/// recomputation, never in place of one.
/// </param>
public sealed record LogEntryDocument(long LogIndex, string LeafHash, JsonValue.Object Entry);

/// <summary>
/// One key from <c>GET /v1/log/jwks</c> (R6.50): the key material, when it became valid, and the
/// leaf at which the log published it.
/// </summary>
/// <remarks>
/// Every log key ever published is served, forever, and none is filtered out here. A retained head
/// is verifiable only for as long as the key that signed it is still fetchable, so a client that
/// kept only currently-valid keys would lose the ability to check the very heads R6.24's monitors
/// depend on.
/// </remarks>
public sealed record LogJwk(ForumJwk Key, string? ValidFrom, long LogIndex);

/// <summary>
/// Parsers for the Acta's five routes. Separate from <see cref="ForumDocuments"/> because these are
/// the log's shapes rather than the serving path's, and because R6.51 makes one of them a document
/// this client must handle differently from every other thing the Forum sends.
/// </summary>
internal static class ActaDocuments
{
    internal static Result<InclusionProofDocument> ReadInclusionProof(JsonValue value)
    {
        if (value is not JsonValue.Object o)
            return Result<InclusionProofDocument>.Fail(
                ClientErrors.ResponseMalformed("inclusion_proof is not an object"));

        if (ClientJson.WholeNumber(o, "log_index") is not { } logIndex
            || ClientJson.WholeNumber(o, "tree_size") is not { } treeSize
            || ClientJson.String(o, "leaf_hash") is not { } leafHash
            || ClientJson.String(o, "root_hash") is not { } rootHash)
            return Result<InclusionProofDocument>.Fail(ClientErrors.ResponseMalformed(
                "inclusion_proof needs log_index, tree_size, leaf_hash and root_hash"));

        var path = ImmutableArray.CreateBuilder<string>();
        foreach (var item in ClientJson.Array(o, "audit_path"))
        {
            if (item is not JsonValue.String s)
                return Result<InclusionProofDocument>.Fail(
                    ClientErrors.ResponseMalformed("inclusion_proof.audit_path holds a non-string"));

            path.Add(s.Value);
        }

        return Result<InclusionProofDocument>.Ok(new InclusionProofDocument(
            logIndex, treeSize, leafHash, path.ToImmutable(), rootHash, Bool(o, "head_signed")));
    }

    internal static Result<SignedHeadDocument> ReadHead(JsonValue value)
    {
        if (value is not JsonValue.Object o)
            return Result<SignedHeadDocument>.Fail(ClientErrors.ResponseMalformed("head is not an object"));

        if (ClientJson.Object(o, "head") is not { } head
            || ClientJson.String(o, "kid") is not { } kid
            || ClientJson.String(o, "signature") is not { } signature)
            return Result<SignedHeadDocument>.Fail(
                ClientErrors.ResponseMalformed("head document needs head, kid and signature"));

        return Result<SignedHeadDocument>.Ok(new SignedHeadDocument(
            head,
            kid,
            signature,
            ClientJson.WholeNumber(o, "log_index") ?? -1,
            Bool(o, "signature_valid"),
            ClientJson.WholeNumber(o, "current_tree_size") ?? -1));
    }

    internal static Result<ConsistencyProofDocument> ReadConsistency(JsonValue value)
    {
        if (value is not JsonValue.Object o)
            return Result<ConsistencyProofDocument>.Fail(
                ClientErrors.ResponseMalformed("consistency proof is not an object"));

        if (ClientJson.WholeNumber(o, "from_size") is not { } from
            || ClientJson.WholeNumber(o, "to_size") is not { } to
            || ClientJson.String(o, "from_root") is not { } fromRoot
            || ClientJson.String(o, "to_root") is not { } toRoot)
            return Result<ConsistencyProofDocument>.Fail(ClientErrors.ResponseMalformed(
                "consistency proof needs from_size, to_size, from_root and to_root"));

        var path = ImmutableArray.CreateBuilder<string>();
        foreach (var item in ClientJson.Array(o, "path"))
        {
            if (item is not JsonValue.String s)
                return Result<ConsistencyProofDocument>.Fail(
                    ClientErrors.ResponseMalformed("consistency proof path holds a non-string"));

            path.Add(s.Value);
        }

        return Result<ConsistencyProofDocument>.Ok(
            new ConsistencyProofDocument(from, to, fromRoot, toRoot, path.ToImmutable()));
    }

    internal static Result<LogEntryDocument> ReadEntry(JsonValue value)
    {
        if (value is not JsonValue.Object o)
            return Result<LogEntryDocument>.Fail(ClientErrors.ResponseMalformed("log entry is not an object"));

        if (ClientJson.WholeNumber(o, "log_index") is not { } logIndex
            || ClientJson.String(o, "leaf_hash") is not { } leafHash
            || ClientJson.Object(o, "entry") is not { } entry)
            return Result<LogEntryDocument>.Fail(
                ClientErrors.ResponseMalformed("log entry needs log_index, leaf_hash and entry"));

        return Result<LogEntryDocument>.Ok(new LogEntryDocument(logIndex, leafHash, entry));
    }

    /// <summary>
    /// R6.50's key set. The key material is read by <see cref="ForumDocuments.ReadJwks"/> -- the
    /// same reader the agents' JWKS uses, because a second JWK parser is a second place for the
    /// <c>EC</c>/<c>OKP</c> distinction to be got wrong, and errata D4 records what that costs.
    /// </summary>
    internal static Result<ImmutableArray<LogJwk>> ReadLogJwks(JsonValue value)
    {
        if (value is not JsonValue.Object o)
            return Result<ImmutableArray<LogJwk>>.Fail(
                ClientErrors.ResponseMalformed("the log key set is not an object"));

        var published = ImmutableArray.CreateBuilder<LogJwk>();
        foreach (var item in ClientJson.Array(o, "keys"))
        {
            if (item is not JsonValue.Object k) continue;

            if (ForumDocuments.ReadJwk(k) is not { } key)
                return Result<ImmutableArray<LogJwk>>.Fail(ClientErrors.ResponseMalformed("log jwk"));

            published.Add(new LogJwk(key, ClientJson.String(k, "valid_from"), ClientJson.WholeNumber(k, "log_index") ?? -1));
        }

        return Result<ImmutableArray<LogJwk>>.Ok(published.ToImmutable());
    }

    private static bool Bool(JsonValue.Object o, string name) =>
        ClientJson.Member(o, name) is JsonValue.Bool b && b.Value;
}
