using System.Diagnostics.CodeAnalysis;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// R9.30 (errata G30) at the adapter: <c>curia_search</c> states the floor's two counts beside the result count, and a count
/// the Forum did not serve is <c>not stated</c> and never <c>0</c>. <see cref="StubLog"/>'s page serves
/// <c>excluded_disputed</c> and no <c>excluded_by_floor</c>, which is the Forum build this fact needs.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class SearchFloorLineTests : IDisposable
{
    private readonly StubLog _log = new();
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-floor-").FullName;

    public void Dispose()
    {
        _log.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task R9_30_AMissingCountIsNotStatedNeverZero()
    {
        var tools = new ForumTools(_log.Client(), MarkingMode.None, new HeadStore(_home));

        var result = await tools.SearchAsync(new SearchCriteria(), TestContext.Current.CancellationToken);

        var first = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        Assert.Contains("results=1", first, StringComparison.Ordinal);
        Assert.Contains("excluded_by_floor=not stated excluded_disputed=0", first, StringComparison.Ordinal);
    }
}
