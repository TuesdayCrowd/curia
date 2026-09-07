using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using Curia.Api;
using Curia.Domain.Serving;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R14.3's property P22, as R14.9 requires it be gated: <i>"Any API path or format parameter
/// returning content without its provenance block → test failure."</i>
///
/// <para><b>This gate did not exist.</b> Phases 1 to 3 shipped with P22 named in R14.3 and nothing
/// implementing it — the only occurrence of "P22" in the tree was an unrelated comment about
/// duplicate normalized keys in canonicalization. R11.18 is P22 applied to MCP, so the adapter is
/// what made its absence worth closing.</para>
///
/// <para><b>The enumeration is derived, not written.</b> Every route comes from the running host's
/// <see cref="EndpointDataSource"/> — the registrations themselves. R14.9's reason is worth quoting,
/// because it is the whole design: <i>"A gate whose scope is hand-written does not report a surface
/// it never heard of — it reports that every surface it heard of passed, which is the same sentence
/// with none of the meaning."</i></para>
///
/// <para><b>And an unclassified route is a failure, never a pass.</b> There is no default. A
/// twenty-fourth route registered without <see cref="ServingSurfaceExtensions.Serves"/> fails by
/// name. An opt-in attribute whose absence meant "serves nothing" would let a new content route
/// ship unnoticed, which R14.9 names as the disguised version of this gate.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class PropertyP22GateTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    private static string Unique(string stem) => $"https://agents.example/{stem}-{Guid.NewGuid().ToString("N")[..8]}";

    private sealed record Surface(string Method, string Route, ServingSurface? Classification);

    /// <summary>Every route the host registers, read off the endpoint data source.</summary>
    private static List<Surface> Registered(ForumFixture forum)
    {
        var source = forum.Services.GetRequiredService<EndpointDataSource>();
        var surfaces = new List<Surface>();

        foreach (var endpoint in source.Endpoints.OfType<RouteEndpoint>())
        {
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"];
            surfaces.Add(new Surface(
                string.Join(",", methods),
                endpoint.RoutePattern.RawText ?? "(no pattern)",
                endpoint.Metadata.GetMetadata<ServingSurface>()));
        }

        return surfaces;
    }

    /// <summary>
    /// R14.9: "a surface the enumeration reaches but the gate cannot evaluate SHALL be reported as a
    /// failure naming that surface, never omitted".
    /// </summary>
    [Fact]
    public void R14_9_EveryRegisteredRouteCarriesAP22Classification()
    {
        var registered = Registered(forum);

        // The enumeration finding nothing would pass every assertion below it. This is the row that
        // fails when the data source stops yielding routes, rather than the suite going quiet.
        Assert.True(registered.Count >= 20, $"the endpoint enumeration found only {registered.Count} routes");

        var unclassified = registered
            .Where(s => s.Classification is null)
            .Select(s => $"{s.Method} {s.Route}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unclassified.Length == 0,
            "These routes carry no ServingSurface metadata, so property P22's gate cannot evaluate " +
            "them:\n  " + string.Join("\n  ", unclassified) +
            "\nClassify each at its registration with .Serves(...): AgentAuthored if it returns " +
            "agent-authored content, None if it does not, or ExemptFromP22 naming the requirement " +
            "that authorises the exemption.");
    }

    /// <summary>
    /// R14.9: "an exempt surface SHALL be listed against the requirement that exempts it, of which
    /// R6.51 is the only one at the time of writing". A second exemption appearing without an
    /// entry here is a specification change wearing a code change's clothes.
    /// </summary>
    [Fact]
    public void R14_9_TheOnlyExemptionIsR6_51sLogEntryRoute()
    {
        var exempt = Registered(forum)
            .Where(s => s.Classification?.Content is ServedContent.ExemptFromP22)
            .ToArray();

        var route = Assert.Single(exempt);
        Assert.Equal("/v1/log/entries/{index:long}", route.Route);
        Assert.Equal("R6.51", route.Classification!.ExemptedBy);
    }

    /// <summary>
    /// The gate proper, over the content routes it can drive anonymously: content reaches a reader
    /// inside the provenance envelope or it does not reach one at all.
    ///
    /// <para><b>What this cannot yet drive is named rather than skipped.</b> Three content routes
    /// need a signed, DPoP-bound request — <c>POST /v1/posts</c> (whose 409 carries the canonical
    /// thread's answers), <c>POST /v1/posts/batch</c> and <c>GET /v1/inbox</c> — and R14.9 reads as
    /// forbidding classification in lieu of probing. Whether the gate must construct a signed
    /// request for every content-returning surface is G12's own open question, recorded there; until
    /// it is settled this test asserts on what it drives and
    /// <see cref="R14_9_TheUndrivenContentRoutesAreNamedRatherThanForgotten"/> holds the rest where
    /// they can be seen.</para>
    /// </summary>
    [Fact]
    public async Task R14_9_ContentServedAnonymouslyCarriesItsProvenanceEnvelope()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "p22-" + Guid.NewGuid().ToString("N")[..8];
        var postId = await AskAsync(client, board, "P22 gate", "does content carry its envelope", ct);

        var drivable = new (string Route, string Url)[]
        {
            ("/v1/posts/{postId}", $"/v1/posts/{postId}"),
            ("/v1/threads/{rootPostId}", $"/v1/threads/{postId}"),
            ("/v1/boards/{board}/posts", $"/v1/boards/{board}/posts"),
            ("/v1/search", $"/v1/search?q=envelope&board={board}&kind=question"),
        };

        foreach (var (route, url) in drivable)
        {
            using var response = await client.GetAsync(new Uri(url, UriKind.Relative), ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(body);

            // Two failures, kept apart deliberately. "The route returned nothing" and "the route
            // returned content without an envelope" are different defects, and a single
            // carried.Length > 0 assertion reports the second while meaning either -- which is the
            // conflation P22's own subject matter is about, committed inside its gate. The first
            // draft of this test had exactly that, and it was intermittent: a search that happened
            // to match nothing failed claiming a missing provenance block.
            Assert.True(
                body.Contains(postId, StringComparison.Ordinal),
                $"{route} did not return the post this test created, so it cannot say anything " +
                "about whether content carries its envelope. That is a defect in this probe, not " +
                "in the route.");

            var carried = Envelopes(document.RootElement).ToArray();

            Assert.True(
                carried.Length > 0,
                $"{route} returned content with no provenance block. Property P22: content reaches " +
                "a reader inside R10.17's envelope or it does not reach one at all.");

            foreach (var envelope in carried)
            {
                Assert.Equal(Provenance.StandardWarning, envelope.GetProperty("warning").GetString());
                Assert.False(string.IsNullOrWhiteSpace(envelope.GetProperty("author").GetString()));
                Assert.False(string.IsNullOrWhiteSpace(envelope.GetProperty("verification_level").GetString()));
            }
        }
    }

    /// <summary>
    /// The three content routes this gate cannot drive without a credential, held where they can be
    /// seen. R14.9 says a surface the enumeration reaches and the gate cannot evaluate is a failure
    /// naming it — this names them, and the test fails if that set changes, so a fourth cannot join
    /// them quietly and the three cannot be forgotten once the gate learns to sign.
    /// </summary>
    [Fact]
    public void R14_9_TheUndrivenContentRoutesAreNamedRatherThanForgotten()
    {
        var content = Registered(forum)
            .Where(s => s.Classification?.Content is ServedContent.AgentAuthored)
            .Select(s => s.Route)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "/v1/boards/{board}/posts",
                "/v1/inbox",
                "/v1/posts",
                "/v1/posts/batch",
                "/v1/posts/{postId}",
                "/v1/search",
                "/v1/threads/{rootPostId}",
            ],
            content);
    }

    /// <summary>Every provenance block in a response, wherever the shape puts it.</summary>
    private static IEnumerable<JsonElement> Envelopes(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var member in element.EnumerateObject())
                {
                    if (member.NameEquals("provenance") && member.Value.ValueKind is JsonValueKind.Object)
                        yield return member.Value;

                    foreach (var nested in Envelopes(member.Value)) yield return nested;
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    foreach (var nested in Envelopes(item)) yield return nested;

                break;

            default:
                break;
        }
    }

    private async Task<string> AskAsync(
        HttpClient client, string board, string title, string body, CancellationToken ct)
    {
        var agent = ForumAgent.Create(Unique("p22"), "p22-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await agent.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);

        using var response = await dpop.PostAsync(
            client, PostsUrl, token, agent.SignQuestion(board, body, title, forum.Now), forum.Now, ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.GetProperty("post_id").GetString()!;
    }
}
