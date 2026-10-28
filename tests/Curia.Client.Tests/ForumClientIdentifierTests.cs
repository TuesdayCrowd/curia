using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Reflection;
using System.Text;
using Curia.Domain.Serving;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R11.57 (errata G43): a reference client refuses an identifier, or a search criterion, that holds U+0000 or an unpaired surrogate before it
/// builds a request, in <see cref="ForumClient"/> and <see cref="ForumSession"/> where it enters a path or a query, so nothing is sent. Measured
/// on the unmodified tree: <c>Uri.EscapeDataString</c> does not throw on either; it sends <c>%00</c>, which Kestrel answers <c>400</c> with no
/// problem document (R11.33), and U+FFFD, which is a different identifier from the one typed. A malformed identifier that holds neither is still
/// sent: refusing more would make the client a second copy of the Forum's id rule and move D61 item 2's non-property.
///
/// <para><b>Oracles.</b> The ids are derived, not listed: every public method of <see cref="ForumClient"/> and <see cref="ForumSession"/> with a
/// string parameter named like an identifier (and the identifier-bearing members of its request types) is found by reflection, and the number
/// found has a floor (the fourteen <c>Uri.EscapeDataString</c> sites Task 0 listed, less the one that is only a resource URI). The bad values are
/// built at run time. The request list is a handler's. Each refusal has its control: a malformed id with neither character is sent.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ForumClientIdentifierTests : IDisposable
{
    private static readonly Uri Forum = new("http://forum.test");
    private readonly string _root = Directory.CreateTempSubdirectory("curia-ident-tests-").FullName;
    private readonly ProfileStore _store;
    private readonly EnrolledAgent _agent;
    private readonly List<HttpClient> _clients = [];

    public ForumClientIdentifierTests()
    {
        _store = new ProfileStore(_root);
        Assert.True(_store.Create("alice", "https://agents.example/alice", "alice-1", Forum).TryGetValue(out var agent, out _));
        _agent = agent!;
    }

    public void Dispose()
    {
        foreach (var client in _clients) client.Dispose();
        _agent.Dispose();
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal ConcurrentQueue<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue($"{request.Method} {request.RequestUri!.PathAndQuery}");
            var token = request.RequestUri.AbsolutePath == "/oauth/token";
            return Task.FromResult(new HttpResponseMessage(token ? HttpStatusCode.OK : HttpStatusCode.NotFound)
            {
                Content = new StringContent(
                    token
                        ? """{"access_token":"test-access-token","token_type":"DPoP","expires_in":300,"scope":"flag:create"}"""
                        : """{"type":"curia/posts/not-found","title":"No such post","detail":"post=x"}""",
                    Encoding.UTF8,
                    token ? "application/json" : "application/problem+json"),
            });
        }
    }

    private (ForumClient Client, ForumSession Session, Handler Handler) Build()
    {
        var handler = new Handler();
        var http = new HttpClient(handler, disposeHandler: true) { BaseAddress = Forum };
        _clients.Add(http);
        var client = new ForumClient(http, Forum);
        return (client, new ForumSession(client, _agent, _store, TimeProvider.System), handler);
    }

    /// <summary>The characters no identifier can carry, built at run time: each with the boundary type R11.34's rule gives it.</summary>
    internal static (string Name, string Value, string Type)[] BadValues() =>
    [
        ("nul-inside", "AB" + (char)0 + "CD", "curia/input/nul"),
        ("lone-high-surrogate", "AB" + (char)0xD800 + "CD", "curia/input/unpaired-surrogate"),
        ("nul-alone", ((char)0).ToString(), "curia/input/nul"),
    ];

    private static readonly HashSet<string> IdNames = new(StringComparer.Ordinal) { "postId", "rootPostId", "answerId", "agentId", "kid", "board" };

    private const string ValidId = "01TESTPOSTID0000000000000A";

    /// <summary>One place an identifier enters a request: the method, the parameter (or request member), and a call that puts <paramref name="value"/> there.</summary>
    internal sealed record Site(string Name, Func<ForumClient, ForumSession, string, Task<object?>> Call);

    private static object? DefaultFor(ParameterInfo parameter, string? badParameter, string value)
    {
        var type = parameter.ParameterType;
        var name = parameter.Name!;
        if (type == typeof(string))
        {
            if (name == badParameter) return value;
            return name switch
            {
                "kind" => "spam",
                "rationale" => "a reason",
                "knownEntityTag" => "\"abc\"",
                _ => ValidId,
            };
        }

        if (type == typeof(string[]) || type == typeof(IReadOnlyList<string>)) return new[] { ValidId };
        if (type == typeof(CancellationToken)) return TestContext.Current.CancellationToken;
        if (type == typeof(MarkingMode)) return MarkingMode.Datamark;
        if (type == typeof(RotationRequest)) return new RotationRequest("s", "a", "p");
        if (type == typeof(RetirementRequest)) return new RetirementRequest("s", "a");
        if (type == typeof(SearchRequest)) return new SearchRequest("x");
        if (type == typeof(InboxRequest)) return new InboxRequest();
        if (type == typeof(long) || type == typeof(long?)) return 0L;
        throw new NotSupportedException($"no default for {type.Name} {name}");
    }

    private static async Task<object?> AwaitAsync(object task)
    {
        var typed = (Task)task;
        await typed.ConfigureAwait(false);
        return typed.GetType().GetProperty("Result")!.GetValue(typed);
    }

    /// <summary>Every (method, identifier parameter) pair of the two public surfaces, by reflection, plus the identifier members of the request types.</summary>
    internal static List<Site> Sites()
    {
        var sites = new List<Site>();
        foreach (var owner in new[] { typeof(ForumClient), typeof(ForumSession) })
        {
            foreach (var method in owner.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (!typeof(Task).IsAssignableFrom(method.ReturnType)) continue;
                var ids = method.GetParameters().Where(p => p.ParameterType == typeof(string) && IdNames.Contains(p.Name!)).ToArray();
                foreach (var id in ids)
                {
                    var m = method;
                    var name = id.Name!;
                    sites.Add(new Site(
                        $"{owner.Name}.{method.Name}({name})",
                        (client, session, value) =>
                            AwaitAsync(m.Invoke(owner == typeof(ForumClient) ? client : session, [.. m.GetParameters().Select(p => DefaultFor(p, name, value))])!)));
                }
            }
        }

        // The request types carry identifiers that reach a query: SearchAsync's board and author, InboxAsync's board. A tag is the filter predicate's (R8.73, PJ45) and the search text is a criterion curia-mcp guards (PJ9); neither is this client seam's.
        sites.Add(new Site("ForumClient.SearchAsync(Board)", (c, _, v) => AwaitAsync(c.SearchAsync(new SearchRequest("x") { Board = v }, MarkingMode.Datamark, TestContext.Current.CancellationToken))));
        sites.Add(new Site("ForumClient.SearchAsync(Author)", (c, _, v) => AwaitAsync(c.SearchAsync(new SearchRequest("x") { Author = v }, MarkingMode.Datamark, TestContext.Current.CancellationToken))));
        sites.Add(new Site("ForumSession.InboxAsync(Board)", (_, s, v) => AwaitAsync(s.InboxAsync(new InboxRequest { Board = v }, MarkingMode.Datamark, TestContext.Current.CancellationToken))));
        return sites;
    }

    private static Task<object?> AwaitAsync(Task task) => AwaitAsync((object)task);

    public static TheoryData<int, int> Cells()
    {
        var sites = Sites();
        var data = new TheoryData<int, int>();
        for (var site = 0; site < sites.Count; site++)
            for (var value = 0; value < BadValues().Length; value++)
                data.Add(site, value);
        return data;
    }

    private static Refusal? RefusalOf(object? result) =>
        result?.GetType().GetProperty("Refusal")!.GetValue(result) as Refusal;

    // ---- PJ7 ------------------------------------------------------------------------------------

    /// <summary>PJ7: no method that takes an id sends one that cannot be carried; the refusal is local and the handler saw nothing.</summary>
    [Theory]
    [MemberData(nameof(Cells))]
    public async Task R11_57_NoMethodThatTakesAnIdSendsOneThatCannotBeCarried(int site, int bad)
    {
        var cell = Sites()[site];
        var (name, value, _) = BadValues()[bad];
        var (client, session, handler) = Build();

        var result = await cell.Call(client, session, value);

        Assert.True(handler.Requests.IsEmpty, $"{cell.Name} with {name} sent: {string.Join(" | ", handler.Requests)}");
        var refusal = RefusalOf(result);
        Assert.NotNull(refusal);
        Assert.Equal(RefusalKind.Local, refusal.Kind);
    }

    /// <summary>PJ7's floor: the derived list holds at least the fourteen sites Task 0 listed (the resource URI of curia-mcp is not a request).</summary>
    [Fact]
    public void R11_57_TheDerivedListOfSitesHasTheKnownFloor()
    {
        var sites = Sites().Select(s => s.Name).ToArray();

        Assert.True(sites.Length >= 14, $"{sites.Length} sites: {string.Join(", ", sites)}");
        foreach (var known in new[] { "GetPostAsync(postId)", "GetThreadAsync(rootPostId)", "GetBoardAsync(board)", "GetJwksAsync(agentId)", "FlagAsync(postId)", "AcceptAsync(answerId)", "FlagsAsync(postId)", "SearchAsync(Board)", "SearchAsync(Author)" })
            Assert.Contains(sites, s => s.EndsWith(known, StringComparison.Ordinal));
    }

    // ---- PJ8 ------------------------------------------------------------------------------------

    /// <summary>
    /// PJ8, the control: an id that is malformed and holds neither character is still sent, and the Forum's "No such post" is what comes back
    /// (D61 item 2's non-property). Green first; held by FPJ9 (a check that refuses a blank or a space) and FPJ10 (one that refuses a non-ULID).
    /// </summary>
    [Theory]
    [InlineData("not an id")]
    [InlineData("a b")]
    [InlineData("sha256:abc")]
    [InlineData("0123456789012345678901234567890123456789")]
    [InlineData("01TESTPOSTID0000000000000A")]
    public async Task R11_57_AMalformedIdThatHoldsNeitherIsStillSent(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var (client, _, handler) = Build();

        var read = await client.GetPostAsync(id, MarkingMode.Datamark, TestContext.Current.CancellationToken);

        Assert.Single(handler.Requests);
        Assert.False(read.TryGetValue(out _, out var refusal));
        Assert.Equal(RefusalKind.NotFound, refusal!.Kind);
    }

    // ---- PJ14 -----------------------------------------------------------------------------------

    /// <summary>
    /// PJ14: the refusal says which of the boundary rule's conditions it is. R11.34's text does not publish the two types
    /// (<c>curia/input/nul</c>, <c>curia/input/unpaired-surrogate</c>), so the probe holds them as literals, a recorded deviation from the plan's
    /// "parsed from the errata".
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task R11_57_TheTypesAreTheBoundaryRulesTypes(int bad)
    {
        var (_, value, type) = BadValues()[bad];
        var (client, _, handler) = Build();

        var read = await client.GetPostAsync(value, MarkingMode.Datamark, TestContext.Current.CancellationToken);

        Assert.True(handler.Requests.IsEmpty, string.Join(" | ", handler.Requests));
        Assert.False(read.TryGetValue(out _, out var refusal));
        Assert.Equal(RefusalKind.Local, refusal!.Kind);
        Assert.Contains(type, refusal.Error.Type + "\n" + refusal.Error.Title + "\n" + refusal.Error.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(value, refusal.Error.Title + refusal.Error.Detail, StringComparison.Ordinal);
    }
}
