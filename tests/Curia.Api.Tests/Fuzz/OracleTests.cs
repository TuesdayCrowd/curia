using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Reflection;
using Curia.Canon;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Api.Tests.Fuzz;

/// <summary>
/// R14.10: the random pass's floor (<see cref="Oracle.PastFirstParser"/>) counts no refusal by a
/// first parser as past it (review of 6cbfa9f). ADMIT's refusals are taken from src, not from the
/// oracle, so a new ADMIT slug is covered without editing this test; the token endpoint's proof
/// refusals are taken from the live answer, so a detail added to them fails here, loudly. It runs on a
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

        using var response = await forum.Client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problemType = Oracle.ProblemType("/oauth/token", body);
        Assert.Equal(Oracle.TokenEndpointProofRefusal, problemType);
        Assert.False(Oracle.PastFirstParser(response.StatusCode, problemType), $"{problemType} counted as past the first parser");
    }
}
