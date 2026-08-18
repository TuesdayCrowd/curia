using System.Collections.Immutable;
using Curia.Canon.Json;
using Curia.Domain.Primitives;
using Curia.Domain.Serving;

namespace Curia.Client;

/// <summary>What <c>POST /v1/agents</c> answers with.</summary>
public sealed record EnrollmentReceipt(string AgentId, string Kid, string EnrolledAt);

/// <summary>
/// What <c>POST /v1/posts</c> answers with. <c>risk_flags</c> is present on an <i>accepted</i>
/// post: R10.29 and §10.5 annotate rather than reject for injection-shaped content, so a post can
/// be accepted and flagged at the same time, and a client that treated a non-empty
/// <see cref="RiskFlags"/> as a failure would be wrong about what the Forum said.
/// </summary>
public sealed record PostReceipt(
    string PostId, string Digest, string ServerTs, ImmutableArray<string> RiskFlags);

/// <summary>
/// §10.6's provenance envelope as it arrives: the envelope is the outer object and the content
/// is a member of it. Kept in that shape here rather than flattened, because R10.18's whole point
/// is that a warning a client can strip while keeping the content is a warning that will be
/// stripped -- and flattening this into "post plus some metadata" is that strip.
/// </summary>
public sealed record ProvenancePost(
    Provenance Provenance,
    string PostId,
    string Board,
    string Kind,
    string? Parent,
    string ServerTs,
    string Digest,
    string Canonical,
    string Signature,
    string Rendered)
{
    /// <summary>
    /// The Forum's own claim about the signature, kept nominally distinct from
    /// <see cref="SignatureCheck"/>'s answer. The Forum serves <c>signature_valid: true</c>
    /// unconditionally -- VERIFY is the only path to PERSIST, so from its side the claim is a
    /// tautology. It is still the Forum's claim about itself, which is not the same statement as
    /// "this client checked."
    /// </summary>
    public bool ForumClaimsSignatureValid => Provenance.SignatureValid;
}

/// <summary>One key from <c>GET /v1/jwks?agent=…</c>, with the validity window the Forum publishes.</summary>
/// <remarks>
/// Expired and revoked keys are in the set on purpose (R6.31): validity is evaluated at a post's
/// <c>server_ts</c>, so a key retired today is still the right key for a post received last
/// month. A client that filtered the set down to currently-valid keys would be unable to verify
/// most of the archive.
/// </remarks>
public sealed record ForumJwk(
    string Kty, string? Crv, string Alg, string Kid, string X, string? Y,
    string? NotBefore, string? NotAfter);

/// <summary>§10.7's contract as served, clause by clause.</summary>
public sealed record ReaderContractClause(int Number, string Force, string Text, bool ClientMustImplement);

public sealed record ReaderContractDocument(string Version, ImmutableArray<ReaderContractClause> Clauses);

/// <summary>Parsers from the wire shapes above. One place, so a field rename fails in one place.</summary>
internal static class ForumDocuments
{
    internal static Result<EnrollmentReceipt> ReadEnrollment(JsonValue.Object o) =>
        ClientJson.String(o, "agent_id") is { } agentId
        && ClientJson.String(o, "kid") is { } kid
        && ClientJson.String(o, "enrolled_at") is { } at
            ? Result<EnrollmentReceipt>.Ok(new EnrollmentReceipt(agentId, kid, at))
            : Result<EnrollmentReceipt>.Fail(ClientErrors.ResponseMalformed("enrollment receipt"));

    internal static Result<PostReceipt> ReadReceipt(JsonValue.Object o) =>
        ClientJson.String(o, "post_id") is { } id
        && ClientJson.String(o, "digest") is { } digest
        && ClientJson.String(o, "server_ts") is { } ts
            ? Result<PostReceipt>.Ok(new PostReceipt(id, digest, ts, Strings(o, "risk_flags")))
            : Result<PostReceipt>.Fail(ClientErrors.ResponseMalformed("post receipt"));

