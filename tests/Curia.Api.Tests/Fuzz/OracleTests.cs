using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Reflection;
using Curia.Canon;
using Curia.Domain.Primitives;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Curia.Api.Tests.Fuzz;

/// <summary>
/// R14.10: the random pass's floor (<see cref="Oracle.PastFirstParser"/>) counts no refusal by a
/// first parser as past it (review of 6cbfa9f). ADMIT's refusals are taken from src, not from the
/// oracle, so a new ADMIT slug is covered without editing this test; the token endpoint's proof
/// refusals are taken from the live answer and the reason it logged, so a reason logged for them that
/// is not a parser refusal fails here, loudly. It runs on a
/// plain <see cref="ForumFixture"/>, not the fuzzer's, so its requests never enter the closed pass's
/// recorder.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class OracleTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    [Fact]
    public void R14_10_EveryAdmitRefusalStopsAtTheFirstParser()
    {
        var types = typeof(CanonErrors)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(Error))
            .Select(m => (Error)m.Invoke(null, [.. m.GetParameters().Select(p => p.ParameterType == typeof(int) ? (object)0 : "")])!)
            .Select(e => e.Type)
            .Where(t => t.StartsWith(Oracle.AdmitPrefix, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(types.Count >= 16, $"only {types.Count} ADMIT refusals found in CanonErrors");
        var past = types.Where(t => Oracle.PastFirstParser(HttpStatusCode.BadRequest, t)).ToList();
        Assert.True(past.Count == 0, "ADMIT refusals counted as past the first parser: " + string.Join(", ", past));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task R14_10_TheTokenEndpointsProofRefusalsStopAtTheFirstParser(bool sendProof)
    {
        var ct = TestContext.Current.CancellationToken;

        // client_assertion_type is required: TokenEndpoint.cs:98-99 refuses without it before the proof is read.
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_assertion"] = "x",
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            ["client_id"] = "x",
            ["grant_type"] = "client_credentials",
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/oauth/token", UriKind.Relative)) { Content = form };
        if (sendProof) request.Headers.TryAddWithoutValidation("DPoP", "a.b.c");

        // A host and a capture of this row's own, so the two rows never share a logger (M5).
        using var logged = new LogCapture();
        await using var host = forum.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<ILoggerProvider>(logged)));
        using var client = host.CreateClient();

        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var capturedType = logged.SingleType(0, 5001);
        var problemType = Oracle.ProblemType("/oauth/token", body, capturedType);
        if (sendProof)
        {
            // Since D29 closed, the proof runs the one proof validator, whose refusal of a header that
            // does not decode logs its slug: a parser's refusal, so the proof was read no further.
            Assert.Equal("curia/authn/malformed", capturedType);
            Assert.Contains(Oracle.Slug(problemType), Oracle.ParserRefusals);
            Assert.Equal("invalid_dpop_proof " + capturedType, problemType);
        }
        else
        {
            // The missing proof is refused with no reason, so it logs none.
            Assert.Equal(string.Empty, capturedType);
            Assert.Equal(Oracle.TokenEndpointProofRefusal, problemType);
        }

        Assert.False(Oracle.PastFirstParser(response.StatusCode, problemType), $"{problemType} counted as past the first parser");
    }

    /// <summary>
    /// M5: a token refusal's body names no reason, so the oracle classifies it by the reason it logged.
    /// A recorded refusal of the client whose logged reason is a signature that failed stopped at the
    /// credential, so it reached nothing; the same body with nothing logged says nothing of what it
    /// reached, so it is not reach either, except the form checks' bare <c>invalid_request</c> (M5,
    /// ruled), a parser refusal, which is reach. Before M5 the oracle read the slug from the body's
    /// <c>detail</c>; reading the body alone now counts every refusal of the client as reach.
    /// </summary>
    [Fact]
    public void M5_TheFuzzOracleClassifiesATokenRefusalByItsLoggedReason()
    {
        const string Body = "{\"error\":\"invalid_client\",\"error_description\":\"The client could not be authenticated\"}";

        var logged = Oracle.ProblemType("/oauth/token", Body, "curia/authn/signature-invalid");
        Assert.Equal("invalid_client curia/authn/signature-invalid", logged);
        Assert.False(Oracle.Reached("/oauth/token", HttpStatusCode.Unauthorized, logged), $"{logged} counted as reach");

        var unlogged = Oracle.ProblemType("/oauth/token", Body, string.Empty);
        Assert.False(Oracle.Reached("/oauth/token", HttpStatusCode.Unauthorized, unlogged), $"{unlogged}, with no reason logged, counted as reach");

        // The form checks' bare invalid_request is a parser refusal, which is reach (D33; M5, ruled).
        const string FormBody = "{\"error\":\"invalid_request\",\"error_description\":\"The request body is not a form this endpoint can read\"}";
        var form = Oracle.ProblemType("/oauth/token", FormBody, string.Empty);
        Assert.Equal("invalid_request", form);
        Assert.True(Oracle.Reached("/oauth/token", HttpStatusCode.BadRequest, form), $"{form}, the form checks' refusal, not counted as reach");

        // Only invalid_request is exempt: any other bare code still says nothing of what was reached.
        Assert.False(Oracle.Reached("/oauth/token", HttpStatusCode.BadRequest, "invalid_dpop_proof"), "a bare invalid_dpop_proof counted as reach");
        Assert.False(Oracle.Reached("/oauth/token", HttpStatusCode.Unauthorized, "invalid_client"), "a bare invalid_client counted as reach");
    }

    /// <summary>
    /// M5, ruled: the token endpoint's form checks refuse a request they cannot read as a token request
    /// with a bare <c>invalid_request</c> and log no reason. Taken from the running endpoint, not from
    /// <see cref="Oracle"/>, so the constant the oracle exempts is checked against what is served: it is
    /// reach (a parser refusal, D33) and, as on main, past the first parser, so the random pass's floor is untouched.
    /// </summary>
    [Fact]
    public async Task R14_10_TheTokenEndpointsFormRefusalIsLoggedWithNoReasonAndIsReach()
    {
        var ct = TestContext.Current.CancellationToken;

        using var logged = new LogCapture();
        await using var host = forum.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<ILoggerProvider>(logged)));
        using var client = host.CreateClient();

        using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(new Uri("/oauth/token", UriKind.Relative), content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using (var json = System.Text.Json.JsonDocument.Parse(body))
        {
            Assert.Equal("invalid_request", json.RootElement.GetProperty("error").GetString());
            Assert.Equal(
                "error,error_description",
                string.Join(",", json.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)));
        }

        var capturedType = logged.SingleType(0, 5001);
        Assert.Equal(string.Empty, capturedType);
        var problemType = Oracle.ProblemType("/oauth/token", body, capturedType);
        Assert.Equal(Oracle.TokenEndpointFormRefusal, problemType);
        Assert.True(Oracle.Reached("/oauth/token", response.StatusCode, problemType), $"{problemType} not counted as reach");
        // The floor is untouched: main served the form refusals' detail as JSON null, which its ProblemType did not append, so main's problem type was this same bare invalid_request, past the first parser there as here.
        Assert.True(Oracle.PastFirstParser(response.StatusCode, problemType), $"{problemType} not counted as past the first parser, which main counted");
    }
}
