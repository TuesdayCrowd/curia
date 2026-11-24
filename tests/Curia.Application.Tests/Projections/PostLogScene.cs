using System.Collections.Immutable;
using System.Globalization;
using Curia.Application.Moderation;
using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Curia.Domain.Serving;
using Curia.Tests.Shared;

namespace Curia.Application.Tests.Projections;

/// <summary>
/// PR 9 (errata G76) probes' log builder: posts appended one by one with the id, digest, author, <c>parent</c> and <c>prev</c> the scene chooses, under a clock the scene
/// sets, and acceptances recorded through <see cref="AcceptAnswer"/> (the use case the route calls). Every expectation a probe holds is written from what the scene
/// built, never read from an index.
/// </summary>
internal sealed class PostLogScene
{
    internal static readonly DateTimeOffset Start = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    private readonly ManualTimeProvider _clock = new(Start);
    private readonly InMemoryEventStore _store;

    internal PostLogScene() => _store = new InMemoryEventStore(_clock, new PermissiveLifecycleAppendGate());

    internal ManualTimeProvider Clock => _clock;

    /// <summary>A valid post id (a 26-character ULID) for a scene's name: <c>01PR9</c>, the tag (Crockford characters only), padded with Z.</summary>
    internal static string Id(string tag) => "01PR9" + tag.PadRight(21, 'Z');

    /// <summary>The digest of the post a scene calls <paramref name="name"/>: sixty-four of one letter, so a probe writes <c>prev</c> from the ledger.</summary>
    internal static string DigestOf(char c) => "sha256:" + new string(c, 64);

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    private static string Canonical(PostKind kind, string author, string? parent, string? prev)
    {
        var members = ImmutableArray.CreateBuilder<KeyValuePair<string, JsonValue>>();
        members.Add(new("v", new JsonValue.Number(PostEnvelope.CurrentVersion)));
        members.Add(new("kind", new JsonValue.String(PostKinds.Wire(kind))));
        members.Add(new("author", new JsonValue.String(author)));
        members.Add(new("board", new JsonValue.String("postgres")));
        if (kind is PostKind.Question) members.Add(new("title", new JsonValue.String("a question")));
        if (parent is not null) members.Add(new("parent", new JsonValue.String(parent)));
        if (prev is not null) members.Add(new("prev", new JsonValue.String(prev)));
        members.Add(new("body", new JsonValue.String("a body")));
        members.Add(new("code_blocks", new JsonValue.Array([])));
        members.Add(new("refs", new JsonValue.Array([])));
        members.Add(new("tags", new JsonValue.Array([])));
        members.Add(new("content_type", new JsonValue.String(PostEnvelope.RequiredContentType)));
        members.Add(new("created_at", new JsonValue.String(Start.ToString("o", CultureInfo.InvariantCulture))));
        members.Add(new("nonce", new JsonValue.String("00112233445566778899aabbccddeeff")));
        var canonical = Require(CanonicalJson.CanonicalizeWithNfc(new JsonValue.Object(members.ToImmutable())));
        return System.Text.Encoding.UTF8.GetString(canonical.Span);
    }

    /// <summary>Appends one post. <paramref name="digest"/> is its ledger digest; a revision's <paramref name="prev"/> is the digest of the post it corrects.</summary>
    internal async Task PostAsync(string id, string digest, PostKind kind, string author, string? parent, string? prev, CancellationToken ct)
    {
        var payload = new List<KeyValuePair<string, JsonValue>>
        {
            new("post_id", new JsonValue.String(id)),
            new("canonical", new JsonValue.String(Canonical(kind, author, parent, prev))),
            new("signature", new JsonValue.String("sig")),
            new("digest", new JsonValue.String(digest)),
            new("author", new JsonValue.String(author)),
            new("board", new JsonValue.String("postgres")),
            new("kind", new JsonValue.String(PostKinds.Wire(kind))),
        };
        if (parent is not null) payload.Add(new("parent", new JsonValue.String(parent)));

        Require(await _store.AppendAsync(
            Require(AggregateId.Create(id)),
            AggregateVersion.New,
            [new DomainEvent(Require(EventId.Create(id)), Require(EventType.Create(PostProjector.PostAcceptedType)), Require(ActorId.Create(author)), new JsonValue.Object([.. payload]))],
            ct).ConfigureAwait(false));
    }

    /// <summary>The asker accepts <paramref name="answer"/> in <paramref name="root"/>'s thread, at the clock's present reading. A replay appends nothing (R8.68).</summary>
    internal async Task<Acceptance> AcceptAsync(string root, string answer, string asker, CancellationToken ct) =>
        Require(await new AcceptAnswer(_store, _clock).RecordAsync(Typed.Post(root), Typed.Post(answer), Typed.Agent(asker), ct).ConfigureAwait(false));

    internal sealed record Built(IReadOnlyList<AppendedEvent> Log, ImmutableArray<PostView> Posts)
    {
        internal PostView Post(string id) => Posts.Single(p => p.PostId == id);

        internal AcceptedAnswerIndex Index(Func<string, bool> servable) => AcceptedAnswerIndex.Fold(Log, Posts, servable);
    }

    internal async Task<Built> BuildAsync(CancellationToken ct)
    {
        var log = Require(await _store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct).ConfigureAwait(false));
        return new Built(log, PostProjector.Fold(log));
    }
}
