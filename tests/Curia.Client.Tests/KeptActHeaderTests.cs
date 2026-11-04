#pragma warning disable CA1416 // the kept-act files are mode 0600 and 0700 (R11.79): the probes read and set Unix modes, as PrivateFiles does

using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// CWK40, CWK41 and CWK42 (errata G59, R11.79 (rev.), R11.80 (rev.); D61 items 121 and 123): what a resend compares in the kept act's header and what the listing and the stop sentence say of
/// the sends. Run at the real command on an <see cref="AgentHost"/>. Each probe is in its failing form: on the seam a resend compares the author and the Forum only, and the listing and the
/// stop sentence say <c>sent</c> of a send that was only asked.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Each host is a using declaration; the analyzer loses the ownership across the awaits.")]
[Collection("console")]
public sealed class KeptActHeaderTests
{
    private static readonly string[] Comment = ["comment", "--board", "dotnet", "--parent", "01PARENT00000000000000000A", "--body", "A body.", "--agent", "a-one"];

    private static HttpResponseMessage Refused() => throw new HttpRequestException(HttpRequestError.ConnectionError, "connection refused");

    private static HttpResponseMessage Unheard() => throw new HttpRequestException(HttpRequestError.Unknown, "connection reset");

    // ----- CWK40 -----

    /// <summary>
    /// CWK40. <c>resend</c> compares kind and board. A header that says <c>answer</c> and <c>dotnet</c> over a comment on <c>meta</c>: <c>NOT SENT</c>, the field named (not its value), 0 requests, the
    /// file untouched; each field separately; the intact file sends. Predicted red: <c>request sent</c>.
    /// </summary>
    [Fact]
    public async Task R11_79_CWK40_AResendRefusesAHeaderThatContradictsTheEnvelope()
    {
        var problems = new List<string>();
        var cases = new (string Name, string HeaderKind, string HeaderBoard, string Field)[]
        {
            ("kind only", "answer", "meta", "kind"),
            ("board only", "comment", "dotnet", "board"),
            ("both", "answer", "dotnet", "kind"),
        };
        foreach (var (name, headerKind, headerBoard, field) in cases)
        {
            using var host = new AgentHost("a-one");
            var (digest, path) = host.PlantAct("a-one", attempts: 1, lastAnswer: "503", headerKind: headerKind, headerBoard: headerBoard, draftBoard: "meta");
            var before = host.FingerprintOf("a-one");
            var run = await host.RunAsync("resend", digest, "--agent", "a-one");
            if (run.Requests.Count != 0) problems.Add($"{name}: request sent ({string.Join(", ", run.Requests.Select(r => r.Method + " " + r.Path))})");
            if (!run.Text.Contains("NOT SENT", StringComparison.Ordinal)) problems.Add($"{name}: not NOT SENT (exit {run.Exit}: \"{Shorten(run.Text)}\")");
            if (!Regex.IsMatch(run.Text, @"\b" + field + @"\b", RegexOptions.CultureInvariant) || run.Text.Contains("cannot be trusted", StringComparison.Ordinal) == false)
                problems.Add($"{name}: the refusal does not say the kept act cannot be trusted and name its {field}");
            if (host.FingerprintOf("a-one") != before) problems.Add($"{name}: the kept act's file was changed");
            if (!File.Exists(path)) problems.Add($"{name}: the file is gone");
        }

        // The positive in the same fact: a header that agrees with its envelope sends.
        using (var intact = new AgentHost("a-one"))
        {
            var (digest, _) = intact.PlantAct("a-one", attempts: 1, lastAnswer: "503", draftBoard: "meta", headerBoard: "meta");
            var run = await intact.RunAsync("resend", digest, "--agent", "a-one");
            if (run.Requests.Count(r => r.Path == "/v1/posts") != 1) problems.Add($"intact: {run.Requests.Count(r => r.Path == "/v1/posts")} post(s) sent, expected 1");
        }

        Red.None(problems, "NOT SENT naming the field, 0 requests and the file untouched for each contradicted field; the intact file sends");
    }

    private static string Shorten(string text) => text.Length <= 120 ? text.Trim() : text[..120].Trim() + "...";

    // ----- CWK41 -----

