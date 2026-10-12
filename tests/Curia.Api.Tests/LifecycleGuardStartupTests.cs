using System.Diagnostics.CodeAnalysis;
using System.Net;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Primitives;
using Curia.Infrastructure;
using Curia.Infrastructure.Lifecycle;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// GF8 (R4.38 (add.), lock 2): the Forum fails to start, loudly and by name, when
/// <c>Curia:Lifecycle:Enabled</c> names a kind the build does not approve, enables rotation or
/// retirement without compromise, or is not <c>none</c> or a comma list over the three kinds. Each case
/// runs <c>Program.cs</c>'s own composition over the embedded (empty) record. Logging and continuing
/// does not count: the host's start throws.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class LifecycleGuardStartupTests(GuardedForumFixture forum) : IClassFixture<GuardedForumFixture>
{
    private const string Setting = "Curia:Lifecycle:Enabled";

    [Fact]
    public void R4_38_StartupRefusesAnUnapprovedKind() =>
        AssertStartRefused("compromise", LifecycleApprovalErrors.KindNotApprovedType);

    [Fact]
    public void R4_38_StartupRefusesRotationWithoutCompromise() =>
        AssertStartRefused("rotation", LifecycleApprovalErrors.CompromiseOrderType);

    [Fact]
    public void R4_38_StartupRefusesAnUnreadableOptIn() =>
        AssertStartRefused("rotation;compromise", LifecycleApprovalErrors.OptInUnreadableType);

    /// <summary>The control: the opt-in <c>none</c> starts, and the started Forum serves.</summary>
    [Fact]
    public async Task R4_38_StartupWithOptInNoneStarts()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = forum.WithWebHostBuilder(builder => builder.UseSetting(Setting, "none"));
        using var client = host.CreateClient();

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// CP-A: the host <c>Program.cs</c> composes, with no <c>Curia:Lifecycle:Enabled</c> setting, resolves
    /// the production gate, which enables nothing; and the event store the host actually holds enforces a
    /// gate that enables nothing (a rotation-shaped append through the resolved store is refused, the stream
    /// unchanged). Red when <c>Program.cs</c> hands the event store a gate other than the
    /// <c>FromEmbedded</c> one (FL-20).
    /// </summary>
    [Fact]
    public async Task R4_38_TheProductionCompositionsGateApprovesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = forum.Services.GetRequiredService<ILifecycleAppendGate>();

        Assert.IsType<EmbeddedLifecycleAppendGate>(gate);
        foreach (var kind in Enum.GetValues<LifecycleKind>())
            Assert.False(gate.Enables(kind), $"the production composition's gate enables {kind}");

        var store = forum.Services.GetRequiredService<IEventStore>();
        var id = "agent://example/guarded-" + Guid.NewGuid().ToString("N")[..12];
        var stream = Require(AggregateId.Create(id));
        var appended = await store.AppendAsync(stream, AggregateVersion.New, [KeyBound(id)], ct);

        Assert.Equal(DomainErrors.LifecycleAppendRefusedType, appended.Match(_ => string.Empty, e => e.Type));
        Assert.Empty(Require(await store.ReadByAggregateAsync(stream, ct)));
    }

    /// <summary>GF4's HTTP half: enrolment over HTTP still appends the pair under the production composition.</summary>
    [Fact]
    public async Task R4_38_EnrollmentStillAppendsItsPairUnderTheGuardOverHttp()
    {
        var ct = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agent = ForumAgent.Create("https://agents.example/guarded-" + suffix, "guarded-" + suffix);

        using var enrolled = await agent.EnrollAsync(forum.Client, ct);

        Assert.Equal(HttpStatusCode.Created, enrolled.StatusCode);
        var events = Require(await forum.Services.GetRequiredService<IEventReader>().ReadByAggregateAsync(Require(AggregateId.Create(agent.AgentId)), ct));
        Assert.Equal(
            [AgentStandingProjector.EnrolledType, AgentStandingProjector.KeyBoundType],
            events.Select(e => e.Event.Type.Value).ToArray());
    }

    /// <summary>
    /// The fixture hands out its permissive gate only for the database it created in this run. Red when
    /// the check is removed.
    /// </summary>
    [Fact]
    public void R4_38_APermissiveCompositionRefusesADatabaseItDidNotCreate() =>
        Assert.Throws<InvalidOperationException>(
            () => forum.PermissiveGateFor("Host=198.51.100.10;Database=curia_retained;Username=nobody"));

    internal static DomainEvent KeyBound(string id) => new(
        Require(EventId.Create("evt-" + Guid.NewGuid().ToString("N"))),
        Require(EventType.Create(AgentStandingProjector.KeyBoundType)),
        Require(ActorId.Create(id)),
        new JsonValue.Object([new(AgentStandingProjector.AgentIdField, new JsonValue.String(id))]));

    internal static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} ({e.Detail})"));

    private void AssertStartRefused(string optIn, string errorType)
    {
        using var host = forum.WithWebHostBuilder(builder => builder.UseSetting(Setting, optIn));

        var thrown = Assert.ThrowsAny<Exception>(() => host.CreateClient().Dispose());

        var messages = new List<string>();
        for (Exception? e = thrown; e is not null; e = e.InnerException)
            messages.Add(e.Message);
        Assert.True(
            messages.Any(m => m.Contains(errorType, StringComparison.Ordinal)),
            $"the host's start did not refuse with {errorType}: " + string.Join(" | ", messages));
    }
}

