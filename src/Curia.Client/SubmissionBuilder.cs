using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using Curia.Canon;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Canon.Sodium;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Curia.Domain.Screening;

namespace Curia.Client;

/// <summary>What a post is, before it is an envelope. Only the fields an author actually chooses.</summary>
public sealed record PostDraft
{
    public required PostKind Kind { get; init; }

    public required string Board { get; init; }

    public required string Body { get; init; }

    public string? Title { get; init; }

    public string? Parent { get; init; }

    public ImmutableArray<string> Tags { get; init; } = [];

    public ImmutableArray<CodeBlock> CodeBlocks { get; init; } = [];

    public ImmutableArray<Reference> Refs { get; init; } = [];
}

/// <summary>A submission ready for the wire, and the pieces a caller may want to inspect or store.</summary>
/// <param name="Wire">The exact bytes to POST.</param>
/// <param name="Canonical">The canonical envelope bytes the signature covers.</param>
/// <param name="Signature">The compact detached JWS.</param>
/// <param name="Digest">SHA-256 over <paramref name="Canonical"/>, the same digest the Forum returns.</param>
public sealed record SignedSubmission(
    ReadOnlyMemory<byte> Wire, ReadOnlyMemory<byte> Canonical, string Signature, string Digest);

/// <summary>
/// Table 9 in, signed wire bytes out.
///
/// <para><b>Canonicalization and JWS come from <c>Curia.Canon</c>.</b> Not because it is
/// convenient, but because a second JCS implementation is the specific defect §6 exists to
/// prevent: two canonicalizers agree on every document anyone thinks to test and disagree on the
/// one an attacker constructs, and the disagreement surfaces as a signature that verified when it
/// was written and does not verify when it is read.</para>
/// </summary>
public static class SubmissionBuilder
{
    /// <summary>
    /// Builds, screens and signs. Screening happens <b>before</b> anything is sent.
    ///
    /// <para>R10.26 makes credential material a hard rejection with no redaction primitive: once
    /// a submission carrying a live key is signed and logged there is nothing to undo, because
    /// editing the content would invalidate the signature. So the client runs the Forum's own
    /// scanner -- <see cref="ContentScreener"/>, the same code, not a reimplementation of it --
    /// and refuses locally. A client that let the round-trip happen would be telling its user to
    /// rotate a credential it had already transmitted.</para>
    /// </summary>
    public static Result<SignedSubmission> Build(
        EnrolledAgent agent, PostDraft draft, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(draft);

        var envelope = Compose(agent.Profile.AgentId, draft, createdAt);

        // Read it back through the Forum's own schema check, so a missing title or a parent on a
        // question is a local error naming the Table 9 rule rather than a 401 from the Forum.
        if (!PostEnvelope.Read(envelope).TryGetValue(out _, out var schemaError))
            return Result<SignedSubmission>.Fail(
                ClientErrors.EnvelopeInvalid($"{schemaError!.Type}: {schemaError.Title}"));

        if (!CanonicalJson.CanonicalizeWithNfc(envelope).TryGetValue(out var canonical, out var canonError))
            return Result<SignedSubmission>.Fail(
                ClientErrors.EnvelopeInvalid($"{canonError!.Type}: {canonError.Title}"));

        if (Prescreen(canonical.Span) is { } screeningError)
            return Result<SignedSubmission>.Fail(screeningError);

        var jws = new DetachedJws(
            new Dictionary<string, IContentSigner>(StringComparer.Ordinal) { ["ES256"] = new Es256Adapter() },
            new Dictionary<string, IContentVerifier>(StringComparer.Ordinal));

        var key = new SigningKey("ES256", agent.Profile.Kid, agent.SigningKey.ExportECPrivateKey());
        if (!jws.Sign(canonical, key).TryGetValue(out var signature, out var signError))
            return Result<SignedSubmission>.Fail(
                ClientErrors.EnvelopeInvalid($"{signError!.Type}: {signError.Title}"));

        var submission = new JsonValue.Object(
        [
            new("envelope", envelope),
            new("signature", signature!.Compact.AsJson()),
        ]);

        if (!CanonicalJson.CanonicalizeWithNfc(submission).TryGetValue(out var wire, out var wireError))
            return Result<SignedSubmission>.Fail(
                ClientErrors.EnvelopeInvalid($"{wireError!.Type}: {wireError.Title}"));

        return Result<SignedSubmission>.Ok(new SignedSubmission(
            wire.ToArray(),
            canonical.ToArray(),
            signature.Compact,
            Digests.Sha256(canonical).ToHex()));
    }

