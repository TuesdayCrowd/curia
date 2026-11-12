using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// The PR N probes (errata G68) compare one summary string with another, and a red probe's text is what the record keeps: xUnit's own <c>Assert.Equal</c> elides the middle of a long string, so a
/// failure here prints both whole, as <c>Expected: ...  Actual: ...</c>.
/// </summary>
internal static class WholeText
{
    internal static void Same(string expected, string actual) =>
        Assert.True(string.Equals(expected, actual, StringComparison.Ordinal), $"Expected: {expected}  Actual: {actual}");
}
