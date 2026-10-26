using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using Curia.Api.Tests.Fuzz;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Mcp;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R11.53 (errata G40, closing D61 item 20): the number <c>curia_recheck</c>'s <c>tools/list</c> description states is the route's cap, and the Forum's
/// own refusal of one digest more names that same cap. <b>The cap is not derived from the description</b>: it is <see cref="ForumEndpoints.BatchCap"/>, and
/// the live refusal is read from a real Forum on a real socket. This fact is the only thing that holds the description's literal to the route.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class RecheckCapEndToEndTests(KestrelForumFixture forum) : IClassFixture<KestrelForumFixture>, IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("curia-recheck-cap-").FullName;

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    [GeneratedRegex(@"\b(?<cap>\d+) digests per call\b", RegexOptions.CultureInvariant)]
    private static partial Regex CapSentence();

    [GeneratedRegex(@"\bcap=(?<cap>\d+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex RefusalCap();

    private static string Digest(int i) => "sha256:" + i.ToString("x", CultureInfo.InvariantCulture).PadLeft(64, '0');

    [Fact]
    public async Task R11_53_TheDescriptionsCapIsTheRoutesCapAndTheLiveRefusalsCap()
    {
        var cap = ForumEndpoints.BatchCap;
        var client = new ForumClient(forum.Client, forum.Client.BaseAddress!);
        await using var session = await McpProtocolSession.StartAsync(new ForumTools(client, MarkingMode.Datamark, new HeadStore(_home)), null);

        var description = (await session.ListAsync()).Single(t => t.Name == "curia_recheck").Description ?? string.Empty;

        // The description states the route's number, and says a Forum states the cap it enforces in its refusal.
        var stated = CapSentence().Match(description);
        Assert.True(
            stated.Success && stated.Groups["cap"].Value == cap.ToString(CultureInfo.InvariantCulture),
            $"Expected description to contain \"{cap} digests per call\"; the description read:\n{description}");
        Assert.Matches(@"(?is)refusal", description);

        // Control: the cap's worth of digests is served, and one more is refused whole with the route's cap in the refusal.
        var served = await session.CallAsync("curia_recheck", new() { ["digests"] = Enumerable.Range(1, cap).Select(Digest).ToArray() });
        Assert.False(served.IsError, served.Text);

        var refused = await session.CallAsync("curia_recheck", new() { ["digests"] = Enumerable.Range(1, cap + 1).Select(Digest).ToArray() });
        Assert.True(refused.IsError, refused.Text);
        var live = RefusalCap().Match(refused.Text);
        Assert.True(live.Success, $"the Forum's refusal of {cap + 1} digests names no cap:\n{refused.Text}");
        Assert.Equal(cap.ToString(CultureInfo.InvariantCulture), live.Groups["cap"].Value);
    }
}
