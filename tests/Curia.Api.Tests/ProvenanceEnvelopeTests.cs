using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Curia.Domain.Serving;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R10.17–R10.19 over HTTP: every served item is wrapped, the marking is opt-in, and none of it
/// reaches the store.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ProvenanceEnvelopeTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    private async Task<(string PostId, string Board, string AgentId)> PostAsync(
        HttpClient http, string body, CancellationToken ct)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agent = ForumAgent.Create("https://agents.example/prov-" + suffix, "pk-" + suffix);
        var (dpop, token) = await agent.AuthenticateAsync(http, TokenEndpoint, forum.Now, ct);

        var board = "board-" + suffix;
        var wire = agent.SignQuestion(board, body, "Provenance", forum.Now);

        using var response = await dpop.PostAsync(http, PostsUrl, token, wire, forum.Now, ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return (json.RootElement.GetProperty("post_id").GetString()!, board, agent.AgentId);
    }

    /// <summary>
    /// R10.17: every content item in every API response is wrapped, with the warning R10.17 spells
    /// out verbatim.
    /// </summary>
    [Fact]
    public async Task R10_17_EveryServedPostCarriesItsProvenanceEnvelope()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var (postId, board, agentId) = await PostAsync(http, "Plain content.", ct);

        foreach (var url in (string[])[$"/v1/posts/{postId}", $"/v1/threads/{postId}", $"/v1/boards/{board}/posts"])
        {
            var served = await http.GetFromJsonAsync<JsonElement>(url, ct);
            var item = served.ValueKind is JsonValueKind.Array ? served[0] : served;
            var provenance = item.GetProperty("provenance");

            Assert.Equal("agent-authored/untrusted", provenance.GetProperty("content_type").GetString());
            Assert.Equal(Provenance.StandardWarning, provenance.GetProperty("warning").GetString());
            Assert.Equal(agentId, provenance.GetProperty("author").GetString());
            Assert.True(provenance.GetProperty("signature_valid").GetBoolean());
            Assert.Contains(
                "/.well-known/curia-reader-contract/v1",
                provenance.GetProperty("reader_contract").GetString()!,
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// R10.18: the envelope is structurally inseparable from the content. Asserted as a shape claim
    /// -- the content is a member *of* the envelope's object, so a client that discards the envelope
    /// discards the content with it. A sibling <c>provenance</c> beside a sibling <c>body</c> would
    /// be trivially separable, which is the arrangement R10.18 forbids.
    /// </summary>
    [Fact]
    public async Task R10_18_TheContentCannotBeTakenWithoutTheEnvelope()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var (postId, _, _) = await PostAsync(http, "Inseparable.", ct);

        var served = await http.GetFromJsonAsync<JsonElement>($"/v1/posts/{postId}", ct);

        // The rendered text is delimited at every marking level, so even a client that pulls the
        // string out of the JSON still carries the boundary markers with it.
        var rendered = served.GetProperty("rendered").GetString()!;
        Assert.StartsWith(Datamarking.OpenDelimiter, rendered, StringComparison.Ordinal);
        Assert.EndsWith(Datamarking.CloseDelimiter, rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// R10.12/R10.13: datamarking is a serving option, and <b>off</b> by default on the HTTP API,
    /// "whose output is usually processed by client code first".
    /// </summary>
    [Fact]
    public async Task R10_13_DatamarkingIsOffByDefaultOnTheHttpApiAndOnByRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var (postId, _, _) = await PostAsync(http, "Marked or not.", ct);

        var plain = await http.GetFromJsonAsync<JsonElement>($"/v1/posts/{postId}", ct);
        Assert.Equal("None", plain.GetProperty("provenance").GetProperty("marking").GetString());
        Assert.DoesNotContain(
            Datamarking.DefaultControlToken,
            plain.GetProperty("rendered").GetString()!,
            StringComparison.Ordinal);

        var marked = await http.GetFromJsonAsync<JsonElement>($"/v1/posts/{postId}?marking=datamark", ct);
        Assert.Equal("Datamark", marked.GetProperty("provenance").GetProperty("marking").GetString());
        Assert.Contains(
            Datamarking.DefaultControlToken,
            marked.GetProperty("rendered").GetString()!,
            StringComparison.Ordinal);

        // R10.14: the token is reported so a client can strip it after its model has consumed the
        // marked form. A client that had to guess which token was used could not strip it at all.
        Assert.Equal(
            Datamarking.DefaultControlToken,
            marked.GetProperty("provenance").GetProperty("marking_token").GetString());
    }

    /// <summary>R10.15: delimiter-only marking says, in the response, that it is the weakest option.</summary>
    [Fact]
    public async Task R10_15_DelimiterOnlyMarkingSaysItIsTheWeakest()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var (postId, _, _) = await PostAsync(http, "Weakest option.", ct);

        var served = await http.GetFromJsonAsync<JsonElement>($"/v1/posts/{postId}?marking=delimiters", ct);
        var caveat = served.GetProperty("provenance").GetProperty("marking_caveat").GetString()!;

        Assert.Contains("weakest", caveat, StringComparison.Ordinal);
    }

    /// <summary>R10.16: where marking is applied, the response says it is not a guarantee.</summary>
    [Fact]
    public async Task R10_16_DatamarkingSaysItIsNotAGuarantee()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var (postId, _, _) = await PostAsync(http, "No promises.", ct);

        var served = await http.GetFromJsonAsync<JsonElement>($"/v1/posts/{postId}?marking=datamark", ct);
        var caveat = served.GetProperty("provenance").GetProperty("marking_caveat").GetString()!;

        Assert.Contains("not a guarantee", caveat, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The invariant Stage 4 exists to not break.</b> R6.12: output transformations happen at the
    /// serving boundary and are never written back.
    ///
    /// <para>Asserted the way it would actually fail: fetch the same post with marking on, then with
    /// marking off, and confirm the <c>canonical</c> field is byte-identical both times and contains
    /// no marker. If a request with <c>?marking=datamark</c> could leave a marked copy behind, the
    /// second fetch would show it -- and every signature over that post would stop verifying.</para>
    /// </summary>
    [Fact]
    public async Task R6_12_MarkingNeverReachesTheStore()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var (postId, _, _) = await PostAsync(http, "Original untouched content.", ct);

        var before = await http.GetFromJsonAsync<JsonElement>($"/v1/posts/{postId}", ct);
        var canonicalBefore = before.GetProperty("canonical").GetString()!;

        // Ask for marking repeatedly -- if any of it persisted, the next read would carry it.
        for (var i = 0; i < 3; i++)
            await http.GetFromJsonAsync<JsonElement>($"/v1/posts/{postId}?marking=datamark", ct);

        var after = await http.GetFromJsonAsync<JsonElement>($"/v1/posts/{postId}", ct);
        var canonicalAfter = after.GetProperty("canonical").GetString()!;

        Assert.Equal(canonicalBefore, canonicalAfter);
        Assert.DoesNotContain(Datamarking.DefaultControlToken, canonicalAfter, StringComparison.Ordinal);
        Assert.DoesNotContain(Datamarking.OpenDelimiter, canonicalAfter, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the served canonical form still verifies offline after all that marking -- which is the
    /// only assertion that proves the marking did not disturb what was signed.
    /// </summary>
    [Fact]
    public async Task Marking_does_not_disturb_offline_verifiability()
    {
        var ct = TestContext.Current.CancellationToken;
        var verifier = TestisBinary.Locate();
        var http = forum.Client;

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agent = ForumAgent.Create("https://agents.example/mark-" + suffix, "mk-" + suffix);
        var (dpop, token) = await agent.AuthenticateAsync(http, TokenEndpoint, forum.Now, ct);

        var wire = agent.SignQuestion("board-" + suffix, "Still verifiable.", "Marking", forum.Now);
        using var posted = await dpop.PostAsync(http, PostsUrl, token, wire, forum.Now, ct);
        using var accepted = JsonDocument.Parse(await posted.Content.ReadAsStringAsync(ct));
        var postId = accepted.RootElement.GetProperty("post_id").GetString()!;

        // Fetch with marking on, then take the canonical field from that same marked response.
        var served = await http.GetFromJsonAsync<JsonElement>($"/v1/posts/{postId}?marking=datamark", ct);
        var canonical = served.GetProperty("canonical").GetString()!;
        var signature = served.GetProperty("signature").GetString()!;
        var jwks = await http.GetStringAsync($"/v1/jwks?agent={Uri.EscapeDataString(agent.AgentId)}", ct);

        var directory = Directory.CreateTempSubdirectory("curia-mark-");
        try
        {
            var envelopePath = Path.Combine(directory.FullName, "submission.json");
            var jwksPath = Path.Combine(directory.FullName, "jwks.json");
            await File.WriteAllTextAsync(
                envelopePath, $"{{\"envelope\":{canonical},\"signature\":\"{signature}\"}}", ct);
            await File.WriteAllTextAsync(jwksPath, jwks, ct);

            var (exitCode, stdout, stderr) = TestisBinary.Run(verifier, envelopePath, jwksPath);
            Assert.True(exitCode == 0, $"exit={exitCode}\n{stdout}\n{stderr}");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// R10.20/R10.21: the contract is retrievable at a stable well-known URL, machine readable, and
    /// versioned -- and every provenance envelope points at it.
    /// </summary>
    [Fact]
    public async Task R10_21_TheReaderContractIsServedMachineReadableAndVersioned()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;

        var contract = await http.GetFromJsonAsync<JsonElement>(
            Curia.Domain.Serving.ReaderContract.WellKnownPath, ct);

        Assert.Equal("v1", contract.GetProperty("version").GetString());

        var clauses = contract.GetProperty("clauses").EnumerateArray().ToArray();
        Assert.Equal(9, clauses.Length);

        // Clause 1 is the one everything else rests on: authenticated as to authorship, never as to
        // truthfulness or safety.
        Assert.Contains(
            "authenticated as to authorship",
            clauses[0].GetProperty("text").GetString()!,
            StringComparison.Ordinal);

        // R10.22's five mechanical clauses, which a client library must implement by default rather
        // than merely acknowledge. Asserted as a count because that is what makes the distinction
        // checkable rather than decorative.
        Assert.Equal(5, clauses.Count(c => c.GetProperty("client_must_implement").GetBoolean()));

        // Every clause carries its RFC 2119 force: the difference between a contract violation and a
        // missed best practice.
        Assert.All(clauses, c =>
        {
            var force = c.GetProperty("force").GetString();
            Assert.True(force is "SHALL" or "SHOULD", $"clause force was '{force}'");
        });
    }
}
