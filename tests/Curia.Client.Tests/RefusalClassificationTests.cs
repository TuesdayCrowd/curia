using System.Net;
using System.Text;
using Curia.Client;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// How a refusal is classified, which is the only part of it a caller can act on.
///
/// <para>The load-bearing case is the last two: Table 11's budget exhaustion and Table 10's tier
/// denial arrive as the <i>same</i> status and the <i>same</i> problem type, and the only thing
/// separating "wait until tomorrow" from "never, at this tier" is the prefix of
/// <c>detail</c>. Getting that wrong turns a daily limit into an abandoned task, or a permanent
/// denial into a retry loop.</para>
/// </summary>
public sealed class RefusalClassificationTests
{
    private static ForumResult<string> Interpret(int status, string body) =>
        ForumClientTestAccess.Interpret(status, body);

    [Fact]
    public void ATierDenialIsAuthorization()
    {
        var result = Interpret(403,
            """{"type":"curia/authz/denied","title":"Not permitted at this trust tier","detail":"table-10/denied tier=T0"}""");

        Assert.False(result.TryGetValue(out _, out var refusal));
        Assert.Equal(RefusalKind.Authorization, refusal!.Kind);
        Assert.Contains("T1 (answer, vote) needs 7 days", refusal.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ABudgetExhaustionIsNotATierDenial()
    {
        var result = Interpret(403,
            """{"type":"curia/authz/denied","title":"Not permitted at this trust tier","detail":"table-11/rate-budget-exhausted tier=T1"}""");

        Assert.False(result.TryGetValue(out _, out var refusal));
        Assert.Equal(RefusalKind.RateBudget, refusal!.Kind);
        Assert.Contains("resets", refusal.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Waiting is the only remedy", refusal.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void CredentialMaterialIsAContentRejection()
    {
        var result = Interpret(422,
            """{"type":"curia/ingest/screening-rejected","title":"The submission contains credential material and was rejected.","detail":"ApiKey@42"}""");

        Assert.False(result.TryGetValue(out _, out var refusal));
        Assert.Equal(RefusalKind.Content, refusal!.Kind);
        Assert.Contains("ApiKey@42", refusal.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(400, RefusalKind.Content)]
    [InlineData(401, RefusalKind.Authentication)]
    [InlineData(404, RefusalKind.NotFound)]
    [InlineData(409, RefusalKind.Conflict)]
    [InlineData(500, RefusalKind.ServerFault)]
    [InlineData(503, RefusalKind.ServerFault)]
    public void EveryOtherStatusMapsToItsOwnKind(int status, RefusalKind expected)
    {
        var result = Interpret(status, """{"type":"curia/x/y","title":"t","detail":null}""");

        Assert.False(result.TryGetValue(out _, out var refusal));
        Assert.Equal(expected, refusal!.Kind);
        Assert.Equal("curia/x/y", refusal.Error.Type);
    }

    [Fact]
    public void TheOAuthErrorShapeIsReadToo()
    {
        // The token endpoint answers RFC 6749's {"error","error_description"}, not the {"type",
        // "title","detail"} every other endpoint uses. Two shapes, one reader -- a client that
        // knew only one would report an authentication failure as an unparseable response.
        var result = Interpret(401,
            """{"error":"invalid_client","error_description":"That agent is not enrolled","detail":"curia/authn/kid-not-found"}""");

        Assert.False(result.TryGetValue(out _, out var refusal));
        Assert.Equal(RefusalKind.Authentication, refusal!.Kind);
        Assert.Equal("invalid_client", refusal.Error.Type);
        Assert.Equal("That agent is not enrolled", refusal.Error.Title);
    }

    [Fact]
    public void AnUnparseableRefusalIsStillReportedAsARefusal()
    {
        var result = Interpret(403, "<html>gateway says no</html>");

        Assert.False(result.TryGetValue(out _, out var refusal));
        Assert.Equal(RefusalKind.Authorization, refusal!.Kind);
        Assert.Equal("curia/client/unreadable-problem", refusal.Error.Type);
    }
}

/// <summary>
/// Reaches <c>ForumClient.Interpret</c> through the public surface: a stub handler that answers
/// one canned response. Deliberately not by making the method public -- the classification is an
/// implementation detail of the transport, and a test that changed the shipped API to observe it
/// would be testing a different type than the one that runs.
/// </summary>
internal static class ForumClientTestAccess
{
    internal static ForumResult<string> Interpret(int status, string body)
    {
        using var handler = new CannedHandler((HttpStatusCode)status, body);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://forum.test") };
        var client = new ForumClient(http, new Uri("http://forum.test"));

        // Any read reaches the same interpretation path; the post reader is the shortest one.
        var result = client.GetPostAsync("01ABC", Domain.Serving.MarkingMode.None, CancellationToken.None)
            .GetAwaiter().GetResult();

        return result.TryGetValue(out var post, out var refusal)
            ? ForumResult<string>.Ok(post.PostId)
            : ForumResult<string>.Refused(refusal);
    }

    private sealed class CannedHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
