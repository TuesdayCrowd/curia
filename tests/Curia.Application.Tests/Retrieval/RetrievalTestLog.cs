using System.Collections.Immutable;
using System.Globalization;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Retrieval;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Content;
using Curia.Domain.Primitives;

namespace Curia.Application.Tests.Retrieval;

/// <summary>Accepted posts in an in-memory log, indexed by the hashed embedder, for the retrieval suites.</summary>
internal static class RetrievalTestLog
{
    private static readonly DateTimeOffset Start = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    public static ManualTimeProvider Clock() => new(Start);

    public static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    public static string Canonical(PostKind kind, string author, string board, string? title, string body, string[] tags, string? parent = null)
    {
        var members = ImmutableArray.CreateBuilder<KeyValuePair<string, JsonValue>>();
        members.Add(new("v", new JsonValue.Number(PostEnvelope.CurrentVersion)));
        members.Add(new("kind", new JsonValue.String(PostKinds.Wire(kind))));
        members.Add(new("author", new JsonValue.String(author)));
        members.Add(new("board", new JsonValue.String(board)));
        if (title is not null) members.Add(new("title", new JsonValue.String(title)));
        if (parent is not null) members.Add(new("parent", new JsonValue.String(parent)));
        members.Add(new("body", new JsonValue.String(body)));
        members.Add(new("code_blocks", new JsonValue.Array([])));
        members.Add(new("refs", new JsonValue.Array([])));
        members.Add(new("tags", new JsonValue.Array([.. tags.Select(t => new JsonValue.String(t))])));
        members.Add(new("content_type", new JsonValue.String(PostEnvelope.RequiredContentType)));
        members.Add(new("created_at", new JsonValue.String(Start.ToString("o", CultureInfo.InvariantCulture))));
        members.Add(new("nonce", new JsonValue.String("00112233445566778899aabbccddeeff")));

        var canonical = Require(CanonicalJson.CanonicalizeWithNfc(new JsonValue.Object(members.ToImmutable())));
        return System.Text.Encoding.UTF8.GetString(canonical.Span);
    }

    /// <summary>Appends a <c>post.accepted</c> and indexes it, returning its digest.</summary>
    public static async Task<string> AcceptAsync(
        InMemoryEventStore store,
        EmbeddingIndexer indexer,
        string postId,
        CancellationToken ct,
        PostKind kind = PostKind.Question,
        string author = "https://agents.example/asker",
        string board = "postgres",
        string? title = "ECONNRESET from npgsql",
        string body = "npgsql throws ECONNRESET after the pooler idles the connection",
        string[]? tags = null,
        string? parent = null,
        string? possibleDuplicateOf = null)
    {
        var digest = "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(postId)));
        var members = new List<KeyValuePair<string, JsonValue>>
        {
            new("post_id", new JsonValue.String(postId)),
            new("canonical", new JsonValue.String(Canonical(kind, author, board, title, body, tags ?? [], parent))),
            new("signature", new JsonValue.String("sig")),
            new("digest", new JsonValue.String(digest)),
            new("author", new JsonValue.String(author)),
            new("board", new JsonValue.String(board)),
            new("kind", new JsonValue.String(PostKinds.Wire(kind))),
        };
        if (possibleDuplicateOf is not null)
            members.Add(new("possible_duplicate", new JsonValue.Object(
            [
                new("of", new JsonValue.String(possibleDuplicateOf)),
                new("cosine", new JsonValue.Number(0.9)),
                new("model", new JsonValue.String(indexer.Model.Id)),
            ])));

        var appended = Require(await store.AppendAsync(
            Require(AggregateId.Create(postId)),
            AggregateVersion.New,
            [new DomainEvent(
                Require(EventId.Create(postId)),
                Require(EventType.Create(PostProjector.PostAcceptedType)),
                Require(ActorId.Create(author)),
                new JsonValue.Object([.. members]))],
            ct).ConfigureAwait(false));

        var searchable = SearchProjector.TryRead(appended[0]) ?? throw new InvalidOperationException("not searchable");
        Require(await indexer.IndexAsync(searchable, ct).ConfigureAwait(false));
        return digest;
    }

    public static async Task<IReadOnlyList<AppendedEvent>> LogAsync(IEventReader reader, CancellationToken ct) =>
        Require(await reader.ReadAllAsync(ct).ConfigureAwait(false));
}
