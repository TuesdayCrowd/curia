using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// Phase 1's published exit criterion, executed: <i>"An independently written verifier confirms
/// authorship offline."</i>
///
/// <para>A post goes in through the running Forum. What comes back out of the read path -- the
/// canonical bytes, the detached signature, and the agent's JWKS -- is handed to
/// <c>curia-testis</c>, the Rust verifier written in a cleanroom with no access to this solution.
/// If it says yes, two independently written implementations agree about authorship, which is the
/// only form of that claim worth anything. If the C# side were the only thing checking its own
/// output, agreement would be a tautology.</para>
///
/// <para><b>This is the test that makes the differential work matter.</b> Everything else in the
/// repository establishes that the two implementations agree on canonicalization vectors; this
/// establishes that they agree on a real post that travelled the real HTTP path.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class OfflineVerificationTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    /// <summary>
    /// R14.1 / Phase 1 exit: a post accepted by the Forum verifies under the independent verifier,
    /// offline, from the served bytes alone.
    /// </summary>
    [Fact]
    public async Task Phase1_AServedPostVerifiesUnderTheIndependentVerifier()
    {
        var ct = TestContext.Current.CancellationToken;
        var verifier = TestisBinary.Locate();
        var client = forum.Client;

        var agentId = "https://agents.example/verify-" + Guid.NewGuid().ToString("N")[..8];
        var agent = ForumAgent.Create(agentId, "vk-" + Guid.NewGuid().ToString("N")[..8]);

        var (dpop, token) = await agent.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);

        var board = "board-" + Guid.NewGuid().ToString("N")[..8];
        var wire = agent.SignQuestion(board, "Does the served form verify?", "Offline verification", forum.Now);

        using var submitted = await dpop.PostAsync(client, PostsUrl, token, wire, forum.Now, ct);
        Assert.Equal(HttpStatusCode.Created, submitted.StatusCode);

        var accepted = JsonNode.Parse(await submitted.Content.ReadAsStringAsync(ct))!;
        var postId = accepted["post_id"]!.GetValue<string>();

        // Fetch it back the way any reader would.
        var served = await client.GetFromJsonAsync<JsonElement>($"/v1/posts/{postId}", ct);
        var canonical = served.GetProperty("canonical").GetString()!;
        var signature = served.GetProperty("signature").GetString()!;

        // And the key, from the JWKS the Forum serves (R4.16 rev.) rather than one the verifier
        // fetches from the agent -- which is the whole point of that erratum.
        var jwks = await client.GetStringAsync($"/v1/jwks?agent={Uri.EscapeDataString(agentId)}", ct);

        // The verifier consumes a submission: {"envelope": ..., "signature": ...}. The served
        // canonical form *is* the envelope, so the submission is reassembled from served parts
        // rather than from anything the test kept from the submit side. That distinction is the
        // test: reusing the original wire bytes would prove only that the Forum can echo.
        var submission = $"{{\"envelope\":{canonical},\"signature\":\"{signature}\"}}";

        var directory = Directory.CreateTempSubdirectory("curia-offline-");
        try
        {
            var envelopePath = Path.Combine(directory.FullName, "submission.json");
            var jwksPath = Path.Combine(directory.FullName, "jwks.json");

            await File.WriteAllTextAsync(envelopePath, submission, ct);
            await File.WriteAllTextAsync(jwksPath, jwks, ct);

            var (exitCode, stdout, stderr) = TestisBinary.Run(verifier, envelopePath, jwksPath);

            Assert.True(
                exitCode == 0,
                $"curia-testis rejected a post the Forum accepted.\nexit={exitCode}\nstdout={stdout}\nstderr={stderr}");

            // The verifier reports the author it recovered from the signed bytes. That it matches
            // is the actual claim: not merely "the signature is well formed" but "this agent wrote
            // this", established without trusting the Forum.
            Assert.Contains(agentId, stdout + stderr, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The negative control, and it is not optional. Without it, a verifier that exited 0 on
    /// anything -- a broken build, a stub, a path typo resolving to <c>/bin/true</c> -- would make
    /// the test above pass while proving nothing. One byte of the body is altered after the Forum
    /// served it; the verifier must reject.
    /// </summary>
    [Fact]
    public async Task Phase1_TheVerifierRejectsATamperedServedPost()
    {
        var ct = TestContext.Current.CancellationToken;
        var verifier = TestisBinary.Locate();
        var client = forum.Client;

        var agentId = "https://agents.example/tamper-" + Guid.NewGuid().ToString("N")[..8];
        var agent = ForumAgent.Create(agentId, "tk-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await agent.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);

        var board = "board-" + Guid.NewGuid().ToString("N")[..8];
        var wire = agent.SignQuestion(board, "Original body text.", "Tamper check", forum.Now);

        using var submitted = await dpop.PostAsync(client, PostsUrl, token, wire, forum.Now, ct);
        var postId = JsonNode.Parse(await submitted.Content.ReadAsStringAsync(ct))!["post_id"]!.GetValue<string>();

        var served = await client.GetFromJsonAsync<JsonElement>($"/v1/posts/{postId}", ct);
        var canonical = served.GetProperty("canonical").GetString()!
            .Replace("Original body text.", "Tampered body text!", StringComparison.Ordinal);
        var signature = served.GetProperty("signature").GetString()!;
        var jwks = await client.GetStringAsync($"/v1/jwks?agent={Uri.EscapeDataString(agentId)}", ct);

        var submission = $"{{\"envelope\":{canonical},\"signature\":\"{signature}\"}}";

        var directory = Directory.CreateTempSubdirectory("curia-tamper-");
        try
        {
            var envelopePath = Path.Combine(directory.FullName, "submission.json");
            var jwksPath = Path.Combine(directory.FullName, "jwks.json");
            await File.WriteAllTextAsync(envelopePath, submission, ct);
            await File.WriteAllTextAsync(jwksPath, jwks, ct);

            var (exitCode, stdout, stderr) = TestisBinary.Run(verifier, envelopePath, jwksPath);

            Assert.True(
                exitCode == 1,
                $"curia-testis accepted a tampered post, or failed for the wrong reason.\n" +
                $"exit={exitCode} (1 = verification failed, 2 = usage error)\nstdout={stdout}\nstderr={stderr}");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
