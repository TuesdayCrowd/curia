using System.Diagnostics.CodeAnalysis;
using System.Net;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Domain;
using Curia.Domain.Authorization;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;
using Curia.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

using Probe = Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>
/// A Forum host for errata G49's seam facts: its three gates counted (an entry at any of them is a read that was gated), its whole-log read, its flag store and
/// its policy decision point made to fail or to deny on demand. Every decorator forwards to the real object; none fabricates an answer except where the
/// switch says so, and a switch left set would be a defect of the fact, so each fact puts it back in a <c>finally</c>.
/// </summary>
public sealed class BudgetSeamsForumFixture : ForumFixture
{
    private int _agentEntries;
    private int _flagEntries;
    private int _revisionEntries;

    internal int AgentEntries => Volatile.Read(ref _agentEntries);

    internal int FlagEntries => Volatile.Read(ref _flagEntries);

    internal int RevisionEntries => Volatile.Read(ref _revisionEntries);

    /// <summary>When set, the whole-log read fails while a per-aggregate read (the token validator's) works.</summary>
    internal volatile bool FailLogReads;

    /// <summary>When set, the flag store cannot be read.</summary>
    internal volatile bool FailFlagStore;

    /// <summary>When set, the policy decision point denies the <c>budget</c> kind.</summary>
    internal volatile bool DenyBudget;

    internal void Reset()
    {
        FailLogReads = false;
        FailFlagStore = false;
        DenyBudget = false;
        Interlocked.Exchange(ref _agentEntries, 0);
        Interlocked.Exchange(ref _flagEntries, 0);
        Interlocked.Exchange(ref _revisionEntries, 0);
    }

    private static ServiceDescriptor Registered<T>(IServiceCollection services)
    {
        var registered = services.Last(d => d.ServiceType == typeof(T));
        if (registered.ImplementationFactory is null)
            throw new InvalidOperationException($"{typeof(T).Name} is registered by a factory; this decorator wraps what it builds");

        return registered;
    }

    protected override void ConfigureStore(IServiceCollection services)
    {
        var store = Registered<IEventStore>(services);
        services.Remove(store);
        services.AddSingleton<IEventStore>(sp => new FailingStore((IEventStore)store.ImplementationFactory!(sp), () => FailLogReads));

        var details = Registered<IFlagDetailStore>(services);
        services.Remove(details);
        services.AddSingleton<IFlagDetailStore>(sp => new FailingDetails((IFlagDetailStore)details.ImplementationFactory!(sp), () => FailFlagStore));

        var decisions = Registered<IPolicyDecisionPoint>(services);
        services.Remove(decisions);
        services.AddSingleton<IPolicyDecisionPoint>(sp => new DenyingDecisions((IPolicyDecisionPoint)decisions.ImplementationFactory!(sp), () => DenyBudget));

        var agentGate = Registered<IAgentWriteGate>(services);
        services.Remove(agentGate);
        services.AddSingleton<IAgentWriteGate>(sp => new CountingAgentGate((IAgentWriteGate)agentGate.ImplementationFactory!(sp), () => Interlocked.Increment(ref _agentEntries)));

        var flagGate = Registered<IFlagRaiserGate>(services);
        services.Remove(flagGate);
        services.AddSingleton<IFlagRaiserGate>(sp => new CountingFlagGate((IFlagRaiserGate)flagGate.ImplementationFactory!(sp), () => Interlocked.Increment(ref _flagEntries)));

        var revisionGate = Registered<IRevisionGate>(services);
        services.Remove(revisionGate);
        services.AddSingleton<IRevisionGate>(sp => new CountingRevisionGate((IRevisionGate)revisionGate.ImplementationFactory!(sp), () => Interlocked.Increment(ref _revisionEntries)));
    }

    private sealed class FailingStore(IEventStore inner, Func<bool> fail) : IEventStore
    {
        public Task<Result<IReadOnlyList<AppendedEvent>>> AppendAsync(AggregateId aggregateId, AggregateVersion expectedVersion, IReadOnlyList<DomainEvent> events, CancellationToken cancellationToken = default) =>
            inner.AppendAsync(aggregateId, expectedVersion, events, cancellationToken);

        public Task<Result<IReadOnlyList<AppendedEvent>>> AppendAsync(AggregateId aggregateId, AggregateVersion expectedVersion, IReadOnlyList<DomainEvent> events, StreamCondition condition, CancellationToken cancellationToken = default) =>
            inner.AppendAsync(aggregateId, expectedVersion, events, condition, cancellationToken);

