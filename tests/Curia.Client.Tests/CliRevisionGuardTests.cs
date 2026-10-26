using System.Diagnostics.CodeAnalysis;
using Curia.Canon.Json;
using Curia.Client;
using Curia.Client.Cli;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// Errata G39 (R11.51) at the verb: <c>curia revision</c> applies R11.47's two guards to the post its verified read holds, in R11.47's order, and
/// states supersession on every receipt. Driven through <see cref="Program.ReviseRefusal"/> with posts the stub serves and
/// <see cref="ForumDocuments.ReadPost"/> reads, so a fact needs no second process.
///
/// <para><b>Oracles.</b> The successors the test put on the stub, the ids it chose, the identities it passed, and the phrase and exit parsed from
/// the errata (<see cref="PublishedCliRules"/>), never the verb's report of itself. Every refusal has its control: the same call that is not refused.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class CliRevisionGuardTests : IDisposable
{
    private static readonly string SuccessorDigest = "sha256:" + new string('7', 64);
    private static readonly string OtherDigest = "sha256:" + new string('8', 64);
    private const string SuccessorId = "01REVISIONID00000000000001";
    private const string OtherSuccessorId = "01REVISIONID00000000000002";
    private const string Agent = "https://x/b";
    private const string Stranger = "https://x/a";

    private readonly StubLog _log = new();

    public void Dispose()
    {
        _log.Dispose();
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The post the stub serves, read through the client (which is <see cref="ForumDocuments.ReadPost"/>), written by <paramref name="author"/>.</summary>
    private async Task<ProvenancePost> PostBy(string author)
    {
        var read = await _log.Client().GetPostAsync(StubLog.PostId, MarkingMode.None, Ct);
        Assert.True(read.TryGetValue(out var post, out var refusal), refusal?.Summary);
        return post! with { Provenance = post!.Provenance with { Author = author } };
    }

    [Fact]
    public async Task R11_51_APostAnotherAgentWroteIsRefused()
    {
        var post = await PostBy(Stranger);

        var (refusal, _) = Program.ReviseRefusal(post, Agent);

        Assert.NotNull(refusal);
        Assert.StartsWith("NOT SENT", refusal, StringComparison.Ordinal);
        Assert.Contains(PublishedCliRules.R1151AuthorPhrase(), refusal, StringComparison.Ordinal);
        Assert.Contains(DisplayLiteral.Of(Stranger), refusal, StringComparison.Ordinal);
        Assert.Contains(DisplayLiteral.Of(Agent), refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R11_51_AnAuthorIsComparedOrdinally()
    {
        var post = await PostBy("HTTPS://X/A");

        var (refusal, _) = Program.ReviseRefusal(post, "https://x/a");

        Assert.NotNull(refusal);
    }

    [Fact]
    public async Task R11_51_TheAuthorOwnPostWithNoSuccessorIsNotRefused()
    {
        var post = await PostBy(Agent);

        var (refusal, _) = Program.ReviseRefusal(post, Agent);

        Assert.Null(refusal);
    }

    [Theory]
    [InlineData("one")]
    [InlineData("unaddressable")]
    [InlineData("two")]
    public async Task R11_51_APostServedAsSupersededIsRefusedNamingTheRevisionToRevise(string scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        _log.SupersededBy = scenario switch
        {
            "one" => [new Successor(SuccessorDigest, SuccessorId)],
            "unaddressable" => [new Successor(SuccessorDigest, null)],
            _ => [new Successor(SuccessorDigest, SuccessorId), new Successor(OtherDigest, OtherSuccessorId)],
        };
        var post = await PostBy(Agent);

        var (refusal, _) = Program.ReviseRefusal(post, Agent);

        Assert.NotNull(refusal);
        Assert.StartsWith("NOT SENT", refusal, StringComparison.Ordinal);
        switch (scenario)
        {
            case "one":
                Assert.Contains(SuccessorId, refusal, StringComparison.Ordinal);
                break;
            case "unaddressable":
                Assert.Contains("cannot be addressed by post id", refusal, StringComparison.Ordinal);
                break;
            default:
                Assert.Contains("FORKED", refusal, StringComparison.Ordinal);
                Assert.Contains(SuccessorId, refusal, StringComparison.Ordinal);
                Assert.Contains(OtherSuccessorId, refusal, StringComparison.Ordinal);
                break;
        }
    }

    [Fact]
    public async Task R11_51_APostServedWithNoWordOnItsRevisionsIsRevisedAndTheReceiptSaysNotChecked()
    {
        _log.OmitSupersededBy = true;
        var unstated = await PostBy(Agent);
        _log.OmitSupersededBy = false;
        var empty = await PostBy(Agent);

        var (unstatedRefusal, unstatedReceipt) = Program.ReviseRefusal(unstated, Agent);
        var (emptyRefusal, emptyReceipt) = Program.ReviseRefusal(empty, Agent);

        Assert.Null(unstatedRefusal);
        Assert.Null(emptyRefusal);
        Assert.Contains("NOT CHECKED", unstatedReceipt.Text, StringComparison.Ordinal);
        Assert.Contains("none was reported", emptyReceipt.Text, StringComparison.Ordinal);
        Assert.NotEqual(unstatedReceipt.Text.Split(' ')[0], emptyReceipt.Text.Split(' ')[0]);
    }

    [Fact]
    public async Task R11_51_TheAuthorIsJudgedBeforeTheSuccessor()
    {
        _log.SupersededBy = [new Successor(SuccessorDigest, SuccessorId)];
        var post = await PostBy(Stranger);

        var (refusal, _) = Program.ReviseRefusal(post, Agent);

        Assert.NotNull(refusal);
        Assert.Contains("written by", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain(SuccessorId, refusal, StringComparison.Ordinal);
    }

    /// <summary>No flag forks or forces: the verb's accepted flags and its help hold none of <c>fork</c>, <c>force</c>, <c>anyway</c> (R11.51's last sentence).</summary>
    [Fact]
    public void R11_51_NoFlagOverridesEitherRefusal()
    {
        string[] overrides = ["fork", "force", "anyway", "override"];
        var flags = Program.SharedVerbFlags("revision");
        var help = Help.For("revision")!;

        Assert.NotEmpty(flags);
        foreach (var word in overrides)
        {
            Assert.DoesNotContain(flags, f => f.Contains(word, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("--" + word, help, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task R11_51_TheAuthorSentenceNamesNothingTheForumWrote()
    {
        const string hostile = "a\"b\nc";
        var post = await PostBy(hostile);

        var (refusal, _) = Program.ReviseRefusal(post, Agent);

        Assert.NotNull(refusal);
        Assert.Contains(DisplayLiteral.Of(hostile), refusal, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', refusal);
        Assert.DoesNotContain("a\"b", refusal, StringComparison.Ordinal);
    }
}
