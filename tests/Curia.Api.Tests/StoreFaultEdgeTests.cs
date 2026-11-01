using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using Curia.Api.Tests.Fuzz;
using Curia.Application.Ports;
using Curia.Infrastructure;
using Curia.Tests.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

using Probe = Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>
/// ULG5, ULG7, ULG35 to ULG38 (errata G52, R7.31, R9.52, R11.71): the edge itself, on the in-process host (Development, the owner's connection): the eleven
/// places the Forum reads the whole log, the control that a real denial stays a denial, the middleware's limits (a 4xx is left alone, a response that has started
/// is not written twice), an unclassified exception as a problem document, and the log - not the response - holding the fault.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("fuzz")]
public sealed partial class StoreFaultEdgeTests(FaultableForumFixture forum) : IClassFixture<FaultableForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The routes that reach <c>ForumEndpoints.ReadEventsAsync</c>, one per call site (the line is where the call was on <c>main</c> at <c>fb04ea74</c>), with the
    /// exemplar variant that stands for the route. The scan fact holds the count of rows to the count of call sites.
    /// </summary>
    private static readonly (int Line, string Route, string Variant)[] Sites =
    [
        (883, "POST /v1/posts", "question"),
        (1175, "POST /v1/posts/{postId}/flags", "plain"),
        (1482, "GET /v1/flags", "plain"),
        (1554, "GET /v1/posts/{postId}", "plain"),
        (1664, "POST /v1/posts/batch", "plain"),
        (1700, "GET /v1/threads/{rootPostId}", "plain"),
        (1745, "GET /v1/boards/{board}/posts", "plain"),
        (1939, "POST /v1/posts/{postId}/accept", "plain"),
        (2280, "GET /v1/search", "plain"),
        (2427, "GET /v1/inbox", "plain"),
        (3243, "POST /v1/agents/{id}/keys", "plain"),
    ];

    /// <summary>
    /// ULG5. A reader that reports a failed whole-log read, at each call site of <c>ReadEventsAsync</c>: never a <c>403</c> (the swallow's empty log makes an
    /// enrolled agent Anonymous), never a <c>200</c>, never an empty list. The answer is <c>503</c> <c>curia/log/unreadable</c>.
    /// </summary>
    [Fact]
    public async Task R7_31_ULG5_AReaderThatReportsAtEachCallSiteOfTheWholeLogReadNeverGives403Or200OrAnEmptyList()
    {
        using var context = await FuzzContext.SeedAsync(forum, Ct);
        var swept = await FaultSweep.RunAsync(
            forum, context, row => Sites.Any(s => s.Route == row.Route && (s.Variant == "plain" || s.Variant == row.Variant)),
            () => forum.Store.Arm(EventStoreFault.ReportForward), forum.Store.Disarm, Ct);

        var problems = new List<string>();
        foreach (var site in Sites)
        {
            var reply = swept.SingleOrDefault(s => s.Route == site.Route && (site.Variant == "plain" || s.Variant == site.Variant));
            if (reply is null)
            {
                problems.Add($":{site.Line} {site.Route}: no exemplar was swept");
                continue;
            }

            if (!FaultSweep.IsProblem(reply.Reply, PublishedStoreFaults.UnavailableStatus, PublishedStoreFaults.LogUnreadableType))
                problems.Add($":{site.Line} expected 503 {PublishedStoreFaults.LogUnreadableType}, actual {reply.Says}");
        }

        Assert.True(problems.Count == 0, $"{problems.Count} of {Sites.Length} call sites:\n" + string.Join('\n', problems));
    }

    /// <summary>ULG5's scan: the table above has one row for every call of <c>ReadEventsAsync</c> in <c>ForumEndpoints.cs</c>, and no more.</summary>
    [Fact]
    public void R7_31_ULG5_TheTableOfCallSitesHasOneRowForEveryCallOfTheWholeLogRead()
    {
        var text = File.ReadAllText(RepositoryFile("src/Curia.Api/ForumEndpoints.cs"));
        var calls = AwaitedRead().Count(text);

        Assert.True(calls == Sites.Length, $"ForumEndpoints.cs calls ReadEventsAsync {calls} times and the table names {Sites.Length} routes");
    }

    [GeneratedRegex(@"await ReadEventsAsync\(", RegexOptions.CultureInvariant)]
    private static partial Regex AwaitedRead();

    private static string RepositoryFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curia.sln")))
            dir = dir.Parent;

        return dir is null
            ? throw new InvalidOperationException($"Curia.sln not found above {AppContext.BaseDirectory}")
            : Path.Combine(dir.FullName, relative);
    }

    /// <summary>
    /// ULG7, a control. On a healthy store an act above the agent's tier is still the PDP's own <c>403</c> denial, so removing the empty-log fallback leaves a real
    /// denial a denial. (The plan's "agent the log does not hold" cannot be built healthy: an enrolled agent is in the log, and the denial it can be given is a tier's.)
    /// </summary>
    [Fact]
    public async Task R7_31_ULG7_OnAHealthyStoreAnActAboveTheAgentsTierIsStillADenial()
    {
        var board = Probe.Board();
        var t0 = await AcceptedAnswerScene.EnrolAsync(forum, "t0", Ct);
        var question = await BudgetBursts.StoredQuestionAsync(forum, board, Ct);

        var sent = await Probe.SendAsync(forum, t0, t0.Agent.SignAnswer(board, Probe.Unrelated(), question.PostId, forum.Now), cancellationToken: Ct);

        Assert.True(
            sent.Status == 403 && sent.Type == "curia/authz/denied" && sent.Detail?.StartsWith("table-10/denied", StringComparison.Ordinal) == true,
            $"expected 403 curia/authz/denied table-10/denied, actual {sent.Says} {sent.Detail}");
    }

    /// <summary>
    /// ULG35, a control (red in FULG1's second form). The middleware is outermost and leaves a <c>4xx</c> alone: with the log armed to fail, an unknown route is
    /// still <c>404</c> <c>curia/request/no-route</c> and a wrong method <c>405</c>, and the status-code pages do not rewrite either.
    /// </summary>
    [Fact]
    public async Task R11_71_ULG35_AnUnknownRouteAndAWrongMethodAreLeftAloneWhileTheLogIsArmed()
    {
        forum.Store.Arm(EventStoreFault.ThrowUnreadable);
        Reply unknown;
        Reply wrong;
        try
        {
            using var get = new HttpRequestMessage(HttpMethod.Get, "/v1/no-such-route");
            unknown = await Reply.SendAsync(forum.Client, get, Ct);
            using var delete = new HttpRequestMessage(HttpMethod.Delete, "/v1/posts");
            wrong = await Reply.SendAsync(forum.Client, delete, Ct);
        }
        finally
        {
            forum.Store.Disarm();
        }

        var problems = new List<string>();
        if (unknown.Status != 404 || unknown.Type != "curia/request/no-route") problems.Add($"an unknown route: expected 404 curia/request/no-route, actual {unknown.Says}");
        if (wrong.Status != 405) problems.Add($"a wrong method: expected 405, actual {wrong.Says}");

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    private static DefaultHttpContext ContextWith(IStoreFaultClassifier classifier, bool started, CancellationToken aborted)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(classifier);
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider(), RequestAborted = aborted };
        context.Response.Body = new MemoryStream();
        if (started) context.Features.Set<IHttpResponseFeature>(new StartedResponse());
        return context;
    }

    private sealed class StartedResponse : HttpResponseFeature
    {
        public override bool HasStarted => true;
    }

    /// <summary>
    /// ULG36. A response that has started is not written to twice. Over a fake context: where the response has not started, a classified fault is answered
    /// <c>503</c> as a problem document by the middleware (the positive, so the fact is red by assertion on the pass-through the seam declares); where it has, the
    /// middleware writes nothing and throws nothing but the original. The middleware takes the classifier and the logger from <c>RequestServices</c>.
    /// </summary>
    [Fact]
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The fact records whatever escapes the middleware.")]
    public async Task R11_71_ULG36_AResponseThatHasStartedIsNotWrittenToTwice()
    {
        var fault = new LogUnreadableException("armed", new NpgsqlException("down", new TimeoutException()));
        // The positive: a fresh response, a classified fault.
        var fresh = ContextWith(new PostgresStoreFaults(), started: false, Ct);
        Exception? freshEscaped = null;
        try
        {
            await new StoreFaultsMiddleware(_ => throw fault).InvokeAsync(fresh);
        }
        catch (Exception e)
        {
            freshEscaped = e;
        }

        var problems = new List<string>();
        if (freshEscaped is not null) problems.Add($"a classified fault escaped the middleware as {freshEscaped.GetType().Name}");
        if (fresh.Response.StatusCode != PublishedStoreFaults.UnavailableStatus) problems.Add($"expected {PublishedStoreFaults.UnavailableStatus}, actual status {fresh.Response.StatusCode}");
        fresh.Response.Body.Position = 0;
        using var reader = new StreamReader(fresh.Response.Body, leaveOpen: true);
        var freshBody = await reader.ReadToEndAsync(Ct);
        if (!freshBody.Contains(PublishedStoreFaults.LogUnreadableType, StringComparison.Ordinal)) problems.Add($"the fresh response's body is not the log's problem document: \"{freshBody}\"");

        // The limit: a response that has started.
        var started = ContextWith(new PostgresStoreFaults(), started: true, Ct);
        started.Response.StatusCode = 200;
        Exception? startedEscaped = null;
        try
        {
            await new StoreFaultsMiddleware(_ => throw fault).InvokeAsync(started);
        }
        catch (Exception e)
        {
            startedEscaped = e;
        }

        if (started.Response.Body.Length != 0) problems.Add($"the middleware wrote {started.Response.Body.Length} bytes to a response that had started");
        if (started.Response.StatusCode != 200) problems.Add($"the middleware changed the status of a started response to {started.Response.StatusCode}");
        if (startedEscaped is not null && !ReferenceEquals(startedEscaped, fault)) problems.Add($"a started response threw {startedEscaped.GetType().Name} and not the original fault");

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    /// <summary>
    /// ULG37. An exception nothing names is a problem document in-process too: <c>500</c> <c>curia/server/fault</c>, the title and the null <c>detail</c> as
    /// published, a body, and no word of the exception in it.
    /// </summary>
    [Fact]
    public async Task R11_71_ULG37_AnUnclassifiedExceptionIsAProblemDocument()
    {
        const string Sentinel = "an-unclassified-bug-text";
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "t0", Ct);
        var token = await actor.Dpop.GetTokenAsync(forum.Client, Probe.TokenEndpoint, forum.Now, Ct);

        forum.Store.Arm(EventStoreFault.ThrowReads, () => StoreFaultKinds.Unclassified(Sentinel));
        Reply budget;
        Reply board;
        try
        {
            budget = await FaultActs.GetAsync(forum, actor, "/v1/budget", Ct, token);
            using var anonymous = new HttpRequestMessage(HttpMethod.Get, "/v1/boards/some-board/posts");
            board = await Reply.SendAsync(forum.Client, anonymous, Ct);
        }
        finally
        {
            forum.Store.Disarm();
        }

        var problems = new List<string>();
        foreach (var (name, reply) in new[] { ("an authenticated read", budget), ("an anonymous read", board) })
        {
            if (!FaultSweep.IsProblem(reply, PublishedStoreFaults.FaultStatus, PublishedStoreFaults.FaultType))
                problems.Add($"{name}: expected {PublishedStoreFaults.FaultStatus} {PublishedStoreFaults.FaultType}, actual {reply.Says}");
            else if (reply.Title != PublishedStoreFaults.FaultTitle)
                problems.Add($"{name}: expected the title \"{PublishedStoreFaults.FaultTitle}\", actual \"{reply.Title}\"");
            if (reply.Body.Contains(Sentinel, StringComparison.Ordinal)) problems.Add($"{name}: the body carries the exception's text");
        }

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    /// <summary>
    /// ULG38. The log, not the response, holds the fault: an exception whose message carries a sentinel is served as <c>503</c>, the body does not contain the
    /// sentinel, the Forum's log holds one <c>StoreFault</c> event (its id from the errata) carrying the exception, and the existing server-fault line for the
    /// withheld detail.
    /// </summary>
    [Fact]
    public async Task R8_82_ULG38_TheLogHoldsTheFaultAndTheResponseDoesNot()
    {
        const string Sentinel = "host=db-secret.example";
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "t0", Ct);
        var token = await actor.Dpop.GetTokenAsync(forum.Client, Probe.TokenEndpoint, forum.Now, Ct);

        var mark = forum.Log.Mark();
        forum.Store.Arm(EventStoreFault.ThrowUnreadable, () => new NpgsqlException($"Failed to connect: {Sentinel} refused", new TimeoutException(Sentinel)));
        Reply reply;
        try
        {
            reply = await FaultActs.GetAsync(forum, actor, "/v1/budget", Ct, token);
        }
        finally
        {
            forum.Store.Disarm();
        }

        var logged = forum.Log.Since(mark);
        var faults = logged.Where(e => e.EventId == PublishedStoreFaults.EventId).ToList();
        var problems = new List<string>();
        if (!FaultSweep.IsProblem(reply, PublishedStoreFaults.UnavailableStatus, PublishedStoreFaults.LogUnreadableType))
            problems.Add($"expected {PublishedStoreFaults.UnavailableStatus} {PublishedStoreFaults.LogUnreadableType}, actual {reply.Says}");
        if (reply.Body.Contains(Sentinel, StringComparison.Ordinal)) problems.Add("the response body contains the sentinel");
        if (faults.Count != 1) problems.Add($"{faults.Count} events {PublishedStoreFaults.EventId} ({PublishedStoreFaults.EventName}) were logged, not one");
        else
        {
            if (faults[0].Name != PublishedStoreFaults.EventName) problems.Add($"the event is named \"{faults[0].Name}\", not {PublishedStoreFaults.EventName}");
            if (!Chain(faults[0].Exception).Contains(Sentinel, StringComparison.Ordinal)) problems.Add("the logged event does not carry the exception");
        }

        if (!logged.Any(e => e.EventId == PublishedStoreFaults.ServerFaultEventId)) problems.Add($"no event {PublishedStoreFaults.ServerFaultEventId} (the withheld detail) was logged");

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    private static async Task<string> BodyOfAsync(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, leaveOpen: true);
        return await reader.ReadToEndAsync(Ct);
    }

    /// <summary>
    /// ULG44 (R5.23's second sentence, at the edge). Over a fake context at <c>/oauth/token</c>, an exception nothing names is <c>500</c> RFC 6749's
    /// <c>server_error</c> with the published description and no <c>detail</c>, and a classified one is <c>503</c> <c>temporarily_unavailable</c>: the two are not
    /// the same answer.
    /// </summary>
    [Fact]
    public async Task R5_23_ULG44_AnUnclassifiedExceptionAtTheTokenRouteIsServerErrorAndAClassifiedOneIsNot()
    {
        var own = ContextWith(new PostgresStoreFaults(), started: false, Ct);
        own.Request.Path = "/oauth/token";
        await new StoreFaultsMiddleware(_ => throw new InvalidOperationException("a bug")).InvokeAsync(own);
        var ownBody = await BodyOfAsync(own);

        var store = ContextWith(new PostgresStoreFaults(), started: false, Ct);
        store.Request.Path = "/oauth/token";
        await new StoreFaultsMiddleware(_ => throw new NpgsqlException("down", new TimeoutException())).InvokeAsync(store);
        var storeBody = await BodyOfAsync(store);

        var problems = new List<string>();
        if (own.Response.StatusCode != 500) problems.Add($"an unclassified exception: expected 500, actual {own.Response.StatusCode} {ownBody}");
        if (!ownBody.Contains($"\"error\":\"{PublishedStoreFaults.TokenOwnFaultCode}\"", StringComparison.Ordinal)) problems.Add($"an unclassified exception: expected the code {PublishedStoreFaults.TokenOwnFaultCode}, actual {ownBody}");
        if (!ownBody.Contains(PublishedStoreFaults.TokenOwnFaultDescription, StringComparison.Ordinal)) problems.Add($"an unclassified exception: the published description is missing from {ownBody}");
        if (ownBody.Contains("detail", StringComparison.Ordinal) || ownBody.Contains("a bug", StringComparison.Ordinal)) problems.Add($"an unclassified exception: the body carries a detail or the exception's text: {ownBody}");
        if (store.Response.StatusCode != PublishedStoreFaults.TokenStatus || !storeBody.Contains($"\"error\":\"{PublishedStoreFaults.TokenCode}\"", StringComparison.Ordinal))
            problems.Add($"a classified fault: expected {PublishedStoreFaults.TokenStatus} {PublishedStoreFaults.TokenCode}, actual {store.Response.StatusCode} {storeBody}");

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    /// <summary>
    /// ULG45. The token route is decided as routing decides it: <c>/OAuth/Token</c> and <c>/oauth/token/</c> reach the endpoint, so a fault there is RFC 6749's
    /// object, while a path that merely resembles it (<c>/v1/oauth/token</c>) is a problem document.
    /// </summary>
    [Theory]
    [InlineData("/oauth/token", true)]
    [InlineData("/OAuth/Token", true)]
    [InlineData("/oauth/token/", true)]
    [InlineData("/v1/oauth/token", false)]
    public async Task R5_23_ULG45_TheTokenRouteIsDecidedAsRoutingDecidesIt(string path, bool oauth)
    {
        var context = ContextWith(new PostgresStoreFaults(), started: false, Ct);
        context.Request.Path = path;
        await new StoreFaultsMiddleware(_ => throw new NpgsqlException("down", new TimeoutException())).InvokeAsync(context);
        var body = await BodyOfAsync(context);

        var isOAuth = body.Contains("\"error\"", StringComparison.Ordinal) && !body.Contains("\"type\"", StringComparison.Ordinal);
        Assert.True(isOAuth == oauth, $"{path}: expected {(oauth ? "RFC 6749's object" : "a problem document")}, actual {context.Response.StatusCode} {body}");
    }

    /// <summary>
    /// ULG46. The answer is the fault's alone: a handler that set a <c>Location</c>, a <c>Cache-Control</c> and a status before it threw (the response had not
    /// started) leaves none of them on the <c>503</c>.
    /// </summary>
    [Fact]
    public async Task R11_71_ULG46_WhatAHandlerSetBeforeItThrewIsNotOnTheFaultsAnswer()
    {
        var context = ContextWith(new PostgresStoreFaults(), started: false, Ct);
        await new StoreFaultsMiddleware(c =>
        {
            c.Response.StatusCode = StatusCodes.Status201Created;
            c.Response.Headers.Location = "/v1/posts/01ABC";
            c.Response.Headers.CacheControl = "public, max-age=600";
            throw new NpgsqlException("down", new TimeoutException());
        }).InvokeAsync(context);

        var problems = new List<string>();
        if (context.Response.StatusCode != PublishedStoreFaults.UnavailableStatus) problems.Add($"expected {PublishedStoreFaults.UnavailableStatus}, actual {context.Response.StatusCode}");
        foreach (var header in new[] { "Location", "Cache-Control" })
            if (context.Response.Headers.TryGetValue(header, out var value)) problems.Add($"the answer still carries {header}: {value}");

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    /// <summary>
    /// ULG49. Each <c>503</c> problem carries the title the errata publishes for its type, as the factory builds it (over a fake context: a log the store would not
    /// read, and a fault of any other store) and as a route serves it (an anonymous read with the log unreadable): a Forum that serves each type under the other's
    /// title says the wrong thing about what failed, and the type alone would not show it.
    /// </summary>
    [Theory]
    [InlineData("log")]
    [InlineData("store")]
    public async Task R8_82_ULG49_EachStoreFaultProblemCarriesThePublishedTitleOfItsType(string which)
    {
        var (type, title) = which == "log"
            ? (PublishedStoreFaults.LogUnreadableType, PublishedStoreFaults.LogUnreadableTitle)
            : (PublishedStoreFaults.StoreUnavailableType, PublishedStoreFaults.StoreUnavailableTitle);
        Exception fault = which == "log"
            ? new LogUnreadableException("armed", new NpgsqlException("down", new TimeoutException()))
            : new NpgsqlException("down", new TimeoutException());
        var context = ContextWith(new PostgresStoreFaults(), started: false, Ct);
        await new StoreFaultsMiddleware(_ => throw fault).InvokeAsync(context);
        var served = new Reply(context.Response.StatusCode, context.Response.ContentType?.Split(';')[0], await BodyOfAsync(context), null);

        var problems = new List<string>();
        if (!FaultSweep.IsProblem(served, PublishedStoreFaults.UnavailableStatus, type)) problems.Add($"the factory: expected {PublishedStoreFaults.UnavailableStatus} {type}, actual {served.Says}");
        else if (served.Title != title) problems.Add($"the factory: {type} is titled \"{served.Title}\", not \"{title}\"");

        if (which == "log")
        {
            forum.Store.Arm(EventStoreFault.ThrowUnreadable);
            Reply route;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/boards/some-board/posts");
                route = await Reply.SendAsync(forum.Client, request, Ct);
            }
            finally
            {
                forum.Store.Disarm();
            }

            if (!FaultSweep.IsProblem(route, PublishedStoreFaults.UnavailableStatus, type)) problems.Add($"a route: expected {PublishedStoreFaults.UnavailableStatus} {type}, actual {route.Says}");
            else if (route.Title != title) problems.Add($"a route: {type} is titled \"{route.Title}\", not \"{title}\"");
        }

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    /// <summary>A classifier that names every exception a store fault: a Forum whose middleware answered a request Kestrel rejected would answer it <c>503</c>.</summary>
    private sealed class EveryExceptionIsAStoreFault : IStoreFaultClassifier
    {
        public bool IsUnavailable(Exception exception) => true;
    }

    /// <summary>
    /// ULG50. A request the server itself rejected (<see cref="BadHttpRequestException"/>: an oversized or malformed body) is the caller's, whatever the store is
    /// doing: the middleware writes nothing and throws the original, so the host answers it as it answers it on a healthy store (<c>400</c>, <c>413</c>) and not
    /// <c>503</c> or <c>500</c>. The classifier names every exception a store fault, so only the exclusion of this type keeps the answer from being a <c>503</c>.
    /// </summary>
    [Theory]
    [InlineData(400)]
    [InlineData(413)]
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The fact records whatever escapes the middleware.")]
    public async Task R11_71_ULG50_ARequestTheServerRejectedIsNotAnsweredAsAStoreFaultWhileTheStoreIsFaulted(int status)
    {
        var rejected = new BadHttpRequestException("the body is not what the request declared", status);
        var context = ContextWith(new EveryExceptionIsAStoreFault(), started: false, Ct);
        Exception? escaped = null;
        try
        {
            await new StoreFaultsMiddleware(_ => throw rejected).InvokeAsync(context);
        }
        catch (Exception e)
        {
            escaped = e;
        }

        var problems = new List<string>();
        if (!ReferenceEquals(escaped, rejected)) problems.Add($"expected the rejection to reach the host unchanged, actual {escaped?.GetType().Name ?? "an answer"}");
        if (context.Response.StatusCode != StatusCodes.Status200OK) problems.Add($"the middleware wrote the status {context.Response.StatusCode} for a request the server rejected with {status}");
        if (context.Response.Body.Length != 0) problems.Add($"the middleware wrote {context.Response.Body.Length} bytes for a request the server rejected");

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    /// <summary>
    /// ULG53 (red in FULG1's third form). The store-fault middleware is the first the Forum composes, and nothing registered before it: a middleware composed
    /// ahead of it (the status-code pages, the charset guard, anything a later change adds) is a place a fault of a store raised there is not answered by the
    /// one rule. Read from <c>Program.cs</c>, between the host's build and its return: the first <c>app.Use...</c> or endpoint registration is <c>UseStoreFaults</c>,
    /// and it is there once.
    /// </summary>
    [Fact]
    public void R11_71_ULG53_TheStoreFaultMiddlewareIsTheFirstTheForumComposes()
    {
        var text = File.ReadAllText(RepositoryFile("src/Curia.Api/Program.cs"));
        var start = text.IndexOf("builder.Build();", StringComparison.Ordinal);
        var end = text.IndexOf("return app;", start < 0 ? 0 : start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "Program.cs has no build-to-return region the fact can read");

        var composed = text[start..end]
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => !l.StartsWith("//", StringComparison.Ordinal) && (l.StartsWith("app.Use", StringComparison.Ordinal) || l.StartsWith("app.Map", StringComparison.Ordinal) || l.Contains(".Map(app)", StringComparison.Ordinal)))
            .ToList();

        var problems = new List<string>();
        if (composed.Count == 0) problems.Add("no middleware or endpoint is composed in the region");
        else if (!composed[0].StartsWith("app.UseStoreFaults(", StringComparison.Ordinal)) problems.Add($"the first thing composed is \"{composed[0]}\", not UseStoreFaults");
        if (composed.Count(l => l.StartsWith("app.UseStoreFaults(", StringComparison.Ordinal)) != 1) problems.Add("UseStoreFaults is not composed exactly once");

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    /// <summary>
    /// ULG54. A driver fault is found where a layer wrapped it: two, four and the published number of exceptions deep is a store fault the middleware answers
    /// <c>503</c> <c>curia/store/unavailable</c>; one deeper than the published depth is not (<c>500</c>). The depth is read from the errata.
    /// </summary>
    [Fact]
    public async Task R11_71_ULG54_AFaultWrappedToThePublishedDepthIsAStoreFaultAndOneDeeperIsNot()
    {
        var depth = PublishedStoreFaults.Depth;
        Assert.True(depth >= 4, $"the published depth is {depth}");

        var problems = new List<string>();
        foreach (var (levels, expected) in new[] { (2, 503), (4, 503), (depth, 503), (depth + 1, 500) })
        {
            Exception wrapped = new NpgsqlException("down", new TimeoutException());
            for (var i = 0; i < levels; i++) wrapped = i % 2 == 0 ? new InvalidOperationException("wrapper " + i, wrapped) : new AggregateException("aggregate " + i, wrapped);
            var context = ContextWith(new PostgresStoreFaults(), started: false, Ct);
            var thrown = wrapped;
            await new StoreFaultsMiddleware(_ => throw thrown).InvokeAsync(context);
            if (context.Response.StatusCode != expected) problems.Add($"a driver fault {levels} deep: expected {expected}, actual {context.Response.StatusCode} {await BodyOfAsync(context)}");
        }

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    private static string Chain(Exception? exception)
    {
        var text = new StringBuilder();
        for (var e = exception; e is not null; e = e.InnerException) text.Append(e.Message).Append(' ');
        return text.ToString();
    }
}
