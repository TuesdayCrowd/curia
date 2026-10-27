using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using Curia.Client;
using Curia.Client.Cli;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R11.54 (errata G41): a reference reader words a <c>curia/posts/withheld</c> answer as withheld and not as "no such post", and keeps the exit code
/// of a refusal that names no post (5). A <c>404</c> of the Forum's post-not-found type is still "No such post", and a <c>404</c> of no type the Forum
/// issues (a proxy's) says neither. The problem types are parsed from the errata's entries by <c>PublishedWithheldWords</c>; the oracle is the words
/// the refusal prints and the exit code the command would return for it, over a stubbed Forum that answers exactly the document each row names.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("console")]
public sealed class WithheldRefusalWordsTests
{
    private const string Id = "01TESTPOSTID0000000000000A";

    private static async Task<Refusal> ReadRefusedAsync(string type, string title)
    {
        using var handler = new Stub(HttpStatusCode.NotFound, $$"""{"type":"{{type}}","title":"{{title}}","detail":"{{Id}}"}""");
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://forum.test") };
        var client = new ForumClient(http, new Uri("http://forum.test"));

        var result = await client.GetPostAsync(Id, MarkingMode.None, CancellationToken.None);

        Assert.False(result.TryGetValue(out _, out var refusal));
        return refusal!;
    }

    /// <summary>PH13. A <c>curia/posts/withheld</c> refusal is worded as withheld, names no author's word, and exits 5.</summary>
    [Fact]
    public async Task R11_54_AReaderWordsAWithheldRefusalAsWithheld()
    {
        var refusal = await ReadRefusedAsync(PublishedWithheldWords.PostWithheldType(), "This post is not being served");

        Assert.Equal(RefusalKind.NotFound, refusal.Kind);
        Assert.Contains("ithheld", refusal.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("No such post", refusal.Summary, StringComparison.Ordinal);
        Assert.Equal(5, ExitCode.For(refusal));
    }

    /// <summary>
    /// PH13 (revision row). <c>curia revision</c> reads and verifies the post it would chain to and reports a refused read with <c>Output.Fail(refusal)</c>
    /// (<c>RevisedDigestAsync</c>): a withheld post is worded withheld on stderr, exits 5, and says nothing was sent. The control is the not-found refusal,
    /// which prints "No such post" and no claim of withholding, through the same call.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task R11_54_ARevisionOfAWithheldPostIsWordedWithheldExitFive(bool withheld)
    {
        var refusal = withheld
            ? await ReadRefusedAsync(PublishedWithheldWords.PostWithheldType(), "This post is not being served")
            : await ReadRefusedAsync(PublishedWithheldWords.PostNotFoundType(), "No such post");

        var (exit, _, printed) = await ConsoleCapture.CaptureAsync(() => Task.FromResult(Output.Fail(refusal)));

        // The second line carries the problem type, which spells "withheld" whatever the words say; the words are the first line.
        var stderr = printed.Split('\n')[0];

        Assert.Equal(5, exit);
        if (withheld)
        {
            Assert.Contains("ithheld", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("No such post", stderr, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("No such post", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("ithheld", stderr, StringComparison.Ordinal);
        }
    }

    /// <summary>PH13. The control: an id no post bears is still "No such post", is not called withheld, and exits 5.</summary>
    [Fact]
    public async Task R11_54_ANotFoundRefusalIsStillNoSuchPost()
    {
        var refusal = await ReadRefusedAsync(PublishedWithheldWords.PostNotFoundType(), "No such post");

        Assert.Contains("No such post", refusal.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("ithheld", refusal.Summary, StringComparison.Ordinal);
        Assert.Equal(5, ExitCode.For(refusal));
    }

    /// <summary>PH13. A <c>404</c> with <c>about:blank</c> (a proxy's) says neither: the Forum did not say anything about the id.</summary>
    [Fact]
    public async Task R11_54_AProxys404SaysNeither()
    {
        var refusal = await ReadRefusedAsync("about:blank", "Not Found");

        Assert.DoesNotContain("ithheld", refusal.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("No such post", refusal.Summary, StringComparison.Ordinal);
    }

    private sealed class Stub(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
