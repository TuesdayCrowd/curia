using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Curia.OperatorTool;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// Stage 4's exit criterion, end to end: the operator signs a head, the Forum serves heads,
/// entries and proofs, every post carries its index and proof, and <c>curia-testis</c> verifies
/// all of it offline from the served JSON alone -- the standard Phase 1 set for authorship.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ActaEndpointTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    /// <summary>One throwaway log key for the class; the Forum never sees it, only its public half via the log.</summary>
    private static readonly string LogKeyPem = LogSigningKey.GeneratePem();

    private async Task<(int Exit, string Out, string Err)> SignHeadAsync(CancellationToken ct)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await OperatorCommands.RunAsync(
            ["sign-head", "--by", "ops"], forum.ConnectionString, forum.Clock, stdout, stderr, ct, logSigningKeyPem: LogKeyPem);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private async Task<string> PostQuestionAsync(HttpClient http, CancellationToken ct)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agent = ForumAgent.Create("https://agents.example/acta-" + suffix, "acta-" + suffix);
        using (var enrolled = await agent.EnrollAsync(http, ct))
            Assert.Equal(HttpStatusCode.Created, enrolled.StatusCode);

        var dpop = DpopClient.For(agent, agent.AssertionKey);
        var token = await dpop.GetTokenAsync(http, TokenEndpoint, forum.Now, ct);
        using var posted = await dpop.PostAsync(
            http, PostsUrl, token, agent.SignQuestion("board-" + suffix, "Is this post in the log?", "R6.18 says it must be", forum.Now), forum.Now, ct);
        Assert.Equal(HttpStatusCode.Created, posted.StatusCode);

        var body = await posted.Content.ReadFromJsonAsync<JsonElement>(ct);
        return body.GetProperty("post_id").GetString()!;
    }

    private static async Task<string> SaveAsync(HttpClient http, string url, string dir, string name, CancellationToken ct)
    {
        using var response = await http.GetAsync(new Uri(url, UriKind.Relative), ct);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {response.StatusCode}");
        var path = Path.Combine(dir, name);
        await File.WriteAllBytesAsync(path, await response.Content.ReadAsByteArrayAsync(ct), ct);
        return path;
    }

    private static string Scratch() => Directory.CreateTempSubdirectory("curia-acta-").FullName;

    /// <summary>An agent, enrolled through the route or as before errata G16, that has asked one question; and that question's id.</summary>
    private async Task<(ForumAgent Agent, string PostId)> AskAsync(HttpClient http, string name, bool beforeKeyBinding, CancellationToken ct)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agent = ForumAgent.Create($"https://agents.example/{name}-{suffix}", $"{name}-{suffix}");
        if (beforeKeyBinding)
        {
            await forum.EnrollBeforeKeyBindingAsync(agent.AgentId, agent.Kid, agent.AssertionKey.ExportSubjectPublicKeyInfo(), ct);
        }
        else
        {
            using var enrolled = await agent.EnrollAsync(http, ct);
            Assert.Equal(HttpStatusCode.Created, enrolled.StatusCode);
        }

        var dpop = DpopClient.For(agent, agent.AssertionKey);
        var token = await dpop.GetTokenAsync(http, TokenEndpoint, forum.Now, ct);
        using var posted = await dpop.PostAsync(
            http, PostsUrl, token, agent.SignQuestion("board-" + suffix, "Whose key does the log say signed this?", "R6.54 " + suffix, forum.Now), forum.Now, ct);
        Assert.Equal(HttpStatusCode.Created, posted.StatusCode);
        return (agent, (await posted.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("post_id").GetString()!);
    }

    /// <summary>
    /// R6.54 (errata G16), offline: <c>curia-testis log author</c> establishes from served log documents
    /// alone -- the post's entry and proof, its key's binding entry and proof, one signed head -- that
    /// the post was signed by the key its author's enrollment bound, earlier in the log. No agent key
    /// set is handed to it. Three controls: another identity's binding offered as the key's is refused
    /// (it binds another identity's <c>kid</c>, exit 1); an identity enrolled before G16, whose log names
    /// its <c>kid</c> and no key, is reported as not checked (exit 3), never as verified; and so is a
    /// post whose author's key the log bound only after it (exit 3), never as failed.
    /// </summary>
    [Fact]
    public async Task R6_54_TestisEstablishesAuthorshipFromTheLogAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var dir = Scratch();

        var (author, postId) = await AskAsync(http, "author", beforeKeyBinding: false, ct);
        var (other, _) = await AskAsync(http, "other", beforeKeyBinding: false, ct);
        var (before, beforePostId) = await AskAsync(http, "before-g16", beforeKeyBinding: true, ct);
        var (late, latePostId) = await AskAsync(http, "bound-late", beforeKeyBinding: true, ct);
        await forum.BindKeyAfterItsPostsAsync(late.AgentId, late.Kid, late.AssertionKey.ExportSubjectPublicKeyInfo(), ct);
        var (exit, _, stderr) = await SignHeadAsync(ct);
        Assert.True(exit == ExitCode.Ok, stderr);

        async Task<long> KeyIndexAsync(ForumAgent agent) =>
            (await http.GetFromJsonAsync<JsonElement>($"/v1/jwks?agent={Uri.EscapeDataString(agent.AgentId)}", ct))
                .GetProperty("keys")[0].TryGetProperty("curia_log_index", out var index)
                ? index.GetInt64()
                : throw new InvalidOperationException($"the key set names no leaf for {agent.AgentId}'s key");

        async Task<string> AuthorAsync(string post, ForumAgent keyOf, string label)
        {
            var postIndex = (await http.GetFromJsonAsync<JsonElement>($"/v1/posts/{post}", ct)).GetProperty("log_index").GetInt64();
            var keyIndex = await KeyIndexAsync(keyOf);
            var args =
                $"log author --entry \"{await SaveAsync(http, $"/v1/log/entries/{postIndex}", dir, label + "-entry.json", ct)}\"" +
                $" --proof \"{await SaveAsync(http, $"/v1/log/proof/{postIndex}", dir, label + "-proof.json", ct)}\"" +
                $" --key-entry \"{await SaveAsync(http, $"/v1/log/entries/{keyIndex}", dir, label + "-key-entry.json", ct)}\"" +
                $" --key-proof \"{await SaveAsync(http, $"/v1/log/proof/{keyIndex}", dir, label + "-key-proof.json", ct)}\"" +
                $" --head \"{await SaveAsync(http, "/v1/log/head", dir, label + "-head.json", ct)}\"" +
                $" --log-jwks \"{await SaveAsync(http, "/v1/log/jwks", dir, label + "-log-jwks.json", ct)}\"";
            var (code, stdout, failure) = TestisBinary.Run(TestisBinary.Locate(), args);
            return $"exit {code}: {stdout}{failure}";
        }

        var verified = await AuthorAsync(postId, author, "own");
        Assert.StartsWith("exit 0:", verified, StringComparison.Ordinal);
        Assert.Contains($"author: {author.AgentId}", verified, StringComparison.Ordinal);
        Assert.Contains($"key_index: {await KeyIndexAsync(author)}", verified, StringComparison.Ordinal);

        var refused = await AuthorAsync(postId, other, "other");
        Assert.StartsWith("exit 1:", refused, StringComparison.Ordinal);
        Assert.Contains("curia/acta/binding-mismatch", refused, StringComparison.Ordinal);

        var notChecked = await AuthorAsync(beforePostId, before, "before");
        Assert.StartsWith("exit 3:", notChecked, StringComparison.Ordinal);
        Assert.Contains("curia/acta/key-not-carried", notChecked, StringComparison.Ordinal);

        var boundLate = await AuthorAsync(latePostId, late, "late");
        Assert.StartsWith("exit 3:", boundLate, StringComparison.Ordinal);
        Assert.Contains("curia/acta/bound-after-post", boundLate, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R6_24_R6_49_TheOperatorSignsAHeadTheForumServesItAndTestisVerifiesItOffline()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var dir = Scratch();

        await PostQuestionAsync(http, ct);
        var (exit, stdout, stderr) = await SignHeadAsync(ct);
        Assert.True(exit == ExitCode.Ok, stderr);
        Assert.Contains("signed head", stdout, StringComparison.Ordinal);

        var head = await http.GetFromJsonAsync<JsonElement>("/v1/log/head", ct);
        Assert.True(head.GetProperty("signature_valid").GetBoolean());
        var treeSize = head.GetProperty("head").GetProperty("tree_size").GetInt64();

        // A head is itself a leaf: it lands right after the leaves it covers, and the log has grown past it.
        Assert.Equal(treeSize, head.GetProperty("log_index").GetInt64());
        Assert.True(head.GetProperty("current_tree_size").GetInt64() > treeSize);

        var headPath = await SaveAsync(http, "/v1/log/head", dir, "head.json", ct);
        var jwksPath = await SaveAsync(http, "/v1/log/jwks", dir, "log-jwks.json", ct);
        var verifier = TestisBinary.Locate();

        var (code, verified, failure) = TestisBinary.Run(verifier, $"log head --head \"{headPath}\" --log-jwks \"{jwksPath}\"");
        Assert.True(code == 0, failure);
        Assert.Contains($"tree_size={treeSize}", verified, StringComparison.Ordinal);

        // One digit inside the signed object, and the head is somebody else's.
        var served = await File.ReadAllTextAsync(headPath, ct);
        var tampered = served.Replace($"\"tree_size\":{treeSize}", $"\"tree_size\":{treeSize + 1}", StringComparison.Ordinal);
        Assert.NotEqual(served, tampered);
        var tamperedPath = Path.Combine(dir, "head-tampered.json");
        await File.WriteAllTextAsync(tamperedPath, tampered, ct);

        var (tamperedCode, _, tamperedFailure) = TestisBinary.Run(verifier, $"log head --head \"{tamperedPath}\" --log-jwks \"{jwksPath}\"");
        Assert.Equal(1, tamperedCode);
        Assert.Contains("curia/jws/signature-invalid", tamperedFailure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R6_18_R6_48_APostCarriesItsLogIndexAndAProofTestisVerifiesFromTheEntry()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var dir = Scratch();

        var postId = await PostQuestionAsync(http, ct);
        var (exit, _, stderr) = await SignHeadAsync(ct);
        Assert.True(exit == ExitCode.Ok, stderr);

        var post = await http.GetFromJsonAsync<JsonElement>($"/v1/posts/{postId}", ct);
        var logIndex = post.GetProperty("log_index").GetInt64();
        var proof = post.GetProperty("inclusion_proof");
        Assert.True(proof.GetProperty("head_signed").GetBoolean());

        var head = await http.GetFromJsonAsync<JsonElement>("/v1/log/head", ct);
        Assert.Equal(head.GetProperty("head").GetProperty("tree_size").GetInt64(), proof.GetProperty("tree_size").GetInt64());
        Assert.Equal(head.GetProperty("head").GetProperty("root_hash").GetString(), proof.GetProperty("root_hash").GetString());

        // The same proof against the log as it stands is not head-signed, and says so.
        var current = await http.GetFromJsonAsync<JsonElement>($"/v1/log/proof/{logIndex}?tree_size={head.GetProperty("current_tree_size").GetInt64()}", ct);
        Assert.False(current.GetProperty("head_signed").GetBoolean());

        var entryPath = await SaveAsync(http, $"/v1/log/entries/{logIndex}", dir, "entry.json", ct);
        var proofPath = await SaveAsync(http, $"/v1/log/proof/{logIndex}", dir, "proof.json", ct);
        var headPath = await SaveAsync(http, "/v1/log/head", dir, "head.json", ct);
        var jwksPath = await SaveAsync(http, "/v1/log/jwks", dir, "log-jwks.json", ct);
        var verifier = TestisBinary.Locate();

        var (code, verified, failure) = TestisBinary.Run(
            verifier, $"log inclusion --entry \"{entryPath}\" --proof \"{proofPath}\" --head \"{headPath}\" --log-jwks \"{jwksPath}\"");
        Assert.True(code == 0, failure);
        Assert.Contains($"log_index: {logIndex}", verified, StringComparison.Ordinal);
        Assert.Contains("head: tree_size=", verified, StringComparison.Ordinal);

        // A changed entry recomputes to a different leaf; the served proof no longer describes it.
        var entry = await File.ReadAllTextAsync(entryPath, ct);
        var tampered = entry.Replace("\"kind\":\"question\"", "\"kind\":\"answer\"", StringComparison.Ordinal);
        Assert.NotEqual(entry, tampered);
        var tamperedPath = Path.Combine(dir, "entry-tampered.json");
        await File.WriteAllTextAsync(tamperedPath, tampered, ct);

        var (tamperedCode, _, tamperedFailure) = TestisBinary.Run(
            verifier, $"log inclusion --entry \"{tamperedPath}\" --proof \"{proofPath}\"");
        Assert.Equal(1, tamperedCode);
        Assert.Contains("curia/acta/leaf-mismatch", tamperedFailure, StringComparison.Ordinal);
    }

    /// <summary>
    /// R10.62 through the Acta: a committed flag is an ordinary leaf, its entry names no post, raiser
    /// or rationale, and <c>curia-testis</c> — which never dispatches on <c>event_type</c> — verifies its
    /// inclusion under a signed head offline, and refuses it once a byte of the entry changes.
    /// </summary>
    [Fact]
    public async Task R10_62_ACommittedFlagIsALeafTestisVerifiesAndItNamesNoRaiserOrPost()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var dir = Scratch();

        var postId = await PostQuestionAsync(http, ct);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var raiser = ForumAgent.Create("https://agents.example/acta-raiser-" + suffix, "acta-raiser-" + suffix);
        using (var enrolled = await raiser.EnrollAsync(http, ct))
            Assert.Equal(HttpStatusCode.Created, enrolled.StatusCode);

        var dpop = DpopClient.For(raiser, raiser.AssertionKey);
        var token = await dpop.GetTokenAsync(http, TokenEndpoint, forum.Now, ct);
        using (var flagged = await dpop.PostAsync(
            http, $"http://localhost/v1/posts/{postId}/flags", token,
            Encoding.UTF8.GetBytes("{\"kind\":\"spam\",\"rationale\":\"Advertising, not a question.\"}"),
            forum.Now, ct, contentType: "application/json"))
            Assert.Equal(HttpStatusCode.Created, flagged.StatusCode);

        var (exit, _, stderr) = await SignHeadAsync(ct);
        Assert.True(exit == ExitCode.Ok, stderr);

        // Found the way any reader finds it: by walking the log.
        long? index = null;
        string? entry = null;
        for (long i = 0; i < 100_000; i++)
        {
            using var response = await http.GetAsync(new Uri($"/v1/log/entries/{i}", UriKind.Relative), ct);
            if (response.StatusCode == HttpStatusCode.NotFound) break;

            var body = await response.Content.ReadAsStringAsync(ct);
            if (body.Contains("\"event_type\":\"flag.committed\"", StringComparison.Ordinal))
                (index, entry) = (i, body);
        }

        Assert.True(index is not null, "no flag.committed leaf in the log: the raise did not commit");
        Assert.DoesNotContain(postId, entry!, StringComparison.Ordinal);
        Assert.DoesNotContain(raiser.AgentId, entry!, StringComparison.Ordinal);
        Assert.DoesNotContain("Advertising", entry!, StringComparison.Ordinal);

        var entryPath = Path.Combine(dir, "entry.json");
        await File.WriteAllTextAsync(entryPath, entry!, ct);
        var proofPath = await SaveAsync(http, $"/v1/log/proof/{index}", dir, "proof.json", ct);
        var headPath = await SaveAsync(http, "/v1/log/head", dir, "head.json", ct);
        var jwksPath = await SaveAsync(http, "/v1/log/jwks", dir, "log-jwks.json", ct);
        var verifier = TestisBinary.Locate();

        var (code, verified, failure) = TestisBinary.Run(
            verifier, $"log inclusion --entry \"{entryPath}\" --proof \"{proofPath}\" --head \"{headPath}\" --log-jwks \"{jwksPath}\"");
        Assert.True(code == 0, failure);
        Assert.Contains($"log_index: {index}", verified, StringComparison.Ordinal);

        // One member changed, and the entry is a different leaf the proof does not describe.
        var tampered = entry!.Replace("\"kind\":\"spam\"", "\"kind\":\"incorrect\"", StringComparison.Ordinal);
        Assert.NotEqual(entry, tampered);
        var tamperedPath = Path.Combine(dir, "entry-tampered.json");
        await File.WriteAllTextAsync(tamperedPath, tampered, ct);

        var (tamperedCode, _, tamperedFailure) = TestisBinary.Run(
            verifier, $"log inclusion --entry \"{tamperedPath}\" --proof \"{proofPath}\"");
        Assert.Equal(1, tamperedCode);
        Assert.Contains("curia/acta/leaf-mismatch", tamperedFailure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R6_23_AConsistencyProofBetweenTwoSignedHeadsVerifiesAndSwappedHeadsDoNot()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var dir = Scratch();

        Assert.Equal(ExitCode.Ok, (await SignHeadAsync(ct)).Exit);
        var earlierPath = await SaveAsync(http, "/v1/log/head", dir, "head-a.json", ct);
        var earlierSize = (await http.GetFromJsonAsync<JsonElement>("/v1/log/head", ct)).GetProperty("head").GetProperty("tree_size").GetInt64();

        await PostQuestionAsync(http, ct);
        Assert.Equal(ExitCode.Ok, (await SignHeadAsync(ct)).Exit);
        var laterPath = await SaveAsync(http, "/v1/log/head", dir, "head-b.json", ct);
        var laterSize = (await http.GetFromJsonAsync<JsonElement>("/v1/log/head", ct)).GetProperty("head").GetProperty("tree_size").GetInt64();
        Assert.True(laterSize > earlierSize);

        var proofPath = await SaveAsync(http, $"/v1/log/consistency?from={earlierSize}&to={laterSize}", dir, "consistency.json", ct);
        var jwksPath = await SaveAsync(http, "/v1/log/jwks", dir, "log-jwks.json", ct);
        var verifier = TestisBinary.Locate();

        var (code, verified, failure) = TestisBinary.Run(
            verifier, $"log consistency --proof \"{proofPath}\" --from-head \"{earlierPath}\" --to-head \"{laterPath}\" --log-jwks \"{jwksPath}\"");
        Assert.True(code == 0, failure);
        Assert.Contains($"from_size: {earlierSize}", verified, StringComparison.Ordinal);
        Assert.Contains("to_head: tree_size=", verified, StringComparison.Ordinal);

        var (swappedCode, _, swappedFailure) = TestisBinary.Run(
            verifier, $"log consistency --proof \"{proofPath}\" --from-head \"{laterPath}\" --to-head \"{earlierPath}\" --log-jwks \"{jwksPath}\"");
        Assert.Equal(1, swappedCode);
        Assert.Contains("curia/acta/head-size-mismatch", swappedFailure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R6_48_ProofsTheLogCannotGiveAreRefusedWithASlug()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;

        using var notALeaf = await http.GetAsync(new Uri("/v1/log/proof/999999999", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.NotFound, notALeaf.StatusCode);
        Assert.Equal("curia/log/not-a-leaf", (await notALeaf.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("type").GetString());

        using var badSizes = await http.GetAsync(new Uri("/v1/log/consistency?from=0&to=1", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.BadRequest, badSizes.StatusCode);
        Assert.Equal("curia/log/bad-sizes", (await badSizes.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("type").GetString());

        using var noEntry = await http.GetAsync(new Uri("/v1/log/entries/999999999", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.NotFound, noEntry.StatusCode);
    }

    [Fact]
    public async Task R6_50_TheLogJwksCarriesTheSigningKeyWithWhenItBecameValid()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;

        Assert.Equal(ExitCode.Ok, (await SignHeadAsync(ct)).Exit);
        var head = await http.GetFromJsonAsync<JsonElement>("/v1/log/head", ct);
        var kid = head.GetProperty("kid").GetString();

        var jwks = await http.GetFromJsonAsync<JsonElement>("/v1/log/jwks", ct);
        var key = Assert.Single(jwks.GetProperty("keys").EnumerateArray(), k => k.GetProperty("kid").GetString() == kid);
        Assert.Equal("EC", key.GetProperty("kty").GetString());
        Assert.Equal("ES256", key.GetProperty("alg").GetString());
        Assert.EndsWith("Z", key.GetProperty("valid_from").GetString(), StringComparison.Ordinal);
        Assert.True(key.GetProperty("log_index").GetInt64() < head.GetProperty("log_index").GetInt64());
    }
}
