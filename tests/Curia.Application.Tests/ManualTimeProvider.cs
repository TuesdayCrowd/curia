namespace Curia.Application.Tests;

/// <summary>
/// Minimal settable <see cref="TimeProvider"/> for CS-9's "the clock port is the only source of
/// time" tests. Deliberately hand-rolled instead of pulling in
/// <c>Microsoft.Extensions.TimeProvider.Testing</c> (the scoping doc sec. 9's package table
/// earmarks that for later phases' richer time-based tests -- token expiry, staleness decay):
/// this store only ever needs "what does the store record when the clock reads X," which a
/// dozen lines already cover, and Stage 1 should not take on a new dependency to answer a
/// question this small.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Set(DateTimeOffset instant) => _now = instant;
}