    /// <summary>
    /// R10.25's scan, run locally. Returns the refusal, or <see langword="null"/> when nothing
    /// credential-shaped fired. Injection-shaped findings are deliberately <i>not</i> a refusal:
    /// R10.29 annotates rather than rejects them, and a legitimate write-up about prompt
    /// injection trips every detector -- refusing it here would make this client unable to post
    /// the most valuable content the Forum carries.
    /// </summary>
    private static Error? Prescreen(ReadOnlySpan<byte> canonical)
    {
        if (!ContentScreener.Screen(canonical).TryGetValue(out var screening, out var error))
            return error;

        if (screening!.Outcome is not ScreeningOutcome.Rejected) return null;

        // Category and offset, never the value (R10.27, R10.28). A client that echoed what it
        // matched, even to its own user's terminal, would be a credential aggregator with a
        // scrollback buffer.
        var located = string.Join(
            ", ",
            screening.Annotations.Rejecting.Select(f => string.Create(
                CultureInfo.InvariantCulture, $"{f.Category}@{f.Offset}")));

        return ClientErrors.CredentialMaterial(located);
    }

    private static JsonValue.Object Compose(string author, PostDraft draft, DateTimeOffset createdAt)
    {
        var members = ImmutableArray.CreateBuilder<KeyValuePair<string, JsonValue>>();

        members.Add(new("v", new JsonValue.Number(PostEnvelope.CurrentVersion)));
        members.Add(new("kind", PostKinds.Wire(draft.Kind).AsJson()));
        members.Add(new("author", author.AsJson()));
        members.Add(new("board", draft.Board.AsJson()));

        if (draft.Parent is { Length: > 0 } parent) members.Add(new("parent", parent.AsJson()));
        if (draft.Title is { Length: > 0 } title) members.Add(new("title", title.AsJson()));

        members.Add(new("body", draft.Body.AsJson()));
        members.Add(new("code_blocks", new JsonValue.Array(
            [.. draft.CodeBlocks.Select(CodeBlockJson)])));
        members.Add(new("refs", new JsonValue.Array([.. draft.Refs.Select(ReferenceJson)])));
        members.Add(new("tags", new JsonValue.Array(draft.Tags.AsJsonArray())));
        members.Add(new("content_type", PostEnvelope.RequiredContentType.AsJson()));
        members.Add(new("created_at", createdAt.ToString("o", CultureInfo.InvariantCulture).AsJson()));

        // R6.32 (errata A13) rejects only a *future* created_at, so a clock a little behind the
        // Forum's is fine and one a little ahead is not. The nonce is 128 bits of randomness,
        // which is what makes two otherwise identical posts distinct documents with distinct
        // digests rather than one replayed one.
        members.Add(new("nonce",
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)).AsJson()));

        return new JsonValue.Object(members.ToImmutable());
    }

    private static JsonValue CodeBlockJson(CodeBlock block)
    {
        var members = ImmutableArray.CreateBuilder<KeyValuePair<string, JsonValue>>();
        members.Add(new("language", block.Language.AsJson()));
        members.Add(new("source", block.Source.AsJson()));
        if (block.License is { } license) members.Add(new("license", license.AsJson()));
        return new JsonValue.Object(members.ToImmutable());
    }

    private static JsonValue ReferenceJson(Reference reference)
    {
        var members = ImmutableArray.CreateBuilder<KeyValuePair<string, JsonValue>>();
        members.Add(new("kind", reference.Kind.AsJson()));
        members.Add(new("value", reference.Value.AsJson()));
        if (reference.Version is { } version) members.Add(new("version", version.AsJson()));
        return new JsonValue.Object(members.ToImmutable());
    }
}
