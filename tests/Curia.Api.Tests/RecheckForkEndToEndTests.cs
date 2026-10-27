using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using Curia.Api.Tests.Fuzz;
using Curia.Client;
using Curia.Domain.Authorization;
using Curia.Domain.Content;
using Curia.Domain.Serving;
using Curia.Mcp;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// Errata G42 (R9.43, R10.83) against a real Forum on a real socket: <c>curia recheck</c> and <c>curia_recheck</c> over a fork, an answer whose
/// acceptance was switched away and a plain post tell each branch it is one of two revisions of the same post and FORKED, give the answer and the
/// plain post the standing words, and neither prints what the erratum forbids; the verb's exit is the one it always had.
///
/// <para><b>Oracles.</b> The digests are those of the receipts of the calls that made the posts, and the second revision is signed in process through
/// the client library (the fork the Forum cannot prevent and the reference client will not make, as <c>RecheckEndToEndTests</c> does); the phrase,
/// the capitals and the forbidden words are parsed from the errata (<see cref="PublishedForkWords"/>).</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class RecheckForkEndToEndTests(KestrelForumFixture forum) : IClassFixture<KestrelForumFixture>, IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("curia-recheck-fork-").FullName;
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Address => forum.Client.BaseAddress!.GetLeftPart(UriPartial.Authority);

    [GeneratedRegex(@"^digest\s+(?<digest>sha256:[0-9a-f]{64})", RegexOptions.Multiline)]
    private static partial Regex ReceiptDigest();

    private ForumTools ToolsFor(EnrolledAgent agent)
    {
        var client = new ForumClient(forum.Client, forum.Client.BaseAddress!);
        var session = new ForumSession(client, agent, new ProfileStore(_home), forum.Clock);
        return new ForumTools(client, MarkingMode.Datamark, new HeadStore(_home), new ForumWriter(agent, session, forum.Clock));
    }

    private async Task<EnrolledAgent> EnrolAsync(string slug)
    {
        Assert.True(
            new ProfileStore(_home).Create(slug, $"https://agents.example/{slug}", $"{slug}-k", forum.Client.BaseAddress!).TryGetValue(out var agent, out var error),
            error?.Detail);
        _owned.Add(agent!);
        Assert.True((await new ForumClient(forum.Client, forum.Client.BaseAddress!).EnrolAsync(agent!, Ct)).TryGetValue(out _, out var refusal), refusal?.Summary);
        return agent!;
    }

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

    private static string PostIdFrom(string receipt) =>
        System.Text.Json.JsonSerializer.Deserialize<string>(receipt.Split('\n').First(l => l.StartsWith("post ", StringComparison.Ordinal))["post".Length..].Trim())!;

    private static string DigestFrom(string receipt) => ReceiptDigest().Match(receipt).Groups["digest"].Value;

    /// <summary>The line for an element asked about: the state word, then the digest.</summary>
    private static string LineNaming(string text, string digest) =>
        Assert.Single(text.Split('\n'), l => Regex.IsMatch(l, "^[A-Za-z]+ +\"" + Regex.Escape(digest) + "\""));

    private static readonly string[] StandingFragments = ["no later revision", "standing", "no signature", "no acceptance"];

    [Fact]
    public async Task G42_BothReadersTellTheBranchAndNeitherSaysCite()
    {
        var tenure = TimeSpan.FromHours(TierPolicy.T1MinimumHours + 1);

        // Both agents as in CliParentEndToEndTests: enrolled and warmed up as if 49 hours ago, so each posts at T1's budget (accepting an answer
        // spends the asker's, which a T0 asker's three posts would already have used). The answerer's first warmup question is the plain post.
        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now - tenure);
        var board = "g42-" + Guid.NewGuid().ToString("N")[..8];
        string? plain = null;
        async Task<(EnrolledAgent Agent, ForumTools Tools)> WarmAsync(string slug)
        {
            var agent = await EnrolAsync(slug);
            await forum.AttestOwnerAsync(agent.Profile.AgentId, Ct, owner: $"owner:{slug}");
            var tools = ToolsFor(agent);
            for (var i = 0; i < TierPolicy.T1MinimumCleanQuestions; i++)
            {
                var warm = Flatten(await tools.AskAsync(board, $"Warmup {slug} {i} {Guid.NewGuid():N}", $"warmup body {i} {Guid.NewGuid():N}", null, null, Ct));
                if (slug == "answerer") plain ??= DigestFrom(warm);
            }

            return (agent, tools);
        }

        var (answerer, answererTools) = await WarmAsync("answerer");
        var (asker, askerTools) = await WarmAsync("asker");

        // The CLI stamps real time on what it signs and proves; the fixture's clock is fixed, and R6.32 refuses the future.
        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now);

        var asked = Flatten(await askerTools.AskAsync(board, $"Which wren {Guid.NewGuid():N}", "A question about a wren.", null, null, Ct));
        var questionId = PostIdFrom(asked);
        var question = DigestFrom(asked);
        var first = DigestFrom(Flatten(await askerTools.ReviseAsync(questionId, "First correction: which wren?", null, null, null, Ct)));

        // The second revision of the same post, signed in process: curia_revise refuses a post already superseded (G34).
        var draft = new PostDraft { Kind = PostKind.Revision, Board = board, Body = "Second correction: which wren, then?", Parent = questionId, Prev = question };
        Assert.True(SubmissionBuilder.Build(asker, draft, DateTimeOffset.UtcNow).TryGetValue(out var submission, out var buildError), buildError?.Detail);
        var forkSession = new ForumSession(new ForumClient(forum.Client, forum.Client.BaseAddress!), asker, new ProfileStore(_home), forum.Clock);
        var posted = await forkSession.SubmitAsync(submission!.Wire, Ct);
        Assert.True(posted.TryGetValue(out var receipt, out var refusal), refusal?.Summary);
        var second = receipt!.Digest;

        // An answer whose acceptance was switched away: two answers to the question, the first accepted and then the second.
        var firstAnswer = Flatten(await answererTools.AnswerAsync(questionId, "It is a house wren.", null, Ct));
        var secondAnswer = Flatten(await answererTools.AnswerAsync(questionId, "It is a winter wren.", null, Ct));
        Assert.StartsWith("POSTED", firstAnswer, StringComparison.Ordinal);
        Assert.StartsWith("POSTED", secondAnswer, StringComparison.Ordinal);
        Assert.StartsWith("ACCEPTED", Flatten(await askerTools.ResolveAsync(PostIdFrom(firstAnswer), Ct)), StringComparison.Ordinal);
        Assert.StartsWith("ACCEPTED", Flatten(await askerTools.ResolveAsync(PostIdFrom(secondAnswer), Ct)), StringComparison.Ordinal);
        var switchedAway = DigestFrom(firstAnswer);

        string[] digests = [first, second, switchedAway, plain!];

        var cli = await CuriaProcess.RunAsync(_home, Ct, ["recheck", .. digests, "--forum", Address]);
        await using var session = await McpProtocolSession.StartAsync(answererTools, answerer.Profile.AgentId);
        var mcp = await session.CallAsync("curia_recheck", new() { ["digests"] = digests });

        Assert.Equal(0, cli.Exit);
        Assert.False(mcp.IsError, mcp.Text);

        foreach (var (reader, text) in new[] { ("curia recheck", cli.Stdout), ("curia_recheck", mcp.Text) })
        {
            Assert.DoesNotContain(PublishedForkWords.ForbiddenWords(), text, StringComparison.Ordinal);

            // Each branch is one of two revisions of the same post, FORKED, and names the other.
            foreach (var (branch, other) in new[] { (first, second), (second, first) })
            {
                var line = LineNaming(text, branch);
                Assert.Contains(PublishedForkWords.OneOfPhrase(2), line, StringComparison.Ordinal);
                Assert.Contains(PublishedForkWords.CapitalisedWords()[0], line, StringComparison.Ordinal);
                Assert.Contains(other, line, StringComparison.Ordinal);
            }

            // The switched-away answer and the plain post are standings, and not forks.
            foreach (var digest in new[] { switchedAway, plain! })
            {
                var line = LineNaming(text, digest);
                Assert.True(StandingFragments.All(f => line.Contains(f, StringComparison.OrdinalIgnoreCase)), $"{reader}: not the standing words: {line}");
                Assert.DoesNotContain(PublishedForkWords.CapitalisedWords()[0], line, StringComparison.Ordinal);
            }
        }
    }
}
