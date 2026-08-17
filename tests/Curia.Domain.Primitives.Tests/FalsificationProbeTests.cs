namespace Curia.Domain.Primitives.Tests;

/// Temporary: proves CI's test step can fail. Reverted immediately after CI reports red.
public sealed class FalsificationProbeTests
{
    [Fact]
    public void Ci_reports_a_failing_test() => Assert.Equal(1, 2);
}
