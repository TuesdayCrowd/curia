using System.Diagnostics.CodeAnalysis;
using Curia.Client.Cli;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R11.48 (errata G34) at <c>curia</c>: a refused <c>--refs</c> item is named by its zero-based index and its value is not echoed, in the
/// real command in a child process. The refusal precedes the profile load (<c>PostAsync</c> parses <c>--refs</c> before
/// <c>store.Load</c>), so the home holds no profile and no Forum is reached; the exit is the usage exit, 1.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class CliRefsIndexTests : IDisposable
{
    private static readonly string Digest = "sha256:" + new string('a', 64);

    private readonly string _home = Directory.CreateTempSubdirectory("curia-cli-refs-index-").FullName;

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("answer")]
    [InlineData("comment")]
    [InlineData("revision")]
    public async Task R11_48_CuriaRefsNamesTheIndexBeforeAnyProfileIsLoaded(string verb)
    {
        var (exit, error) = await HelpTextTests.RunCuriaAsync(
            [verb, "--agent", "nobody", "--board", "b", "--parent", "01TESTPOSTID0000000000000A", "--body", "x", "--refs", $"{Digest},ZQ7BAD,{Digest}"],
            _home,
            TestContext.Current.CancellationToken);

        Assert.Equal(ExitCode.Usage, exit);
        Assert.Contains("refs[1]", error, StringComparison.Ordinal);
        Assert.DoesNotContain("refs[0]", error, StringComparison.Ordinal);
        Assert.DoesNotContain("refs[2]", error, StringComparison.Ordinal);
        Assert.DoesNotContain("ZQ7BAD", error, StringComparison.Ordinal);
    }
}
