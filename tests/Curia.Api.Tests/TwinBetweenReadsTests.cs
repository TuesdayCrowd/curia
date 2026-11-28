using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Domain;
using Curia.Domain.Primitives;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>
/// Runs a hook once, just before the first whole-log read a submission request makes: the moment between the authorization read (the author's own rows) and the read the
/// checks and the append's guard are decided on. Reads outside a request pass through.
/// </summary>
internal sealed class HookBeforeWholeLogReader(IEventReader inner, IHttpContextAccessor requests, Func<Task> hook) : IEventReader
{
    private int _fired;

    public Task<Result<IReadOnlyList<AppendedEvent>>> ReadByAggregateAsync(AggregateId aggregateId, CancellationToken cancellationToken = default) =>
        inner.ReadByAggregateAsync(aggregateId, cancellationToken);

    public async Task<Result<IReadOnlyList<AppendedEvent>>> ReadForwardAsync(EventSequence afterSeq, int? maxCount = null, CancellationToken cancellationToken = default)
    {
        if (requests.HttpContext is { Request.Path.Value: "/v1/posts" } && Interlocked.Exchange(ref _fired, 1) == 0)
            await hook().ConfigureAwait(false);

        return await inner.ReadForwardAsync(afterSeq, maxCount, cancellationToken).ConfigureAwait(false);
    }

    public Task<Curia.Application.Reads.LogSlice> ReadAsync(Curia.Application.Reads.ReadPlan plan, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(plan, cancellationToken);
}

/// <summary>
/// R8.78, R8.86, RVO45's window made deterministic. An identical comment that commits after a request's authorization read and before its whole-log read is in the second read
/// and before its position, so the append's guard (which judges only what follows the position) cannot see it: the route must check the digest on the read it takes its
/// position from. Before that check, two events of one digest were stored when a twin landed in the window.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class TwinBetweenReadsTests(PassThroughGateForumFixture forum) : IClassFixture<PassThroughGateForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task R8_86_TWB1_ATwinThatCommitsBetweenTheRoutesReadsIsRefusedAndTheDigestIsStoredOnce()
    {
        var board = Board();
        var author = await AcceptedAnswerScene.T1Async(forum, board, Ct);
        var question = await AskAsync(author, board);
        var wire = author.Agent.Sign(Curia.Domain.Content.PostKind.Comment, board, "A comment " + Unrelated(), null, question.PostId, forum.Now);
        var token = await author.Dpop.GetTokenAsync(forum.Client, TokenEndpoint, forum.Now, Ct);

        Sent? twin = null;
        await using var host = forum.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddHttpContextAccessor();
            services.AddSingleton<IEventReader>(sp => new HookBeforeWholeLogReader(
                sp.GetRequiredService<IEventStore>(),
                sp.GetRequiredService<IHttpContextAccessor>(),
                async () => twin = await SendAsync(forum, author, wire, token, Ct)));
        }));
        using var client = host.CreateClient();

        var second = await SendViaAsync(forum, client, author, wire, token, Ct);
        var digest = (twin ?? throw new Xunit.Sdk.XunitException("the hook never fired")).Digest!;
        var events = await EventsWithDigestAsync(forum, digest);

        Said("201 | 409 curia/ingest/already-recorded | 1 events", $"{twin.Status} | {second.Says} | {events} events");
    }

    private Task<ScenePost> AskAsync(SceneActor actor, string board) => RevisionProbe.AskAsync(forum, actor, board, Ct);
}