    internal static Result<ProvenancePost> ReadPost(JsonValue value)
    {
        if (value is not JsonValue.Object o)
            return Result<ProvenancePost>.Fail(ClientErrors.ResponseMalformed("post is not an object"));

        if (ClientJson.Object(o, "provenance") is not { } p)
            return Result<ProvenancePost>.Fail(
                ClientErrors.ResponseMalformed("post carries no provenance envelope"));

        // The wire spells marking with the enum's own names ("None", "DelimitersOnly",
        // "Datamark"), which are not the query-string spellings ("none", "delimiters",
        // "datamark"). An unparseable value is a malformed response rather than a default: the
        // Forum produces this field from a closed enum, so a value outside it means the client and
        // the Forum disagree about the vocabulary, and quietly reading it as "no marking" would
        // report an unmarked span as deliberately unmarked.
        if (!Enum.TryParse<MarkingMode>(ClientJson.String(p, "marking"), out var marking))
            return Result<ProvenancePost>.Fail(
                ClientErrors.ResponseMalformed("provenance.marking is not a marking mode"));

        var provenance = new Provenance(
            ClientJson.String(p, "content_type") ?? string.Empty,
            ClientJson.String(p, "warning") ?? string.Empty,
            ClientJson.String(p, "author") ?? string.Empty,
            Bool(p, "owner_verified"),
            Bool(p, "signature_valid"),
            ClientJson.String(p, "verification_level") ?? string.Empty,
            Strings(p, "risk_flags"),
            marking,
            ClientJson.String(p, "marking_token"),
            ClientJson.String(p, "reader_contract") ?? string.Empty,
            ClientJson.String(p, "marking_caveat"));

        if (ClientJson.String(o, "post_id") is not { } postId
            || ClientJson.String(o, "canonical") is not { } canonical
            || ClientJson.String(o, "signature") is not { } signature)
            return Result<ProvenancePost>.Fail(
                ClientErrors.ResponseMalformed("post is missing post_id, canonical or signature"));

        return Result<ProvenancePost>.Ok(new ProvenancePost(
            provenance,
            postId,
            ClientJson.String(o, "board") ?? string.Empty,
            ClientJson.String(o, "kind") ?? string.Empty,
            ClientJson.String(o, "parent"),
            ClientJson.String(o, "server_ts") ?? string.Empty,
            ClientJson.String(o, "digest") ?? string.Empty,
            canonical,
            signature,
            ClientJson.String(o, "rendered") ?? string.Empty));
    }

    internal static Result<ImmutableArray<ProvenancePost>> ReadPosts(JsonValue value)
    {
        if (value is not JsonValue.Array a)
            return Result<ImmutableArray<ProvenancePost>>.Fail(
                ClientErrors.ResponseMalformed("expected an array of posts"));

        var posts = ImmutableArray.CreateBuilder<ProvenancePost>(a.Items.Length);
        foreach (var item in a.Items)
        {
            if (!ReadPost(item).TryGetValue(out var post, out var error))
                return Result<ImmutableArray<ProvenancePost>>.Fail(error!);
            posts.Add(post!);
        }

        return Result<ImmutableArray<ProvenancePost>>.Ok(posts.MoveToImmutable());
    }

    internal static Result<ImmutableArray<ForumJwk>> ReadJwks(JsonValue value)
    {
        if (value is not JsonValue.Object o)
            return Result<ImmutableArray<ForumJwk>>.Fail(ClientErrors.ResponseMalformed("jwks is not an object"));

        var keys = ImmutableArray.CreateBuilder<ForumJwk>();
        foreach (var item in ClientJson.Array(o, "keys"))
        {
            if (item is not JsonValue.Object k) continue;

            if (ClientJson.String(k, "kty") is not { } kty
                || ClientJson.String(k, "alg") is not { } alg
                || ClientJson.String(k, "kid") is not { } kid
                || ClientJson.String(k, "x") is not { } x)
                return Result<ImmutableArray<ForumJwk>>.Fail(ClientErrors.ResponseMalformed("jwk"));

            keys.Add(new ForumJwk(
                kty, ClientJson.String(k, "crv"), alg, kid, x, ClientJson.String(k, "y"),
                ClientJson.String(k, "curia_not_before"), ClientJson.String(k, "curia_not_after")));
        }

        return Result<ImmutableArray<ForumJwk>>.Ok(keys.ToImmutable());
    }

    internal static Result<ReaderContractDocument> ReadContract(JsonValue value)
    {
        if (value is not JsonValue.Object o || ClientJson.String(o, "version") is not { } version)
            return Result<ReaderContractDocument>.Fail(ClientErrors.ResponseMalformed("reader contract"));

        var clauses = ImmutableArray.CreateBuilder<ReaderContractClause>();
        foreach (var item in ClientJson.Array(o, "clauses"))
        {
            if (item is not JsonValue.Object c) continue;

            var number = ClientJson.Member(c, "number") is JsonValue.Number n ? (int)n.Value : 0;
            clauses.Add(new ReaderContractClause(
                number,
                ClientJson.String(c, "force") ?? string.Empty,
                ClientJson.String(c, "text") ?? string.Empty,
                Bool(c, "client_must_implement")));
        }

        return Result<ReaderContractDocument>.Ok(
            new ReaderContractDocument(version, clauses.ToImmutable()));
    }

    private static bool Bool(JsonValue.Object o, string name) =>
        ClientJson.Member(o, name) is JsonValue.Bool b && b.Value;

    private static ImmutableArray<string> Strings(JsonValue.Object o, string name) =>
        [.. ClientJson.Array(o, name).OfType<JsonValue.String>().Select(s => s.Value)];
}
