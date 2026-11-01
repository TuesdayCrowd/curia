using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.RegularExpressions;
using Curia.Client.Cli;
using Curia.Domain.Content;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// AFC20 to AFC25, AFC31 and AFC32 (errata G55, R11.77, D106): a signed post whose request ends with no response read, at the real command. <c>Program.Main</c> is run
/// in process against a Forum on a loopback socket (<see cref="RawStubForum"/>) that reads a submission and then says nothing, closes (FIN) or resets (RST), with the
/// client's <c>HttpClient</c> given a 300 ms timeout through <c>Program.HttpClientFactory</c> (the shipped client has the 30 seconds and no flag or variable for another).
/// Every sentence, title and type is read from the errata (<see cref="PublishedConfirmWords"/>, <see cref="PublishedStoreFaults"/>), folded for whitespace; the digest the
/// act line must carry is recomputed from the bytes the stub received (<see cref="ReceivedAct"/>), never from what the client computed. A probe is written in its failing
/// form and is red on the seam by assertion; the text each prints is recorded next to its prediction in the Task 0c report.
/// </summary>
[Collection("console")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class UnheardActTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Fold(string text) => Whitespace().Replace(text, " ");

    /// <summary>Asserts two descriptions are equal and says both in full when they are not: a red text is what these probes leave behind.</summary>
    private static void Said(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    private static void Said(IEnumerable<string> expected, IEnumerable<string> actual) => Said(string.Join(" | ", expected), string.Join(" | ", actual));

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"sha256:[0-9a-f]{64}", RegexOptions.CultureInvariant)]
    private static partial Regex Digest();

    private static readonly string[] AskArgs = ["ask", "--agent", "alice", "--board", "b", "--title", "A title?", "--body", "A body."];

    private static Func<Uri, HttpClient> ShortTimeout => forum => new HttpClient { BaseAddress = forum, Timeout = TimeSpan.FromMilliseconds(300) };

    /// <summary>What a run of the command printed.</summary>
    private sealed record Printed(int Exit, string Text);

    /// <summary>
    /// Runs <c>Program.Main</c> with <paramref name="args"/> and <c>--forum</c> at <paramref name="forum"/>, in a profile store of its own, the client built by
    /// <paramref name="factory"/>; the factory and the store's variable are restored afterwards.
    /// </summary>
    private static async Task<Printed> RunAsync(Uri forum, Func<Uri, HttpClient> factory, params string[] args)
    {
        using var home = new CliHome(forum);
        var savedHome = Environment.GetEnvironmentVariable("CURIA_CLIENT_HOME");
        var savedFactory = Program.HttpClientFactory;
        try
        {
            Environment.SetEnvironmentVariable("CURIA_CLIENT_HOME", home.Path);
            Program.HttpClientFactory = factory;
            var run = await ConsoleCapture.CaptureAsync(() => Program.RunAsync([.. args, "--forum", forum.ToString()]));
            return new Printed(run.Exit, Fold(run.Stdout + " " + run.Stderr));
        }
        finally
        {
            Program.HttpClientFactory = savedFactory;
            Environment.SetEnvironmentVariable("CURIA_CLIENT_HOME", savedHome);
        }
    }

    /// <summary>What a text holds of the unheard act's words, in the order they must come.</summary>
    private sealed record Facts(bool UnheardTitle, bool NotReachedTitle, bool Sentence, int ActLines, string? ActDigest, bool TimedOut, int OrderViolations)
    {
        internal string Says => string.Join(", ",
        [
            UnheardTitle ? "titled as unheard" : NotReachedTitle ? "titled as not reached" : "no title of either",
            Sentence ? "the unheard sentence" : "no unheard sentence",
            ActLines == 0 ? "no act line" : $"{ActLines} act line{(ActLines == 1 ? string.Empty : "s")} ({ActDigest ?? "no digest"})",
            TimedOut ? "\"error: timed out.\"" : "no \"error: timed out.\"",
        ]);
    }

    private static Facts Read(string text)
    {
        var unheardTitle = Fold(PublishedConfirmWords.UnheardRefusal().Title);
        var notReached = Fold(PublishedConfirmWords.NotSentTitle);
        var sentence = Fold(PublishedConfirmWords.UnheardSentence);
        var lead = Fold(PublishedStoreFaults.ActSentence[..PublishedStoreFaults.ActSentence.IndexOf("<digest>", StringComparison.Ordinal)]);
        var acts = Regex.Matches(text, Regex.Escape(lead) + @"\s*(sha256:[0-9a-f]{64})", RegexOptions.CultureInvariant);

        var title = text.IndexOf(unheardTitle, StringComparison.Ordinal);
        var at = text.IndexOf(sentence, StringComparison.Ordinal);
        var act = text.IndexOf(lead, StringComparison.Ordinal);
        var violations = 0;
        if (title >= 0 && at >= 0 && at < title) violations++;
        if (at >= 0 && act >= 0 && act < at) violations++;

        return new Facts(
            title >= 0, text.Contains(notReached, StringComparison.Ordinal), at >= 0, acts.Count, acts.Count > 0 ? acts[^1].Groups[1].Value : null,
            text.Contains("error: timed out.", StringComparison.Ordinal), violations);
    }

    private static string SaidOf(int exit, Facts facts) => $"exit {exit}, {facts.Says}";

    /// <summary>
    /// AFC20. A write that timed out, through the signed path. The stub reads the submission and never answers; the client's timeout is 300 ms. <c>curia ask</c> exits 8
    /// and prints a refusal titled as one that read no answer, then the unheard sentence, then the act line, and not <c>error: timed out.</c>; the digest in the act line is
    /// the digest of the canonical bytes the stub received, recomputed here and not read from the client.
    /// </summary>
    [Fact]
    public async Task R11_77_AFC20_ASignedPostThatTimedOutPrintsTheUnheardWordsAndTheDigestOfTheActSent()
    {
        using var stub = new RawStubForum(UnheardBehaviour.Stall);
        var run = await RunAsync(stub.Address, ShortTimeout, AskArgs);
        var facts = Read(run.Text);
        var received = stub.Submissions.LastOrDefault();
        var digest = received is null ? null : ReceivedAct.DigestOf(received);

        var expected = $"exit {ExitCode.ForumFault}, titled as unheard, the unheard sentence, 1 act line ({digest ?? "a digest of the act the stub received"}), no \"error: timed out.\", in that order";
        var actual = received is null
            ? "the stub received no submission"
            : $"{SaidOf(run.Exit, facts)}{(facts.OrderViolations == 0 ? string.Empty : ", out of order")}";
        if (received is not null && run.Exit == ExitCode.ForumFault && facts.UnheardTitle && facts.Sentence && facts.ActLines == 1 && facts.ActDigest == digest && !facts.TimedOut && facts.OrderViolations == 0)
            actual = expected;
        Said(expected, actual);
    }

    private sealed record Row(string Name, bool Unheard);

    /// <summary>The live rows and the constructed rows of AFC21, each with the verdict the entry gives it.</summary>
    private static Row[] Rows() =>
    [
        new("a FIN after the request was read (ResponseEnded)", true),
        new("an RST after the request was read (no name)", true),
        new("a refused connection (ConnectionError)", false),
        new("an unresolvable name (NameResolutionError)", false),
        new("a constructed SecureConnectionError", false),
        new("a constructed ProxyTunnelError", false),
        new("a constructed InvalidResponse", true),
        new("a constructed HttpProtocolError", true),
        new("a constructed exception that names no error", true),
    ];

    /// <summary>A handler that answers the token endpoint like a Forum and throws <paramref name="thrown"/> for anything else: the transport failure a real connection would raise.</summary>
    private sealed class ThrowingHandler(Func<HttpRequestException> thrown) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/oauth/token")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"access_token":"stub-access-token","token_type":"DPoP","expires_in":300,"scope":"question:create"}""", System.Text.Encoding.UTF8, "application/json"),
                });
            }

            throw thrown();
        }
    }

    private static Func<Uri, HttpClient> Throwing(Func<HttpRequestException> thrown) =>
        forum => new HttpClient(new ThrowingHandler(thrown)) { BaseAddress = forum, Timeout = TimeSpan.FromSeconds(30) };

    private static async Task<Printed> RunRowAsync(string name)
    {
        switch (name)
        {
            case "a FIN after the request was read (ResponseEnded)":
            {
                using var stub = new RawStubForum(UnheardBehaviour.Fin);
                return await RunAsync(stub.Address, ShortTimeout, AskArgs);
            }

            case "an RST after the request was read (no name)":
            {
                using var stub = new RawStubForum(UnheardBehaviour.Reset);
                return await RunAsync(stub.Address, ShortTimeout, AskArgs);
            }

            case "a refused connection (ConnectionError)":
                return await RunAsync(UnusedPort(), ShortTimeout, AskArgs);
            case "an unresolvable name (NameResolutionError)":
                return await RunAsync(new Uri("http://forum.invalid"), ShortTimeout, AskArgs);
            case "a constructed SecureConnectionError":
                return await RunAsync(new Uri("http://forum.test"), Throwing(() => new HttpRequestException(HttpRequestError.SecureConnectionError, "tls")), AskArgs);
            case "a constructed ProxyTunnelError":
                return await RunAsync(new Uri("http://forum.test"), Throwing(() => new HttpRequestException(HttpRequestError.ProxyTunnelError, "proxy")), AskArgs);
            case "a constructed InvalidResponse":
                return await RunAsync(new Uri("http://forum.test"), Throwing(() => new HttpRequestException(HttpRequestError.InvalidResponse, "garbled")), AskArgs);
            case "a constructed HttpProtocolError":
                return await RunAsync(new Uri("http://forum.test"), Throwing(() => new HttpRequestException(HttpRequestError.HttpProtocolError, "protocol")), AskArgs);
            default:
                return await RunAsync(new Uri("http://forum.test"), Throwing(() => new HttpRequestException("plain")), AskArgs);
        }
    }

    private static Uri UnusedPort()
    {
        using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return new Uri($"http://127.0.0.1:{port}");
    }

    /// <summary>
    /// AFC21. What is unheard, case by case, in one fact. A FIN and an RST after the request was read, a constructed <c>InvalidResponse</c>, <c>HttpProtocolError</c> and an
    /// exception that names no error print the unheard title, the sentence and the act line; a refused connection, an unresolvable name and a constructed
    /// <c>SecureConnectionError</c> and <c>ProxyTunnelError</c> print the title of a request that was not sent and neither line.
    /// </summary>
    [Fact]
    public async Task R11_77_AFC21_OnlyARequestThatProvablyWasNotSentIsPrintedAsNotReached()
    {
        var expected = new List<string>();
        var actual = new List<string>();
        foreach (var row in Rows())
        {
            var facts = Read((await RunRowAsync(row.Name)).Text);
            expected.Add($"{row.Name}: " + (row.Unheard ? "titled as unheard, the unheard sentence, 1 act line" : "titled as not reached, no unheard sentence, no act line"));
            var held = facts.UnheardTitle ? "titled as unheard" : facts.NotReachedTitle ? "titled as not reached" : "no title of either";
            var ok = row.Unheard
                ? facts.UnheardTitle && facts.Sentence && facts.ActLines == 1
                : facts.NotReachedTitle && !facts.Sentence && facts.ActLines == 0;
            actual.Add($"{row.Name}: " + (ok
                ? (row.Unheard ? "titled as unheard, the unheard sentence, 1 act line" : "titled as not reached, no unheard sentence, no act line")
                : $"{held}, {(facts.Sentence ? "the unheard sentence" : "no unheard sentence")}, {facts.ActLines} act line(s)"));
        }

        Said(expected, actual);
    }

    /// <summary>
    /// AFC22, controls. Nothing else prints the unheard words: a read that times out, a <c>resolve</c> and a <c>flag</c> that time out (no signed post's digest is held,
    /// so nothing prints), a verb that signs no post, and an <c>ask</c> whose token step times out (the post was provably not sent) print neither the sentence nor an act line.
    /// </summary>
    [Fact]
    public async Task R11_77_AFC22_NothingButASignedPostThatWasSentAndNotHeardPrintsTheUnheardWords()
    {
        var problems = new List<string>();

        async Task CheckAsync(string name, RawStubForum stub, params string[] args)
        {
            var run = await RunAsync(stub.Address, ShortTimeout, args);
            var facts = Read(run.Text);
            if (facts.Sentence || facts.ActLines > 0 || facts.UnheardTitle) problems.Add($"{name}: {facts.Says}");
        }

        using (var reads = new RawStubForum(UnheardBehaviour.Stall) { StallReads = true })
        {
            await CheckAsync("a read that timed out", reads, "budget", "--agent", "alice");
            await CheckAsync("a resolve that timed out", reads, "resolve", "01TESTPOSTID0000000000000A", "--agent", "alice");
            await CheckAsync("a flag that timed out", reads, "flag", "01TESTPOSTID0000000000000A", "--agent", "alice", "--kind", "spam", "--rationale", "A reason.");
        }

        using (var tokens = new RawStubForum(UnheardBehaviour.Stall) { StallToken = true })
            await CheckAsync("an ask whose token step timed out", tokens, AskArgs);

        using (var plain = new RawStubForum(UnheardBehaviour.Stall))
            await CheckAsync("a verb that signs no post", plain, "whoami", "--agent", "alice");

        Said("no read, resolve, flag, token step or verb that signs no post printed the unheard title, sentence or an act line", problems.Count == 0
            ? "no read, resolve, flag, token step or verb that signs no post printed the unheard title, sentence or an act line"
            : string.Join("; ", problems));
    }

    /// <summary>
    /// AFC24. No retry, no wait, one definition. The stub sees exactly one submission for a timed-out ask (control); the transport's error values are named in
    /// <c>UnheardTransport.cs</c> and in no other source file; <c>UnheardTransport</c> is called from <c>ForumSession.cs</c> alone; and neither printer compares a
    /// problem type to the store type.
    /// </summary>
    [Fact]
    public async Task R11_77_AFC24_OneDefinitionDecidesWhatIsUnheardAndNeitherPrinterDecidesIt()
    {
        using var stub = new RawStubForum(UnheardBehaviour.Stall);
        await RunAsync(stub.Address, ShortTimeout, AskArgs);
        var posts = stub.Requests.Count(r => r == "POST /v1/posts");

        var src = Path.Combine(RepoRoot(), "src");
        var files = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();
        string[] Naming(string text) => [.. files.Where(f => File.ReadAllText(f).Contains(text, StringComparison.Ordinal)).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];

        var errorNamers = Naming("HttpRequestError");
        var callers = Naming("UnheardTransport.").Where(f => f != "UnheardTransport.cs").ToArray();
        var comparers = PrinterFiles
            .Where(f => TypeComparison().IsMatch(File.ReadAllText(Path.Combine(RepoRoot(), f)))).ToArray();

        Said(
            "one submission reached the stub; HttpRequestError is named in UnheardTransport.cs alone; UnheardTransport is called from ForumSession.cs alone; no printer compares a problem type to the store type",
            $"{(posts == 1 ? "one submission reached the stub" : posts + " submissions reached the stub")}; HttpRequestError is named in {(errorNamers.Length == 0 ? "no file" : string.Join(", ", errorNamers))}; " +
            $"UnheardTransport is called from {(callers.Length == 0 ? "no file" : string.Join(", ", callers))}; {(comparers.Length == 0 ? "no printer compares a problem type to the store type" : string.Join(" and ", comparers.Select(Path.GetFileName)) + " compare a problem type to the store type")}");
    }

    private static readonly string[] PrinterFiles = ["src/Curia.Client.Cli/Program.cs", "src/Curia.Mcp/WriteTools.cs"];

    [GeneratedRegex(@"Error\.Type,\s*StoreWording\.StoreUnavailableType", RegexOptions.CultureInvariant)]
    private static partial Regex TypeComparison();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curia.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Curia.sln not found");
    }

    /// <summary>
    /// AFC25, a wording probe. The unheard sentence and the act line do not each claim the other's work (trap 95): the sentence carries the wait, once, and it is the
    /// figure the store sentence carries; the act line carries no wait and no instruction to send, and says <c>recheck</c> answers as of the moment it is asked.
    /// </summary>
    [Fact]
    public void R11_77_AFC25_TheUnheardSentenceCarriesTheWaitAndTheActLineDoesNot()
    {
        var sentence = PublishedConfirmWords.UnheardSentence;
        var act = PublishedStoreFaults.ActSentence;
        var wait = PublishedClientWords.Wait;
        var problems = new List<string>();
        if (Regex.Count(sentence, Regex.Escape(wait)) != 1) problems.Add($"the unheard sentence does not carry \"{wait}\" once");
        if (act.Contains("seconds", StringComparison.Ordinal) || act.Contains(wait, StringComparison.Ordinal)) problems.Add("the act line carries a wait");
        if (act.Contains("send", StringComparison.Ordinal)) problems.Add("the act line carries an instruction to send");
        if (!act.Contains("as of the moment it is asked", StringComparison.Ordinal)) problems.Add("the act line lacks \"as of the moment it is asked\"");
        if (sentence.Contains("as of the moment", StringComparison.Ordinal)) problems.Add("the unheard sentence claims the act line's reading");

        Said("the unheard sentence carries the wait once; the act line carries no wait, no instruction and states its moment", problems.Count == 0
            ? "the unheard sentence carries the wait once; the act line carries no wait, no instruction and states its moment"
            : string.Join("; ", problems));
    }

    /// <summary>
    /// AFC31. A client timeout becomes a refusal in the signed path; a caller's cancellation does not. <c>ForumSession.SubmitAsync</c> under a 300 ms timeout returns a
    /// refusal that carries <c>Unheard</c> and the type <c>curia/client/unheard</c>; under a cancelled caller token it throws <c>OperationCanceledException</c> (control);
    /// a read's timeout, in <c>ForumClient</c>, keeps <c>Unheard</c> false and the old transport type.
    /// </summary>
    [Fact]
    public async Task R11_77_AFC31_ATimeoutIsARefusalAndACallersCancellationIsNot()
    {
        using var stub = new RawStubForum(UnheardBehaviour.Stall);
        using var home = new CliHome(stub.Address);
        var store = new ProfileStore(home.Path);
        Assert.True(store.Load("alice").TryGetValue(out var agent, out var loadError), loadError?.Type);
        using var owned = agent;
        var draft = new PostDraft { Kind = PostKind.Question, Board = "b", Title = "A title?", Body = "A body." };
        Assert.True(SubmissionBuilder.Build(agent!, draft, DateTimeOffset.UtcNow).TryGetValue(out var signed, out var buildError), buildError?.Type);

        async Task<string> TimedOutAsync()
        {
            using var http = ShortTimeout(stub.Address);
            var session = new ForumSession(new ForumClient(http, stub.Address), agent!, store, TimeProvider.System);
            try
            {
                var result = await session.SubmitAsync(signed!.Wire, Ct);
                return result.TryGetValue(out _, out var refusal) ? "a receipt" : $"a refusal {refusal!.Error.Type}, Unheard {(refusal.Unheard ? "true" : "false")}";
            }
            catch (OperationCanceledException)
            {
                return "OperationCanceledException escaped PostOnceAsync";
            }
        }

        async Task<string> CallerCancelledAsync()
        {
            using var http = new HttpClient { BaseAddress = stub.Address, Timeout = TimeSpan.FromSeconds(30) };
            var session = new ForumSession(new ForumClient(http, stub.Address), agent!, store, TimeProvider.System);
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            cancel.CancelAfter(TimeSpan.FromMilliseconds(300));
            try
            {
                await session.SubmitAsync(signed!.Wire, cancel.Token);
                return "returned";
            }
            catch (OperationCanceledException)
            {
                return "OperationCanceledException";
            }
        }

        async Task<string> ReadTimedOutAsync()
        {
            using var reads = new RawStubForum(UnheardBehaviour.Stall) { StallReads = true };
            using var http = ShortTimeout(reads.Address);
            var client = new ForumClient(http, reads.Address);
            var result = await client.GetReaderContractAsync(Ct);
            return result.TryGetValue(out _, out var refusal) ? "a document" : $"a refusal {refusal!.Error.Type}, Unheard {(refusal.Unheard ? "true" : "false")}";
        }

        Said(
            "a client timeout: a refusal curia/client/unheard, Unheard true; a caller's cancellation: OperationCanceledException; a read's timeout: a refusal curia/client/transport, Unheard false",
            $"a client timeout: {await TimedOutAsync()}; a caller's cancellation: {await CallerCancelledAsync()}; a read's timeout: {await ReadTimedOutAsync()}");
    }

    /// <summary>
    /// AFC32. The title does not contradict the sentence (trap 95): of the runs of AFC20 and AFC21 that hold the unheard sentence, none holds "could not be reached", and of
    /// those that hold "could not be reached", none holds the sentence; and every unheard run holds the sentence while every unsent one holds the title of a request not sent.
    /// </summary>
    [Fact]
    public async Task R11_77_AFC32_NoOutputHoldsBothTheUnheardSentenceAndTheTitleOfARequestThatWasNotSent()
    {
        var expected = new List<string>();
        var actual = new List<string>();
        foreach (var row in Rows())
        {
            var facts = Read((await RunRowAsync(row.Name)).Text);
            expected.Add($"{row.Name}: " + (row.Unheard ? "the sentence, not \"could not be reached\"" : "\"could not be reached\", not the sentence"));
            actual.Add($"{row.Name}: " + $"{(facts.Sentence ? "the sentence" : "no sentence")}, {(facts.NotReachedTitle ? "\"could not be reached\"" : "not \"could not be reached\"")}");
            if (row.Unheard ? facts.Sentence && !facts.NotReachedTitle : !facts.Sentence && facts.NotReachedTitle) actual[^1] = expected[^1];
        }

        Said(expected, actual);
    }
}
