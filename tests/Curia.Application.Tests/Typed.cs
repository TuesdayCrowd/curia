using Curia.Domain.Moderation;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;

namespace Curia.Application.Tests;

/// <summary>R11.34 (errata G18): the boundary types a port or a use case takes, built from a test's
/// literal. A literal that is no such type is a broken test, so it throws rather than answering.</summary>
internal static class Typed
{
    public static AgentId Agent(string value) => Require(AgentId.Parse(value));

    public static Kid Kid(string value) => Require(Curia.Domain.Primitives.Boundary.Kid.Parse(value));

    public static PostId Post(string value) => Require(PostId.Parse(value));

    public static ParentReference Parent(string value) => Require(ParentReference.Parse(value));

    public static BoardName Board(string value) => Require(BoardName.Parse(value));

    public static Rationale Rationale(string value) => Require(Curia.Domain.Moderation.Rationale.Parse(value));

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Detail}"));
}
