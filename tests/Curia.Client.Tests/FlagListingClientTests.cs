using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using Curia.Client;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// The client half of R7.18's <c>flag</c>/<c>list</c>.
///
/// <para>The endpoint's own tests prove the Forum emits <c>{"flags":[{post_id,kind,raised_at}]}</c>;
/// these prove the client reads exactly that and refuses anything else. The seam between the two is
/// where this project's defects live, and neither side's tests can see it alone —
/// <c>Curia.Api.Tests</c> does not reference the client, and doing so would cut across the
/// dependency rule <c>Curia.Architecture.Tests</c> enforces.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class FlagListingClientTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("curia-flaglist-tests-").FullName;
    private readonly ProfileStore _store;
    private readonly EnrolledAgent _agent;

    /// <summary>Owned here so each test can build a session in one line and still dispose cleanly.</summary>
    private readonly List<HttpClient> _clients = [];

    private static readonly Uri Forum = new("http://forum.test");

    public FlagListingClientTests()
    {
        _store = new ProfileStore(_root);
        Assert.True(_store.Create("alice", "https://agents.example/alice", "alice-1", Forum)
            .TryGetValue(out var agent, out _));
        _agent = agent!;
    }

    public void Dispose()
    {
        foreach (var client in _clients) client.Dispose();
        _agent.Dispose();
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private sealed class Handler(string flagsBody) : HttpMessageHandler
    {
        internal List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);

            var body = path == "/oauth/token"
                ? """{"access_token":"test-access-token","token_type":"DPoP","expires_in":300,"scope":"flag:list"}"""
                : flagsBody;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private ForumSession SessionFor(Handler handler)
    {
        var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = Forum };
        _clients.Add(http);
        return new ForumSession(new ForumClient(http, Forum), _agent, _store, TimeProvider.System);
    }

    [Fact]
    public async Task R10_44_AListedFlagCarriesPostKindAndInstant()
    {
        var ct = TestContext.Current.CancellationToken;
        using var handler = new Handler(
            """{"flags":[{"post_id":"01TESTPOSTID0000000000000A","kind":"spam","raised_at":"2026-08-26T12:00:00.0000000+00:00"}]}""");

        var listed = await SessionFor(handler).FlagsAsync(postId: null, ct);

        Assert.True(listed.TryGetValue(out var flags, out var refusal), refusal?.Error.Title);
        var flag = Assert.Single(flags);

        Assert.Equal("01TESTPOSTID0000000000000A", flag.PostId);
        Assert.Equal("spam", flag.Kind);
        Assert.Equal("2026-08-26T12:00:00.0000000+00:00", flag.RaisedAt);
    }

    /// <summary>
    /// A bare listing reads the agent's own flags; a listing with a post id reads that post's.
    /// Asserted on the URL because the two differ only there, and getting it wrong would silently
    /// answer the wrong question rather than fail.
    /// </summary>
    [Fact]
    public async Task R7_18_ThePostIdSelectsWhichListingIsRequested()
    {
        var ct = TestContext.Current.CancellationToken;
        const string Body = """{"flags":[]}""";

        using var bare = new Handler(Body);
        await SessionFor(bare).FlagsAsync(postId: null, ct);
        Assert.Contains("/v1/flags", bare.Paths);

        using var scoped = new Handler(Body);
        await SessionFor(scoped).FlagsAsync("01TESTPOSTID0000000000000A", ct);
        Assert.Contains("/v1/posts/01TESTPOSTID0000000000000A/flags", scoped.Paths);
    }

    /// <summary>
    /// An empty listing is a legitimate answer and must parse, not refuse — "no flags" is precisely
    /// the question an author asks.
    /// </summary>
    [Fact]
    public async Task AnEmptyListingIsAnAnswerRatherThanAFailure()
    {
        var ct = TestContext.Current.CancellationToken;
        using var handler = new Handler("""{"flags":[]}""");

        var listed = await SessionFor(handler).FlagsAsync(postId: null, ct);

        Assert.True(listed.TryGetValue(out var flags, out var refusal), refusal?.Error.Title);
        Assert.Empty(flags);
    }

    /// <summary>
    /// A response carrying no <c>flags</c> member is malformed, and must not read as an empty
    /// listing.
    ///
    /// <para>This is the case the reader was first written to get wrong: <c>ClientJson.Array</c>
    /// yields an empty array for an absent member, so the obvious spelling turned "the Forum sent
    /// something else entirely" into "you have no flags" — an answer the caller would have acted
    /// on.</para>
    /// </summary>
    [Fact]
    public async Task AResponseWithNoFlagsMemberIsMalformedRatherThanEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        using var handler = new Handler("""{"results":[]}""");

        var listed = await SessionFor(handler).FlagsAsync(postId: null, ct);

        Assert.False(listed.TryGetValue(out _, out var refusal));
        Assert.NotNull(refusal);
    }

    /// <summary>A flag missing a required field is malformed, rather than half-read.</summary>
    [Fact]
    public async Task AFlagMissingAFieldIsMalformed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var handler = new Handler("""{"flags":[{"post_id":"01TESTPOSTID0000000000000A","kind":"spam"}]}""");

        var listed = await SessionFor(handler).FlagsAsync(postId: null, ct);

        Assert.False(listed.TryGetValue(out _, out var refusal));
        Assert.NotNull(refusal);
    }
}
