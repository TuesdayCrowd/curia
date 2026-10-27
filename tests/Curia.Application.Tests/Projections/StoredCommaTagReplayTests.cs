using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Application.Tests.Projections;

/// <summary>
/// R8.73 and R11.31 (errata G43): the rule that a tag is one the filter can name binds a submission, not the log. A question that
/// holds a comma tag may already be stored, and replay must still project it. If the rule moves into <see cref="PostEnvelope.Read"/>,
/// which the search projection applies to every stored post, such a post drops out (PJ24; FPJ17 holds it).
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class StoredCommaTagReplayTests
{
    private const string Author = "https://agents.example/alice";
    private const string PostId = "01JQUESTION2";
    private static readonly DateTimeOffset Start = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    private static string Canonical()
    {
        var members = ImmutableArray.CreateBuilder<KeyValuePair<string, JsonValue>>();
        members.Add(new("v", new JsonValue.Number(PostEnvelope.CurrentVersion)));
        members.Add(new("kind", new JsonValue.String(PostKinds.Wire(PostKind.Question))));
        members.Add(new("author", new JsonValue.String(Author)));
        members.Add(new("board", new JsonValue.String("b")));
        members.Add(new("title", new JsonValue.String("A question stored before the rule")));
        members.Add(new("body", new JsonValue.String("a body")));
        members.Add(new("code_blocks", new JsonValue.Array([])));
        members.Add(new("refs", new JsonValue.Array([])));
        members.Add(new("tags", new JsonValue.Array([new JsonValue.String("a,b"), new JsonValue.String(""), new JsonValue.String(" x")])));
        members.Add(new("content_type", new JsonValue.String(PostEnvelope.RequiredContentType)));
        members.Add(new("created_at", new JsonValue.String(Start.ToString("o", CultureInfo.InvariantCulture))));
        members.Add(new("nonce", new JsonValue.String("00112233445566778899aabbccddeeff")));

        var canonical = Require(CanonicalJson.CanonicalizeWithNfc(new JsonValue.Object(members.ToImmutable())));
        return System.Text.Encoding.UTF8.GetString(canonical.Span);
    }

    private static async Task<IReadOnlyList<AppendedEvent>> StoredQuestionAsync(CancellationToken ct)
    {
        var store = new InMemoryEventStore(new ManualTimeProvider(Start), new PermissiveLifecycleAppendGate());
        var payload = new JsonValue.Object(
        [
            new("post_id", new JsonValue.String(PostId)),
            new("canonical", new JsonValue.String(Canonical())),
            new("signature", new JsonValue.String("sig")),
            new("digest", new JsonValue.String("sha256:" + new string('c', 64))),
            new("author", new JsonValue.String(Author)),
            new("board", new JsonValue.String("b")),
            new("kind", new JsonValue.String("question")),
        ]);

        Require(await store.AppendAsync(
            Require(AggregateId.Create(PostId)),
            AggregateVersion.New,
            [new DomainEvent(
                Require(EventId.Create(PostId)),
                Require(EventType.Create(PostProjector.PostAcceptedType)),
                Require(ActorId.Create(Author)),
                payload)],
            ct).ConfigureAwait(false));

        return Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct).ConfigureAwait(false));
    }

    /// <summary>The discriminator: red if <see cref="PostEnvelope.Read"/> refuses a question that holds an unfilterable tag.</summary>
    [Fact]
    public async Task R11_31_AStoredQuestionWithAnUnfilterableTagIsStillSearchable()
    {
        var log = await StoredQuestionAsync(TestContext.Current.CancellationToken);

        var post = Assert.Single(SearchProjector.Fold(log));

        Assert.Equal(PostKind.Question, post.Kind);
    }

    /// <summary>The post model keeps the tags as stored; a control that stays green with the rule in the wrong place.</summary>
    [Fact]
    public async Task R11_31_AStoredQuestionWithAnUnfilterableTagStillFoldsIntoThePostModel()
    {
        var log = await StoredQuestionAsync(TestContext.Current.CancellationToken);

        Assert.Single(PostProjector.Fold(log));
    }
}
