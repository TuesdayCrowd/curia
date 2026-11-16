using Xunit;

namespace Curia.Api.Tests;

/// <summary>FCI5, a throwaway probe branch never merged: one failing fact in a class the rest leg runs.</summary>
public sealed class ProbeFci5Tests
{
    [Fact]
    public void ThisFactFailsOnPurpose() => Assert.Fail("FCI5: a deliberate failure in the rest leg");
}
