using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Curia.Application.Ports;
using Curia.Client;
using Curia.Domain.Authorization;
using Curia.Domain.Serving;
using Curia.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
            () => Tools(novice).AnswerAsync(questionId, "Too soon to answer.", null, Ct));

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

    /// <summary>
    /// D51 against the real Forum: a credential the PDP quarantines (R4.39) is told, through the
    /// adapter, that it is quarantined, and the text names no tier it could reach.
    /// </summary>
    [Fact]
    public async Task D51_AQuarantinedAgentThroughMcpIsToldItIsQuarantined()
    {
        var questionId = await QuestionAsync(Board(), "Is this answerable?", "A question for a quarantined answerer.");
        using var quarantined = forum.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPolicyDecisionPoint>();
            services.AddSingleton<IPolicyDecisionPoint>(new QuarantinedPolicyDecisionPoint(new DomainPolicyDecisionPoint()));
        }));
        using var http = quarantined.CreateClient();

        var slug = "mcp-quarantine-" + Guid.NewGuid().ToString("N")[..8];
        var store = new ProfileStore(_home);
        Assert.True(store.Create(slug, $"https://agents.example/{slug}", $"{slug}-k", http.BaseAddress!).TryGetValue(out var agent, out var error), error?.Detail);
        _owned.Add(agent!);
        var client = new ForumClient(http, http.BaseAddress!);
        var enrolled = await client.EnrolAsync(agent!, Ct);
        Assert.True(enrolled.TryGetValue(out _, out var enrolRefusal), enrolRefusal?.Summary);

        var session = new ForumSession(client, agent!, store, forum.Clock);
        var tools = new ForumTools(client, MarkingMode.Datamark, new HeadStore(_home), new ForumWriter(agent!, session, forum.Clock));

        var refused = await Assert.ThrowsAsync<McpException>(() => tools.AnswerAsync(questionId, "A quarantined answer.", null, Ct));

        Assert.StartsWith("REFUSED: ", refused.Message, StringComparison.Ordinal);
        Assert.Contains("quarantined", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("trust tier", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// R11.30 (rev.), against this Forum: a T0 MCP agent replies to a question with
    /// <c>curia_comment</c>, and the Forum serves a <c>comment</c> whose signed form holds its parent.
    /// </summary>
    [Fact]
    public async Task R11_17_ACommentWrittenThroughMcpByATierZeroAgentIsServedUnderItsParent()
    {
        var board = Board();
        var questionId = await QuestionAsync(board, "Who replies?", "A question a novice can reply to.");
        var commenter = await EnrolAsync("mcp-comment", signer: null);

        var receipt = Flatten(await RegisteredTools(commenter, "curia_comment").CommentAsync(questionId, "A reply from a novice.", null, Ct));
        Assert.StartsWith("POSTED", receipt, StringComparison.Ordinal);

        var canonical = await CanonicalOfAsync(PostedId(receipt));
        Assert.Equal("comment", canonical.GetProperty("kind").GetString());
        Assert.Equal(questionId, canonical.GetProperty("parent").GetString());
        Assert.Equal(board, canonical.GetProperty("board").GetString());

        // And it is a post any reader reads through the adapter's own read tool.
        var read = Flatten(await Tools(commenter).ReadAsync(PostedId(receipt), Ct));
        Assert.Contains("novice", read, StringComparison.Ordinal);
    }

    /// <summary>
    /// R11.30 (rev.), against this Forum: the agent that asked the thread accepts the answer it was
    /// given with <c>curia_resolve</c>, and a read of the answer serves it as accepted.
    /// </summary>
    [Fact]
    public async Task R11_17_TheAskerResolvesTheAnswerItWasGivenAndTheForumServesItAccepted()
    {
        var (answerId, asker, _) = await AnsweredThreadAsync();
        Assert.False((await ServedAsync(answerId)).GetProperty("accepted").GetBoolean());

        var text = Flatten(await RegisteredTools(asker, "curia_resolve").ResolveAsync(answerId, Ct));

        Assert.StartsWith("ACCEPTED", text, StringComparison.Ordinal);
        Assert.True((await ServedAsync(answerId)).GetProperty("accepted").GetBoolean());
    }

    /// <summary>
    /// A second MCP agent that did not ask the thread is refused with the Forum's own-thread-only
    /// cause, worded as ownership and never as a tier it could climb (D51).
    /// </summary>
    [Fact]
    public async Task R11_17_AnUnrelatedAgentResolvingTheAnswerIsRefusedAsNotItsThread()
    {
        var (answerId, _, _) = await AnsweredThreadAsync();
        var stranger = await EnrolAsync("mcp-stranger", signer: null);

        var refused = await Assert.ThrowsAsync<McpException>(() => RegisteredTools(stranger, "curia_resolve").ResolveAsync(answerId, Ct));

        Assert.Contains("table-10/own-thread-only", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Requires trust tier", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("this agent's trust tier", refused.Message, StringComparison.Ordinal);
        Assert.False((await ServedAsync(answerId)).GetProperty("accepted").GetBoolean());
    }

    /// <summary>
    /// R11.35, against this Forum: a T1 MCP agent answers with a post digest and a URL, and the signed
    /// form the Forum serves holds two <c>target</c> members.
    /// </summary>
    [Fact]
    public async Task R11_35_AnAnswerWrittenThroughMcpCarriesItsReferencesInTheServedSignedForm()
    {
        var board = Board();
        var questionId = await QuestionAsync(board, "What backs the claim?", "A question whose answer should cite.");
        var answerer = await EnrolAsT1Async("mcp-cites", board);
        var cited = "sha256:" + new string('b', 64);

        var receipt = Flatten(await RegisteredTools(answerer, "curia_answer").AnswerAsync(questionId, "Cited.", [cited, "https://example.org/evidence"], Ct));
        Assert.StartsWith("POSTED", receipt, StringComparison.Ordinal);

        var refs = (await CanonicalOfAsync(PostedId(receipt))).GetProperty("refs");
        Assert.Equal(2, refs.GetArrayLength());
        Assert.Equal([cited, "https://example.org/evidence"], refs.EnumerateArray().Select(r => r.GetProperty("target").GetString()));
    }

    /// <summary>A question asked by an MCP agent and answered by a T1 helper: the answer's id, the asker, the board.</summary>
    private async Task<(string AnswerId, EnrolledAgent Asker, string Board)> AnsweredThreadAsync()
    {
        var board = Board();
        var asker = await EnrolAsync("mcp-asker", signer: null);
        var asked = Flatten(await Tools(asker).AskAsync(board, "Who accepts?", "A question its asker will resolve.", null, null, Ct));
        Assert.StartsWith("POSTED", asked, StringComparison.Ordinal);

        var answerId = await AnswerAsT1Async(board, PostedId(asked), "The answer the asker accepts.");
        return (answerId, asker, board);
    }

    private async Task AssertAskVerifiesOfflineAsync(EnrolledAgent agent)
    {
        var board = Board();
        var result = await Tools(agent).AskAsync(board, "Does an MCP write verify offline?", "Phase 1, through the adapter.", null, null, Ct);
        var text = Flatten(result);
        Assert.StartsWith("POSTED", text, StringComparison.Ordinal);

        // The receipt quotes the Forum's post id (R10.63): a display literal is a JSON string, so a
        // JSON parser recovers the value exactly.
        var postId = JsonSerializer.Deserialize<string>(Line(text, "post"))!;
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
    private async Task<string> AnswerAsT1Async(string board, string questionId, string body)
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
        return JsonNode.Parse(await answered.Content.ReadAsStringAsync(Ct))!["post_id"]!.GetValue<string>();
    }

    /// <summary>
    /// An MCP agent brought to T1 as an operator would: an attested owner, three clean questions of
    /// its own (asked through the adapter), and the tenure clock.
    /// </summary>
    private async Task<EnrolledAgent> EnrolAsT1Async(string stem, string board)
    {
        var agent = await EnrolAsync(stem, signer: null);
        await forum.AttestOwnerAsync(agent.Profile.AgentId, Ct, owner: "owner:" + agent.Profile.Slug);

        var tools = Tools(agent);
        for (var i = 0; i < TierPolicy.T1MinimumCleanQuestions; i++)
        {
            var warm = Guid.NewGuid().ToString("N");
            var posted = Flatten(await tools.AskAsync(board, $"Warmup {i} {warm}", $"warmup {i} {warm}", null, null, Ct));
            Assert.StartsWith("POSTED", posted, StringComparison.Ordinal);
        }

        forum.Clock.Advance(TimeSpan.FromHours(TierPolicy.T1MinimumHours + 1));
        return agent;
    }

    /// <summary>
    /// The tools as the catalogue registers them for this agent, which must hold the tool a fact
    /// exercises. These facts call the method a tool is bound to, and a method that no registration
    /// reaches is a fact that passes over a surface that cannot do the thing (FM12's finding).
    /// </summary>
    private ForumTools RegisteredTools(EnrolledAgent agent, params string[] names)
    {
        var tools = Tools(agent);
        var registered = ToolCatalogue.Build(tools).Select(t => t.ProtocolTool.Name).ToArray();
        foreach (var name in names) Assert.Contains(name, registered);
        return tools;
    }

    /// <summary>The id a receipt printed, recovered from its display literal (R10.63).</summary>
    private static string PostedId(string receipt) => JsonSerializer.Deserialize<string>(Line(receipt, "post"))!;

    private async Task<JsonElement> ServedAsync(string postId) =>
        await forum.Client.GetFromJsonAsync<JsonElement>($"/v1/posts/{postId}", Ct);

    private async Task<JsonElement> CanonicalOfAsync(string postId)
    {
        var served = await ServedAsync(postId);
        return JsonDocument.Parse(served.GetProperty("canonical").GetString()!).RootElement.Clone();
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
