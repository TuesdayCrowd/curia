namespace Curia.AuthN.Tests;

/// <summary>
/// Minimal settable <see cref="TimeProvider"/> for CS-9's "time enters only through the clock
/// port" tests -- every expiry, skew, and nonce-freshness assertion in this project sets an
/// exact instant rather than sleeping. Mirrors (does not reference -- CS-5 forbids sharing
/// internals between production assemblies, and there is no established mechanism for sharing
/// them between two *test* projects either) <c>Curia.Application.Tests.ManualTimeProvider</c>.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Set(DateTimeOffset instant) => _now = instant;

    public void Advance(TimeSpan by) => _now += by;
}
