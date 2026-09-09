using System.Diagnostics.CodeAnalysis;
using Curia.Client;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// The fixture the write tools will be proved against, proved first.
///
/// <para><b>Why this file exists before any write tool does.</b> The stub's handler did not read
/// <c>request.Method</c> at all — zero occurrences in the file — so a
/// <c>POST /v1/posts/{id}/flags</c> matched the <c>StartsWith("/v1/posts/")</c> read branch and came
/// back as a <i>post document</i> with 200. A flag test written on that fixture would have passed
/// having exercised the read path and asserted nothing about writing. That is trap 12, a fixture
/// that agrees with the defect, and it is the one ordering constraint in this stage that is not a
/// preference: every test that proves a write tool is worthless until this holds.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class StubWriteSurfaceTests : IDisposable
{
    private readonly StubLog _log = new();

    public void Dispose()
    {
        _log.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A POST and a GET to the same path prefix are answered differently.
    ///
    /// <para>The assertion that carries the information is the <b>shape</b> of each answer, not that
    /// both succeeded: the defect this replaces returned <c>200</c> with a well-formed post document
    /// for a flag, so "the call succeeded" was true of the broken fixture too.</para>
    /// </summary>
    [Fact]
    public async Task APostAndAGetToTheSamePrefixAreNotTheSameRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = _log.RawClient();

        using var read = await http.GetAsync(new Uri($"/v1/posts/{StubLog.PostId}", UriKind.Relative), ct);
        var readBody = await read.Content.ReadAsStringAsync(ct);

        using var flagBody = Empty();
        using var written = await http.PostAsync(
            new Uri($"/v1/posts/{StubLog.PostId}/flags", UriKind.Relative), flagBody, ct);
        var writtenBody = await written.Content.ReadAsStringAsync(ct);

        // The read is a provenance-wrapped post; the write is a receipt. Neither is the other.
        Assert.Contains("\"provenance\"", readBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\"raised_at\"", readBody, StringComparison.Ordinal);

        Assert.Contains("\"raised_at\"", writtenBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\"provenance\"", writtenBody, StringComparison.Ordinal);

        // And the statuses differ, which is what a client's refusal classification turns on.
        Assert.Equal(System.Net.HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Created, written.StatusCode);
    }

    /// <summary>An unrouted POST is a 404 naming the method, not a read served by accident.</summary>
    [Fact]
    public async Task AnUnroutedPostIsRefusedRatherThanAnsweredFromTheReadTable()
    {
        using var http = _log.RawClient();

        using var body_ = Empty();
        using var response = await http.PostAsync(
            new Uri("/v1/log/head", UriKind.Relative), body_, TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("no POST", body, StringComparison.Ordinal);

        // Non-vacuity: the same path answers a GET, so the refusal is about the method rather than
        // about the route being absent.
        using var read = await http.GetAsync(
            new Uri("/v1/log/head", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, read.StatusCode);
    }

    /// <summary>
    /// RFC 9449 §8's nonce challenge is the normal flow. A stub that never challenges cannot tell a
    /// client that retries from one that does not, so the knob exists and the challenge is
    /// one-shot — the second request carries the nonce and succeeds.
    /// </summary>
    [Fact]
    public async Task TheTokenEndpointCanChallengeOnceWithANonce()
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = _log.RawClient();
        _log.PendingNonceChallenge = true;

        using var first = Empty();
        using var challenged = await http.PostAsync(new Uri("/oauth/token", UriKind.Relative), first, ct);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, challenged.StatusCode);
        Assert.True(challenged.Headers.TryGetValues("DPoP-Nonce", out var values));
        Assert.Equal(StubLog.NonceValue, values.Single());

        using var second = Empty();
        using var retried = await http.PostAsync(new Uri("/oauth/token", UriKind.Relative), second, ct);
        Assert.Equal(System.Net.HttpStatusCode.OK, retried.StatusCode);
    }

    /// <summary>
    /// The stub records what it was asked, so a write test can assert the request was made rather
    /// than inferring it from a response that a read could also have produced.
    /// </summary>
    [Fact]
    public async Task TheStubRecordsTheMethodAndPathOfEveryRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = _log.RawClient();

        using (await http.GetAsync(new Uri($"/v1/posts/{StubLog.PostId}", UriKind.Relative), ct)) { }
        using var submitted = Empty();
        using (await http.PostAsync(new Uri("/v1/posts", UriKind.Relative), submitted, ct)) { }

        Assert.Equal(
            [$"GET /v1/posts/{StubLog.PostId}", "POST /v1/posts"],
            _log.Requests);

        Assert.Single(_log.WrittenBodies);
    }

    /// <summary>
    /// §8.5's duplicate refusal, which is what makes <c>curia_ask</c> a three-outcome tool. Asserted
    /// here so the tool's own test is about the tool rather than about whether the fixture can
    /// produce a 409 at all.
    /// </summary>
    [Fact]
    public async Task R8_18_TheSubmitPathCanRefuseADuplicateWithTheCanonicalThread()
    {
        using var http = _log.RawClient();
        _log.RefusesAsDuplicate = true;

        using var content = Empty();
        using var response = await http.PostAsync(
            new Uri("/v1/posts", UriKind.Relative), content, TestContext.Current.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("duplicate-question", body, StringComparison.Ordinal);

        // R8.61: both measures AND both thresholds, and the model.
        Assert.Contains("\"cosine_bp\"", body, StringComparison.Ordinal);
        Assert.Contains("\"lexical_overlap_bp\"", body, StringComparison.Ordinal);
        Assert.Contains("\"refuse_cosine_bp\"", body, StringComparison.Ordinal);
        Assert.Contains("\"refuse_lexical_overlap_bp\"", body, StringComparison.Ordinal);
        Assert.Contains("\"model\"", body, StringComparison.Ordinal);

        // And the canonical thread's answers, wrapped in their provenance envelopes.
        Assert.Contains("\"answers\"", body, StringComparison.Ordinal);
        Assert.Contains("\"provenance\"", body, StringComparison.Ordinal);
    }

    private static StringContent Empty() => new("{}", System.Text.Encoding.UTF8, "application/json");
}
