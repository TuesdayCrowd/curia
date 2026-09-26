using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R10.62, R10.44 and errata G3's holding, as a gate: no surface the Forum serves hands a third
/// party a flag's rationale, its raiser, its salt, or — before a moderator adjudicates it — the post it concerns.
///
/// <para><b>The scope is the registrations, never a list beside the test.</b> The two flag-listing
/// routes were held to R10.44 by name, and the log route served every flag in full the whole time
/// (errata G13, finding 2) — trap 15, a classification that classified only what someone thought of.
/// Every route comes from the host's <see cref="EndpointDataSource"/>; a route this gate cannot drive
/// is a failure naming it (R14.9's discipline); and a route it deliberately does not drive is named in
/// <see cref="WriteRoutes"/>, where a reviewer can see it.</para>
///
/// <para><b>The rules are per leaf, never per route or per response.</b> Every JSON object with an
/// <c>event_type</c> is a leaf, on whichever route serves it, and each is judged alone: a page of
/// events holding the raiser's enrolment exempts that one leaf and nothing beside it.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class FlagPrivacyGateTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";
    private const string EntriesRoute = "/v1/log/entries/{index:long}";
    private const string RaisedFlagsRoute = "/v1/flags";

    /// <summary>Routes that write rather than read. A new route is placed here or given a driver, never neither.</summary>
    private static readonly HashSet<string> WriteRoutes = new(StringComparer.Ordinal)
    {
        "POST /v1/agents",
        "POST /v1/posts",
        "POST /v1/posts/{postId}/flags",
        "POST /v1/posts/{postId}/accept",
        "POST /oauth/token",
    };

    private sealed record Party(ForumAgent Agent, DpopClient Dpop, string Token);

    private async Task<Party> PartyAsync(HttpClient client, string stem, CancellationToken ct)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agent = ForumAgent.Create($"https://agents.example/{stem}-{suffix}", $"{stem}-{suffix}");
        var (dpop, token) = await agent.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);
        return new Party(agent, dpop, token);
    }

    private List<(string Method, string Route)> Registered()
    {
        var source = forum.Services.GetRequiredService<EndpointDataSource>();
        var routes = new List<(string, string)>();

        foreach (var endpoint in source.Endpoints.OfType<RouteEndpoint>())
            foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"])
                routes.Add((method, endpoint.RoutePattern.RawText ?? "(no pattern)"));

        return routes;
    }

    /// <summary>The requests that drive one route, or null when this gate has no driver for it.</summary>
    private static List<(string Url, object? Body)>? Drive(
        string method, string route, string postId, string digest, string board, string authorId, long treeSize)
    {
        List<(string, object?)> Each(string prefix) =>
            [.. Enumerable.Range(0, checked((int)treeSize)).Select(i => ($"{prefix}{i}", (object?)null))];

        return (method, route) switch
        {
            ("GET", EntriesRoute) => Each("/v1/log/entries/"),
            ("GET", "/v1/log/proof/{index:long}") => Each("/v1/log/proof/"),
            ("GET", "/v1/log/head") => [("/v1/log/head", null)],
            ("GET", "/v1/log/consistency") => [($"/v1/log/consistency?from=1&to={treeSize}", null)],
            ("GET", "/v1/log/jwks") => [("/v1/log/jwks", null)],
            ("GET", "/v1/posts/{postId}") => [($"/v1/posts/{postId}", null)],
            ("GET", "/v1/threads/{rootPostId}") => [($"/v1/threads/{postId}", null)],
            ("GET", "/v1/boards/{board}/posts") => [($"/v1/boards/{board}/posts", null)],
            ("GET", "/v1/search") => [($"/v1/search?q=ordering&board={board}", null)],
            ("GET", "/v1/inbox") => [($"/v1/inbox?board={board}", null)],
            ("GET", "/v1/flags") => [("/v1/flags", null)],
            ("GET", "/v1/posts/{postId}/flags") => [($"/v1/posts/{postId}/flags", null)],
            ("GET", "/v1/jwks") => [($"/v1/jwks?agent={Uri.EscapeDataString(authorId)}", null)],
            ("GET", "/.well-known/reader-contract/v1") => [("/.well-known/reader-contract/v1", null)],
            ("GET", "/health") => [("/health", null)],
            ("GET", "/oauth/jwks") => [("/oauth/jwks", null)],
            ("GET", "/.well-known/oauth-authorization-server") => [("/.well-known/oauth-authorization-server", null)],
            ("POST", "/v1/posts/batch") => [("/v1/posts/batch", new { digests = new[] { digest } })],
            _ => null,
        };
    }

    /// <summary>One request, anonymously or as <paramref name="who"/>; DPoP's <c>htu</c> excludes the query (RFC 9449 §4.2).</summary>
    private async Task<(HttpStatusCode Status, string Body)> FetchAsync(
        HttpClient client, string url, object? body, Party? who, CancellationToken ct)
    {
        if (body is not null)
        {
            using var posted = await client.PostAsJsonAsync(new Uri(url, UriKind.Relative), body, ct);
            return (posted.StatusCode, await posted.Content.ReadAsStringAsync(ct));
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url, UriKind.Relative));
        if (who is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("DPoP", who.Token);
            request.Headers.Add("DPoP", who.Dpop.Proof("GET", "http://localhost" + url.Split('?')[0], forum.Now, who.Token, nonce: null));
        }

        using var response = await client.SendAsync(request, ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    private static async Task<long> TreeSizeAsync(HttpClient client, CancellationToken ct)
    {
        for (long i = 0; i < 100_000; i++)
        {
            using var response = await client.GetAsync(new Uri($"/v1/log/entries/{i}", UriKind.Relative), ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return i;
        }

        throw new InvalidOperationException("the log did not end within 100,000 entries; this gate would not finish");
    }

    /// <summary>
    /// A question, and a flag against it from an agent that authors nothing, carrying a nonce rationale.
    /// <paramref name="Salt"/> is read back from the private store; null when no row holds the flag.
    /// </summary>
    private sealed record Flagged(
        Party Author, Party Raiser, Party Bystander, string Board, string PostId, string Digest, string Nonce, string? Salt);

    private async Task<Flagged> FlagAQuestionAsync(HttpClient client, CancellationToken ct)
    {
        var board = "gate-" + Guid.NewGuid().ToString("N")[..8];

        var author = await PartyAsync(client, "gate-author", ct);
        var raiser = await PartyAsync(client, "gate-raiser", ct);
        var bystander = await PartyAsync(client, "gate-bystander", ct);

        using var asked = await author.Dpop.PostAsync(
            client, PostsUrl, author.Token,
            author.Agent.SignQuestion(board, "How does JCS order object members?", "Member ordering " + Guid.NewGuid().ToString("N")[..8], forum.Now),
            forum.Now, ct);
        Assert.Equal(HttpStatusCode.Created, asked.StatusCode);

        string postId, digest;
        using (var receipt = JsonDocument.Parse(await asked.Content.ReadAsStringAsync(ct)))
        {
            postId = receipt.RootElement.GetProperty("post_id").GetString()!;
            digest = receipt.RootElement.GetProperty("digest").GetString()!;
        }

        // The raiser authors nothing, so its identifier appearing anywhere but its own enrolment is the flag talking.
        var nonce = "rationale-nonce-" + Guid.NewGuid().ToString("N");
        using var raised = await raiser.Dpop.PostAsync(
            client, $"http://localhost/v1/posts/{postId}/flags", raiser.Token,
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { kind = "spam", rationale = nonce })),
            forum.Now, ct, contentType: "application/json");
        Assert.Equal(HttpStatusCode.Created, raised.StatusCode);

        // The salt is as private as the rationale (R10.62), and only the store it was written to knows it.
        var rows = (await forum.Services.GetRequiredService<IFlagDetailStore>().ReadAllAsync(ct))
            .Match(r => r, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));
        var salt = rows.SingleOrDefault(r =>
            string.Equals(r.RaisedBy, raiser.Agent.AgentId, StringComparison.Ordinal)
            && string.Equals(r.Rationale, nonce, StringComparison.Ordinal))?.Salt;

        return new Flagged(author, raiser, bystander, board, postId, digest, nonce, salt);
    }

    /// <summary>
    /// One leaf of a response: a JSON object carrying an <c>event_type</c>, with every name and value
    /// under it that no nested leaf claims. The entry with a null type holds everything outside every
    /// leaf, and a body that is not JSON is that entry whole.
    /// </summary>
    private sealed record Leaf(string? EventType, string? AggregateId, List<string> Text)
    {
        public bool Mentions(string value) => Text.Exists(t => t.Contains(value, StringComparison.Ordinal));
    }

    private static List<Leaf> Leaves(string served)
    {
        var outside = new Leaf(null, null, []);
        var leaves = new List<Leaf> { outside };

        try
        {
            using var document = JsonDocument.Parse(served);
            Walk(document.RootElement, outside, leaves);
        }
        catch (JsonException)
        {
            outside.Text.Add(served);
        }

        return leaves;
    }

    private static void Walk(JsonElement element, Leaf within, List<Leaf> leaves)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("event_type", out var type) && type.ValueKind == JsonValueKind.String)
                {
                    var aggregate = element.TryGetProperty("aggregate_id", out var id) && id.ValueKind == JsonValueKind.String
                        ? id.GetString()
                        : null;

                    within = new Leaf(type.GetString(), aggregate, []);
                    leaves.Add(within);
                }

                foreach (var member in element.EnumerateObject())
                {
                    within.Text.Add(member.Name);
                    Walk(member.Value, within, leaves);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    Walk(item, within, leaves);

                break;

            case JsonValueKind.String:
                within.Text.Add(element.GetString()!);
                break;

            default:
                within.Text.Add(element.GetRawText());
                break;
        }
    }

    /// <summary>
    /// What one response discloses, judged leaf by leaf whichever route served it: the rationale or the
    /// salt anywhere; the raiser anywhere but its own <c>agent.enrolled</c> leaf; and the post inside
    /// any flag's own leaf.
    /// </summary>
    private static IEnumerable<string> Disclosures(List<Leaf> leaves, Flagged flagged)
    {
        var raiser = flagged.Raiser.Agent.AgentId;

        foreach (var leaf in leaves)
        {
            if (leaf.Mentions(flagged.Nonce))
                yield return "served the flag's rationale";

            if (flagged.Salt is not null && leaf.Mentions(flagged.Salt))
                yield return "served the flag's salt";

            var ownEnrolment = string.Equals(leaf.EventType, AgentStandingProjector.EnrolledType, StringComparison.Ordinal)
                && string.Equals(leaf.AggregateId, raiser, StringComparison.Ordinal);
            if (leaf.Mentions(raiser) && !ownEnrolment)
                yield return "served the raiser's identity";

            // R10.62: the post stays out of the flag's own leaf, whatever else becomes public later --
            // flag.committed, and any flag type after it.
            if (leaf.EventType is { } eventType && eventType.StartsWith("flag.", StringComparison.Ordinal) && leaf.Mentions(flagged.PostId))
                yield return "served the flagged post's id in the flag's own leaf";
        }
    }

    /// <summary>The entries a flag listing holds, or null when the body is not one.</summary>
    private static int? ListedFlags(string served)
    {
        try
        {
            using var document = JsonDocument.Parse(served);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("flags", out var flags)
                && flags.ValueKind == JsonValueKind.Array
                    ? flags.GetArrayLength()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>What one sweep of every registered surface met.</summary>
    /// <param name="Leaks">Each disclosure, and a private fact no surface could be checked for.</param>
    /// <param name="BystanderListed">Whether the uninvolved agent was answered <c>200</c> by <c>GET /v1/flags</c>: its credential worked.</param>
    private sealed record Sweep(
        List<string> Undriven, List<string> Leaks, int Fetched, long TreeSize, bool SawPostLeaf, List<string> FlagLeaves, string? RecordLeaf,
        bool BystanderListed);

    /// <summary>
    /// Drives every registered read surface, anonymously and as the bystander, and records each
    /// disclosure <see cref="Disclosures"/> finds, and each flag listing that shows either caller any
    /// entry: neither raised a flag nor wrote a flagged post, so R7.18's <c>(own)</c> leaves them none.
    /// </summary>
    private async Task<Sweep> SweepAsync(HttpClient client, Flagged flagged, CancellationToken ct)
    {
        var treeSize = await TreeSizeAsync(client, ct);
        var undriven = new List<string>();
        var leaks = new List<string>();
        var fetched = 0;
        var sawPostLeaf = false;
        var flagLeaves = new List<string>();
        string? recordLeaf = null;
        var bystanderListed = false;

        if (flagged.Salt is null)
            leaks.Add("no private row holds the flag (R10.62, R11.32), so no surface could be checked for its salt");

        foreach (var (method, route) in Registered())
        {
            var key = $"{method} {route}";
            if (WriteRoutes.Contains(key)) continue;

            var requests = Drive(method, route, flagged.PostId, flagged.Digest, flagged.Board, flagged.Author.Agent.AgentId, treeSize);
            if (requests is null)
            {
                undriven.Add(key);
                continue;
            }

            var listing = method == "GET" && route.EndsWith("/flags", StringComparison.Ordinal);

            foreach (var (url, body) in requests)
            {
                foreach (var who in (Party?[])[null, flagged.Bystander])
                {
                    if (body is not null && who is not null) continue; // the batch is an anonymous read

                    var (status, served) = await FetchAsync(client, url, body, who, ct);
                    fetched++;
                    var reader = who is null ? "an anonymous caller" : "an uninvolved agent";
                    var leaves = Leaves(served);

                    foreach (var disclosure in Disclosures(leaves, flagged).Distinct(StringComparer.Ordinal))
                        leaks.Add($"{key} {disclosure} to {reader} ({url})");

                    if (listing)
                    {
                        var listed = ListedFlags(served);
                        if (listed > 0)
                            leaks.Add($"{key} served the flag listing to {reader} holding entries ({listed}), where a caller that raised no flag and wrote no flagged post is owed none ({url})");

                        if (status == HttpStatusCode.OK && listed is null)
                            undriven.Add($"{key} (answered 200 with a body this gate cannot read as a flag listing)");

                        if (route == RaisedFlagsRoute && who is not null && status == HttpStatusCode.OK)
                            bystanderListed = true;
                    }

                    // The log walk's own bookkeeping: what it met, kept as the whole entry served.
                    if (route == EntriesRoute)
                    {
                        if (leaves.Exists(l => string.Equals(l.EventType, PostProjector.PostAcceptedType, StringComparison.Ordinal)
                            && l.Mentions(flagged.PostId)))
                            sawPostLeaf = true;

                        if (leaves.Exists(l => l.EventType is { } t && t.StartsWith("flag.", StringComparison.Ordinal)))
                            flagLeaves.Add(served);

                        if (leaves.Exists(l => string.Equals(l.EventType, FlagProjector.ModerationAppliedType, StringComparison.Ordinal)
                            && l.Mentions(flagged.PostId)))
                            recordLeaf = served;
                    }
                }
            }
        }

        return new Sweep(undriven, leaks, fetched, treeSize, sawPostLeaf, flagLeaves, recordLeaf, bystanderListed);
    }

    /// <summary>Non-vacuity, each in its own assertion: a failure here is a defect in this gate, not in the Forum.</summary>
    private static void AssertTheSweepReachedEverything(Sweep sweep)
    {
        Assert.True(sweep.Undriven.Count == 0,
            "Registered surfaces this gate cannot drive (R14.9: a surface the enumeration reaches and the gate " +
            "cannot evaluate is a failure, never an omission): " + string.Join(", ", sweep.Undriven));
        Assert.True(sweep.Fetched >= 2 * sweep.TreeSize, $"the gate fetched {sweep.Fetched} responses over a log of {sweep.TreeSize}; it cannot have walked it");
        Assert.True(sweep.SawPostLeaf, "the log walk never met the question's own leaf -- a defect in this gate, not in the Forum");
        Assert.True(sweep.FlagLeaves.Count > 0, "the log walk never met a flag's leaf -- a defect in this gate, not in the Forum");
        Assert.True(sweep.BystanderListed,
            "GET /v1/flags never answered the uninvolved agent 200 -- its credential failed, so every read made " +
            "as it met a refusal, and half this gate proved nothing");
    }

    [Fact]
    public async Task R10_62_NoSurfaceServesAFlagsRaiserRationaleOrUnadjudicatedPost()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var flagged = await FlagAQuestionAsync(client, ct);

        var sweep = await SweepAsync(client, flagged, ct);

        AssertTheSweepReachedEverything(sweep);
        Assert.True(sweep.Leaks.Count == 0, string.Join("\n", sweep.Leaks));
    }

    /// <summary>
    /// The same gate over a moderated fixture (spec Increment 4). Once a moderator adjudicates the
    /// flag, the record is public and names the post, its envelope digest, the category and the flag
    /// it adjudicates (R10.60, R6.25). Still no surface serves the raiser or the rationale, the record
    /// included, and the flag's own leaf still names no post. The record is written by R10.59's
    /// writer, as every withholding in this suite is (trap 16).
    /// </summary>
    [Fact]
    public async Task R10_60_AfterModerationTheRecordIsPublicAndStillNamesNoRaiser()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var flagged = await FlagAQuestionAsync(client, ct);

        await forum.WithholdAsync(flagged.PostId, ct);
        var sweep = await SweepAsync(client, flagged, ct);

        AssertTheSweepReachedEverything(sweep);
        Assert.True(sweep.RecordLeaf is not null, "the log walk never met the moderation record -- the withholding wrote nothing");
        Assert.True(sweep.Leaks.Count == 0, string.Join("\n", sweep.Leaks));

        using var record = JsonDocument.Parse(sweep.RecordLeaf!);
        var payload = record.RootElement.GetProperty("entry").GetProperty("payload");
        Assert.Equal(flagged.PostId, payload.GetProperty("post_id").GetString());
        Assert.Equal(flagged.Digest, payload.GetProperty("digest").GetString());
        Assert.Equal("spam", payload.GetProperty("category").GetString());

        // The record names the flag by the event id its public entry carries, so the two can be joined from the log alone.
        var adjudicated = Assert.Single(payload.GetProperty("adjudicates").EnumerateArray()).GetString()!;
        Assert.Contains(sweep.FlagLeaves, leaf => leaf.Contains($"\"event_id\":\"{adjudicated}\"", StringComparison.Ordinal));
    }
}
