using System.Diagnostics.CodeAnalysis;
using Curia.Application.Moderation;
using Curia.Application.Projections;
using Curia.Application.Retrieval;
using Curia.Application.Tests.InMemory;
using Curia.Domain.Content;
using Curia.Tests.Shared;
using Xunit;
using static Curia.Application.Tests.Retrieval.RetrievalTestLog;

namespace Curia.Application.Tests.Projections;

/// <summary>
/// R9.36 (errata G36): the answer a question's standing acceptance names, defined once, so a question's <c>accepted_answer</c> and the
/// <c>accepted</c> of the answer it names are one rule.
///
/// <para><b>Oracles.</b> The log is built by the use case that writes it (<see cref="AcceptAnswer"/>) over posts the test appended, and the
/// digests expected are the ones <see cref="Curia.Application.Tests.Retrieval.RetrievalTestLog.AcceptAsync"/> returned for the posts it wrote, never a
/// value read from the index. A negative row has a control: the same log with every post servable names the answer by id.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AcceptedAnswerIndexTests
{
    private const string Root = "01JP0ST0000000000000000001";
    private const string Answer1 = "01JP0ST00000000000000000A1";
    private const string Answer2 = "01JP0ST00000000000000000A2";
    private const string Ghost = "01JP0ST00000000000000000G0";
    private const string Asker = "https://agents.example/asker";

    private static readonly DateTimeOffset Start = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private sealed record Seeded(InMemoryEventStore Store, string RootDigest, string Digest1, string Digest2, ManualTimeProvider Clock);

    private static async Task<Seeded> SeedAsync(CancellationToken ct)
    {
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock, new PermissiveLifecycleAppendGate());
        var indexer = new EmbeddingIndexer(new HashedNGramEmbedder(), new InMemoryVectorIndex());

        var root = await AcceptAsync(store, indexer, Root, ct, author: Asker, title: "ECONNRESET from npgsql", body: "npgsql throws ECONNRESET after the pooler idles").ConfigureAwait(false);
        var one = await AcceptAsync(store, indexer, Answer1, ct, kind: PostKind.Answer, title: null, body: "set the keepalive", parent: Root, author: "https://agents.example/one").ConfigureAwait(false);
        var two = await AcceptAsync(store, indexer, Answer2, ct, kind: PostKind.Answer, title: null, body: "lower the pooler idle timeout", parent: Root, author: "https://agents.example/two").ConfigureAwait(false);
        return new Seeded(store, root, one, two, clock);
    }

    private static async Task AcceptNamedAsync(Seeded seeded, string answer, CancellationToken ct)
    {
        seeded.Clock.Set(seeded.Clock.GetUtcNow() + TimeSpan.FromMinutes(1));
        Require(await new AcceptAnswer(seeded.Store, seeded.Clock).RecordAsync(Typed.Post(Root), Typed.Post(answer), Typed.Agent(Asker), ct).ConfigureAwait(false));
    }

    private static async Task<(string? Digest, string? PostId, bool IsNone)> StandingAsync(Seeded seeded, Func<string, bool> servable, CancellationToken ct)
    {
        var log = await LogAsync(seeded.Store, ct).ConfigureAwait(false);
        var posts = PostProjector.Fold(log);
        var member = AcceptedAnswerIndex.Fold(log, posts, servable).StandingOf(posts.Single(p => p.PostId == Root));
        return (member.Digest, member.PostId, member.IsNone);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("one")]
    [InlineData("second wins")]
    [InlineData("not servable")]
    [InlineData("model lacks the post")]
    public async Task R9_36_TheStandingAcceptanceIsFoldedFromTheAcceptEventsTheTestAppended(string scenario)
    {
        var ct = TestContext.Current.CancellationToken;
        var seeded = await SeedAsync(ct);

        switch (scenario)
        {
            case "none":
                break;
            case "one" or "not servable":
                await AcceptNamedAsync(seeded, Answer1, ct);
                break;
            case "second wins":
                await AcceptNamedAsync(seeded, Answer1, ct);
                await AcceptNamedAsync(seeded, Answer2, ct);
                break;
            case "model lacks the post":
                await AcceptNamedAsync(seeded, Ghost, ct);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "not a scenario this fact builds");
        }

        // What the log says, written from the events this fact appended and the digests it was handed.
        (string? Digest, string? PostId, bool IsNone) expected = scenario switch
        {
            "none" or "model lacks the post" => (null, null, true),
            "one" => (seeded.Digest1, Answer1, false),
            "second wins" => (seeded.Digest2, Answer2, false),
            "not servable" => (seeded.Digest1, null, false),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "not a scenario this fact builds"),
        };

        var actual = await StandingAsync(seeded, id => scenario != "not servable" || id != Answer1, ct);

        Assert.Equal(expected, actual);
    }

    /// <summary>Control for the "not servable" row: the same acceptance, every post servable, names the answer by id as well as by digest.</summary>
    [Fact]
    public async Task R9_36_AnAcceptedAnswerTheForumServesIsNamedByDigestAndByPostId()
    {
        var ct = TestContext.Current.CancellationToken;
        var seeded = await SeedAsync(ct);
        await AcceptNamedAsync(seeded, Answer1, ct);

        Assert.Equal((seeded.Digest1, Answer1, false), await StandingAsync(seeded, _ => true, ct));
    }

    /// <summary>Control for the rows above: a question nobody accepted an answer to says none, and says it without a digest.</summary>
    [Fact]
    public async Task R9_36_AQuestionWithNoAcceptanceNamesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var seeded = await SeedAsync(ct);

        Assert.Equal((null, null, true), await StandingAsync(seeded, _ => true, ct));
    }
}
