using System.Diagnostics.CodeAnalysis;
using System.Net;
using Xunit;

namespace Curia.Api.Tests.Fuzz;

/// <summary>
/// R14.10 with R9.26 (errata G12 item 9): <see cref="SurfaceInventory.RefusedQuery"/> is held to the
/// answer, so a filter the route honours cannot be listed there to escape the fuzzer. It runs on a
/// plain <see cref="ForumFixture"/>, not the fuzzer's, so its requests never enter the closed pass's
/// recorder and cannot discharge that pass's both-ways check for it.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class RefusedQueryTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    [Fact]
    public async Task R14_10_EveryRefusedQueryParameterIsRefusedByItsRoute()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.NotEmpty(SurfaceInventory.RefusedQuery);
        Assert.False(
            SurfaceInventory.RefusedQuery.Any(r => SurfaceInventory.RequestReadQuery.Contains((r.Method, r.Pattern, r.Name))),
            "a name both read and refused by the same route");

        var client = forum.Client;
        foreach (var (method, pattern, name, problemType) in SurfaceInventory.RefusedQuery)
        {
            var entry = $"{method} {pattern} {name}";
            Assert.True(string.Equals(method, "GET", StringComparison.Ordinal), "RefusedQuery has an entry this fact cannot send; extend the sender");
            Assert.True(!pattern.Contains('{', StringComparison.Ordinal), "RefusedQuery has an entry this fact cannot send; extend the sender");

            using var response = await client.GetAsync(new Uri($"{pattern}?q=x&{name}=x", UriKind.Relative), ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{entry}: answered {(int)response.StatusCode}, not 400");
            Assert.True(body.Contains(problemType, StringComparison.Ordinal), $"{entry}: the body does not carry {problemType}");
            Assert.True(body.Contains($"parameter={name}", StringComparison.Ordinal), $"{entry}: the body does not name parameter={name}");
        }
    }
}
