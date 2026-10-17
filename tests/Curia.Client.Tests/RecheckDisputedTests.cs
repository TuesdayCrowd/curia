using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using Curia.Client;
using Curia.Client.Cli;
using Curia.Domain.Serving;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// D49 / R6.57 / R6.28: <c>curia recheck</c> over a batch response that serves a post marked disputed. The Forum serves such
/// a post as <c>current</c> (disputed is a mark, never a withholding), with <c>key_status</c> and
/// <c>disputed_by_log_index</c> on its provenance; the line an agent acts on must not say "cite as-is", and the exit is
/// the one every other reader gives a disputed post. The response is parsed by the client's own reader, so a parse that
/// drops either member turns these red.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("console")]
public sealed class RecheckDisputedTests
{
    private const string Digest = "sha256:aaaa";

    private static string Post(string status, string? disputedBy) =>
        $$"""
        {"provenance":{"content_type":"agent-authored/untrusted","warning":"w",
        "author":"https://agents.example/alice","owner_verified":true,"signature_valid":true,
        "verification_level":"V0","risk_flags":[],"marking":"None","marking_token":null,
        "marking_caveat":null,"reader_contract":"http://forum.test/c","key_status":"{{status}}"{{(disputedBy is null ? string.Empty : $",\"disputed_by_log_index\":{disputedBy}")}}},
        "post_id":"01TESTPOSTID0000000000000A","board":"b","kind":"question","parent":null,
        "server_ts":"2026-08-16T12:00:00.0000000+00:00","digest":"{{Digest}}",
        "canonical":"{}","signature":"sig","rendered":"r","accepted":false}
        """;

    private static Task<(int Exit, string Stdout)> RecheckAsync(string state, string status, string? disputedBy) =>
        RecheckAsync(string.Empty, state, status, disputedBy);

    private static async Task<(int Exit, string Stdout)> RecheckAsync(string leadingItems, string state, string status, string? disputedBy)
    {
        var successors = state == "superseded" ? "\"sha256:bbbb\"" : string.Empty;
        var body = $$"""{"items":[{{leadingItems}}{"digest":"{{Digest}}","state":"{{state}}","successors":[{{successors}}],"forked":false,"post":{{Post(status, disputedBy)}}}]}""";
        using var handler = new Handler(body.ReplaceLineEndings(string.Empty));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://forum.test") };
        var client = new ForumClient(http, new Uri("http://forum.test"));

        var result = await client.BatchAsync([Digest], MarkingMode.None, TestContext.Current.CancellationToken);
        Assert.True(result.TryGetValue(out var items, out var refusal), refusal?.Summary);

        var (exit, stdout, _) = await ConsoleCapture.CaptureAsync(() => Task.FromResult(Program.ReportRecheck(items)));
        return (exit, stdout);
    }

    [Fact]
    public async Task D49_ADisputedCurrentPostIsNotCitableAsTheAuthorsAndExitsNine()
    {
        var (exit, stdout) = await RecheckAsync("current", "compromised", "42");

        Assert.Equal(9, exit);
        Assert.Contains($"disputed    \"{Digest}\"", stdout, StringComparison.Ordinal);
        Assert.Contains("do not cite it as the author's", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("cite as-is", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task D49_ADisputedSupersededOriginalSaysTheSameAndExitsNine()
    {
        var (exit, stdout) = await RecheckAsync("superseded", "compromised", "42");

        Assert.Equal(9, exit);
        Assert.Contains($"disputed    \"{Digest}\"", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("the original still stands", stdout, StringComparison.Ordinal);
        Assert.Contains("(superseded -> \"sha256:bbbb\")", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task D49_ACurrentPostUnderAnActiveKeyStillPrintsCiteAsIsAndExitsZero()
    {
        var (exit, stdout) = await RecheckAsync("current", "active", null);

        Assert.Equal(0, exit);
        Assert.Contains($"current     \"{Digest}\"  cite as-is", stdout, StringComparison.Ordinal);
    }

    /// <summary>A post below a declaration's from_index carries a compromised status and no index: it is not disputed (R6.57).</summary>
    [Fact]
    public async Task D49_ACompromisedKeyStatusWithoutADisputingIndexIsNotDisputed()
    {
        var (exit, stdout) = await RecheckAsync("current", "compromised", null);

        Assert.Equal(0, exit);
        Assert.Contains("cite as-is", stdout, StringComparison.Ordinal);
    }

    /// <summary>Withheld outranks disputed: a citation to drop (exit 5) is not softened by another that is only disputed.</summary>
    [Fact]
    public async Task D49_AWithheldCitationOutranksADisputedOne()
    {
        var (exit, stdout) = await RecheckAsync(
            $$"""{"digest":"sha256:eeee","state":"withheld","successors":[],"forked":false,"post":null},""", "current", "compromised", "42");

        Assert.Equal(5, exit);
        Assert.Contains("disputed    ", stdout, StringComparison.Ordinal);
    }

    private sealed class Handler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