/// <summary>
/// GF10 (R4.38 (add.), spec section 4.13): a guarded Forum whose log holds a lifecycle-kind entry starts,
/// and says so once, with the count. The entry is seeded through the test composition (the fixture's
/// permissive gate) into the fixture's own throwaway database.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class LifecycleEntryScanTests(GuardedForumFixture forum) : IClassFixture<GuardedForumFixture>
{
    private const int EventId7006 = 7006;

    [Fact]
    public async Task R4_38_AGuardedForumOverALifecycleEntryStartsAndSaysSo()
    {
        var ct = TestContext.Current.CancellationToken;
        var id = "agent://example/retired-" + Guid.NewGuid().ToString("N")[..12];
        var adapters = new PostgresAdapters(forum.ConnectionString, forum.Clock, forum.PermissiveGate());
        await using (adapters)
        {
            var retired = new DomainEvent(
                LifecycleGuardStartupTests.Require(EventId.Create("evt-" + Guid.NewGuid().ToString("N"))),
                LifecycleGuardStartupTests.Require(EventType.Create(LifecycleAppendGuard.KeyRetiredType)),
                LifecycleGuardStartupTests.Require(ActorId.Create(id)),
                new JsonValue.Object([new(AgentStandingProjector.AgentIdField, new JsonValue.String(id))]));
            LifecycleGuardStartupTests.Require(
                await adapters.EventStore.AppendAsync(LifecycleGuardStartupTests.Require(AggregateId.Create(id)), AggregateVersion.New, [retired], ct));
        }

        await using var captured = new CapturedForum(forum);

        var scan = captured.Log.Single(0, EventId7006);
        Assert.Equal("1", scan.Value("Count"));
        using var served = await captured.Client.GetAsync(new Uri("/health", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
    }
}

/// <summary>GF10's control: a guarded Forum over a log with no lifecycle entry beyond enrolment pairs says nothing.</summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class LifecycleEntryScanControlTests(GuardedForumFixture forum) : IClassFixture<GuardedForumFixture>
{
    [Fact]
    public async Task R4_38_AGuardedForumOverNoLifecycleEntrySaysNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agent = ForumAgent.Create("https://agents.example/scan-" + suffix, "scan-" + suffix);
        using (var enrolled = await agent.EnrollAsync(forum.Client, ct))
            Assert.Equal(HttpStatusCode.Created, enrolled.StatusCode);

        await using var captured = new CapturedForum(forum);

        Assert.DoesNotContain(captured.Log.All, e => e.EventId == 7006);
    }
}
