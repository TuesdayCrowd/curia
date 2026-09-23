using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using Curia.Client;
using Curia.Domain.Content;
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
///
/// <para><b>And the first repair was itself trap 12, twice over.</b> It put RFC 9449's nonce
/// challenge on the token endpoint, which the Forum never challenges, and served the duplicate
/// refusal in a shape the Forum never serves — both caught by reading the Forum rather than the
/// fixture. The rows below that assert protocol now assert the Forum's protocol, and the duplicate
/// refusal is read through the client's own reader rather than searched for member names, which the
/// wrong shape also contained.</para>
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

        using var flag = Write($"/v1/posts/{StubLog.PostId}/flags", Proof("jti-flag", _log.CurrentNonce));
        using var written = await http.SendAsync(flag, ct);
        var writtenBody = await written.Content.ReadAsStringAsync(ct);

        // The read is a provenance-wrapped post; the write is a receipt. Neither is the other.
        Assert.Contains("\"provenance\"", readBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\"raised_at\"", readBody, StringComparison.Ordinal);

        Assert.Contains("\"raised_at\"", writtenBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\"provenance\"", writtenBody, StringComparison.Ordinal);

        // And the statuses differ, which is what a client's refusal classification turns on.
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.Created, written.StatusCode);
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

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("no POST", body, StringComparison.Ordinal);

        // Non-vacuity: the same path answers a GET, so the refusal is about the method rather than
        // about the route being absent.
        using var read = await http.GetAsync(
            new Uri("/v1/log/head", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    /// <summary>
    /// RFC 9449 §8's nonce challenge, where the Forum issues it: on the write paths (R5.19), and not
    /// on the token endpoint. A write whose proof carries no nonce is answered <c>401</c> with the
    /// nonce to use and <c>use_dpop_nonce</c>, and the same write carrying it is accepted.
    /// </summary>
    [Fact]
    public async Task R5_19_WritePathsChallengeForANonceAndTheTokenEndpointDoesNot()
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = _log.RawClient();

        using (var tokenBody = new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = StubLog.Author }))
        using (var token = await http.PostAsync(new Uri("/oauth/token", UriKind.Relative), tokenBody, ct))
            Assert.Equal(HttpStatusCode.OK, token.StatusCode);

        using var unnonced = Write($"/v1/posts/{StubLog.PostId}/flags", Proof("jti-a", nonce: null));
        using var challenged = await http.SendAsync(unnonced, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, challenged.StatusCode);
        Assert.True(challenged.Headers.TryGetValues("DPoP-Nonce", out var values));
        Assert.Equal(_log.CurrentNonce, values.Single());
        Assert.Contains("use_dpop_nonce", challenged.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);

        using var nonced = Write($"/v1/posts/{StubLog.PostId}/flags", Proof("jti-b", _log.CurrentNonce));
        using var accepted = await http.SendAsync(nonced, ct);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
    }

    /// <summary>
    /// A rotated nonce is challenged again, with the new one. A client that cached the last nonce
    /// meets this on the Forum's own schedule, and must recover rather than fail intermittently.
    /// </summary>
    [Fact]
    public async Task R5_19_AStaleNonceIsChallengedWithTheCurrentOne()
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = _log.RawClient();

        var old = _log.CurrentNonce;
        _log.RotateNonce();
        Assert.NotEqual(old, _log.CurrentNonce);

        using var stale = Write($"/v1/posts/{StubLog.PostId}/flags", Proof("jti-stale", old));
        using var response = await http.SendAsync(stale, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(_log.CurrentNonce, response.Headers.GetValues("DPoP-Nonce").Single());
        Assert.Contains("nonce-stale", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>jti</c> the Forum has already accepted is refused as a replay, and <b>without</b> a
    /// nonce: handing out a fresh nonce there would invite a retry that fails identically. The
    /// replay test the plan names — "reuse a DPoP proof → the replay test gets
    /// <c>curia/authn/replay</c>" — needs a fixture that refuses one.
    /// </summary>
    [Fact]
    public async Task R5_19_AReusedJtiIsRefusedAsAReplayAndNotChallenged()
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = _log.RawClient();

        using var first = Write($"/v1/posts/{StubLog.PostId}/flags", Proof("jti-once", _log.CurrentNonce));
        using (var accepted = await http.SendAsync(first, ct))
            Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);

        using var again = Write($"/v1/posts/{StubLog.PostId}/flags", Proof("jti-once", _log.CurrentNonce));
        using var replayed = await http.SendAsync(again, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, replayed.StatusCode);
        Assert.Contains("curia/authn/replay", await replayed.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        Assert.False(replayed.Headers.Contains("DPoP-Nonce"), "a replay is not a nonce problem, and must not be answered with a nonce");
    }

    /// <summary>
    /// The Forum's order, reproduced: the nonce is checked <b>before</b> the <c>jti</c> is burned
    /// (<c>AccessTokenValidator</c>), so a proof refused for its nonce never reached the replay
    /// cache. A stub that burned first would refuse the reference client's retry as a replay the day
    /// the client reused an identifier, and the Forum would not.
    /// </summary>
    [Fact]
    public async Task R5_19_AProofRefusedForItsNonceHasNotSpentItsJti()
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = _log.RawClient();

        using var unnonced = Write($"/v1/posts/{StubLog.PostId}/flags", Proof("jti-same", nonce: null));
        using (var challenged = await http.SendAsync(unnonced, ct))
            Assert.Equal(HttpStatusCode.Unauthorized, challenged.StatusCode);

        using var nonced = Write($"/v1/posts/{StubLog.PostId}/flags", Proof("jti-same", _log.CurrentNonce));
        using var accepted = await http.SendAsync(nonced, ct);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
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
    /// The receipt carries the digest of what was sent, computed from the envelope as the Forum
    /// computes it. It used to carry the stub's own post's digest whatever arrived, so every
    /// submission came back naming a different document.
    /// </summary>
    [Fact]
    public async Task TheReceiptNamesTheDigestOfTheSubmittedEnvelope()
    {
        var ct = TestContext.Current.CancellationToken;
        using var agent = Alice();
        var session = new ForumSession(_log.Client(), agent, _log.Store, TimeProvider.System);

        var submission = Signed(agent, "Is the receipt about this post?");
        var posted = await session.SubmitAsync(submission.Wire, ct);

        Assert.True(posted.TryGetValue(out var receipt, out var refusal), refusal?.Summary);
        Assert.Equal(submission.PrefixedDigest, receipt.Digest);

        // Non-vacuity: the stub's own post is a different document, so equality above is about the
        // submission rather than about a constant the two happen to share.
        Assert.NotEqual(_log.Submission.PrefixedDigest, receipt.Digest);
    }

    /// <summary>
    /// The Forum verifies the envelope's author against the token's subject, not against the
    /// envelope's claim about itself. An envelope signed as another agent under this agent's token is
    /// refused, as it would be there.
    /// </summary>
    [Fact]
    public async Task AnEnvelopeWhoseAuthorIsNotTheTokenSubjectIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        using var http = _log.RawClient();

        using (var tokenBody = new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = "https://agents.example/mallory" }))
        using (await http.PostAsync(new Uri("/oauth/token", UriKind.Relative), tokenBody, ct)) { }

        using var agent = Alice();
        var submission = Signed(agent, "Whose post is this?");

        using var request = Write("/v1/posts", Proof("jti-mismatch", _log.CurrentNonce), submission.Wire);
        using var response = await http.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("author-principal-mismatch", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    /// <summary>
    /// §8.5's duplicate refusal, which is what makes <c>curia_ask</c> a three-outcome tool — read
    /// through the client's own reader, so the fixture is proved in the shape a caller consumes.
    ///
    /// <para>The previous version of this row searched the body for member names. The fixture then
    /// served a shape the Forum never serves, which contained every name searched for, and the
    /// client's reader returned null for it: green over a refusal no caller could have read.</para>
    /// </summary>
    [Fact]
    public async Task R8_19_TheDuplicateRefusalReadsCompletelyThroughTheClient()
    {
        var ct = TestContext.Current.CancellationToken;
        using var agent = Alice();
        var session = new ForumSession(_log.Client(), agent, _log.Store, TimeProvider.System);
        _log.RefusesAsDuplicate = true;

        var posted = await session.SubmitAsync(Signed(agent, "A question the Forum has seen before?").Wire, ct);

        Assert.False(posted.TryGetValue(out _, out var refusal));
        Assert.Equal(RefusalKind.Conflict, refusal.Kind);

        var duplicate = refusal.AsDuplicate;
        Assert.NotNull(duplicate);

        Assert.Equal(StubLog.PostId, duplicate.CanonicalPostId);
        Assert.Equal(_log.Submission.PrefixedDigest, duplicate.CanonicalDigest);

        // R8.61: both measures, both refusal thresholds and the annotation one, and the model.
        Assert.Equal(9200, duplicate.CosineBp);
        Assert.Equal(7400, duplicate.LexicalOverlapBp);
        Assert.Equal(9400, duplicate.RefuseCosineBp);
        Assert.Equal(6000, duplicate.RefuseLexicalOverlapBp);
        Assert.Equal(8500, duplicate.AnnotateCosineBp);
        Assert.Equal("hashed-ngram@1", duplicate.Model);

        // The canonical thread's answer, with its provenance envelope, and none unreadable.
        var answer = Assert.Single(duplicate.Answers);
        Assert.Equal(0, duplicate.UnreadableAnswers);
        Assert.Equal(StubLog.AnswerPostId, answer.PostId);
        Assert.Contains(StubLog.AnswerMarker, answer.Rendered, StringComparison.Ordinal);
        Assert.Equal(StubLog.Author, answer.Provenance.Author);
    }

    private EnrolledAgent Alice() =>
        _log.Store.Load("alice").TryGetValue(out var agent, out var error)
            ? agent!
            : throw new InvalidOperationException(error!.Type);

    private static SignedSubmission Signed(EnrolledAgent agent, string title) =>
        SubmissionBuilder.Build(
                agent,
                new PostDraft { Kind = PostKind.Question, Board = "b", Title = title, Body = "Body of: " + title },
                DateTimeOffset.UnixEpoch)
            .TryGetValue(out var signed, out var error)
            ? signed!
            : throw new InvalidOperationException(error!.Type);

    /// <summary>
    /// A DPoP proof carrying the named <c>jti</c> and nonce. Not signed with anything meaningful:
    /// the stub reads the claims and checks the protocol, and the signature is the Forum's
    /// concern, exercised against the real one in <c>Curia.Api.Tests</c>.
    /// </summary>
    private static string Proof(string jti, string? nonce)
    {
        var header = Base64Url.EncodeToString("""{"typ":"dpop+jwt","alg":"ES256"}"""u8);
        var claims = nonce is null
            ? $$"""{"jti":"{{jti}}","htm":"POST"}"""
            : $$"""{"jti":"{{jti}}","htm":"POST","nonce":"{{nonce}}"}""";

        return $"{header}.{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(claims))}.c2lnbmF0dXJl";
    }

    private static HttpRequestMessage Write(string path, string proof, ReadOnlyMemory<byte>? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = body is { } bytes ? new ReadOnlyMemoryContent(bytes) : Empty(),
        };

        request.Headers.Add("DPoP", proof);
        return request;
    }

    private static StringContent Empty() => new("{}", Encoding.UTF8, "application/json");
}
