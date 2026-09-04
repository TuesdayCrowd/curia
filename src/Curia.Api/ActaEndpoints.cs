using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.AuthN.Dpop;
using Curia.Canon.Canonical;
using Curia.Canon.Envelope;
using Curia.Canon.Jws;
using Curia.Domain.Acta;
using Curia.Domain.Primitives;
using Microsoft.AspNetCore.Mvc;
using CanonJson = Curia.Canon.Json;

namespace Curia.Api;

/// <summary>R6.48: an audit path with everything a verifier needs to check it, and whether a signed head covers the size it is against.</summary>
public sealed record InclusionProofResponse(
    [property: JsonPropertyName("log_index")] long LogIndex,
    [property: JsonPropertyName("tree_size")] long TreeSize,
    [property: JsonPropertyName("leaf_hash")] string LeafHash,
    [property: JsonPropertyName("audit_path")] ImmutableArray<string> AuditPath,
    [property: JsonPropertyName("root_hash")] string RootHash,

    /// <summary>
    /// Whether a signed tree head exists for exactly <see cref="TreeSize"/>. A proof against an
    /// unsigned size is worth the consistency proof that later ties that size to a signed head,
    /// and a field that looks like a proof and is signed by nobody must say so.
    /// </summary>
    [property: JsonPropertyName("head_signed")] bool HeadSigned);

/// <summary>R6.23: a consistency proof between two sizes and the roots it connects.</summary>
public sealed record ConsistencyProofResponse(
    [property: JsonPropertyName("from_size")] long FromSize,
    [property: JsonPropertyName("to_size")] long ToSize,
    [property: JsonPropertyName("from_root")] string FromRoot,
    [property: JsonPropertyName("to_root")] string ToRoot,
    [property: JsonPropertyName("path")] ImmutableArray<string> Path);

/// <summary>
/// R6.49: the latest signed tree head. <see cref="Head"/> is the signed object exactly as the
/// log holds it; everything beside it is unsigned and outside it on purpose.
/// </summary>
public sealed record LogHeadResponse(
    [property: JsonPropertyName("head")] JsonNode Head,
    [property: JsonPropertyName("kid")] string Kid,
    [property: JsonPropertyName("signature")] string Signature,
    [property: JsonPropertyName("log_index")] long LogIndex,

    /// <summary>The Forum's own re-verification against the keys the log publishes -- a convenience, never the basis of trust (R6.20's spirit).</summary>
    [property: JsonPropertyName("signature_valid")] bool SignatureValid,

    /// <summary>How far the log has grown past the head, so a stale head is visible rather than absent.</summary>
    [property: JsonPropertyName("current_tree_size")] long CurrentTreeSize);

/// <summary>R6.46: one log entry, exactly the object a verifier canonicalizes and hashes.</summary>
public sealed record LogEntryResponse(
    [property: JsonPropertyName("log_index")] long LogIndex,
    [property: JsonPropertyName("leaf_hash")] string LeafHash,
    [property: JsonPropertyName("entry")] JsonNode Entry);

