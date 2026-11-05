using System.Diagnostics.CodeAnalysis;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R11.86 (errata G62), NMN35 and NMN36: <c>curia search</c> refuses a present <c>--kind</c> or <c>--tags</c> that names nothing, and <c>curia inbox</c> a <c>--tags</c>, exit 1
/// by the name of the flag, before a profile is loaded or a request built; a blank <c>--kind</c> is not counted as a filter, so <c>curia search --kind ""</c> is no listing; and the
/// client library (<see cref="ForumClient.SearchAsync"/>, <see cref="ForumSession.InboxAsync"/>) refuses the same and a blank cursor, so that no caller of it can send what the Forum now
/// refuses.
///
/// <para><b>Oracles.</b> "Nothing was sent" is a recorder's account (<see cref="RecordingForum"/>) or the stub's request list, never the command's. The exit is R11.78's blank exit
/// (<see cref="PublishedBoardWords.BlankExit"/>). The refusals run on a home with <b>no agent enrolled</b>, so a check made after the profile is loaded would print another message;
/// the controls run enrolled and are sent.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class BlankListCriterionTests : IDisposable
{
    private readonly RecordingForum _forum = new();
    private readonly List<string> _homes = [];

    public void Dispose()
    {
        _forum.Dispose();
        foreach (var home in _homes) Directory.Delete(home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Home(bool enrolled)
    {
        var home = CuriaCli.NewHome(enrolled ? _forum.Address : null);
        _homes.Add(home);
        return home;
    }

    public static TheoryData<string[], string> Refused => new()
    {
        { ["search", "x", "--kind", ""], "--kind" },
        { ["search", "x", "--kind", "  "], "--kind" },
        { ["search", "x", "--kind", ","], "--kind" },
        { ["search", "--kind", ""], "--kind" },
        { ["search", "x", "--tags", ""], "--tags" },
        { ["search", "x", "--tags", ","], "--tags" },
        { ["search", "x", "--tags", " , "], "--tags" },
        { ["search", "--tags", ","], "--tags" },
        { ["inbox", "--tags", ""], "--tags" },
        { ["inbox", "--tags", ","], "--tags" },
    };

    /// <summary>
    /// NMN35's controls (green on the seam): a real <c>--kind</c> and <c>--tags</c>, and <c>--tags a,,b</c> (which names something), are sent. The refusals below are red only for the
    /// rule, because these show the same lines are otherwise sent.
    /// </summary>
    [Theory]
    [InlineData("search", "x", "--kind", "question")]
    [InlineData("search", "x", "--tags", "a,b")]
    [InlineData("search", "x", "--tags", "a,,b")]
    public async Task R11_86_NMN35_ARealKindOrTagsIsSent(params string[] argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        var home = Home(enrolled: true);

        await CuriaCli.RunAsync(home, Ct, [.. argv, "--forum", _forum.Address.ToString()]);

        Assert.Contains(_forum.Lines, l => l.StartsWith("GET /v1/search", StringComparison.Ordinal));
    }

    /// <summary>
    /// NMN35. A present <c>--kind</c> or <c>--tags</c> that names nothing: exit 1, stderr names the flag and says nothing was sent, zero requests, on a home with no agent, and a
    /// <c>--kind ""</c> beside no term is the refusal and not a listing. Predicted red on the seam: the line is sent (or refused for another reason: a bare search's usage text).
    /// </summary>
    [Theory]
    [MemberData(nameof(Refused))]
    public async Task R11_86_NMN35_ABlankKindOrTagsIsRefusedByTheNameOfTheFlagBeforeAnythingIsSent(string[] argv, string flag)
    {
        ArgumentNullException.ThrowIfNull(argv);
        var home = Home(enrolled: false);

        var (exit, stdout, stderr) = await CuriaCli.RunAsync(home, Ct, [.. argv, "--forum", _forum.Address.ToString()]);

        var said = $"exit {exit}; stdout: {stdout}; stderr: {stderr}; requests: {string.Join(", ", _forum.Lines)}";
        Assert.True(exit == PublishedBoardWords.BlankExit(), said);
        Assert.True(stderr.Contains($"{flag} is empty or blank", StringComparison.Ordinal) || stderr.Contains($"{flag} names nothing", StringComparison.Ordinal), said);
        Assert.True(stderr.Contains("nothing was sent", StringComparison.Ordinal), said);
        Assert.Empty(_forum.Lines);
    }

    /// <summary>
    /// NMN36. The library's net: <see cref="ForumClient.SearchAsync"/> with a blank cursor, a blank <c>kinds</c> item and a blank tag item, and
    /// <see cref="ForumSession.InboxAsync"/> with a blank tag item and a blank cursor, are local refusals and send nothing. Predicted red on the seam: each is sent (the client's
    /// <c>Add</c> drops the blank, so the request goes out without it).
    /// </summary>
    [Fact]
    public async Task R11_86_NMN36_TheLibraryRefusesWhatTheForumRefusesBeforeARequestIsBuilt()
    {
        using var log = new StubLog();
        Assert.True(log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        using (agent)
        {
            var client = log.Client();
            var session = new ForumSession(client, agent!, log.Store, TimeProvider.System);
            var searches = new (string Name, SearchRequest Request)[]
            {
                ("a blank cursor", new SearchRequest("x") { Cursor = "  " }),
                ("a blank kinds item", new SearchRequest("x") { Kinds = ["question", " "] }),
                ("a blank tag item", new SearchRequest("x") { Tags = ["a", " "] }),
            };
            var inboxes = new (string Name, InboxRequest Request)[]
            {
                ("a blank inbox cursor", new InboxRequest { Cursor = "  " }),
                ("a blank inbox tag item", new InboxRequest { Tags = ["a", " "] }),
            };

            var expected = new List<string>();
            var actual = new List<string>();
            foreach (var (name, request) in searches)
            {
                log.Requests.Clear();
                var result = await client.SearchAsync(request, MarkingMode.None, Ct);
                expected.Add($"{name}: refused locally, 0 requests");
                actual.Add($"{name}: {Said(result.TryGetValue(out _, out var refusal), refusal)}, {log.Requests.Count(r => r.StartsWith("GET /v1/search", StringComparison.Ordinal))} requests");
            }

            foreach (var (name, request) in inboxes)
            {
                log.Requests.Clear();
                var result = await session.InboxAsync(request, MarkingMode.None, Ct);
                expected.Add($"{name}: refused locally, 0 requests");
                actual.Add($"{name}: {Said(result.TryGetValue(out _, out var refusal), refusal)}, {log.Requests.Count(r => r.Contains("/v1/inbox", StringComparison.Ordinal))} requests");
            }

            var (e, a) = (string.Join(" | ", expected), string.Join(" | ", actual));
            if (!string.Equals(e, a, StringComparison.Ordinal)) throw new Xunit.Sdk.XunitException($"Expected: {e}{Environment.NewLine}Actual:   {a}");
        }
    }

    /// <summary>What the net did, in the probe's words: a local refusal is one made on this side, before a request is built.</summary>
    private static string Said(bool served, Refusal? refusal) => served ? "served" : refusal?.Kind == RefusalKind.Local ? "refused locally" : "refused";
}
