using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.OperatorTool;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R6.52, R6.53 and R11.29 against a real Forum: the reference client verifies a post it read,
/// and the independently written Rust verifier agrees with it about the same material.
///
/// <para><b>Why this exists beside the stub-driven suite.</b> <c>Curia.Client.Tests</c> exercises
/// every branch against a Forum this repository wrote, which can only ever confirm that the client
/// agrees with a fixture the same authors built. This class runs the client against the Forum the
/// Forum's own tests run against, and then hands the same four documents to <c>curia-testis</c> --
/// a verifier built in a cleanroom with no access to the C# implementation. The disagreement is the
/// product; two implementations agreeing is the assertion.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ClientVerificationTests(ForumFixture forum) : IClassFixture<ForumFixture>, IDisposable
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    private static readonly string LogKeyPem = LogSigningKey.GeneratePem();

    private readonly string _home = Directory.CreateTempSubdirectory("curia-client-verification-").FullName;

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The whole chain, once: signature over re-canonicalized bytes, a leaf recomputed from the
    /// log's own entry, an audit path to a root, and a head signed by a key the Forum never holds.
    /// </summary>
    [Fact]
    public async Task R6_52_TheReferenceClientVerifiesARealPostAgainstARealSignedHead()
    {
        var ct = TestContext.Current.CancellationToken;
        var postId = await PostQuestionAsync(ct);
        Assert.Equal(ExitCode.Ok, await SignHeadAsync(ct));

        var result = await VerifyAsync(postId, new HeadStore(_home), ct);

        Assert.Equal(CheckOutcome.Verified, result.Signature.Outcome);
        Assert.Equal(CheckOutcome.Verified, result.Inclusion.Outcome);
        Assert.Equal(CheckOutcome.Verified, result.Overall);

        // The proof was anchored to a head, and the anchor is named rather than implied: a
        // verification that reported success against no anchor is the one this stage exists to
        // make impossible.
        Assert.NotNull(result.AnchorTreeSize);
        Assert.NotNull(result.LogIndex);
        Assert.True(result.LogIndex < result.AnchorTreeSize);
    }

    /// <summary>
    /// Cross-implementation: the same served documents, through two verifiers written from the same
    /// specification and no shared code. This is Phase 1's exit criterion re-run over the Acta.
    /// </summary>
    [Fact]
    public async Task R6_52_TheCSharpAndRustVerifiersAgreeAboutTheSamePost()
    {
        var ct = TestContext.Current.CancellationToken;
        var postId = await PostQuestionAsync(ct);
        Assert.Equal(ExitCode.Ok, await SignHeadAsync(ct));

        var mine = await VerifyAsync(postId, new HeadStore(_home), ct);
        Assert.Equal(CheckOutcome.Verified, mine.Inclusion.Outcome);

        var dir = Directory.CreateTempSubdirectory("curia-cross-").FullName;
        var index = mine.LogIndex!.Value;
        var entry = await SaveAsync($"/v1/log/entries/{index}", dir, "entry.json", ct);
        var proof = await SaveAsync($"/v1/log/proof/{index}?tree_size={mine.AnchorTreeSize}", dir, "proof.json", ct);
        var head = await SaveAsync("/v1/log/head", dir, "head.json", ct);
        var jwks = await SaveAsync("/v1/log/jwks", dir, "log-jwks.json", ct);

        var (code, stdout, stderr) = TestisBinary.Run(
            TestisBinary.Locate(),
            $"log inclusion --entry \"{entry}\" --proof \"{proof}\" --head \"{head}\" --log-jwks \"{jwks}\"");

        Assert.True(code == 0, stderr);
        Assert.Contains($"log_index: {index}", stdout, StringComparison.Ordinal);

        // Not merely "both said yes": both climbed to the same root, from a leaf each recomputed
        // for itself. A shared root is what makes the agreement mean something.
        Assert.Contains("head: tree_size=", stdout, StringComparison.Ordinal);
        Assert.Contains(RootOf(stdout), mine.Inclusion.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// R6.53's loop over a real log: retain, grow, prove the growth, advance. The second
    /// verification is the first one in this repository that has ever had a retained head to check
    /// against -- which is why the plan's trap 2 warns that a consistency assertion written before
    /// this point would have passed while checking nothing.
    /// </summary>
    [Fact]
    public async Task R6_53_ASecondVerificationChecksTheGrownLogAgainstTheRetainedHead()
    {
        var ct = TestContext.Current.CancellationToken;
        var heads = new HeadStore(_home);

        var first = await PostQuestionAsync(ct);
        Assert.Equal(ExitCode.Ok, await SignHeadAsync(ct));

        var before = await VerifyAsync(first, heads, ct);
        Assert.Equal(CheckOutcome.CouldNotCheck, before.Consistency.Outcome);

        var retained = heads.Read(forum.Client.BaseAddress!);
        Assert.NotNull(retained);

        // The log grows and the operator signs again.
        var second = await PostQuestionAsync(ct);
        Assert.Equal(ExitCode.Ok, await SignHeadAsync(ct));

        var after = await VerifyAsync(second, heads, ct);

        Assert.Equal(CheckOutcome.Verified, after.Consistency.Outcome);
        Assert.Equal(CheckOutcome.Verified, after.Overall);

        // And the retained head advanced, which is the half of R6.53 that a passing consistency
        // check alone would not establish.
        var advanced = heads.Read(forum.Client.BaseAddress!);
        Assert.NotNull(advanced);
        Assert.True(advanced.TreeSize > retained.TreeSize);
    }

    /// <summary>
    /// A post accepted since the operator last signed. The Forum serves its proof against the whole
    /// log with <c>head_signed: false</c>, and the honest answer is that the check could not run --
    /// not that it failed, which would raise an alarm about the post every time a signing schedule
    /// fell behind.
    /// </summary>
    [Fact]
    public async Task R6_48_APostNewerThanTheLatestSignedHeadIsReportedAsNotYetCheckable()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(ExitCode.Ok, await SignHeadAsync(ct));

        var postId = await PostQuestionAsync(ct);
        var result = await VerifyAsync(postId, new HeadStore(_home), ct);

        Assert.Equal(CheckOutcome.CouldNotCheck, result.Inclusion.Outcome);
        Assert.Contains("not covered by the signed head", result.Inclusion.Detail, StringComparison.Ordinal);

        // Authorship does not wait on the operator's cron.
        Assert.Equal(CheckOutcome.Verified, result.Signature.Outcome);
        Assert.NotEqual(CheckOutcome.Failed, result.Overall);
    }

    private async Task<PostVerification> VerifyAsync(string postId, HeadStore heads, CancellationToken ct)
    {
        var client = new ForumClient(forum.Client, forum.Client.BaseAddress!);
        var verifier = new PostVerifier(client, heads);

        var result = await verifier.VerifyAsync(postId, MarkingMode.None, ct);
        Assert.True(result.TryGetValue(out var verification, out var refusal), refusal?.Summary);
        return verification!;
    }

    /// <summary>The root <c>curia-testis</c> printed, as it printed it, for comparison against ours.</summary>
    private static string RootOf(string stdout) =>
        stdout.Split('\n').First(l => l.StartsWith("root: ", StringComparison.Ordinal))["root: ".Length..].Trim();

    private async Task<int> SignHeadAsync(CancellationToken ct)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        return await OperatorCommands.RunAsync(
            ["sign-head", "--by", "ops"], forum.ConnectionString, forum.Clock, stdout, stderr, ct, logSigningKeyPem: LogKeyPem);
    }

    private async Task<string> PostQuestionAsync(CancellationToken ct)
    {
        var http = forum.Client;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agent = ForumAgent.Create("https://agents.example/verify-" + suffix, "verify-" + suffix);

        using (var enrolled = await agent.EnrollAsync(http, ct))
            Assert.Equal(HttpStatusCode.Created, enrolled.StatusCode);

        var dpop = DpopClient.For(agent, agent.AssertionKey);
        var token = await dpop.GetTokenAsync(http, TokenEndpoint, forum.Now, ct);

        using var posted = await dpop.PostAsync(
            http,
            PostsUrl,
            token,
            agent.SignQuestion("board-" + suffix, "Does the client verify this?", "R6.52 says it must", forum.Now),
            forum.Now,
            ct);

        Assert.Equal(HttpStatusCode.Created, posted.StatusCode);
        var body = await posted.Content.ReadFromJsonAsync<JsonElement>(ct);
        return body.GetProperty("post_id").GetString()!;
    }

    private async Task<string> SaveAsync(string url, string dir, string name, CancellationToken ct)
    {
        using var response = await forum.Client.GetAsync(new Uri(url, UriKind.Relative), ct);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {response.StatusCode}");

        var path = Path.Combine(dir, name);
        await File.WriteAllBytesAsync(path, await response.Content.ReadAsByteArrayAsync(ct), ct);
        return path;
    }
}