    /// <summary>
    /// CWK41. <c>asked</c>. After three sends that provably never left, the listing reads <c>asked 3 time(s)</c> beside <c>last answered "not sent"</c>; in the mixed shape (a first send answered
    /// <c>503</c>, three refused connections, one unheard) the listing says <c>asked 5 time(s)</c> and the stop sentence <c>it has been asked to send 5 times in all</c> beside <c>the last 2 sends</c>
    /// unchanged. Predicted red: <c>sent 3 time(s)</c>; mixed: <c>it has been sent 5 times in all</c> with two sends on the wire.
    /// </summary>
    [Fact]
    public async Task R11_79_CWK41_TheListingAndTheStopSentenceSayAsked()
    {
        var problems = new List<string>();

        using (var host = new AgentHost("a-one"))
        {
            var (digest, _) = host.PlantAct("a-one", attempts: 0, lastAnswer: null);
            host.OnPost = (_, _) => Refused();
            for (var i = 0; i < 3; i++) await host.RunAsync("resend", digest, "--agent", "a-one");
            var listing = await host.RunAsync("acts", "--agent", "a-one");
            if (!listing.Text.Contains("asked 3 time(s)", StringComparison.Ordinal)) problems.Add($"three refused sends: the listing says \"{Regex.Match(listing.Text, @"(sent|asked) \d+ time\(s\)").Value}\", expected \"asked 3 time(s)\"");
            if (!listing.Text.Contains("last answered \"not sent\"", StringComparison.Ordinal)) problems.Add("three refused sends: the listing lacks `last answered \"not sent\"`");
        }

        using (var host = new AgentHost("a-one"))
        {
            host.OnPost = (_, _) => ScriptedForum.Problem(503, "curia/store/unavailable", "A store fault");
            var first = await host.RunAsync(Comment);
            var digest = Regex.Match(first.Text, @"digest (sha256:[0-9a-f]{64})").Groups[1].Value;
            if (digest.Length == 0) { problems.Add("the first send printed no digest, so the mixed shape did not start"); }
            else
            {
                host.OnPost = (_, _) => Refused();
                for (var i = 0; i < 3; i++) await host.RunAsync("resend", digest, "--agent", "a-one");
                host.OnPost = (_, _) => Unheard();
                var stop = await host.RunAsync("resend", digest, "--agent", "a-one");
                var listing = await host.RunAsync("acts", "--agent", "a-one");
                if (!listing.Text.Contains("asked 5 time(s)", StringComparison.Ordinal)) problems.Add($"mixed: the listing says \"{Regex.Match(listing.Text, @"(sent|asked) \d+ time\(s\)").Value}\", expected \"asked 5 time(s)\"");
                if (!stop.Text.Contains("it has been asked to send 5 times in all", StringComparison.Ordinal)) problems.Add($"mixed: the stop sentence says \"{Regex.Match(stop.Text, @"it has been [a-z ]*\d+ times in all").Value}\", expected \"it has been asked to send 5 times in all\"");
                if (!stop.Text.Contains("the last 2 sends", StringComparison.Ordinal)) problems.Add("mixed: the stop sentence's count of unanswered sends is not \"the last 2 sends\"");
            }
        }

        Red.None(problems, "asked 3 time(s) with last answered \"not sent\"; mixed: asked 5 time(s) and \"asked to send 5 times in all\" beside \"the last 2 sends\"");
    }

    // ----- CWK42 -----

    /// <summary>
    /// CWK42. The listing still lists a forged header (R11.79 allows it: the header is advice and a listing verifies nothing) and the resend sends exactly the signed bytes where it sends at all.
    /// A control on the seam.
    /// </summary>
    [Fact]
    public async Task R11_79_CWK42_TheListingStillListsAForgedHeaderAndAResendSendsOnlyTheSignedBytes()
    {
        var problems = new List<string>();
        using var host = new AgentHost("a-one");
        var (forged, _) = host.PlantAct("a-one", attempts: 1, lastAnswer: "503", headerKind: "answer", headerBoard: "dotnet", draftBoard: "meta");
        var listing = await host.RunAsync("acts", "--agent", "a-one");
        if (!listing.Text.Contains(forged, StringComparison.Ordinal)) problems.Add("the listing does not list the forged header's act");
        if (!Regex.IsMatch(listing.Text, @"answer\s+board ""dotnet""", RegexOptions.CultureInvariant)) problems.Add("the listing does not show the header's own kind and board");
        if (listing.Requests.Count != 0) problems.Add("a listing made a request");

        var intact = new AgentHost("a-one");
        using (intact)
        {
            var sent = new List<string>();
            intact.OnPost = (_, body) => { sent.Add(body!); return ScriptedForum.Problem(503, "curia/store/unavailable", "A store fault"); };
            var (digest, _) = intact.PlantAct("a-one", attempts: 0, lastAnswer: null);
            await intact.RunAsync("resend", digest, "--agent", "a-one");
            if (sent.Count != 1 || ReceivedAct.DigestOf(sent[0]) != digest) problems.Add("the resend did not send the signed bytes of the kept act");
        }

        Red.None(problems, "the listing lists a forged header and shows its own words; a resend sends the kept signed bytes and nothing else");
    }
}
