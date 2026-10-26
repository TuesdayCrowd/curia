using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
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
using Xunit;

namespace Curia.Application.Tests.Projections;

/// <summary>
/// R9.41 (errata G40): a question's accepted-answer member lists the revisions R9.28 counts as successors of the answer it names, from
/// the rule that sets the answer's own list (<see cref="CorrectionIndex"/>). The log is built post by post with a ledger of what each scene
/// signed (an id, a digest, an author, a <c>prev</c>), and the expected list is written from that ledger and R9.28's sentence, never read
/// from either index.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AcceptedAnswerSupersededTests
{
    private const string Root = "01JP0ST0000000000000000001";
    private const string AnswerA = "01JP0ST000000000000000000A";
    private const string Revision2 = "01JP0ST00000000000000000R2";
    private const string Revision3 = "01JP0ST00000000000000000R3";
    private const string StrangerRevision = "01JP0ST00000000000000000RS";
    private const string StrangerComment = "01JP0ST00000000000000000CS";
    private const string Asker = "https://agents.example/asker";
    private const string Answerer = "https://agents.example/ans1";
    private const string Stranger = "https://agents.example/stranger";

    private static readonly string DigestQ = Digest('0');
    private static readonly string DigestA = Digest('a');
    private static readonly string Digest2 = Digest('2');
    private static readonly string Digest3 = Digest('3');
    private static readonly string DigestS = Digest('5');
    private static readonly string DigestC = Digest('6');

    private static readonly DateTimeOffset Start = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static string Digest(char c) => "sha256:" + new string(c, 64);

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    private sealed record Scene(string[] Posts, bool Accepted, string[] Withheld, (string Digest, string? PostId)[] Expected);

    /// <summary>The scenes. The ledger of each is its <c>Posts</c>; its expectation is written here from R9.28 and R10.36.</summary>
    private static Scene SceneOf(string name) => name switch
    {
        "a revision by the answer's author" => new([Revision2], true, [], [(Digest2, Revision2)]),
        "a stranger's revision and a comment" => new([StrangerRevision, StrangerComment], true, [], []),
        "a fork" => new([Revision2, Revision3], true, [], [(Digest2, Revision2), (Digest3, Revision3)]),
        "a revision the Forum withholds" => new([Revision2], true, [Revision2], [(Digest2, null)]),
        "no acceptance" => new([Revision2], false, [], []),
        "a chain A to R2 to R3" => new(["chain"], true, [], [(Digest2, Revision2)]),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "not a scene this fact builds"),
    };

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

    private static async Task AppendAsync(
        InMemoryEventStore store, string id, string digest, PostKind kind, string author, string? parent, string? prev, CancellationToken ct)
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

        Require(await store.AppendAsync(
            Require(AggregateId.Create(id)),
            AggregateVersion.New,
            [new DomainEvent(Require(EventId.Create(id)), Require(EventType.Create(PostProjector.PostAcceptedType)), Require(ActorId.Create(author)), new JsonValue.Object([.. payload]))],
            ct).ConfigureAwait(false));
    }

    private sealed record Built(IReadOnlyList<AppendedEvent> Log, ImmutableArray<PostView> Posts, Func<string, bool> Servable, Scene Scene);

    private static async Task<Built> BuildAsync(string name, CancellationToken ct)
    {
        var scene = SceneOf(name);
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock, new PermissiveLifecycleAppendGate());

        await AppendAsync(store, Root, DigestQ, PostKind.Question, Asker, null, null, ct).ConfigureAwait(false);
        await AppendAsync(store, AnswerA, DigestA, PostKind.Answer, Answerer, Root, null, ct).ConfigureAwait(false);

        foreach (var post in scene.Posts)
        {
            switch (post)
            {
                case Revision2: await AppendAsync(store, post, Digest2, PostKind.Revision, Answerer, Root, DigestA, ct).ConfigureAwait(false); break;
                case Revision3 when name == "a fork": await AppendAsync(store, post, Digest3, PostKind.Revision, Answerer, Root, DigestA, ct).ConfigureAwait(false); break;
                case Revision3: await AppendAsync(store, post, Digest3, PostKind.Revision, Answerer, Root, Digest2, ct).ConfigureAwait(false); break;
                case StrangerRevision: await AppendAsync(store, post, DigestS, PostKind.Revision, Stranger, Root, DigestA, ct).ConfigureAwait(false); break;
                case StrangerComment: await AppendAsync(store, post, DigestC, PostKind.Comment, Stranger, AnswerA, DigestA, ct).ConfigureAwait(false); break;
                case "chain":
                    await AppendAsync(store, Revision2, Digest2, PostKind.Revision, Answerer, Root, DigestA, ct).ConfigureAwait(false);
                    await AppendAsync(store, Revision3, Digest3, PostKind.Revision, Answerer, Root, Digest2, ct).ConfigureAwait(false);
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(name), post, "not a post this scene signs");
            }
        }

        if (scene.Accepted)
        {
            clock.Set(Start + TimeSpan.FromMinutes(1));
            Require(await new AcceptAnswer(store, clock).RecordAsync(Typed.Post(Root), Typed.Post(AnswerA), Typed.Agent(Asker), ct).ConfigureAwait(false));
        }

        var log = Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct).ConfigureAwait(false));
        return new Built(log, PostProjector.Fold(log), id => !scene.Withheld.Contains(id), scene);
    }

    private static (string Digest, string? PostId)[] Listed(ImmutableArray<Successor> list) =>
        [.. list.Select(s => (s.Digest, s.PostId))];

    public static TheoryData<string> SceneNames => new()
    {
        "a revision by the answer's author",
        "a stranger's revision and a comment",
        "a fork",
        "a revision the Forum withholds",
        "no acceptance",
        "a chain A to R2 to R3",
    };

    [Theory]
    [MemberData(nameof(SceneNames))]
    public async Task R9_41_StandingOfListsTheAnswersCorrectionsAsR932Counts(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        var built = await BuildAsync(name, ct);
        var question = built.Posts.Single(p => p.PostId == Root);

        var member = AcceptedAnswerIndex.Fold(built.Log, built.Posts, built.Servable).StandingOf(question);

        if (!built.Scene.Accepted)
        {
            // Control: no acceptance names no answer, and a member that names none carries no list.
            Assert.True(member.IsNone);
            Assert.True(member.SupersededBy.IsEmpty);
            return;
        }

        Assert.False(member.IsNone);
        Assert.Equal(built.Scene.Expected, Listed(member.SupersededBy));
    }

    /// <summary>The question's list and the answer's own list come from one rule: they are equal, scene by scene.</summary>
    [Theory]
    [MemberData(nameof(SceneNames))]
    public async Task R9_41_TheQuestionsListIsTheListOfTheAnswersOwnEnvelope(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        var built = await BuildAsync(name, ct);
        if (!built.Scene.Accepted) return;

        var answer = built.Posts.Single(p => p.PostId == AnswerA);
        var own = CorrectionIndex.Fold(built.Posts, built.Servable).For(answer);
        var member = AcceptedAnswerIndex.Fold(built.Log, built.Posts, built.Servable).StandingOf(built.Posts.Single(p => p.PostId == Root));

        // Non-vacuity for the scenes that list something: the answer's own list is what the ledger says it is.
        Assert.Equal(built.Scene.Expected, Listed(own));
        Assert.Equal(Listed(own), Listed(member.SupersededBy));
    }
}
