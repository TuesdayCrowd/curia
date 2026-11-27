using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json.Nodes;
using Curia.Domain.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Curia.Api.Tests.OriginProbe;

namespace Curia.Api.Tests;

/// <summary>
/// Errata G82's probes of R5.26 and R5.27's binding half (the plan's ORG4 to ORG6, ORG17, ORG18 and ORG-tok), over the plain fixture whose origin is <c>http://localhost</c>. <b>Written against the Forum
/// that builds every URL from the request</b>: each row is red by its own assertion there, and a row that is green there is a control and says so. Credentials are hand-built from literals (see
/// <see cref="OriginProbe"/>). Every row of a method is run and every failure reported together, so one first red text names each row that is red.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class OriginBindingTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string Own = "http://localhost";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// ORG4. A token the Forum mints carries <c>iss</c> and <c>aud</c> equal to the origin, as a literal in the test, and the authorization-server metadata states the issuer, the token endpoint and the key
    /// set built from it. Red on the tree: both are <c>https://forum.local</c>, the default nobody chose (T0-12).
    /// </summary>
    [Fact]
    public async Task R5_26_ORG4_TheTokenAndTheMetadataNameTheOrigin()
    {
        var (_, dpop) = await EnrolledAsync(forum, "org4", Ct);
        var token = AccessToken(await TokenAsync(forum.Client, "/oauth/token", dpop, Own + "/oauth/token", Own + "/oauth/token", forum.Now, Ct));
        var failures = new List<string>();
        Expect(failures, token is not null, "the control token request (own name, own spelling) was refused, so the claims below were not read");
        if (token is not null)
        {
            var claims = Claims(token);
            Expect(failures, (string?)claims["iss"] == Own, $"iss: expected {Own}, the token says {claims["iss"]}");
            Expect(failures, (string?)claims["aud"] == Own, $"aud: expected {Own}, the token says {claims["aud"]}");
        }

        using var metadata = await forum.Client.GetAsync("/.well-known/oauth-authorization-server", Ct);
        var served = JsonNode.Parse(await metadata.Content.ReadAsStringAsync(Ct))!;
        Expect(failures, (string?)served["issuer"] == Own, $"metadata issuer: expected {Own}, served {served["issuer"]}");
        Expect(failures, (string?)served["token_endpoint"] == Own + "/oauth/token", $"metadata token_endpoint: expected {Own}/oauth/token, served {served["token_endpoint"]}");
        Expect(failures, (string?)served["jwks_uri"] == Own + "/oauth/jwks", $"metadata jwks_uri: expected {Own}/oauth/jwks, served {served["jwks_uri"]}");
        NoFailures(failures, "R5.26: the Forum's name is the configured origin everywhere it states one");
    }

    /// <summary>
    /// ORG4, the metadata under each <c>Host</c> spelling the Host refusal admits (the case variant and the explicit default port): the document states the origin's issuer, token endpoint and key set whatever
    /// the request called the Forum. Added by Task 6: FORG16 (the metadata's <c>token_endpoint</c> built from the request) stayed green under ORG4's plain request, which carries the origin's own spelling, and was
    /// killed by the fence alone.
    /// </summary>
    [Fact]
    public async Task R5_26_ORG4_TheMetadataIsTheOriginsWhateverAdmittedHostAskedForIt()
    {
        var failures = new List<string>();
        foreach (var host in new[] { "localhost", "LOCALHOST", "localhost:80" })
        {
            using var client = ClientWith(forum, host);
            using var metadata = await client.GetAsync("/.well-known/oauth-authorization-server", Ct);
            Expect(failures, metadata.StatusCode == HttpStatusCode.OK, $"Host {host}: expected 200, the Forum answered {(int)metadata.StatusCode}");
            if (metadata.StatusCode != HttpStatusCode.OK) continue;
            var served = JsonNode.Parse(await metadata.Content.ReadAsStringAsync(Ct))!;
            Expect(failures, (string?)served["issuer"] == Own, $"Host {host}, metadata issuer: expected {Own}, served {served["issuer"]}");
            Expect(failures, (string?)served["token_endpoint"] == Own + "/oauth/token", $"Host {host}, metadata token_endpoint: expected {Own}/oauth/token, served {served["token_endpoint"]}");
            Expect(failures, (string?)served["jwks_uri"] == Own + "/oauth/jwks", $"Host {host}, metadata jwks_uri: expected {Own}/oauth/jwks, served {served["jwks_uri"]}");
        }

        NoFailures(failures, "R5.26: the metadata names the configured origin whatever admitted Host asked for it");
    }

    /// <summary>
    /// ORG5. A post's provenance envelope names <c>origin + /.well-known/reader-contract/v1</c> whatever <c>Host</c> a reader's request carries: the plain request is the <b>control</b> (green on the tree),
    /// the case variant and the explicit default port, both of which the Host refusal admits, are red on the tree (the URL follows the <c>Host</c>).
    /// </summary>
    [Fact]
    public async Task R10_21_ORG5_TheReaderContractUrlIsTheOriginsWhateverHostAskedForIt()
    {
        var (agent, dpop) = await EnrolledAsync(forum, "org5", Ct);
        var token = AccessToken(await TokenAsync(forum.Client, "/oauth/token", dpop, Own + "/oauth/token", Own + "/oauth/token", forum.Now, Ct));
        Assert.True(token is not null, "the control token request was refused");
        using var posted = await dpop.PostAsync(forum.Client, Own + "/v1/posts", token!, agent.SignQuestion("org5-" + Guid.NewGuid().ToString("N")[..8], "A question about the reader contract.", "Contract URL", forum.Now), forum.Now, Ct);
        Assert.True(posted.StatusCode == HttpStatusCode.Created, $"the control post was refused ({(int)posted.StatusCode})");
        var id = JsonNode.Parse(await posted.Content.ReadAsStringAsync(Ct))!["post_id"]!.GetValue<string>();

        var failures = new List<string>();
        foreach (var host in new[] { "localhost", "LOCALHOST", "localhost:80" })
        {
            using var client = ClientWith(forum, host);
            var read = await ReadAsync(await client.GetAsync("/v1/posts/" + id, Ct), Ct);
            var served = read.Status == 200 ? ReaderContractOf(read) : null;
            Expect(failures, served == Own + "/.well-known/reader-contract/v1", $"Host {host}: expected {Own}/.well-known/reader-contract/v1, served {served ?? read.Says}");
        }

        NoFailures(failures, "R10.21 (add.): the contract URL a provenance envelope carries is the origin's");
    }

    /// <summary>
    /// ORG6a, the relay as D41 filed it: credentials made for <c>another.example</c> and sent with that <c>Host</c> are refused <b>by name</b> (<c>host-not-served</c>, RFC 6749's form at the token endpoint),
    /// on the token endpoint, a resource route and the enrollment route; no token is issued and nothing is appended. Red on the tree: the Forum answers 200, 200 and 201 (T0-1).
    /// </summary>
    [Fact]
    public async Task R5_27_ORG6a_TheRelayAsFiledIsRefusedByName()
    {
        var (_, dpop) = await EnrolledAsync(forum, "org6a", Ct);
        var honestToken = AccessToken(await TokenAsync(forum.Client, "/oauth/token", dpop, Own + "/oauth/token", Own + "/oauth/token", forum.Now, Ct));
        Assert.True(honestToken is not null, "the control token request (own name) was refused");
        using var relay = forum.CreateDefaultClient(new Uri("http://another.example"));
        var failures = new List<string>();

        var token = await TokenAsync(relay, "/oauth/token", dpop, "http://another.example/oauth/token", "http://another.example/oauth/token", forum.Now, Ct);
        Expect(failures, token.IsHostRefusal, $"token endpoint: expected 400 invalid_request \"{HostRefusalTitle}\", the Forum answered {token.Says}");
        Expect(failures, AccessToken(token) is null, "token endpoint: a token was issued to a relay");

        var resource = await GetAsync(relay, "/v1/inbox", dpop, "http://another.example/v1/inbox", honestToken!, forum.Now, Ct);
        Expect(failures, resource.IsHostRefusal, $"resource route: expected 400 {HostRefusalType}, the Forum answered {resource.Says}");

        var events = await EnrollProofProbe.EventsAsync(forum, Ct);
        var agent = EnrollProofProbe.NewAgent("org6a-enrol");
        var enrol = await EnrolAsync(relay, agent, "http://another.example/v1/agents", forum.Now, Ct);
        Expect(failures, enrol.IsHostRefusal, $"enrollment route: expected 400 {HostRefusalType}, the Forum answered {enrol.Says}");
        Expect(failures, await EnrollProofProbe.EventsAsync(forum, Ct) == events, "enrollment route: the log gained a row from a relayed enrollment");
        Expect(failures, await EnrollProofProbe.KeyRowsAsync(forum, agent.AgentId, Ct) == 0, "enrollment route: a key row was registered by a relayed enrollment");
        NoFailures(failures, "R5.27: a request addressed to another name is refused by the Forum, in its own words");
    }

    /// <summary>ORG6b: the case variant <c>Host: LOCALHOST</c>, which the Host refusal admits, with credentials made for <c>http://LOCALHOST/...</c>: refused <b>by the binding</b>, as the same credentials are refused under the honest <c>Host</c>.</summary>
    [Fact]
    public Task R5_26_ORG6b_ACredentialForTheCaseVariantIsRefusedByTheBindingOnAnAdmittedHost() =>
        VariantRefusedAsync("LOCALHOST", "http://LOCALHOST");

    /// <summary>ORG6c: the explicit default port <c>Host: localhost:80</c>, with credentials made for <c>http://localhost:80/...</c>: refused by the binding.</summary>
    [Fact]
    public Task R5_26_ORG6c_ACredentialForTheExplicitDefaultPortIsRefusedByTheBindingOnAnAdmittedHost() =>
        VariantRefusedAsync("localhost:80", "http://localhost:80");

    /// <summary>
    /// ORG6d, the converse of ORG6b and ORG6c: the honest credential, made for the origin's own spelling, is <b>honoured</b> under each admitted <c>Host</c> (<c>200</c>, <c>200</c>, <c>201</c>). A binding that still
    /// read the <c>Host</c> refuses these, and a Forum that refused every spelling but the exact one would pass ORG6b and ORG6c for the wrong reason.
    /// </summary>
    [Fact]
    public async Task R5_26_ORG6d_TheCredentialForTheOriginsOwnSpellingIsHonouredOnEveryAdmittedHost()
    {
        var failures = new List<string>();
        foreach (var host in new[] { "localhost", "LOCALHOST", "localhost:80" })
        {
            using var client = ClientWith(forum, host);
            var (agent, dpop) = await EnrolledAsync(forum, "org6d", Ct);
            var token = await TokenAsync(client, "/oauth/token", dpop, Own + "/oauth/token", Own + "/oauth/token", forum.Now, Ct);
            Expect(failures, token.Status == 200, $"Host {host}, token endpoint: expected 200, the Forum answered {token.Says}");

            var minted = AccessToken(await TokenAsync(forum.Client, "/oauth/token", dpop, Own + "/oauth/token", Own + "/oauth/token", forum.Now, Ct));
            var resource = await GetAsync(client, "/v1/inbox", dpop, Own + "/v1/inbox", minted!, forum.Now, Ct);
            Expect(failures, resource.Status == 200, $"Host {host}, resource route: expected 200, the Forum answered {resource.Says}");

            var enrolling = EnrollProofProbe.NewAgent("org6d-enrol");
            var enrol = await EnrolAsync(client, enrolling, Own + "/v1/agents", forum.Now, Ct);
            Expect(failures, enrol.Status == 201, $"Host {host}, enrollment route: expected 201, the Forum answered {enrol.Says}");
            _ = agent;
        }

        NoFailures(failures, "R5.26 and R5.27: the binding is the origin's whatever admitted Host a request carries");
    }

    private async Task VariantRefusedAsync(string host, string spelling)
    {
        var failures = new List<string>();
        using var variant = ClientWith(forum, host);
        using var honest = ClientWith(forum, "localhost");

        // The token endpoint makes two comparisons, the assertion's aud and the proof's htu: three rows, so that restoring either alone is seen (amendment 1, #4). The oracle is the Forum's own answer to the same
        // credentials under the honest Host, where they are plainly for another spelling.
        foreach (var (row, assertionAudience, proofUrl) in new[]
        {
            ("both spelled for the variant", spelling + "/oauth/token", spelling + "/oauth/token"),
            ("assertion aud spelled for the variant, proof htu the origin's", spelling + "/oauth/token", Own + "/oauth/token"),
            ("assertion aud the origin's, proof htu spelled for the variant", Own + "/oauth/token", spelling + "/oauth/token"),
        })
        {
            var (_, dpop) = await EnrolledAsync(forum, "org6bc", Ct);
            var oracle = await TokenAsync(honest, "/oauth/token", dpop, assertionAudience, proofUrl, forum.Now, Ct);
            var got = await TokenAsync(variant, "/oauth/token", dpop, assertionAudience, proofUrl, forum.Now, Ct);
            Expect(failures, oracle.Status is >= 400 and < 500, $"token ({row}): the oracle, the same credentials under Host localhost, was not refused: {oracle.Says}");
            Expect(failures, got.Status == oracle.Status && got.Kind == oracle.Kind && !got.IsHostRefusal && AccessToken(got) is null,
                $"token ({row}), Host {host}: expected the binding's refusal ({oracle.Status} {oracle.Kind}), the Forum answered {got.Says}");
        }

        {
            var (agent, dpop) = await EnrolledAsync(forum, "org6bc-res", Ct);
            _ = agent;
            var token = AccessToken(await TokenAsync(honest, "/oauth/token", dpop, Own + "/oauth/token", Own + "/oauth/token", forum.Now, Ct))!;
            var oracle = await GetAsync(honest, "/v1/inbox", dpop, spelling + "/v1/inbox", token, forum.Now, Ct);
            var got = await GetAsync(variant, "/v1/inbox", dpop, spelling + "/v1/inbox", token, forum.Now, Ct);
            Expect(failures, oracle.Status == 401 && oracle.Type == "curia/authn/url-mismatch", $"resource: the oracle (Host localhost) was expected 401 curia/authn/url-mismatch (T0-1), the Forum answered {oracle.Says}");
            Expect(failures, got.Status == 401 && got.Type == "curia/authn/url-mismatch" && !got.IsHostRefusal,
                $"resource, Host {host}: expected 401 curia/authn/url-mismatch, the Forum answered {got.Says}");
        }

        {
            var oracleAgent = EnrollProofProbe.NewAgent("org6bc-enrol-o");
            var oracle = await EnrolAsync(honest, oracleAgent, spelling + "/v1/agents", forum.Now, Ct);
            var agent = EnrollProofProbe.NewAgent("org6bc-enrol");
            var got = await EnrolAsync(variant, agent, spelling + "/v1/agents", forum.Now, Ct);
            Expect(failures, oracle.Status == 400 && oracle.Type == "curia/enroll/proof-mismatch" && oracle.Detail == "claim=aud", $"enrollment: the oracle (Host localhost) was expected 400 proof-mismatch claim=aud (T0-1), the Forum answered {oracle.Says}");
            Expect(failures, got.Status == 400 && got.Type == "curia/enroll/proof-mismatch" && got.Detail == "claim=aud" && !got.IsHostRefusal,
                $"enrollment, Host {host}: expected 400 curia/enroll/proof-mismatch claim=aud, the Forum answered {got.Says}");
            Expect(failures, await EnrollProofProbe.KeyRowsAsync(forum, agent.AgentId, Ct) == 0, $"enrollment, Host {host}: a key row was registered");
        }

        NoFailures(failures, $"R5.26: a credential made for {spelling} is refused by the binding on Host {host}, which the Host refusal admits");
    }

    /// <summary>
    /// ORG17. At <c>/oauth/token</c> the Host refusal is RFC 6749's object and no other shape (R11.33): exactly <c>error</c> and <c>error_description</c>, <c>invalid_request</c> and the title, in the content type the
    /// endpoint's other <c>invalid_request</c> uses. Red on the tree: the request is served.
    /// </summary>
    [Fact]
    public async Task R11_33_ORG17_TheTokenEndpointRefusesAForeignHostInRfc6749sShape()
    {
        using var honest = ClientWith(forum, "localhost");
        using var empty = new FormUrlEncodedContent(new Dictionary<string, string>());
        var oracle = await ReadAsync(await honest.PostAsync("/oauth/token", empty, Ct), Ct);

        using var foreign = ClientWith(forum, "foreign.example");
        using var body = new FormUrlEncodedContent(new Dictionary<string, string>());
        var got = await ReadAsync(await foreign.PostAsync("/oauth/token", body, Ct), Ct);

        var failures = new List<string>();
        Expect(failures, oracle.Error == "invalid_request" && oracle.ContentType is not null, $"the oracle (an unreadable token request under Host localhost) was not an invalid_request object: {oracle.Says}");
        Expect(failures, got.IsHostRefusal, $"expected 400 invalid_request \"{HostRefusalTitle}\", the Forum answered {got.Says}");
        var members = got.Body.Length > 0 && got.Body[0] == '{' ? ((JsonObject)JsonNode.Parse(got.Body)!).Select(m => m.Key).Order(StringComparer.Ordinal).ToArray() : [];
        Expect(failures, members.SequenceEqual(["error", "error_description"]), $"expected exactly the members error and error_description, the body has [{string.Join(", ", members)}]");
        Expect(failures, got.ContentType == oracle.ContentType, $"expected the content type {oracle.ContentType} of the endpoint's other invalid_request, got {got.ContentType}");
        NoFailures(failures, "R11.33: the token endpoint refuses in RFC 6749's form");
    }

    /// <summary>
    /// ORG18 (alpha, non-property (k)). A token whose <c>iss</c> and <c>aud</c> are the old default <c>https://forum.local</c>, signed by the issuer's own key, is refused <c>401 curia/authn/issuer-mismatch</c>;
    /// the same token re-signed with the origin's name is the control and is honoured. Red on the tree: the default is the Forum's issuer, so the stale token is honoured.
    /// </summary>
    [Fact]
    public async Task R5_26_ORG18_ATokenOfTheOldDefaultNameIsRefused()
    {
        var (_, dpop) = await EnrolledAsync(forum, "org18", Ct);
        var token = AccessToken(await TokenAsync(forum.Client, "/oauth/token", dpop, Own + "/oauth/token", Own + "/oauth/token", forum.Now, Ct))!;
        var failures = new List<string>();

        var named = Resign(token, forum.IssuerSigningKeyPem, c => { c["iss"] = Own; c["aud"] = Own; });
        var control = await GetAsync(forum.Client, "/v1/inbox", dpop, Own + "/v1/inbox", named, forum.Now, Ct);
        Expect(failures, control.Status == 200, $"the control (a token re-signed with iss and aud {Own}) was refused: {control.Says}");

        var stale = Resign(token, forum.IssuerSigningKeyPem, c => { c["iss"] = "https://forum.local"; c["aud"] = "https://forum.local"; });
        var got = await GetAsync(forum.Client, "/v1/inbox", dpop, Own + "/v1/inbox", stale, forum.Now, Ct);
        Expect(failures, got.Status == 401 && got.Type == "curia/authn/issuer-mismatch", $"expected 401 curia/authn/issuer-mismatch for a token of https://forum.local, the Forum answered {got.Says}");
        NoFailures(failures, "R5.26: the issuer and audience are the origin and no other name");
    }

    /// <summary>
    /// ORG-tok. A token is bound to its Forum: two hosts on one database and one signing key, with origins <c>http://localhost</c> and <c>http://other.example</c>. A token one minted is honoured by it and
    /// refused by the other, <c>401 curia/authn/issuer-mismatch</c>, in both directions; a post signed once is accepted by the first with its token and refused by the second with the same token. This is
    /// what stops a relay moving an agent's post into another Forum, since a signed post names no Forum (non-property (a)). Red on the tree: both hosts share the default issuer.
    /// </summary>
    [Fact]
    public async Task R5_26_ORGtok_ATokenIsBoundToTheForumThatMintedIt()
    {
        using var other = forum.WithWebHostBuilder(b =>
        {
            // R11.112 (errata G85): this host holds a public origin, so it runs Production (IWebHostBuilder has no UseEnvironment here; the setting is what it sets).
            b.UseSetting("environment", "Production");
            b.UseSetting("Curia:PublicOrigin", "http://other.example");
        });
        using var otherClient = other.CreateClient();
        otherClient.DefaultRequestHeaders.Host = "other.example";
        var (agent, dpop) = await EnrolledAsync(forum, "orgtok", Ct);
        var failures = new List<string>();

        var first = AccessToken(await TokenAsync(forum.Client, "/oauth/token", dpop, Own + "/oauth/token", Own + "/oauth/token", forum.Now, Ct))!;
        var second = AccessToken(await TokenAsync(otherClient, "/oauth/token", dpop, "http://other.example/oauth/token", "http://other.example/oauth/token", forum.Now, Ct));
        Expect(failures, second is not null, "the second host did not mint a token for its own origin (the control)");

        var firstAtFirst = await GetAsync(forum.Client, "/v1/inbox", dpop, Own + "/v1/inbox", first, forum.Now, Ct);
        Expect(failures, firstAtFirst.Status == 200, $"the control: a token minted by the first host is honoured by it: {firstAtFirst.Says}");
        if (second is not null)
        {
            var secondAtSecond = await GetAsync(otherClient, "/v1/inbox", dpop, "http://other.example/v1/inbox", second, forum.Now, Ct);
            Expect(failures, secondAtSecond.Status == 200, $"the control: a token minted by the second host is honoured by it: {secondAtSecond.Says}");
            var secondAtFirst = await GetAsync(forum.Client, "/v1/inbox", dpop, Own + "/v1/inbox", second, forum.Now, Ct);
            Expect(failures, secondAtFirst.Status == 401 && secondAtFirst.Type == "curia/authn/issuer-mismatch", $"the second host's token at the first host: expected 401 curia/authn/issuer-mismatch, the Forum answered {secondAtFirst.Says}");
        }

        var firstAtSecond = await GetAsync(otherClient, "/v1/inbox", dpop, "http://other.example/v1/inbox", first, forum.Now, Ct);
        Expect(failures, firstAtSecond.Status == 401 && firstAtSecond.Type == "curia/authn/issuer-mismatch", $"the first host's token at the second host: expected 401 curia/authn/issuer-mismatch, the Forum answered {firstAtSecond.Says}");

        var wire = agent.SignQuestion("orgtok-" + Guid.NewGuid().ToString("N")[..8], "A post signed once and offered to two Forums.", "One post, two Forums", forum.Now);
        using var atSecond = await dpop.PostAsync(otherClient, "http://other.example/v1/posts", first, wire, forum.Now, Ct);
        Expect(failures, atSecond.StatusCode == HttpStatusCode.Unauthorized, $"the post with the first host's token at the second host: expected 401, the Forum answered {(int)atSecond.StatusCode}");
        using var atFirst = await dpop.PostAsync(forum.Client, Own + "/v1/posts", first, wire, forum.Now, Ct);
        Expect(failures, atFirst.StatusCode == HttpStatusCode.Created, $"the same post with the same token at the first host: expected 201, the Forum answered {(int)atFirst.StatusCode}");
        NoFailures(failures, "R5.26: a token is bound to the Forum's origin, and so is what it lets an agent post");
    }
}
