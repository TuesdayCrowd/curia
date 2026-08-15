namespace Curia.Infrastructure.Tests;

/// <summary>
/// Minimal settable <see cref="TimeProvider"/> for CS-9's "the clock port is the only source of
/// time" tests. Duplicated from <c>Curia.Application.Tests.ManualTimeProvider</c> rather than
/// shared: that copy is <see langword="internal"/>, and each test project in this solution
/// keeps this dozen-line helper self-contained (mirrors <c>Curia.AuthN.Tests</c>' and
/// <c>Curia.Domain.Primitives.Tests</c>' own copies) rather than growing a shared test-utility
/// project for one small type.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Set(DateTimeOffset instant) => _now = instant;
}
