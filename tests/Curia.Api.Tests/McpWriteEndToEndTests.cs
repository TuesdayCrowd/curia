using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Curia.Client;
using Curia.Domain.Authorization;
using Curia.Domain.Serving;
using Curia.Mcp;
using Curia.Tests.Shared;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// The MCP plan's Stage 4 against this Forum: the write tools of <c>curia-mcp</c>, driven through
/// the reference client over the HTTP surface the Forum's own tests run against.
///
/// <para><b>The strongest single assertion available</b>, in the plan's words, is Phase 1's exit
/// criterion re-run through the new path: a post written by <c>curia_ask</c> is handed, as served, to
/// <c>curia-testis</c> — the verifier written in a cleanroom with no access to this solution — and it
/// recovers the author from the signed bytes. Run twice: with the registered key in this process, and
/// with it held by an external signer this process never sees the key of (R11.20).</para>
///
/// <para>The rest are the rows a stub cannot settle: a real T0 refusal, a real 409 with a real
/// answer in it, a real flag. <c>StubFidelityTests</c> holds the stub to the same documents; these
/// hold the adapter to the Forum directly.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class McpWriteEndToEndTests(ForumFixture forum) : IClassFixture<ForumFixture>, IDisposable
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-e2e-").FullName;
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Phase 1's exit criterion through <c>curia_ask</c>, with the key in this process.</summary>
    [Fact]
    public async Task Phase1_AQuestionAskedThroughMcpVerifiesUnderTheIndependentVerifier()
    {
        // Not "mcp-ask": "ask-" followed by the random suffix reads as an `sk-` API key in the
        // credential screener's separator-stripped view, and the Forum refuses the envelope. That is
        // a live screener false positive, register entry D17, and not this test's subject.
        var agent = await EnrolAsync("mcp-writer", signer: null);
        await AssertAskVerifiesOfflineAsync(agent);
    }

    /// <summary>
    /// Phase 1's exit criterion through <c>curia_ask</c> with the registered key held by another
    /// process — R11.20's separation, end to end. The profile directory holds no signing key at all,
    /// so there is nothing in this process that could have produced the signature the independent
    /// verifier accepts.
    /// </summary>
    [Fact]
    public async Task R11_20_AQuestionSignedByAnExternalSignerVerifiesUnderTheIndependentVerifier()
    {
        using var signer = TestSigner.Create("mcp-ext-" + Guid.NewGuid().ToString("N")[..8]);
        var agent = await EnrolAsync("mcp-ext", signer);

        var store = new ProfileStore(_home);
        Assert.False(File.Exists(Path.Combine(store.DirectoryFor(agent.Profile.Slug), "signing-key.pem")));

        var before = signer.Signatures;
        await AssertAskVerifiesOfflineAsync(agent);

        // The enrolment's own assertion aside, the ask cost the signer two signatures -- the token's
        // client assertion and the envelope -- and this process none.
        Assert.True(signer.Signatures >= before + 2, $"the signer signed {signer.Signatures - before} times");
    }

    /// <summary>
    /// The plan's row: "A T0 agent calling <c>curia_answer</c> is refused with the tier requirement
    /// stated, not a bare 403" — against this Forum's real denial, which names the deciding table
    /// and the tier and nothing about how to reach the next one.
    /// </summary>
    [Fact]
    public async Task R11_26_AT0AnswerThroughMcpIsRefusedWithWhatReachesT1()
    {
        var questionId = await QuestionAsync(Board(), "Who may answer?", "A question a novice cannot answer.");
        var novice = await EnrolAsync("mcp-novice", signer: null);

        var refused = await Assert.ThrowsAsync<McpException>(
            () => Tools(novice).AnswerAsync(questionId, "Too soon to answer.", Ct));

        Assert.Contains("tier=T0", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Requires trust tier T1 or above", refused.Message, StringComparison.Ordinal);
        Assert.Contains($"at least {TierPolicy.T1MinimumHours} hours since enrolment", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Retrying will not change this", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// R8.19 against a real 409: a question already answered on its board comes back as a
    /// successful result carrying the thread and the answer with its provenance envelope, both
    /// measures against the thresholds this Forum published, and no span of the matched question.
    /// </summary>
    [Fact]
    public async Task R8_19_ADuplicateAskedThroughMcpIsAnsweredWithTheRealThread()
    {
        const string Title = "Why does the pooler drop idle connections";
        const string Body = "The pooler drops a connection idle for ten minutes and the client sees ECONNRESET";
        const string AnswerText = "Enable TCP keepalive below the pooler's idle timeout.";

        var board = Board();
        var questionId = await QuestionAsync(board, Title, Body);
        await AnswerAsT1Async(board, questionId, AnswerText);

        var asker = await EnrolAsync("mcp-dup", signer: null);
        var result = await Tools(asker).AskAsync(board, Title, Body, null, null, Ct);
        var preamble = Assert.IsType<TextContentBlock>(result.Content[0]).Text;

        Assert.NotEqual(true, result.IsError);
        Assert.StartsWith("NOT POSTED: A NEAR DUPLICATE", preamble, StringComparison.Ordinal);
        Assert.Contains(questionId, preamble, StringComparison.Ordinal);
        Assert.Contains("refused at  cosine >= 9400 bp", preamble, StringComparison.Ordinal);
        Assert.Contains("hashed-ngram@1", preamble, StringComparison.Ordinal);

        var passage = Assert.IsType<TextResourceContents>(
            Assert.IsType<EmbeddedResourceBlock>(Assert.Single(result.Content.Skip(1))).Resource);
        Assert.Contains("signature verified locally", passage.Text, StringComparison.Ordinal);

        // Datamarked by the Forum (the adapter's default), so the answer's words arrive marked rather
        // than verbatim -- the Forum drew the boundary. Non-vacuity first: a word of the answer is
        // there, so the sentence's absence is marking and not an empty passage.
        Assert.Contains("keepalive", passage.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(AnswerText, passage.Text, StringComparison.Ordinal);
        Assert.Contains("marking=Datamark", passage.Text, StringComparison.Ordinal);

        // R8.61: nothing of the matched question's body, in any form.
        var all = Flatten(result);
        Assert.DoesNotContain("ECONNRESET", all, StringComparison.Ordinal);
        Assert.DoesNotContain("idle for ten minutes", all, StringComparison.Ordinal);
    }

    /// <summary>R10.35 through the adapter, against this Forum: a T0 agent may flag.</summary>
    [Fact]
    public async Task R10_35_AFlagRaisedThroughMcpIsRecorded()
    {
        var questionId = await QuestionAsync(Board(), "Is this right?", "A premise worth flagging.");
        var flagger = await EnrolAsync("mcp-flag", signer: null);

        var text = Flatten(await Tools(flagger).FlagAsync(questionId, "incorrect", "The premise is wrong.", Ct));

        Assert.StartsWith("FLAGGED", text, StringComparison.Ordinal);
        Assert.Contains(questionId, text, StringComparison.Ordinal);
        Assert.DoesNotContain("The premise is wrong.", text, StringComparison.Ordinal);
    }

    private async Task AssertAskVerifiesOfflineAsync(EnrolledAgent agent)
    {
        var board = Board();
        var result = await Tools(agent).AskAsync(board, "Does an MCP write verify offline?", "Phase 1, through the adapter.", null, null, Ct);
        var text = Flatten(result);
        Assert.StartsWith("POSTED", text, StringComparison.Ordinal);

        var postId = Line(text, "post");
        var printedDigest = Line(text, "digest").Split(' ')[0];

        // The post as any reader gets it, and the key from the Forum's JWKS (R4.16 rev.).
        var served = await forum.Client.GetFromJsonAsync<JsonElement>($"/v1/posts/{postId}", Ct);
        Assert.Equal(agent.Profile.AgentId, served.GetProperty("provenance").GetProperty("author").GetString());
        Assert.Equal(printedDigest, served.GetProperty("digest").GetString());

        var canonical = served.GetProperty("canonical").GetString()!;
        var signature = served.GetProperty("signature").GetString()!;
        var jwks = await forum.Client.GetStringAsync($"/v1/jwks?agent={Uri.EscapeDataString(agent.Profile.AgentId)}", Ct);

        var directory = Directory.CreateTempSubdirectory("curia-mcp-offline-");
        try
        {
            var submissionPath = Path.Combine(directory.FullName, "submission.json");
            var jwksPath = Path.Combine(directory.FullName, "jwks.json");
            await File.WriteAllTextAsync(submissionPath, $"{{\"envelope\":{canonical},\"signature\":\"{signature}\"}}", Ct);
            await File.WriteAllTextAsync(jwksPath, jwks, Ct);

            var (exitCode, stdout, stderr) = TestisBinary.Run(TestisBinary.Locate(), submissionPath, jwksPath);

            Assert.True(exitCode == 0, $"curia-testis rejected a post written through MCP.\nexit={exitCode}\nstdout={stdout}\nstderr={stderr}");
            Assert.Contains(agent.Profile.AgentId, stdout + stderr, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// An identity enrolled through the reference client, as <c>curia enrol</c> enrols one — with an
    /// in-process key, or through an external signer that holds it.
    /// </summary>
    private async Task<EnrolledAgent> EnrolAsync(string stem, TestSigner? signer)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var slug = $"{stem}-{suffix}";
        var agentId = $"https://agents.example/{slug}";
        var store = new ProfileStore(_home);

        Result created;
        EnrolledAgent? agent;
        if (signer is null)
        {
            var made = store.Create(slug, agentId, $"{slug}-k", forum.Client.BaseAddress!);
            created = made.TryGetValue(out agent, out var error) ? Result.Ok : Result.Failed(error!.Detail);
        }
        else
        {
            Assert.True(ExternalSigner.Describe(signer.Command).TryGetValue(out var external, out var describeError), describeError?.Detail);
            var made = store.Create(slug, agentId, forum.Client.BaseAddress!, external!);
            created = made.TryGetValue(out agent, out var error) ? Result.Ok : Result.Failed(error!.Detail);
        }

        Assert.True(created.Succeeded, created.Detail);
        _owned.Add(agent!);

        var enrolled = await new ForumClient(forum.Client, forum.Client.BaseAddress!).EnrolAsync(agent!, Ct);
        Assert.True(enrolled.TryGetValue(out _, out var refusal), refusal?.Summary);

        return agent!;
    }

    private ForumTools Tools(EnrolledAgent agent)
    {
        var client = new ForumClient(forum.Client, forum.Client.BaseAddress!);
        var session = new ForumSession(client, agent, new ProfileStore(_home), forum.Clock);
        return new ForumTools(client, MarkingMode.Datamark, new HeadStore(_home), new ForumWriter(agent, session, forum.Clock));
    }

    /// <summary>A question from an agent outside the adapter, through the Forum's own test helpers.</summary>
    private async Task<string> QuestionAsync(string board, string title, string body)
    {
        var asker = ForumAgent.Create(Unique("asker"), "asker-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await asker.AuthenticateAsync(forum.Client, TokenEndpoint, forum.Now, Ct);

        using var asked = await dpop.PostAsync(forum.Client, PostsUrl, token, asker.SignQuestion(board, body, title, forum.Now), forum.Now, Ct);
        Assert.Equal(HttpStatusCode.Created, asked.StatusCode);
        return JsonNode.Parse(await asked.Content.ReadAsStringAsync(Ct))!["post_id"]!.GetValue<string>();
    }

    /// <summary>An answer from a T1 agent: an attested owner, three questions, and the tenure clock.</summary>
    private async Task AnswerAsT1Async(string board, string questionId, string body)
    {
        var helper = ForumAgent.Create(Unique("helper"), "helper-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, _) = await helper.AuthenticateAsync(forum.Client, TokenEndpoint, forum.Now, Ct);
        await forum.AttestOwnerAsync(helper.AgentId, Ct, owner: "owner:" + helper.Kid);

        for (var i = 0; i < TierPolicy.T1MinimumCleanQuestions; i++)
        {
            var warm = Guid.NewGuid().ToString("N");
            using var warmed = await dpop.PostAsync(
                forum.Client, PostsUrl, await dpop.GetTokenAsync(forum.Client, TokenEndpoint, forum.Now, Ct),
                helper.SignQuestion(board, $"warmup {i} {warm}", $"Warmup {i} {warm}", forum.Now), forum.Now, Ct);
            Assert.Equal(HttpStatusCode.Created, warmed.StatusCode);
        }

        forum.Clock.Advance(TimeSpan.FromHours(TierPolicy.T1MinimumHours + 1));

        using var answered = await dpop.PostAsync(
            forum.Client, PostsUrl, await dpop.GetTokenAsync(forum.Client, TokenEndpoint, forum.Now, Ct),
            helper.SignAnswer(board, body, questionId, forum.Now), forum.Now, Ct);
        Assert.Equal(HttpStatusCode.Created, answered.StatusCode);
    }

    private static string Line(string text, string label) =>
        text.Split('\n').First(l => l.StartsWith(label + " ", StringComparison.Ordinal))[label.Length..].Trim();

    private static string Flatten(CallToolResult result)
    {
        var builder = new StringBuilder();
        foreach (var block in result.Content)
        {
            switch (block)
            {
                case TextContentBlock text:
                    builder.AppendLine(text.Text);
                    break;
                case EmbeddedResourceBlock { Resource: TextResourceContents resource }:
                    builder.AppendLine(resource.Text);
                    break;
                default:
                    break;
            }
        }

        return builder.ToString();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Board() => "mcp-" + Guid.NewGuid().ToString("N")[..8];

    private static string Unique(string stem) => $"https://agents.example/{stem}-{Guid.NewGuid().ToString("N")[..8]}";

    private readonly record struct Result(bool Succeeded, string? Detail)
    {
        internal static Result Ok => new(true, null);

        internal static Result Failed(string? detail) => new(false, detail);
    }
}
