using System.Diagnostics.CodeAnalysis;
using Curia.Client;
using Curia.Domain.Content;
using Curia.Domain.Serving;
using Curia.Mcp;
using Curia.Tests.Shared;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// D67 against the Forum itself: a flag or an acceptance of a post id holding a space, a colon, a literal
/// percent sign, a non-ASCII character or <c>%2F</c> reaches the Forum's use case and is answered by it,
/// and is never answered <c>curia/authn/url-mismatch</c>. That answer names a fault of the agent's
/// credential (an agent that reads it re-enrols or rotates), when the fault was the client's proof over the
/// display form of a URL the Forum compares byte for byte with the one it rebuilds. On a real socket, since
/// the rebuilt target is Kestrel's.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class PathIdWriteEndToEndTests(KeyRouteKestrelFixture forum) : IClassFixture<KeyRouteKestrelFixture>, IDisposable
{
    private const string UrlMismatch = "curia/authn/url-mismatch";

    private readonly string _home = Directory.CreateTempSubdirectory("curia-pathid-").FullName;
    private readonly List<IDisposable> _owned = [];

    public static TheoryData<string> Ids => ["a b", "sha256:abc", "100%", "\u00E9", "a%2Fb"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ProfileStore Store => new(_home);

    private ForumClient Client => new(forum.Client, forum.Client.BaseAddress!);

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private async Task<EnrolledAgent> EnrolAsync()
    {
        var slug = "p-" + Guid.NewGuid().ToString("N")[..8];
        Assert.True(Store.Create(slug, $"https://agents.example/{slug}", slug + "-0", forum.Client.BaseAddress!).TryGetValue(out var agent, out var error), error?.Title);
        _owned.Add(agent!);
        var enrolled = await Client.EnrolAsync(agent!, Ct);
        Assert.True(enrolled.TryGetValue(out _, out var refusal), refusal?.Summary);
        return agent!;
    }

    private ForumSession SessionFor(EnrolledAgent agent) => new(Client, agent, Store, forum.Clock);

    private ForumTools ToolsFor(EnrolledAgent agent) =>
        new(Client, MarkingMode.Datamark, new HeadStore(_home), new ForumWriter(agent, SessionFor(agent), forum.Clock));

    private static string Harm(string id, string type) =>
        $"id {id}: the Forum answered {type}, a fault of the agent's credential, when the client proved the wrong URL; an agent that reads it re-enrols or rotates";

    [Theory]
    [MemberData(nameof(Ids))]
    public async Task Flag_OfAnIdThatNamesNothingIsNotACredentialFault(string id)
    {
        var flagger = await EnrolAsync();

        var flagged = await SessionFor(flagger).FlagAsync(id, "spam", "names nothing", Ct);

        Assert.False(flagged.TryGetValue(out _, out var refusal));
        Assert.NotEqual(UrlMismatch, refusal!.Error.Type);
        _ = Harm(id, refusal.Error.Type);
        Assert.Equal("curia/flag/no-such-post", refusal.Error.Type); // recorded by running the fixed build, D67 "As run"
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public async Task Accept_OfAnIdThatNamesNothingIsNotACredentialFault(string id)
    {
        var asker = await EnrolAsync();

        var accepted = await SessionFor(asker).AcceptAsync(id, Ct);

        Assert.False(accepted.TryGetValue(out _, out var refusal));
        Assert.NotEqual(UrlMismatch, refusal!.Error.Type);
        _ = Harm(id, refusal.Error.Type);
        Assert.Equal("curia/accept/no-such-post", refusal.Error.Type); // recorded by running the fixed build, D67 "As run"
    }

    /// <summary>
    /// The flag listing of one post (<c>GET /v1/posts/{id}/flags</c>) is the third route with an id in its path
    /// and proves its target in the same way: found at ForumSession's read of it, beyond the plan's two writes.
    /// </summary>
    [Theory]
    [MemberData(nameof(Ids))]
    public async Task FlagListing_OfAnIdThatNamesNothingIsNotACredentialFault(string id)
    {
        var agent = await EnrolAsync();

        var listed = await SessionFor(agent).FlagsAsync(id, Ct);

        Assert.False(listed.TryGetValue(out _, out var refusal), "a post that does not exist has no flags to list");
        Assert.NotEqual(UrlMismatch, refusal!.Error.Type);
        _ = Harm(id, refusal.Error.Type);
        Assert.Equal("curia/post/not-found", refusal.Error.Type); // recorded by running the fixed build, D67 "As run"
    }

    [Fact]
    public async Task CuriaFlagToolRelaysTheForumsAnswerNotACredentialFault()
    {
        var agent = await EnrolAsync();
        var tools = ToolsFor(agent);

        var flag = await Said(() => tools.FlagAsync("a b", "spam", "names nothing", Ct));
        var resolve = await Said(() => tools.ResolveAsync("a b", Ct));

        Assert.DoesNotContain(UrlMismatch, flag, StringComparison.Ordinal);
        Assert.DoesNotContain("htu", flag, StringComparison.Ordinal);
        Assert.DoesNotContain(UrlMismatch, resolve, StringComparison.Ordinal);
        Assert.DoesNotContain("htu", resolve, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Flag_OfARealPostStillSucceeds()
    {
        var asker = await EnrolAsync();
        var flagger = await EnrolAsync();
        var board = "pathid-" + Guid.NewGuid().ToString("N")[..8];
        Assert.True(
            SubmissionBuilder.Build(
                asker,
                new PostDraft { Kind = PostKind.Question, Board = board, Title = "A real post?", Body = "A premise worth flagging." },
                forum.Now).TryGetValue(out var signed, out var buildError),
            buildError?.Title);
        var posted = await SessionFor(asker).SubmitAsync(signed!.Wire, Ct);
        Assert.True(posted.TryGetValue(out var receipt, out var postRefusal), postRefusal?.Summary);

        var flagged = await SessionFor(flagger).FlagAsync(receipt!.PostId, "incorrect", "The premise is wrong.", Ct);

        Assert.True(flagged.TryGetValue(out var flag, out var refusal), refusal?.Error.Type);
        Assert.Equal(receipt.PostId, flag!.PostId);
    }

    /// <summary>The text of a tool's answer, whether it returned it or threw it as a refusal.</summary>
    private static async Task<string> Said(Func<Task<CallToolResult>> call)
    {
        try
        {
            var result = await call();
            return string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        }
        catch (McpException refused)
        {
            return refused.Message;
        }
    }
}
