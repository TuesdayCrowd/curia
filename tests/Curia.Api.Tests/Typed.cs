using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;

namespace Curia.Api.Tests;

/// <summary>R11.34 (errata G18): the boundary types a use case takes, built from a test's literal. A
/// literal that is no such type is a broken test, so it throws rather than answering.</summary>
internal static class Typed
{
    public static AgentId Agent(string value) => Require(AgentId.Parse(value));

    public static PostId Post(string value) => Require(PostId.Parse(value));

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Detail}"));
}
