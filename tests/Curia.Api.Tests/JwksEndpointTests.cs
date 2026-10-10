using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json.Nodes;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// <c>GET /v1/jwks</c>'s own answers to a request that names no agent (R11.34, errata G18). The route
/// bound a non-nullable <c>string agent</c>, so with the parameter absent the binder answered
/// <c>curia/request/unreadable</c> before the handler's <c>curia/keys/agent-required</c> was reached
/// (the exercise's audit, finding 5): the one answer PR B changes that was not a ledger row.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class JwksEndpointTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    [Fact]
    public async Task R11_34_AJwksRequestWithNoAgentIsAskedForOne()
    {
        var ct = TestContext.Current.CancellationToken;

        using var response = await forum.Client.GetAsync(new Uri("/v1/jwks", UriKind.Relative), ct);
        var type = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?["type"]?.GetValue<string>();

        Assert.Equal((HttpStatusCode.BadRequest, "curia/keys/agent-required"), (response.StatusCode, type));
    }
}
