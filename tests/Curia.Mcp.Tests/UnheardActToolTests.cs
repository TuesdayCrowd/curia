using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.RegularExpressions;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// AFC17 (the tools' half) and AFC23 (errata G55, R11.76, R11.77): <c>curia-mcp</c>'s write tools. The stub Forum (<see cref="StubLog"/>) is reached through a handler
/// that, for a submission, either throws a transport failure (a reset) or never answers under a 300 ms <c>HttpClient</c> timeout, and records the body it was sent so
/// the digest of the act is recomputed from what was received (<see cref="ReceivedAct"/>). Every sentence, title and type is read from the errata
/// (<see cref="PublishedConfirmWords"/>, <see cref="PublishedStoreFaults"/>), folded for whitespace. The tool's own host cancellation is not covered by these facts: a
/// cancelled token throws as before.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class UnheardActToolTests : IDisposable
{
    private readonly List<IDisposable> _owned = [];
    private readonly List<string> _homes = [];

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        foreach (var home in _homes) Directory.Delete(home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Fold(string text) => Whitespace().Replace(text, " ");

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    private static void Said(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    private static string Text(CallToolResult result) => string.Join('\n', result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    /// <summary>How the handler treats a submission.</summary>
    private enum Fate
    {
        Reset,
        NeverAnswered,
    }

    /// <summary>Forwards everything to the stub except a submission (or, when <c>everything</c> is set, any write after the token), which it records and then ends as <see cref="Fate"/> says.</summary>
    private sealed class EndingHandler(HttpClient inner, Fate fate, bool everyWrite) : HttpMessageHandler
    {
        internal List<string> Bodies { get; } = [];

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var isWrite = request.Method == HttpMethod.Post && path != "/oauth/token";
            var isSubmission = request.Method == HttpMethod.Post && path == "/v1/posts";
            if (isSubmission || (everyWrite && isWrite))
            {
                if (request.Content is not null) Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                if (fate == Fate.Reset) throw new HttpRequestException(HttpRequestError.Unknown, "reset by the stub", new IOException("Connection reset by peer"));
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }

            // The stub's client is its own HttpClient, which will not send a message another client has sent: forward a copy.
            using var copy = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers) copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (request.Content is not null)
            {
                var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                copy.Content = new ByteArrayContent(bytes);
                foreach (var header in request.Content.Headers) copy.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return await inner.SendAsync(copy, cancellationToken).ConfigureAwait(false);
        }
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The handler owns the stub's client and the HttpClient owns the handler; the HttpClient is kept in _owned and disposed with the fact.")]
    private (ForumTools Tools, EndingHandler Handler) ToolsEndingAs(StubLog log, Fate fate, bool everyWrite = false)
    {
        Assert.True(log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        var home = Directory.CreateTempSubdirectory("curia-mcp-afc-").FullName;
        _homes.Add(home);
        _owned.Add(agent!);
        var handler = new EndingHandler(log.RawClient(), fate, everyWrite);
        var http = new HttpClient(new ShortTimeoutHandler(handler)) { BaseAddress = StubLog.Forum, Timeout = TimeSpan.FromSeconds(30) };
        _owned.Add(http);
        var client = new ForumClient(http, StubLog.Forum);
        var session = new ForumSession(client, agent!, log.Store, TimeProvider.System);
        return (new ForumTools(client, MarkingMode.Datamark, new HeadStore(home), new ForumWriter(agent!, session, TimeProvider.System)), handler);
    }

    private static async Task<string> CallAsync(string tool, ForumTools tools)
    {
        try
        {
            var result = tool switch
            {
                "curia_ask" => await tools.AskAsync("b", "A title?", "A body.", null, null, Ct),
                "curia_answer" => await tools.AnswerAsync(StubLog.PostId, "An answer.", null, Ct),
                "curia_comment" => await tools.CommentAsync(StubLog.PostId, "A comment.", null, Ct),
                "curia_revise" => await tools.ReviseAsync(StubLog.PostId, "A corrected body.", null, null, null, Ct),
                "curia_resolve" => await tools.ResolveAsync(StubLog.PostId, Ct),
                "curia_flag" => await tools.FlagAsync(StubLog.PostId, "spam", "this reads as spam", Ct),
                _ => throw new InvalidOperationException($"this fact does not know how to call {tool}"),
            };
            return (result.IsError == true ? "error: " : "not an error: ") + Text(result);
        }
        catch (McpException refused)
        {
            return "error: " + refused.Message;
        }
        catch (OperationCanceledException)
        {
            return "OperationCanceledException (nothing relayed)";
        }
    }

    public static TheoryData<string, string> PostToolsByFate() => new()
    {
        { "curia_ask", "Reset" }, { "curia_answer", "Reset" }, { "curia_comment", "Reset" }, { "curia_revise", "Reset" },
        { "curia_ask", "NeverAnswered" }, { "curia_answer", "NeverAnswered" }, { "curia_comment", "NeverAnswered" }, { "curia_revise", "NeverAnswered" },
    };

    /// <summary>
    /// AFC23. The four signing tools relay a write that ended with no response (a reset, and a stub that never answers under a short timeout) as an error that carries the
    /// unheard title, then the unheard sentence, then the act line with the digest recomputed from the body the stub was sent.
    /// </summary>
    [Theory]
    [MemberData(nameof(PostToolsByFate))]
    public async Task R11_77_AFC23_EveryPostToolRelaysAnUnheardWriteWithTheUnheardWordsAndTheActLine(string tool, string fate)
    {
        using var log = new StubLog();
        var (tools, handler) = ToolsEndingAs(log, Enum.Parse<Fate>(fate));
        var relayed = Fold(await CallAsync(tool, tools));
        var received = handler.Bodies.LastOrDefault(ReceivedAct.IsSubmission);
        var digest = received is null ? null : ReceivedAct.DigestOf(received);

        var title = Fold(PublishedConfirmWords.UnheardRefusal().Title);
        var sentence = Fold(PublishedConfirmWords.UnheardSentence);
        var act = digest is null ? string.Empty : Fold(PublishedStoreFaults.ActLine(digest));
        var at = (relayed.IndexOf(title, StringComparison.Ordinal), relayed.IndexOf(sentence, StringComparison.Ordinal), act.Length == 0 ? -1 : relayed.IndexOf(act, StringComparison.Ordinal));

        var expected = $"{tool} ({fate}): an error that holds the unheard title, then the unheard sentence, then the act line with the digest received";
        var actual = received is null
            ? $"{tool} ({fate}): the stub received no submission"
            : at.Item1 >= 0 && at.Item2 > at.Item1 && at.Item3 > at.Item2 && relayed.StartsWith("error: ", StringComparison.Ordinal)
                ? expected
                : $"{tool} ({fate}): {(relayed.StartsWith("error: ", StringComparison.Ordinal) ? "an error" : "not an error")} that holds " +
                  $"{(at.Item1 >= 0 ? "the unheard title" : "no unheard title")}, {(at.Item2 >= 0 ? "the unheard sentence" : "no unheard sentence")}, {(at.Item3 >= 0 ? "the act line" : "no act line")}. Relayed: \"{relayed}\"";
        Said(expected, actual);
    }

    private async Task<string> RecheckedWithoutAnAnswerAsync()
    {
        using var log = new StubLog();
        var (tools, _) = ToolsEndingAs(log, Fate.NeverAnswered, everyWrite: true);
        try
        {
            return Fold(Text(await tools.RecheckAsync(["sha256:" + new string('a', 64)], Ct)));
        }
        catch (McpException refused)
        {
            return Fold(refused.Message);
        }
    }

    private async Task<string> RelayedWithoutAnAnswerAsync(string tool)
    {
        using var log = new StubLog();
        var (tools, _) = ToolsEndingAs(log, Fate.NeverAnswered, everyWrite: true);
        return Fold(await CallAsync(tool, tools));
    }

    /// <summary>
    /// AFC23, the controls. <c>curia_resolve</c> and <c>curia_flag</c> that never get an answer, and <c>curia_recheck</c> that never gets one, relay no unheard sentence and
    /// no act line: no signed post's digest is held for them.
    /// </summary>
    [Fact]
    public async Task R11_77_AFC23_AResolveAFlagAndARecheckThatAreNotAnsweredPrintNeitherTheSentenceNorAnActLine()
    {
        var problems = new List<string>();
        var sentence = Fold(PublishedConfirmWords.UnheardSentence);
        var lead = Fold(PublishedStoreFaults.ActSentence[..PublishedStoreFaults.ActSentence.IndexOf("<digest>", StringComparison.Ordinal)]);
        foreach (var tool in new[] { "curia_resolve", "curia_flag" })
        {
            var relayed = await RelayedWithoutAnAnswerAsync(tool);
            if (relayed.Contains(sentence, StringComparison.Ordinal) || relayed.Contains(lead, StringComparison.Ordinal)) problems.Add($"{tool} relays the unheard words or an act line");
        }

        var rechecked = await RecheckedWithoutAnAnswerAsync();
        if (rechecked.Contains(sentence, StringComparison.Ordinal) || rechecked.Contains(lead, StringComparison.Ordinal)) problems.Add("curia_recheck relays the unheard words or an act line");

        Said("curia_resolve, curia_flag and curia_recheck relay neither the unheard sentence nor an act line", problems.Count == 0 ? "curia_resolve, curia_flag and curia_recheck relay neither the unheard sentence nor an act line" : string.Join("; ", problems));
    }

    /// <summary>
    /// AFC17, the tools' half. The description of every write tool (the catalogue's own list) carries R11.76's sentence, and none carries G53's.
    /// </summary>
    [Fact]
    public void R11_76_AFC17_TheDescriptionOfEveryWriteToolCarriesR1176sSentence()
    {
        using var log = new StubLog();
        Assert.True(log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        _owned.Add(agent!);
        var home = Directory.CreateTempSubdirectory("curia-mcp-afc17-").FullName;
        _homes.Add(home);
        var session = new ForumSession(log.Client(), agent!, log.Store, TimeProvider.System);
        var tools = new ForumTools(log.Client(), MarkingMode.Datamark, new HeadStore(home), new ForumWriter(agent!, session, TimeProvider.System));

        var names = ToolCatalogue.WriteToolNames(tools);
        var descriptions = ToolCatalogue.Build(tools).ToDictionary(t => t.ProtocolTool.Name, t => Fold(t.ProtocolTool.Description ?? string.Empty), StringComparer.Ordinal);
        // Errata G58 (R11.80): the description of a tool carries the clause of its own act.
        string ClauseOf(string tool) => Fold(tool == "curia_resolve" ? PublishedResendWords.Clauses().Resolve : tool == "curia_flag" ? PublishedResendWords.Clauses().Flag : PublishedResendWords.Clauses().Post);
        var missing = names.Where(n => !descriptions.TryGetValue(n, out var d) || !d.Contains(ClauseOf(n), StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        var expected = $"R11.76's sentence is in the description of 7 write tools ({string.Join(' ', names.Order(StringComparer.Ordinal))})";
        var actual = names.Count == 7 && missing.Length == 0
            ? expected
            : $"{names.Count} write tools; R11.76's sentence is absent from the description of {missing.Length} ({string.Join(' ', missing)})";
        Said(expected, actual);
    }

    /// <summary>
    /// AFC26. <c>curia-mcp</c>'s version is the one the newest entry names: the code's version and the parser's agree (the parser read R11.78's until the resend PR, R11.81's until the client-checks
    /// PR, errata G59, which bumped to 0.10.1). R11.81's words state 0.10.0, the version they were built at, and are history: this fact holds that they still state it and that the catalogue and the
    /// parser (the one reader of a version, <c>PublishedReadersLastWords.Version()</c>) agree on the newest. The version is not parsed here.
    /// </summary>
    [Fact]
    public void R11_81_AFC26_CuriaMcpIsTheNewestVersionAndR1181StatesTheOneItWasBuiltAt()
    {
        var code = ToolCatalogue.Version;
        var parser = PublishedReadersLastWords.Version();
        var history = PublishedConfirmWords.Paragraph("R11.81 words").Contains("`curia-mcp` 0.10.0.", StringComparison.Ordinal);
        const string Expected = "R11.81 words state 0.10.0 (history), and the catalogue reports the version the parser reads";
        Said(
            Expected,
            history && string.Equals(code, parser, StringComparison.Ordinal)
                ? Expected
                : $"the catalogue reports {code}; the parser reads {parser}; R11.81 words {(history ? "state" : "do not state")} 0.10.0");
    }
}
