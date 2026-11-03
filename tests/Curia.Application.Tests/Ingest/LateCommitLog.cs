#pragma warning disable CA2007 // test code: there is no SynchronizationContext to resume on (as the other suites of this project do, per await, with ConfigureAwait(true))
using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Canonical;
using Curia.Domain.Content;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;
using Curia.Tests.Shared;

namespace Curia.Application.Tests.Ingest;

/// <summary>
/// A log for the late-commit facts of the pure layer (errata G57, LCB20, LCB27, LCB28, LCB30), built through <see cref="InMemoryEventStore"/> so every event carries a sequence the
/// store assigned and no test constructs an <see cref="AppendedEvent"/> (CS-15's write surface stays as it was). Posts are shaped as <c>IngestPipeline</c> shapes them, with the digest
/// the caller names (the projectors compare digests and never recompute them).
/// </summary>
internal sealed class LateCommitLog
{
    internal static readonly DateTimeOffset Start = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryEventStore _store = new(new ManualTimeProvider(Start), new PermissiveLifecycleAppendGate());
    private int _next;

    internal static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} ({e.Detail})"));

    internal static string Digest(string postId) =>
        "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(postId)));

    /// <summary>The canonical text of a vote by <paramref name="author"/> for <paramref name="target"/>: a full envelope, because the one-vote rule re-reads the target through <see cref="PostEnvelope"/>.</summary>
    internal static string VoteCanonical(string author, string board, string target, string nonce)
    {
        var envelope = new JsonValue.Object(
        [
            new("v", new JsonValue.Number(PostEnvelope.CurrentVersion)),
            new("kind", new JsonValue.String("vote")),
            new("author", new JsonValue.String(author)),
            new("board", new JsonValue.String(board)),
            new("target", new JsonValue.String(target)),
            new("endorse", new JsonValue.Bool(true)),
            new("predicted_endorsement_bp", new JsonValue.Number(5000)),
            new("epoch", new JsonValue.Number(1)),
            new("refs", new JsonValue.Array([])),
            new("code_blocks", new JsonValue.Array([])),
            new("tags", new JsonValue.Array([])),
            new("content_type", new JsonValue.String(PostEnvelope.RequiredContentType)),
            new("created_at", new JsonValue.String(Start.ToString("o", System.Globalization.CultureInfo.InvariantCulture))),
            new("nonce", new JsonValue.String(nonce)),
        ]);
        return System.Text.Encoding.UTF8.GetString(Require(CanonicalJson.CanonicalizeWithNfc(envelope)).ToArray());
    }

    /// <summary>The canonical text of a revision of <paramref name="prev"/> (only the members the projections read).</summary>
    internal static string RevisionCanonical(string author, string board, string prev) =>
        $"{{\"author\":\"{author}\",\"board\":\"{board}\",\"kind\":\"revision\",\"prev\":\"{prev}\"}}";

    internal Task AppendPostAsync(string postId, string kind, string author, string canonical, string? digest = null, string board = "board") =>
        AppendRawAsync(postId, PostProjector.PostAcceptedType, new JsonValue.Object(
        [
            new("post_id", new JsonValue.String(postId)),
            new("canonical", new JsonValue.String(canonical)),
            new("signature", new JsonValue.String("sig")),
            new("digest", new JsonValue.String(digest ?? Digest(postId))),
            new("author", new JsonValue.String(author)),
            new("board", new JsonValue.String(board)),
            new("kind", new JsonValue.String(kind)),
        ]));

    internal Task AppendModerationAsync(string postId, string effect, string? idSuffix = null) =>
        AppendRawAsync(postId + (idSuffix ?? "-" + effect + "-" + _next), FlagProjector.ModerationAppliedType, new JsonValue.Object(
        [
            new(FlagProjector.PostIdField, new JsonValue.String(postId)),
            new(FlagProjector.ActorIdField, new JsonValue.String("operator:fixture")),
            new(FlagProjector.RationaleField, new JsonValue.String("moderated by the probe")),
            new(FlagProjector.ModeratorField, new JsonValue.String(ModeratorKinds.Wire(ModeratorKind.Human))),
            new(FlagProjector.EffectField, new JsonValue.String(effect)),
            new(FlagProjector.CategoryField, new JsonValue.String(FlagKinds.Wire(FlagKind.Spam))),
            new(FlagProjector.AdjudicatesField, new JsonValue.Array([])),
        ]));

    internal Task AppendOtherAsync(string type, JsonValue? payload = null) =>
        AppendRawAsync("other-" + _next, type, payload ?? new JsonValue.Object([]));

    internal async Task AppendRawAsync(string aggregate, string type, JsonValue payload)
    {
        var id = $"e{_next++}-{aggregate}";
        Require(await _store.AppendAsync(
            Require(AggregateId.Create(aggregate + "#" + _next)),
            AggregateVersion.New,
            [new DomainEvent(Require(EventId.Create(id)), Require(EventType.Create(type)), null, payload)],
            CancellationToken.None).ConfigureAwait(false));
    }

    internal async Task<IReadOnlyList<AppendedEvent>> ReadAsync() =>
        Require(await _store.ReadForwardAsync(EventSequence.Zero, cancellationToken: CancellationToken.None).ConfigureAwait(false));
}
