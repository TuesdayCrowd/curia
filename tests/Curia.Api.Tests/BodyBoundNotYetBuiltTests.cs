using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>The Forum on a real socket (<c>TestServer</c> does not enforce <c>MaxRequestBodySize</c>, so a probe of the host's body limit needs Kestrel).</summary>
public sealed class BodyBoundKestrelFixture : ForumFixture
{
    public BodyBoundKestrelFixture() => UseReservedKestrelPort();
}

/// <summary>
/// BCAP1 and BCAP2 (errata G83 non-property (c); D125; R6.68 specified, not built): the Forum bounds no request body of its own, and the host's default of 30,000,000 bytes is the only bound in force. These
/// two rows <b>assert today's behaviour</b> through the real route on a Kestrel host, so that the sentence in <c>docs/runbooks/reverse-proxy.md</c> ("The Forum has no bound of its own until D125 is closed")
/// is held true and fails the day it stops being. They are the known-defect row pattern: a row that passes in the fixed state fails the run. D125's stage deletes both, the runbook's sentence and G83's non-property (c).
/// Task 0b measured what the missing bound costs: about 5 bytes of resident memory per body byte at eight concurrent 29 MB bodies, and an empty-bodied <c>413</c> on the post route.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class BodyBoundNotYetBuiltTests(BodyBoundKestrelFixture forum) : IClassFixture<BodyBoundKestrelFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string WhatToDo =
        " D125 (the Forum reads a request body with no bound of its own) is closed or the host's limit moved: delete BCAP1 and BCAP2, the sentence 'The Forum has no bound of its own until D125 is closed' in docs/runbooks/reverse-proxy.md, and G83's non-property (c).";

    /// <summary>
    /// BCAP1: a body of 2 MiB (past R6.39's 1 MiB) sent to <c>POST /v1/agents</c> is <b>read and answered by the route</b> (400, a problem document of the route's own type), not refused by the host as too large.
    /// </summary>
    [Fact]
    public async Task G83_BCAP1_ABodyPastR6_39IsReadByTheRouteNotRefusedByTheHost()
    {
        const int size = 2 * 1024 * 1024;
        var head = Encoding.ASCII.GetBytes("{\"agent_id\":\"");
        var tail = Encoding.ASCII.GetBytes("\"}");
        var body = new byte[size];
        head.CopyTo(body, 0);
        Array.Fill(body, (byte)'a', head.Length, size - head.Length - tail.Length);
        tail.CopyTo(body, size - tail.Length);

        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var response = await forum.Client.PostAsync(new Uri("/v1/agents", UriKind.Relative), content, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        string? type = null;
        try
        {
            type = JsonDocument.Parse(text).RootElement.GetProperty("type").GetString();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException)
        {
            // Not a problem document: the assertion below says what was answered.
        }

        Assert.True(
            response.StatusCode == HttpStatusCode.BadRequest && type is not null && type != "curia/request/too-large",
            $"a 2 MiB body to POST /v1/agents should be read by the route and answered 400 with the route's own problem type; got {(int)response.StatusCode} type {type ?? "none"}.{WhatToDo}");
    }

    /// <summary>BCAP2: a body whose declared length is past the host's default of 30,000,000 bytes is refused <c>413</c> by the host: the default is what is in force.</summary>
    [Fact]
    public async Task G83_BCAP2_TheHostsDefaultOf30000000BytesIsTheOnlyBoundInForce()
    {
        var port = forum.Client.BaseAddress!.Port;
        var reply = await RawHttp.PostAsync(port, forum.Client.BaseAddress.Authority, "/v1/agents", 30_000_001, 65_536, "application/json", Ct);
        Assert.True(reply.Status == 413, $"a declared length of 30,000,001 bytes should be refused 413 by the host's default; got {reply.Status}.{WhatToDo}");
    }
}
