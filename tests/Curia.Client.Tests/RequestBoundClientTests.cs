using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using Curia.Client;
using Curia.Domain.Primitives;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// Errata G84 (R11.111, R11.79 (rev.)): a client that meets the Forum's <c>413</c> says it was refused and keeps nothing. The type, the sentence and the table row are read from the errata
/// (<see cref="PublishedRequestBound"/>), never from the client's own constants. CLW1 is the classification and its twins, CLW2 the kept-act table, CLW3 the words.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class RequestBoundClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Answering(HttpStatusCode status, string body, string contentType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, contentType) });
    }

    private static async Task<Refusal> RefusalOfAsync(int status, string body, string contentType = "application/json")
    {
        using var handler = new Answering((HttpStatusCode)status, body, contentType);
        using var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://forum.invalid") };
        var client = new ForumClient(http, new Uri("http://forum.invalid"));
        var result = await client.GetLogHeadAsync(Ct);
        Assert.False(result.IsOk, "the stand-in answered a refusal and the client read a value");
        return result.Refusal!;
    }

    private static string Problem(string type) => $$"""{"type":"{{type}}","title":"A title","detail":null}""";

    /// <summary>
    /// CLW1. A 413 whose problem document is the Forum's <c>curia/request/too-large</c> is a refusal of the content; the twins stay <c>Malformed</c>, so the arm decides on the type and is not "every 413 is content":
    /// a 413 with no body, with an HTML body (an edge's own page), with another Forum type, with <c>about:blank</c>, and in RFC 6749's shape (which the Forum answers at <c>/oauth/token</c> and an honest client cannot reach).
    /// </summary>
    [Fact]
    public async Task R11_111_CLW1_ATypedTooLargeIsContentAndItsTwinsAreNot()
    {
        var type = PublishedRequestBound.TooLargeType();
        var rows = new (string Name, int Status, string Body, string Media, RefusalKind Expected)[]
        {
            ("the Forum's typed 413", 413, Problem(type), "application/json", RefusalKind.Content),
            ("a 413 with no body", 413, string.Empty, "application/json", RefusalKind.Malformed),
            ("a 413 with an HTML body", 413, "<html><body>413 Request Entity Too Large</body></html>", "text/html", RefusalKind.Malformed),
            ("a 413 with another Forum type", 413, Problem("curia/request/unreadable"), "application/json", RefusalKind.Malformed),
            ("a 413 typed about:blank", 413, Problem("about:blank"), "application/json", RefusalKind.Malformed),
            ("a 413 in RFC 6749's shape", 413, """{"error":"invalid_request","error_description":"The request is larger than this route reads"}""", "application/json", RefusalKind.Malformed),
            ("the typed document at another status (control)", 400, Problem(type), "application/json", RefusalKind.Content),
        };

        var problems = new List<string>();
        foreach (var row in rows)
        {
            var refusal = await RefusalOfAsync(row.Status, row.Body, row.Media);
            if (refusal.Kind != row.Expected) problems.Add($"{row.Name}: expected {row.Expected}, was {refusal.Kind}");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// CLW2. R11.79 (rev.)'s sentence is in the errata (read, so that a table row cannot be added without the sentence), and <see cref="KeptActs.Decide"/> agrees with it: a first send answered the typed 413 removes the act; the same
    /// with another type, or with no readable body, keeps it; a resend answered the typed 413 keeps it; the six statuses the table already named are unchanged; <c>408</c> and an untyped <c>413</c> stay kept.
    /// </summary>
    [Fact]
    public void R11_79_CLW2_ATypedTooLargeRemovesAFirstSentActAndNothingElseMoves()
    {
        var type = PublishedRequestBound.TooLargeType();
        var sentence = PublishedAcceptBudget.Paragraph("**R11.79 (rev.)** In R11.79's table");
        Assert.Contains("413", sentence, StringComparison.Ordinal);
        Assert.Contains(type, sentence, StringComparison.Ordinal);

        static ActAnswer Answered(int status, string? type, bool readable = true) => new(ActTransport.Answered, status, type, readable);
        var rows = new (string Name, ActSend Send, ActAnswer Answer, ActFate Expected)[]
        {
            ("first send, typed 413", ActSend.First, Answered(413, type), ActFate.Remove),
            ("first send, 413 of another type", ActSend.First, Answered(413, "curia/request/unreadable"), ActFate.Keep),
            ("first send, 413 with no type", ActSend.First, Answered(413, null), ActFate.Keep),
            ("first send, typed 413 with an unreadable body", ActSend.First, Answered(413, type, readable: false), ActFate.Keep),
            ("resend, typed 413", ActSend.Resend, Answered(413, type), ActFate.Keep),
            ("first send, 408", ActSend.First, Answered(408, "curia/request/refused"), ActFate.Keep),
            ("first send, 400", ActSend.First, Answered(400, "curia/admit/size-exceeded"), ActFate.Remove),
            ("first send, 401", ActSend.First, Answered(401, "curia/authn/x"), ActFate.Remove),
            ("first send, 403", ActSend.First, Answered(403, "curia/authz/denied"), ActFate.Remove),
            ("first send, 404", ActSend.First, Answered(404, "curia/posts/not-found"), ActFate.Remove),
            ("first send, 409", ActSend.First, Answered(409, "curia/ingest/already-revised"), ActFate.Remove),
            ("first send, 422", ActSend.First, Answered(422, "curia/ingest/x"), ActFate.Remove),
            ("resend, 400", ActSend.Resend, Answered(400, "curia/admit/size-exceeded"), ActFate.Keep),
        };

        var problems = rows.Where(r => KeptActs.Decide(r.Send, r.Answer) != r.Expected)
            .Select(r => $"{r.Name}: expected {r.Expected}, was {KeptActs.Decide(r.Send, r.Answer)}");
        Assert.True(!problems.Any(), string.Join(Environment.NewLine, problems));
    }

    private static Refusal TypedRefusal(RefusalKind kind = RefusalKind.Content) =>
        new(kind, 413, new Error(PublishedRequestBound.TooLargeType(), "The request is larger than this route reads")) { Act = ActContext.Post, Agent = "quill" };

    private static int Count(string text, string phrase)
    {
        var count = 0;
        for (var at = text.IndexOf(phrase, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(phrase, at + phrase.Length, StringComparison.OrdinalIgnoreCase)) count++;
        return count;
    }

    /// <summary>
    /// CLW3. The line for the typed 413 is the Forum's title and type and then R11.111's sentence, read from the errata, once; "nothing was recorded" is said once (R11.81 (c)'s count); no <c>resend</c> and no digest, and
    /// nothing follows the refusal (the session keeps no act, so <see cref="StoreWording.AfterRefusal"/> has none). The control, a <c>Malformed</c> refusal of a first-sent act that the session did keep, prints the recipe
    /// through the same two calls, so the harness is able to see one.
    /// </summary>
    [Fact]
    public void R11_111_CLW3_TheLineIsTheErratasSentenceOnceAndOffersNoResend()
    {
        var sentence = PublishedRequestBound.Sentence();
        var line = TypedRefusal().Summary;
        var after = StoreWording.AfterRefusal(TypedRefusal(), "sha256:" + new string('a', 64));
        var control = new Refusal(RefusalKind.Malformed, 413, new Error("curia/x/y", "A refusal"), null, false) { Act = ActContext.Post, Agent = "quill", KeptAttempts = 1 };
        var controlText = control.Summary + " " + string.Join(' ', StoreWording.AfterRefusal(control, "sha256:" + new string('a', 64)));

        var problems = new List<string>();
        if (Count(line, sentence) != 1) problems.Add($"R11.111's sentence is on the line {Count(line, sentence)} times: {line}");
        if (!line.Contains("The request is larger than this route reads", StringComparison.Ordinal)) problems.Add("the Forum's title is not on the line");
        if (!line.Contains(PublishedRequestBound.TooLargeType(), StringComparison.Ordinal)) problems.Add("the Forum's type is not on the line");
        else if (line.Contains(sentence, StringComparison.Ordinal) && line.IndexOf(PublishedRequestBound.TooLargeType(), StringComparison.Ordinal) > line.IndexOf(sentence, StringComparison.Ordinal)) problems.Add("the sentence does not follow the title and type");
        if (Count(line, "nothing was recorded") + Count(line, "nothing was stored") != 1) problems.Add($"\"nothing was recorded\" is said {Count(line, "nothing was recorded") + Count(line, "nothing was stored")} times");
        if (line.Contains("resend", StringComparison.OrdinalIgnoreCase) || line.Contains("sha256:", StringComparison.Ordinal)) problems.Add("the line offers a resend or names a digest");
        if (after.Count != 0) problems.Add($"{after.Count} line(s) follow the refusal: {string.Join(" | ", after)}");
        if (!controlText.Contains("curia resend sha256:", StringComparison.Ordinal)) problems.Add("the control prints no recipe, so this harness cannot see one");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
