using Curia.Domain.Credentials;
using Curia.Domain.Primitives;

namespace Curia.Domain.Tests.Credentials;

/// <summary>Shared plumbing for the Credentials test suite: a <see cref="Require{T}"/> unwrapper
/// (mirrors <c>DomainEventTests.Require</c>) plus fixed, reusable event-construction fields, since
/// every test here needs to build <see cref="CredentialTransitionedEvent"/> values and none of them
/// care about the actor/reason/timestamp specifics.</summary>
internal static class TestSupport
{
    public static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException(e.Type));

    public static readonly ActorId TestActor = Require(ActorId.Create("actor-1"));
    public static readonly TransitionReason TestReason = Require(TransitionReason.Create("test"));
    public static readonly DateTimeOffset TestTimestamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static CredentialTransitionedEvent Event(CredentialTrigger trigger) =>
        new(trigger, TestActor, TestReason, TestTimestamp);
}