        public Task<Result<IReadOnlyList<AppendedEvent>>> ReadByAggregateAsync(AggregateId aggregateId, CancellationToken cancellationToken = default) =>
            inner.ReadByAggregateAsync(aggregateId, cancellationToken);

        public Task<Result<IReadOnlyList<AppendedEvent>>> ReadForwardAsync(EventSequence afterSeq, int? maxCount = null, CancellationToken cancellationToken = default) =>
            fail()
                ? Task.FromResult(Result<IReadOnlyList<AppendedEvent>>.Fail(new Error("curia/test/log-unreadable", "The test store cannot read the whole log")))
                : inner.ReadForwardAsync(afterSeq, maxCount, cancellationToken);
    }

    private sealed class FailingDetails(IFlagDetailStore inner, Func<bool> fail) : IFlagDetailStore
    {
        public Task<Result<FlagDetail>> AppendAsync(FlagDetail detail, CancellationToken cancellationToken = default) => inner.AppendAsync(detail, cancellationToken);

        public Task<Result<IReadOnlyList<FlagDetail>>> ReadAllAsync(CancellationToken cancellationToken = default) =>
            fail()
                ? Task.FromResult(Result<IReadOnlyList<FlagDetail>>.Fail(new Error("curia/test/flag-store-unreadable", "The test store cannot read the flag rows")))
                : inner.ReadAllAsync(cancellationToken);
    }

    private sealed class DenyingDecisions(IPolicyDecisionPoint inner, Func<bool> deny) : IPolicyDecisionPoint
    {
        public ValueTask<Result<AuthorizationDecision>> EvaluateAsync(AuthorizationRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            return deny() && string.Equals(request.Resource.ToString(), "Budget", StringComparison.Ordinal)
                ? ValueTask.FromResult(Result<AuthorizationDecision>.Ok(new AuthorizationDecision(DecisionEffect.Deny, "table-10/denied", GrantQualifier.None)))
                : inner.EvaluateAsync(request, cancellationToken);
        }
    }

    private sealed class CountingAgentGate(IAgentWriteGate inner, Action hit) : IAgentWriteGate
    {
        public Task<Result<IAsyncDisposable>> EnterAsync(AgentId agent, CancellationToken cancellationToken)
        {
            hit();
            return inner.EnterAsync(agent, cancellationToken);
        }
    }

    private sealed class CountingFlagGate(IFlagRaiserGate inner, Action hit) : IFlagRaiserGate
    {
        public Task<Result<IAsyncDisposable>> TryEnterAsync(string raisedBy, CancellationToken cancellationToken)
        {
            hit();
            return inner.TryEnterAsync(raisedBy, cancellationToken);
        }
    }

    private sealed class CountingRevisionGate(IRevisionGate inner, Action hit) : IRevisionGate
    {
        public Task<Result<IAsyncDisposable>> TryEnterAsync(string prevDigest, CancellationToken cancellationToken)
        {
            hit();
            return inner.TryEnterAsync(prevDigest, cancellationToken);
        }
    }
}