/// <summary>
/// The Acta's routes (Appendix E, R6.23, R6.24, R6.50).
///
/// <para><b>Anonymous, by the same argument as <c>/v1/jwks</c> and the Reader Contract.</b> R6.19's
/// verifier "can confirm authorship without executing Forum-supplied code and without trusting
/// Forum-supplied results"; a head or a proof that a reader must authenticate to fetch is a check
/// most readers will not make, and a monitor (R6.24) is by definition not an enrolled agent.
/// Nothing here is Table 10 content: the log carries only what has already been served.</para>
///
/// <para><b>What the Forum cannot do here.</b> It holds no log key (R11.7). Heads reach it as
/// <c>log.head</c> entries appended by <c>curia-operator sign-head</c>; it folds them, re-verifies
/// them against the <c>log.key</c> entries the same log carries, and serves the result. A Forum
/// that wanted to lie about its log could serve a wrong root, but it could not sign one.</para>
/// </summary>
public static class ActaEndpoints
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/v1/log/head", GetHeadAsync);
        app.MapGet("/v1/log/proof/{index:long}", GetProofAsync);
        app.MapGet("/v1/log/consistency", GetConsistencyAsync);
        app.MapGet("/v1/log/entries/{index:long}", GetEntryAsync);
        app.MapGet("/v1/log/jwks", GetLogJwksAsync);
    }

    private static async Task<IResult> GetHeadAsync(
        IEventReader events, IReadOnlyDictionary<string, IContentVerifier> verifiers, CancellationToken cancellationToken)
    {
        var (acta, failure) = await FoldAsync(events, cancellationToken).ConfigureAwait(false);
        if (failure is not null) return failure;

        if (acta!.LatestHead is not { } head)
        {
            var e = ActaErrors.NoHead(acta.TreeSize);
            return Results.NotFound(new Problem(e.Type, e.Title, e.Detail));
        }

        return Results.Ok(new LogHeadResponse(
            ToObject(head.Document),
            head.Kid,
            head.Signature,
            head.LogIndex,
            SignatureValid(acta, head, verifiers),
            acta.TreeSize));
    }

    private static async Task<IResult> GetProofAsync(
        long index, [FromQuery(Name = "tree_size")] long? treeSize, IEventReader events, CancellationToken cancellationToken)
    {
        var (acta, failure) = await FoldAsync(events, cancellationToken).ConfigureAwait(false);
        if (failure is not null) return failure;

        var proof = ProofFor(acta!, index, treeSize);
        if (proof is null)
        {
            var e = ActaErrors.NotALeaf(index, treeSize ?? acta!.TreeSize);
            return Results.NotFound(new Problem(e.Type, e.Title, e.Detail));
        }

        return Results.Ok(proof);
    }

    private static async Task<IResult> GetConsistencyAsync(
        [FromQuery] long from, [FromQuery] long? to, IEventReader events, CancellationToken cancellationToken)
    {
        var (acta, failure) = await FoldAsync(events, cancellationToken).ConfigureAwait(false);
        if (failure is not null) return failure;

        var toSize = to ?? acta!.TreeSize;
        var proof = acta!.Consistency(from, toSize);
        if (proof is null)
        {
            var e = ActaErrors.BadSizes(from, toSize, acta.TreeSize);
            return Results.BadRequest(new Problem(e.Type, e.Title, e.Detail));
        }

        return Results.Ok(new ConsistencyProofResponse(
            proof.FromSize,
            proof.ToSize,
            LogEntries.Prefixed(proof.FromRoot),
            LogEntries.Prefixed(proof.ToRoot),
            [.. proof.Path.Select(LogEntries.Prefixed)]));
    }

    private static async Task<IResult> GetEntryAsync(long index, IEventReader events, CancellationToken cancellationToken)
    {
        var (acta, failure) = await FoldAsync(events, cancellationToken).ConfigureAwait(false);
        if (failure is not null) return failure;

        if (index < 0 || index >= acta!.TreeSize)
        {
            var e = ActaErrors.NotALeaf(index, acta!.TreeSize);
            return Results.NotFound(new Problem(e.Type, e.Title, e.Detail));
        }

        var i = checked((int)index);
        return Results.Ok(new LogEntryResponse(
            index,
            LogEntries.Prefixed(acta.Leaves[i]),
            ToObject(LogLeaf.Entry(acta.Events[i]))));
    }

    /// <summary>
    /// R6.50 / R12.16: every log key ever published, with when it became valid. Never only the
    /// current one -- a retained head is verifiable forever or the monitors R6.24 relies on have
    /// nothing to hold. Separate from the issuer's and the agents' JWKS on purpose: one document
    /// holding both invites one rotation procedure (R11.7).
    /// </summary>
    private static async Task<IResult> GetLogJwksAsync(IEventReader events, CancellationToken cancellationToken)
    {
        var (acta, failure) = await FoldAsync(events, cancellationToken).ConfigureAwait(false);
        if (failure is not null) return failure;

        var keys = new JsonArray();
        foreach (var key in acta!.Keys)
        {
            var node = ToObject(key.Jwk);
            node["valid_from"] = key.ValidFrom;
            node["log_index"] = key.LogIndex;
            keys.Add(node);
        }

        return Results.Ok(new JsonObject { ["keys"] = keys });
    }

    /// <summary>
    /// R6.18's per-item proof: against the latest signed head when it covers the leaf, otherwise
    /// against the whole log as it stands -- and the response says which (R6.48). A caller may
    /// name a size instead, to reproduce a proof against a head it retained.
    /// </summary>
    public static InclusionProofResponse? ProofFor(ActaLog acta, long index, long? requestedTreeSize)
    {
        ArgumentNullException.ThrowIfNull(acta);

        var size = requestedTreeSize ?? (acta.LatestHead is { } head && index < head.TreeSize ? head.TreeSize : acta.TreeSize);
        var proof = acta.Inclusion(index, size);
        if (proof is null) return null;

        return new InclusionProofResponse(
            proof.LogIndex,
            proof.TreeSize,
            LogEntries.Prefixed(proof.LeafHash),
            [.. proof.AuditPath.Select(LogEntries.Prefixed)],
            LogEntries.Prefixed(proof.Root),
            acta.Heads.Any(h => h.TreeSize == proof.TreeSize));
    }

    /// <summary>
    /// Reads and folds the log, or says why not. Unlike the post read paths, an unreadable store
    /// is not an empty log here: an empty tree has a root, and serving it would be serving a
    /// wrong answer with a 200.
    /// </summary>
    internal static async Task<(ActaLog? Acta, IResult? Failure)> FoldAsync(IEventReader events, CancellationToken cancellationToken)
    {
        var read = await events.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        if (!read.TryGetValue(out var log, out var readError))
        {
            return (null, Results.Json(
                new Problem("curia/log/unreadable", "The event log could not be read", readError!.Type),
                statusCode: StatusCodes.Status503ServiceUnavailable));
        }

        if (!ActaLog.Fold(log!).TryGetValue(out var acta, out var foldError))
        {
            return (null, Results.Json(
                new Problem(foldError!.Type, foldError.Title, foldError.Detail),
                statusCode: StatusCodes.Status500InternalServerError));
        }

        return (acta, null);
    }

    private static bool SignatureValid(ActaLog acta, SignedHead head, IReadOnlyDictionary<string, IContentVerifier> verifiers)
    {
        var key = acta.Keys.LastOrDefault(k => string.Equals(k.Kid, head.Kid, StringComparison.Ordinal));
        if (key is null) return false;

        if (!LogEntries.HeadCanonical(head.Document).TryGetValue(out var canonical, out _)) return false;
        if (!PublicKeyOf(key).TryGetValue(out var material, out _)) return false;

        var jws = new DetachedJws(new Dictionary<string, IContentSigner>(StringComparer.Ordinal), verifiers, DetachedJws.HeadTyp);
        return jws.Verify(canonical, new JwsSignature(head.Signature), material!).Match(_ => true, _ => false);
    }

    private static Result<PublicKeyMaterial> PublicKeyOf(LogKey key) =>
        CanonicalJson.Canonicalize(key.Jwk).Bind(bytes =>
        {
            using var document = JsonDocument.Parse(bytes.ToArray());
            return JwkParser.Parse(document.RootElement).Map(jwk => jwk.ToPublicKeyMaterial(key.Kid));
        });

    /// <summary>Canon's JSON tree as a System.Text.Json node, member order preserved; the verifier canonicalizes what it receives.</summary>
    private static JsonNode? ToNode(CanonJson.JsonValue value) => value switch
    {
        CanonJson.JsonValue.Object o => ToObject(o),
        CanonJson.JsonValue.Array a => ToArray(a),
        CanonJson.JsonValue.String s => System.Text.Json.Nodes.JsonValue.Create(s.Value),
        CanonJson.JsonValue.Number n => System.Text.Json.Nodes.JsonValue.Create(n.Value),
        CanonJson.JsonValue.Bool b => System.Text.Json.Nodes.JsonValue.Create(b.Value),
        _ => null,
    };

    private static JsonObject ToObject(CanonJson.JsonValue.Object o)
    {
        var node = new JsonObject();
        foreach (var m in o.Members)
            node.Add(m.Key, ToNode(m.Value));
        return node;
    }

    private static JsonArray ToArray(CanonJson.JsonValue.Array a)
    {
        var node = new JsonArray();
        foreach (var item in a.Items)
            node.Add(ToNode(item));
        return node;
    }
}
