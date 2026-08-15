namespace Curia.Domain.Primitives.Tests;

/// <summary>
/// Minimal settable <see cref="TimeProvider"/> for CS-9's "the clock port is the only source of
/// time" tests. Deliberately hand-rolled and local to this test project rather than shared from
/// <c>Curia.Application.Tests</c>/<c>Curia.AuthN.Tests</c>'s own copies of the same dozen lines:
/// per CS-5, sharing internals between two test projects is exactly how hexagonal seams rot, and
/// each project's own <see cref="UlidGenerator"/> tests only ever need "what does the generator
/// mint when the clock reads X."
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Set(DateTimeOffset instant) => _now = instant;

    public void Advance(TimeSpan by) => _now += by;
}