/// <summary>
/// Errata G49 against stores and decisions that fail or deny: SEB26 (an unreadable flag store is never zero), SEB29 (the policy decides), SEB30 (no gate is
/// entered) and SEB53 (an unreadable log is never zeros and never a tier denial). Each fact puts its switch back in a <c>finally</c> and carries its
/// healthy twin in the same fact.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class BudgetReadoutSeamsEndpointTests(BudgetSeamsForumFixture forum) : IClassFixture<BudgetSeamsForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// SEB26 (the flag-store leg only; ruling 19). A failing flag store is <c>503</c> with R9.50's type and no budget member, never a <c>200</c> with a flag
    /// count of zero; the healthy host answers <c>200</c> in the same fact.
    /// </summary>
    [Fact]
    public async Task R9_50_SEB26_AnUnreadableFlagStoreIsNeverZero()
    {
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "flagstore", Ct);
        try
        {
            forum.FailFlagStore = true;
            var failing = await BudgetReadouts.GetAsync(forum, actor);
            forum.FailFlagStore = false;
            var healthy = await BudgetReadouts.GetAsync(forum, actor);

            Assert.True(
                failing.Status == HttpStatusCode.ServiceUnavailable && failing.Type == PublishedBudgetReadout.FlagStoreType() && !failing.Raw.Contains("\"budgets\"", StringComparison.Ordinal),
                $"Expected: 503 {PublishedBudgetReadout.FlagStoreType()} and no budget member Actual: {failing.Says}");
            Assert.True(healthy.Status == HttpStatusCode.OK, $"Expected: 200 from the healthy host Actual: {healthy.Says}");
        }
        finally
        {
            forum.Reset();
        }
    }

    /// <summary>
    /// SEB53. An unreadable log is never zeros and never a tier denial: the whole-log read fails while the per-aggregate read (the token validator's) works;
    /// the readout is <c>503</c> with R9.50's type, served without detail, with no budget member, and never <c>200</c> or a <c>403</c> naming the Anonymous tier;
    /// the healthy host answers <c>200</c> in the same fact.
    /// </summary>
    [Fact]
    public async Task R9_50_SEB53_AnUnreadableLogIsNeverZerosAndNeverATierDenial()
    {
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "logread", Ct);
        var token = await BudgetReadouts.TokenAsync(forum, actor);
        try
        {
            forum.FailLogReads = true;
            var failing = await BudgetReadouts.GetAsync(forum, actor, token: token);
            forum.FailLogReads = false;
            var healthy = await BudgetReadouts.GetAsync(forum, actor);

            var detail = failing.Body.ValueKind == System.Text.Json.JsonValueKind.Object && failing.Body.TryGetProperty("detail", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.String ? d.GetString() : null;
            Assert.True(
                failing.Status == HttpStatusCode.ServiceUnavailable && failing.Type == PublishedBudgetReadout.LogUnreadableType() && detail is null && !failing.Raw.Contains("\"budgets\"", StringComparison.Ordinal),
                $"Expected: 503 {PublishedBudgetReadout.LogUnreadableType()} with no detail and no budget member Actual: {failing.Says}");
            Assert.True(healthy.Status == HttpStatusCode.OK, $"Expected: 200 from the healthy host Actual: {healthy.Says}");
        }
        finally
        {
            forum.Reset();
        }
    }

    /// <summary>
    /// SEB29. The policy decides: a decision point that denies <c>budget</c>/<c>read</c> yields <c>403</c> with the policy's reason and no budget member;
    /// the real decision point allows in the same fact. (The clause "a model with no such pair yields a failure and never an allow" is SEB9's: it needs the
    /// Table 10 kind, which does not exist on the seam tree.)
    /// </summary>
    [Fact]
    public async Task R9_50_SEB29_ThePolicyDecides()
    {
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "denied", Ct);
        try
        {
            var allowed = await BudgetReadouts.GetAsync(forum, actor);
            forum.DenyBudget = true;
            var denied = await BudgetReadouts.GetAsync(forum, actor);

            Assert.True(allowed.Status == HttpStatusCode.OK, $"Expected: 200 from the real decision point Actual: {allowed.Says}");
            Assert.True(
                denied.Status == HttpStatusCode.Forbidden && denied.Raw.Contains("table-10/denied", StringComparison.Ordinal) && !denied.Raw.Contains("\"budgets\"", StringComparison.Ordinal),
                $"Expected: 403 with the policy's reason and no budget member Actual: {denied.Says}");
        }
        finally
        {
            forum.Reset();
        }
    }

    /// <summary>
    /// SEB30. No gate is entered: after a readout (asserted <c>200</c> in this fact, so the fact cannot pass on an unmapped route) the agent, flag and revision
    /// gates recorded nothing; a post by the same agent records exactly one agent-gate entry (the positive).
    /// </summary>
    [Fact]
    public async Task R7_28_SEB30_NoGateIsEntered()
    {
        var acts = await BudgetActor.T0Async(forum, "posting", Ct);
        forum.Reset();
        try
        {
            var reply = await BudgetReadouts.GetAsync(forum, acts.Actor);
            Assert.True(reply.Status == HttpStatusCode.OK, $"Expected: 200 Actual: {reply.Says}");
            Probe.Said("agent 0 flag 0 revision 0", $"agent {forum.AgentEntries} flag {forum.FlagEntries} revision {forum.RevisionEntries}");

            Assert.Equal(201, (await acts.NextAsync()).Status);
            Assert.Equal(1, forum.AgentEntries);
        }
        finally
        {
            forum.Reset();
        }
    }
}
