using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using Curia.Application.Ports;
using Curia.Domain;
using Curia.Domain.Authorization;
using Curia.Domain.Primitives;
using Curia.Tests.Shared;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Curia.Api.Tests.NameProbe;

namespace Curia.Api.Tests;

/// <summary>
/// R9.57 (errata G62), NMN26 to NMN28: <c>GET /v1/posts/{postId}</c>, <c>POST /v1/posts/batch</c> and <c>GET /v1/threads/{rootPostId}</c> read the query name <c>marking</c> and no
/// other, and refuse any other <c>400</c> by R9.29's rules, as <c>curia/posts/unknown-parameter</c> (the first two) and <c>curia/threads/unknown-parameter</c>, after the caller is
/// known to be allowed and before the log is read; the neighbours that read other names are untouched.
///
/// <para><b>Oracles.</b> The types, the title and the tail of the detail are R9.57 words (<see cref="PublishedNameWords"/>); the post is one the test stored. Each refusal has its
/// control (<c>?marking=datamark</c> in two spellings of the name, <c>200</c>), the first assertion of the fact and green on the seam. The read count is a counting
/// <see cref="IEventReader"/> compared with the board listing's own refusal of a name, which is made at the same place.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class ReadRouteUnknownNameTests(PassThroughGateForumFixture forum) : IClassFixture<PassThroughGateForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<NameReply> BatchAsync(HttpClient client, string query, string body, CancellationToken ct)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(new Uri("/v1/posts/batch" + query, UriKind.Relative), content, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        JsonElement json = default;
        try
        {
            using var doc = JsonDocument.Parse(text);
            json = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Not JSON.
        }

        return new NameReply(response.StatusCode, text, json);
    }

    private static string DetailOf(NameReply reply) =>
        reply.Json.ValueKind == JsonValueKind.Object && reply.Json.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()! : string.Empty;

    private static string TitleOf(NameReply reply) =>
        reply.Json.ValueKind == JsonValueKind.Object && reply.Json.TryGetProperty("title", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()! : string.Empty;

    /// <summary>The words check: two types (the first for the single read and the batch), a title and a detail tail ending <c>marking</c>.</summary>
    [Fact]
    public void R9_57_NMN26_ThePublishedWordsAreTheOnesTheFactsUse()
    {
        Assert.Equal(("curia/posts/unknown-parameter", "curia/threads/unknown-parameter"), PublishedNameWords.UnknownTypes());
        Assert.Equal("this route reads exactly these: marking", PublishedNameWords.UnknownDetailTail());
        Assert.StartsWith("That parameter is not read by this route", PublishedNameWords.UnknownTitle(), StringComparison.Ordinal);
    }

    /// <summary>
    /// NMN26. A name the route does not read, on each of the three routes: <c>400</c>, the family's type, R9.29's title, a detail that names <c>x</c> and ends <c>this route reads
    /// exactly these: marking</c>; <c>?marking=datamark</c> and <c>?Marking=datamark</c> are served; a near miss (<c>?markings=</c>) is told the spelling. Predicted red on the seam:
    /// <c>200</c> for the unknown name.
    /// </summary>
    [Fact]
    public async Task R9_57_NMN26_ANameTheThreeReadRoutesDoNotReadIsRefusedByName()
    {
        var (postsType, threadsType) = PublishedNameWords.UnknownTypes();
        var (_, stored) = await StoreQuestionAsync(forum, Board(), Ct);
        var batchBody = JsonSerializer.Serialize(new { digests = new[] { stored.Digest } });
        var tail = PublishedNameWords.UnknownDetailTail();
        var expected = new List<string>();
        var actual = new List<string>();

        foreach (var control in new[] { "?marking=datamark", "?Marking=datamark" })
        {
            expected.Add($"{control}: 200 200 200");
            actual.Add($"{control}: {(int)(await GetAsync(forum.Client, $"/v1/posts/{stored.PostId}{control}", ct: Ct)).Status} {(int)(await GetAsync(forum.Client, $"/v1/threads/{stored.PostId}{control}", ct: Ct)).Status} {(int)(await BatchAsync(forum.Client, control, batchBody, Ct)).Status}");
        }

        var routes = new (string Name, Func<string, Task<NameReply>> Send, string Type)[]
        {
            ("GET /v1/posts/{id}", q => GetAsync(forum.Client, $"/v1/posts/{stored.PostId}{q}", ct: Ct), postsType),
            ("GET /v1/threads/{id}", q => GetAsync(forum.Client, $"/v1/threads/{stored.PostId}{q}", ct: Ct), threadsType),
            ("POST /v1/posts/batch", q => BatchAsync(forum.Client, q, batchBody, Ct), postsType),
        };
        foreach (var (name, send, type) in routes)
        {
            var refused = await send("?x=1");
            var nearMiss = await send("?markings=1");
            expected.Add($"{name} ?x=1: 400 {type}, the title, names x, ends \"{tail}\"");
            actual.Add($"{name} ?x=1: {refused.Says}, {(TitleOf(refused).StartsWith("That parameter is not read by this route", StringComparison.Ordinal) ? "the title" : "not the title")}, {(DetailOf(refused).Contains("parameter=x", StringComparison.Ordinal) ? "names x" : "does not name x")}, {(DetailOf(refused).EndsWith(tail, StringComparison.Ordinal) ? $"ends \"{tail}\"" : "does not end with the tail")}");
            expected.Add($"{name} ?markings=1: the hint");
            actual.Add($"{name} ?markings=1: {(DetailOf(nearMiss).Contains("did you mean marking", StringComparison.Ordinal) ? "the hint" : $"no hint ({nearMiss.Says})")}");
        }

        Said(expected, actual);
    }

    private sealed class CountingReader(IEventReader inner, Counter counter) : IEventReader
    {
        public Task<Result<IReadOnlyList<AppendedEvent>>> ReadByAggregateAsync(AggregateId aggregateId, CancellationToken cancellationToken = default)
        {
            counter.Count++;
            return inner.ReadByAggregateAsync(aggregateId, cancellationToken);
        }

        public Task<Result<IReadOnlyList<AppendedEvent>>> ReadForwardAsync(EventSequence afterSeq, int? maxCount = null, CancellationToken cancellationToken = default)
        {
            counter.Count++;
            return inner.ReadForwardAsync(afterSeq, maxCount, cancellationToken);
        }
    }

    private sealed class Counter
    {
        internal int Count { get; set; }
    }

    /// <summary>
    /// A decorator that denies an anonymous read of a thread (the resource and action the three read routes ask the policy about) while <c>deny</c> says so, and forwards everything else.
    /// It is the seam that makes a denied anonymous read constructible; it fabricates nothing but the denial.
    /// </summary>
    private sealed class DenyingThreadReads(IPolicyDecisionPoint inner, Func<bool> deny) : IPolicyDecisionPoint
    {
        public ValueTask<Result<AuthorizationDecision>> EvaluateAsync(AuthorizationRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            return deny() && request.Resource == ResourceKind.Thread && request.Action == ActionKind.Read
                ? ValueTask.FromResult(Result<AuthorizationDecision>.Ok(new AuthorizationDecision(DecisionEffect.Deny, "table-10/denied", GrantQualifier.None)))
                : inner.EvaluateAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// NMN27 (rewritten in Amendment 3, ruling 36, M4: as built it could not see the order, because the policy never denied). The order, in both of its halves, on each of the three routes:
    /// with the policy denying an anonymous read of a thread, a request that carries an unknown name is answered the denial (<c>403 curia/authz/denied</c>) and not the name, and the log is read
    /// zero times; with the policy allowing, the same request is answered the unknown-name refusal and the log is read zero times. So the name is refused after the allowed check and before the
    /// log is read. The batch's refusal of its body (<c>batch-malformed</c>) keeps its place before the name. Falsification FNMN30 (the name before the allowed check) and FNMN31 (after the log read).
    /// </summary>
    [Fact]
    public async Task R9_57_NMN27_TheNameIsRefusedAfterTheAllowedCheckAndBeforeTheLogIsRead()
    {
        var counter = new Counter();
        var deny = false;
        await using var host = forum.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IEventReader>(sp => new CountingReader(sp.GetRequiredService<IEventStore>(), counter));
            var decisions = services.Last(d => d.ServiceType == typeof(IPolicyDecisionPoint));
            if (decisions.ImplementationFactory is null) throw new InvalidOperationException("IPolicyDecisionPoint is registered by a factory; this decorator wraps what it builds");
            services.Remove(decisions);
            services.AddSingleton<IPolicyDecisionPoint>(sp => new DenyingThreadReads((IPolicyDecisionPoint)decisions.ImplementationFactory(sp), () => Volatile.Read(ref deny)));
        }));
        using var client = host.CreateClient();
        var (_, stored) = await StoreQuestionAsync(forum, Board(), Ct);
        var batchBody = JsonSerializer.Serialize(new { digests = new[] { stored.Digest } });
        var (postsType, threadsType) = PublishedNameWords.UnknownTypes();

        async Task<NameReply> Send(int route) => route switch
        {
            0 => await GetAsync(client, $"/v1/posts/{stored.PostId}?x=1", ct: Ct),
            1 => await GetAsync(client, $"/v1/threads/{stored.PostId}?x=1", ct: Ct),
            _ => await BatchAsync(client, "?x=1", batchBody, Ct),
        };

        var routes = new (string Name, string Refused)[]
        {
            ("GET /v1/posts/{id}", "400 " + postsType),
            ("GET /v1/threads/{id}", "400 " + threadsType),
            ("POST /v1/posts/batch", "400 " + postsType),
        };

        var expected = new List<string>();
        var actual = new List<string>();
        foreach (var allowing in new[] { false, true })
        {
            Volatile.Write(ref deny, !allowing);
            try
            {
                for (var route = 0; route < routes.Length; route++)
                {
                    counter.Count = 0;
                    var reply = await Send(route);
                    expected.Add($"{(allowing ? "allowed" : "denied")} {routes[route].Name} ?x=1: {(allowing ? routes[route].Refused : "403 curia/authz/denied")}, 0 reads");
                    actual.Add($"{(allowing ? "allowed" : "denied")} {routes[route].Name} ?x=1: {reply.Says}, {counter.Count} reads");
                }
            }
            finally
            {
                Volatile.Write(ref deny, false);
            }
        }

        var malformed = await BatchAsync(client, "?x=1", """{"post_ids":"01ABC"}""", Ct);
        expected.Add("a malformed body with a name: 400 curia/posts/batch-malformed");
        actual.Add($"a malformed body with a name: {malformed.Says}");

        Said(expected, actual);
    }

    /// <summary>
    /// NMN28. The neighbours are untouched (non-property (h)): the route families that read other names or none keep their statuses with an extra name: <c>GET /v1/jwks</c>,
    /// <c>GET /v1/flags</c> (denied to an anonymous caller as it was), <c>GET /health</c>. A control, green on the seam; FNMN33 turns it red.
    /// </summary>
    [Fact]
    public async Task R9_57_NMN28_TheRoutesThatReadOtherNamesAreNotTouched()
    {
        var (authorId, _) = await StoreQuestionAsync(forum, Board(), Ct);
        var rows = new (string Path, string Expected)[]
        {
            ("/health?x=1", "200"),
            ("/v1/jwks?agent=" + Uri.EscapeDataString(authorId) + "&x=1", "404"),
            ("/v1/flags?x=1", "401"),
        };

        var actual = new List<string>();
        foreach (var (path, _) in rows)
            actual.Add($"{path}: {(int)(await GetAsync(forum.Client, path, ct: Ct)).Status}");

        // The authenticated row: a caller the flag listing admits is answered 200 with a name it does not read (a recorded non-property, G37's shape): an unknown-name check added
        // to that route after authentication is a change the anonymous 401 above cannot see (FNMN33).
        var caller = await AcceptedAnswerScene.EnrolAsync(forum, "nmn28", Ct);
        var token = await caller.Dpop.GetTokenAsync(forum.Client, TokenEndpoint, forum.Now, Ct);
        var listed = await GetAsync(forum.Client, "/v1/flags?x=1", token, caller, forum, Ct);
        actual.Add($"/v1/flags?x=1 as an admitted caller: {(int)listed.Status}");

        Said(rows.Select(r => $"{r.Path}: {r.Expected}").Append("/v1/flags?x=1 as an admitted caller: 200"), actual);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(forum.Client, "/health", ct: Ct)).Status);
    }
}
