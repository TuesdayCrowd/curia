using System.Diagnostics.CodeAnalysis;
using Curia.Client;
using Curia.Tests.Shared;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R6.52's collapse, on the <i>rendering</i> path rather than the verifying one.
///
/// <para><b>Why this is a separate class from <see cref="PostVerifierTests"/>.</b> There are two
/// paths on which a reader learns what this client established about a signature, and only one of
/// them goes through <see cref="PostVerifier"/>. <c>curia_read</c> and the CLI's own rendering build
/// a <see cref="SignatureVerdict"/> directly and print <see cref="SignatureVerdict.Describe"/>, so
/// the third outcome has to exist on the verdict as well as in the verifier -- and it did not, until
/// a falsification run showed that breaking the verdict's outcome left every verifier test green.
/// The two paths are tested separately because they are separately capable of the collapse.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class UnreachableKeySetTests : IDisposable
{
    private readonly StubLog _log = new();

    public void Dispose()
    {
        _log.Dispose();
        GC.SuppressFinalize(this);
    }

    private static Refusal Transport() => new(
        RefusalKind.Transport,
        0,
        new Error("curia/client/transport", "The Forum could not be reached", "connection refused"));

    /// <summary>
    /// The verdict for an unfetched key set is the third outcome, and the word a reader sees says
    /// so. <c>Assert.Equal</c> on the outcome refuses both collapses at once: mapping it to
    /// <c>Failed</c> and mapping it to <c>Verified</c> each fail this line.
    /// </summary>
    [Fact]
    public void R6_52_AnUnfetchedKeySetIsItsOwnOutcomeOnTheRenderingPath()
    {
        var verdict = SignatureCheck.Unreachable(Post(), Transport());

        Assert.Equal(CheckOutcome.CouldNotCheck, verdict.Outcome);
        Assert.StartsWith("COULD NOT BE CHECKED", verdict.Describe, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sentence is the whole point. Before this stage both callers passed an empty key array
    /// instead of the refusal, and <c>SelectKey</c> answered "The author's JWKS carries no key
    /// matching the post's kid" -- a statement about the author, on the evidence of a network fault,
    /// delivered to a reader who cannot ask a follow-up question.
    /// </summary>
    [Fact]
    public void R6_52_TheSentenceNamesTheFaultRatherThanTheAuthorsKeys()
    {
        var verdict = SignatureCheck.Unreachable(Post(), Transport());

        Assert.Contains("could not be fetched", verdict.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("no key matching", verdict.Detail, StringComparison.Ordinal);

        // Non-vacuity: that sentence is what an empty key array still produces, so the assertion
        // above is about which path was taken rather than about the phrase having gone away.
        var collapsed = SignatureCheck.Verify(Post(), []);
        Assert.Contains("no-key-for-post", collapsed.Detail, StringComparison.Ordinal);
        Assert.Equal(CheckOutcome.Failed, collapsed.Outcome);
    }

    /// <summary>
    /// The digest survives the fault. It is a fact about the served document and depends on no key,
    /// so a reader who cannot check authorship can still record what it was they could not check.
    /// </summary>
    [Fact]
    public void R6_52_TheDigestIsStillReportedWhenTheKeysAreUnreachable()
    {
        var verdict = SignatureCheck.Unreachable(Post(), Transport());

        Assert.NotNull(verdict.PrefixedDigest);
        Assert.Equal(_log.Submission.PrefixedDigest, verdict.PrefixedDigest);
    }

    /// <summary>
    /// A verdict that could not be checked never renders as a verified one, and a passage carrying
    /// it says so above the boundary where the client's own words go.
    /// </summary>
    [Fact]
    public void R6_52_APassageWithAnUncheckedVerdictSaysSoInTheFrame()
    {
        var rendered = new Passage(Post(), SignatureCheck.Unreachable(Post(), Transport())).Render();

        Assert.Contains("COULD NOT BE CHECKED", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("verified locally", rendered, StringComparison.Ordinal);
    }

    private ProvenancePost Post()
    {
        var parsed = ParseServedPost(_log.PostJson());
        return parsed;
    }

    /// <summary>
    /// The post as this client parses it from the wire, rather than a record built by hand: a
    /// fixture assembled in the test could carry a digest the Forum never serves, which is exactly
    /// how the prefixed/bare digest defect survived its own covering test.
    /// </summary>
    private static ProvenancePost ParseServedPost(string json)
    {
        using var handler = new OneShot(json);
        using var http = new HttpClient(handler) { BaseAddress = StubLog.Forum };
        var client = new ForumClient(http, StubLog.Forum);

        var read = client
            .GetPostAsync(StubLog.PostId, Domain.Serving.MarkingMode.None, TestContext.Current.CancellationToken)
            .GetAwaiter()
            .GetResult();

        Assert.True(read.TryGetValue(out var post, out var refusal), refusal?.Summary);
        return post!;
    }

    private sealed class OneShot(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };

            return Task.FromResult(response);
        }
    }
}
