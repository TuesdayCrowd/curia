using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Curia.Application.Retrieval;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Json;
using Curia.Domain.Content;
using Curia.Domain.Search;
using Xunit;
using static Curia.Application.Tests.Retrieval.RetrievalTestLog;

namespace Curia.Application.Tests.Retrieval;

[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class DuplicateCheckTests
{
    private sealed record Harness(InMemoryEventStore Store, EmbeddingIndexer Indexer, DuplicateCheck Check);

    private static Harness NewHarness(DuplicateThresholds? thresholds = null)
    {
        var embedder = new HashedNGramEmbedder();
        var index = new InMemoryVectorIndex();
        return new Harness(new InMemoryEventStore(Clock()), new EmbeddingIndexer(embedder, index), new DuplicateCheck(embedder, index, thresholds ?? DuplicateThresholds.Published));
    }

    private static PostEnvelope Envelope(PostKind kind, string title, string body, string board = "postgres", bool? notDuplicate = null, string? rationale = null)
    {
        var json = Canonical(kind, "https://agents.example/asker", board, kind == PostKind.Answer ? null : title, body, [], kind == PostKind.Answer ? "q0" : null);
        var root = (JsonValue.Object)Require(JsonReader.ParseUnrestricted(System.Text.Encoding.UTF8.GetBytes(json)));
        if (notDuplicate is { } nd)
        {
            var members = root.Members.ToList();
            members.Add(new("not_duplicate", new JsonValue.Bool(nd)));
            if (rationale is not null) members.Add(new("duplicate_rationale", new JsonValue.String(rationale)));
            root = new JsonValue.Object([.. members]);
        }
        return Require(PostEnvelope.Read(root));
    }

    private static async Task<DuplicateAssessment> AssessAsync(Harness h, PostEnvelope envelope, CancellationToken ct) =>
        Require(await h.Check.AssessAsync(await LogAsync(h.Store, ct).ConfigureAwait(false), envelope, ct).ConfigureAwait(false));

    [Fact]
    public async Task R8_18_TheSameQuestionOnTheSameBoardIsRefusedAndTheCanonicalIsNamed()
    {
        var ct = TestContext.Current.CancellationToken;
        var h = NewHarness();
        var original = await AcceptAsync(h.Store, h.Indexer, "orig", ct, title: "ECONNRESET from npgsql", body: "npgsql throws ECONNRESET after the pooler idles the connection");

        var verdict = await AssessAsync(h, Envelope(PostKind.Question, "ECONNRESET from npgsql", "npgsql throws ECONNRESET after the pooler idles the connection"), ct);

        Assert.Equal(DuplicateVerdict.Refuse, verdict.Verdict);
        Assert.Equal(original, verdict.Nearest!.Digest);
        Assert.Equal(1.0, verdict.Cosine, 1e-6);
        Assert.Equal(1.0, verdict.LexicalOverlap, 1e-12);
        Assert.Equal(HashedNGramEmbedding.Model, verdict.Model);
        Assert.Equal(DuplicateThresholds.Published, verdict.Thresholds);
    }

    [Fact]
    public async Task R8_18_ARewordingWithLittleSharedVocabularyIsNotRefusedByTheHashedEmbedder()
    {
        var ct = TestContext.Current.CancellationToken;
        var h = NewHarness();
        await AcceptAsync(h.Store, h.Indexer, "orig", ct, title: "ECONNRESET on keep-alive sockets", body: "the socket is reset while it is kept alive");

        var verdict = await AssessAsync(h, Envelope(PostKind.Question, "connection reset by peer", "a pooled connection comes back reset when reused"), ct);

        // This is the hashed embedder being honest about what it is: a lexical geometry, not a
        // semantic one. conformance/retrieval/ measures exactly this gap.
        Assert.NotEqual(DuplicateVerdict.Refuse, verdict.Verdict);
    }

    [Fact]
    public async Task R8_60_AnIdenticalAnswerIsAnnotatedNeverRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var h = NewHarness();
        var original = await AcceptAsync(h.Store, h.Indexer, "a1", ct, kind: PostKind.Answer, title: null, body: "set KeepAlive=30 in the connection string", parent: "q0");

        var verdict = await AssessAsync(h, Envelope(PostKind.Answer, "", "set KeepAlive=30 in the connection string"), ct);

        Assert.Equal(DuplicateVerdict.Annotate, verdict.Verdict);
        Assert.Equal(original, verdict.Nearest!.Digest);
    }

    [Fact]
    public async Task R8_20_ASignedOverrideDowngradesTheRefusalToAnAnnotation()
    {
        var ct = TestContext.Current.CancellationToken;
        var h = NewHarness();
        await AcceptAsync(h.Store, h.Indexer, "orig", ct, title: "ECONNRESET from npgsql", body: "npgsql throws ECONNRESET after the pooler idles the connection");

        var verdict = await AssessAsync(h, Envelope(PostKind.Question, "ECONNRESET from npgsql", "npgsql throws ECONNRESET after the pooler idles the connection",
            notDuplicate: true, rationale: "that thread is about pgbouncer; mine is the built-in pool"), ct);

        Assert.Equal(DuplicateVerdict.Annotate, verdict.Verdict);
        Assert.True(verdict.Overridden);
    }

    [Fact]
    public async Task R8_60_ADifferentBoardIsADifferentAudience()
    {
        var ct = TestContext.Current.CancellationToken;
        var h = NewHarness();
        await AcceptAsync(h.Store, h.Indexer, "orig", ct, board: "postgres", title: "ECONNRESET from npgsql", body: "npgsql throws ECONNRESET after the pooler idles the connection");

        var verdict = await AssessAsync(h, Envelope(PostKind.Question, "ECONNRESET from npgsql", "npgsql throws ECONNRESET after the pooler idles the connection", board: "dotnet"), ct);

        Assert.Equal(DuplicateVerdict.None, verdict.Verdict);
        Assert.Null(verdict.Nearest);
        Assert.Equal(0, verdict.CandidatesConsidered);
    }

    [Fact]
    public async Task R8_21_TheThresholdsAreTheCheckedInOnesUnlessConfigured()
    {
        var ct = TestContext.Current.CancellationToken;
        var strict = NewHarness(new DuplicateThresholds(1.0, 1.0, 1.0));
        await AcceptAsync(strict.Store, strict.Indexer, "orig", ct, title: "ECONNRESET from npgsql", body: "npgsql throws ECONNRESET after the pooler idles the connection");

        var verdict = await AssessAsync(strict, Envelope(PostKind.Question, "ECONNRESET from npgsql", "npgsql throws ECONNRESET after the pooler idles the connection now"), ct);
        Assert.Equal(DuplicateVerdict.None, verdict.Verdict);
        Assert.Throws<ArgumentException>(() => new DuplicateCheck(new HashedNGramEmbedder(), new InMemoryVectorIndex(), new DuplicateThresholds(0.5, 0.5, 0.9)));
    }
}
