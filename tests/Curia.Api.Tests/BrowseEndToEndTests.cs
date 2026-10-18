using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Curia.Canon.Json;
using Curia.Client;
using Curia.Client.Cli;
using Curia.Domain.Content;
using Curia.Domain.Serving;
using Curia.OperatorTool;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using CliExit = Curia.Client.Cli.ExitCode;
using CliProgram = Curia.Client.Cli.Program;

namespace Curia.Api.Tests;

/// <summary>
/// The real Forum on a real socket, with one addition a test needs and a Forum does not offer: a key set that can be made to
/// answer for another identity, so a served post's signature fails to verify under the keys the Forum publishes for its
/// author. That is what a forged post, or a Forum lying about a key, looks like to a reader; the Forum's own honest fold
/// cannot make one (it refuses a post under a compromised key).
/// </summary>
public sealed class BrowseForumFixture : ForumFixture
{
    public BrowseForumFixture() => UseKestrel(0);

    /// <summary>Agent id to the key-set document the Forum will serve for it from now on.</summary>
    internal ConcurrentDictionary<string, string> KeySetSwaps { get; } = new(StringComparer.Ordinal);

    /// <summary>A request path to the number of requests still answered before the Forum accepts every later one and never answers it.</summary>
    internal ConcurrentDictionary<string, Stall> Stalls { get; } = new(StringComparer.Ordinal);

    /// <summary>The requests to a path that are answered before the rest hang (spec 14.6 item 6: a Forum that accepts and never answers).</summary>
    internal sealed class Stall(int answered)
    {
        internal int Remaining = answered;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services => services.AddTransient<IStartupFilter>(_ => new SwapKeySets(KeySetSwaps, Stalls)));
    }

    private sealed class SwapKeySets(ConcurrentDictionary<string, string> swaps, ConcurrentDictionary<string, Stall> stalls) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, pass) =>
            {
                if (stalls.TryGetValue(context.Request.Path.Value ?? string.Empty, out var stall) && Interlocked.Decrement(ref stall.Remaining) < 0)
                {
                    try
                    {
                        await Task.Delay(Timeout.Infinite, context.RequestAborted);
                    }
                    catch (OperationCanceledException)
                    {
                        // The client gave up, which is what the fact is about.
                    }

                    return;
                }

                if (context.Request.Path == "/v1/jwks"
                    && context.Request.Query["agent"].ToString() is { Length: > 0 } agent
                    && swaps.TryGetValue(agent, out var body))
                {
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(body);
                    return;
                }

                await pass();
            });
            next(app);
        };
    }
}

/// <summary>
/// <c>curia browse</c>, the shipped command run as a child against the real Forum (errata G22, R10.72 to R10.74). Two
/// halves. Redirected streams: the line form, whose stdout and exit code are the matching verb's, byte for byte, with the
/// reason on stderr. A pseudo-terminal (<c>tests/Shared/pty-run.py</c>): what the screen does when a key, a signal, a resize
/// or a hang-up arrives, and what it exits with after a person has seen a disputed or a failed post.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class BrowseEndToEndTests(BrowseForumFixture forum) : IClassFixture<BrowseForumFixture>, IDisposable
{
    /// <summary>
    /// What <c>ScreenWriter.Leave()</c> writes, spelled out here and not read from the type that writes it: Plain, Home, Clear,
    /// WrapOn, CursorShown, KeypadOff, AlternateScreenOff.
    /// </summary>
    private const string Leave = "\u001b[0m\u001b[H\u001b[2J\u001b[?7h\u001b[?25h\u001b[?1l\u001b>\u001b[?1049l";

    private static readonly TimeSpan ChildTimeout = TimeSpan.FromSeconds(30);

    private readonly string _home = Directory.CreateTempSubdirectory("curia-browse-").FullName;

    private string Address => forum.Client.BaseAddress!.GetLeftPart(UriPartial.Authority);

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    [GeneratedRegex("\u001b\\[(?<row>[0-9]+);[0-9]+H(?<text>.*?)\u001b\\[K", RegexOptions.Singleline)]
    private static partial Regex RowWrite();

    [GeneratedRegex("\u001b\\[[0-9]+;[0-9]+H")]
    private static partial Regex AnyAddress();

    [GeneratedRegex(@"^(?<key>posted|digest)\s+""?(?<value>[^\s""]+)", RegexOptions.Multiline)]
    private static partial Regex Receipt();

    // ---- running the command ---------------------------------------------------------------

    private static ProcessStartInfo CuriaStart(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "curia.dll"));
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return start;
    }

    /// <summary>Runs <c>curia &lt;args&gt;</c> with every stream redirected, as a script or a model would.</summary>
    private async Task<(int Exit, string Stdout, string Stderr)> CuriaAsync(CancellationToken ct, IReadOnlyDictionary<string, string>? env = null, params string[] args)
    {
        var start = CuriaStart(args);
        start.Environment["CURIA_CLIENT_HOME"] = _home;
        start.Environment.Remove("CURIA_BROWSE");
        foreach (var (name, value) in env ?? new Dictionary<string, string>()) start.Environment[name] = value;

        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        // A child that draws a screen with no terminal waits for a key that never comes: it fails the fact, it does not hang the suite.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(ChildTimeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"curia {string.Join(' ', args)} did not exit within {ChildTimeout.TotalSeconds:0} s with every stream redirected: it is waiting on a terminal it was not given");
        }

        return (process.ExitCode, await stdout, await stderr);
    }

    /// <summary><c>curia browse &lt;args&gt;</c> on a pseudo-terminal of <paramref name="size"/>, driven as the options say.</summary>
    private Task<PtyResult> BrowseAsync(string[] options, string[] browseArgs, CancellationToken ct, string size = "120x50") =>
        PtyRun.RunAsync(
            [
                "--env", "CURIA_CLIENT_HOME=" + _home,
                "--env", "TERM=xterm-256color",
                "--env", "LANG=en_US.UTF-8",
                "--env", "LC_ALL=en_US.UTF-8",
                "--env", "CURIA_BROWSE=screen",
                "--size", size,
                "--timeout", "40",
                .. options,
            ],
            ["dotnet", Path.Combine(AppContext.BaseDirectory, "curia.dll"), "browse", .. browseArgs, "--forum", Address],
            ct);

    // ---- a Forum to read ---------------------------------------------------------------------

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private void AdvanceClock() => forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now);

    /// <summary>Enrols an agent with the shipped command; returns its id and key.</summary>
    private async Task<(string Slug, string AgentId, string Kid)> EnrolAsync(CancellationToken ct)
    {
        AdvanceClock();
        var slug = "b" + Suffix();
        var agentId = $"https://agents.example/{slug}";
        var kid = slug + "-1";
        var enrolled = await CuriaAsync(ct, null, "enrol", "--agent", slug, "--agent-id", agentId, "--kid", kid, "--forum", Address);
        Assert.True(enrolled.Exit == 0, enrolled.Stdout + enrolled.Stderr);
        return (slug, agentId, kid);
    }

    private async Task<string> AskAsync(string slug, string board, string title, string body, CancellationToken ct)
    {
        AdvanceClock();
        var asked = await CuriaAsync(ct, null, "ask", "--agent", slug, "--board", board, "--title", title, "--body", body, "--forum", Address);
        Assert.True(asked.Exit == 0, asked.Stdout + asked.Stderr);
        return Receipt().Matches(asked.Stdout).First(m => m.Groups["key"].Value == "posted").Groups["value"].Value;
    }

    /// <summary>The operator's declaration that the key is compromised from its binding on, as the compromise facts seed it.</summary>
    private async Task DeclareCompromisedAsync(string agentId, CancellationToken ct)
    {
        AdvanceClock();
        var pem = LogSigningKey.GeneratePem();

        async Task<int> OperatorAsync(params string[] args)
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var exit = await OperatorCommands.RunAsync(
                args, forum.ConnectionString, forum.Clock, forum.PermissiveGate(), OperatorCommands.LoadLogSigner, stdout, stderr, ct,
                readLogSigningKeyPem: () => pem);
            Assert.True(exit == 0, stdout.ToString() + stderr);
            return exit;
        }

        var boundAt = await KeyActRequests.LatestLifecycleAsync(forum.Client, agentId, ct);
        await OperatorAsync("sign-head", "--by", "ops");
        await OperatorAsync("compromise", "--agent", agentId, "--kid", await KidOfAsync(agentId, ct), "--from-index", boundAt.ToString(System.Globalization.CultureInfo.InvariantCulture), "--reason", "owner-reported", "--operator", "ops");
        await OperatorAsync("sign-head", "--by", "ops");
    }

    private async Task<string> KidOfAsync(string agentId, CancellationToken ct)
    {
        var body = await forum.Client.GetStringAsync($"/v1/jwks?agent={Uri.EscapeDataString(agentId)}", ct);
        using var json = System.Text.Json.JsonDocument.Parse(body);
        return json.RootElement.GetProperty("keys")[0].GetProperty("kid").GetString()!;
    }

    /// <summary>Makes the Forum answer for <paramref name="agentId"/> with another identity's keys: its posts then fail to verify.</summary>
    private async Task ForgeAsync(string agentId, string asIfAgentId, CancellationToken ct) =>
        forum.KeySetSwaps[agentId] = await forum.Client.GetStringAsync($"/v1/jwks?agent={Uri.EscapeDataString(asIfAgentId)}", ct);

    // ---- the line form -----------------------------------------------------------------------

    private const string Reason = "browse: line form, not a screen: standard input is not a terminal\n";

    [Fact]
    public async Task R10_73_BrowseBoardThreadAndReadAreTheMatchingVerbsBytesAndExitCodeWhenStreamsAreRedirected()
    {
        var ct = TestContext.Current.CancellationToken;
        var author = await EnrolAsync(ct);
        var board = "board-" + Suffix();
        var postId = await AskAsync(author.Slug, board, "A title to index", "A body for the line form.", ct);

        foreach (var (browse, verb) in new[]
        {
            (new[] { "browse", "board", board }, new[] { "board", board, "--titles" }),
            (["browse", "thread", postId], ["thread", postId]),
            (["browse", "read", postId], ["read", postId]),
        })
        {
            var screen = await CuriaAsync(ct, null, [.. browse, "--forum", Address]);
            var line = await CuriaAsync(ct, null, [.. verb, "--forum", Address]);

            Assert.True(line.Stdout.Length > 0 && line.Exit == 0, $"the verb printed nothing or failed: {line.Exit} {line.Stderr}");
            Assert.Equal(line.Stdout, screen.Stdout);
            Assert.Equal(line.Exit, screen.Exit);
            Assert.Equal(Reason + line.Stderr, screen.Stderr);
        }
    }

    [Fact]
    public async Task R10_73_PlainAndTheVariableAreTheCallersAskForTheLineFormAndWriteNoStderrLine()
    {
        var ct = TestContext.Current.CancellationToken;
        var author = await EnrolAsync(ct);
        var board = "board-" + Suffix();
        await AskAsync(author.Slug, board, "A title to index", "A body.", ct);

        var verb = await CuriaAsync(ct, null, "board", board, "--titles", "--forum", Address);
        var plain = await CuriaAsync(ct, null, "browse", "board", board, "--plain", "--forum", Address);
        var variable = await CuriaAsync(ct, new Dictionary<string, string> { ["CURIA_BROWSE"] = "plain" }, "browse", "board", board, "--forum", Address);

        foreach (var run in new[] { plain, variable })
        {
            Assert.Equal(verb.Stdout, run.Stdout);
            Assert.Equal(verb.Exit, run.Exit);
            Assert.Equal(string.Empty, run.Stderr);
        }
    }

    [Fact]
    public async Task R10_73_AForumThatHangsUpIsTheSameExitForBrowseAsForTheVerbAndBrowseAddsOnlyItsReason()
    {
        var ct = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var stalled = Task.Run(
            async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    using var connection = await listener.AcceptTcpClientAsync(ct);
                    var buffer = new byte[4096];
                    _ = await connection.GetStream().ReadAsync(buffer, ct);
                }
            },
            ct);
        var address = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var verb = await CuriaAsync(ct, null, "board", "general", "--titles", "--forum", address);
        var browse = await CuriaAsync(ct, null, "browse", "board", "general", "--forum", address);
        listener.Stop();
        await Task.WhenAny(stalled);

        Assert.Equal(CliExit.ForumFault, verb.Exit);
        Assert.Equal(verb.Exit, browse.Exit);
        Assert.Equal(verb.Stdout, browse.Stdout);
        Assert.Equal(Reason + verb.Stderr, browse.Stderr);
    }

    [Theory]
    [InlineData("browse")]
    [InlineData("browse frob x")]
    [InlineData("browse board")]
    [InlineData("browse board a b")]
    [InlineData("browse board general --plain --screen")]
    [InlineData("browse board general --zz-unknown x")]
    public async Task R10_73_ABadBrowseCommandIsAUsageErrorBeforeAnythingIsDrawn(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var ct = TestContext.Current.CancellationToken;

        var run = await CuriaAsync(ct, null, [.. line.Split(' '), "--forum", Address]);

        Assert.Equal(CliExit.Usage, run.Exit);
        Assert.Equal(string.Empty, run.Stdout);
        Assert.StartsWith("error:", run.Stderr, StringComparison.Ordinal);
    }

    // ---- the screen under a pseudo-terminal -----------------------------------------------------

    /// <summary>A forum with one honest post on a board, and the command that opens it as a screen.</summary>
    private async Task<(string PostId, string Board)> OnePostAsync(CancellationToken ct)
    {
        var author = await EnrolAsync(ct);
        var board = "board-" + Suffix();
        var postId = await AskAsync(author.Slug, board, "A title", "line one\nline two\nline three", ct);
        return (postId, board);
    }

    private static void AssertEndsWithLeave(string output)
    {
        var at = output.LastIndexOf(Leave, StringComparison.Ordinal);
        Assert.True(at >= 0, "Leave() was never written: " + Escaped(output[Math.Max(0, output.Length - 200)..]));

        var after = output[(at + Leave.Length)..];
        Assert.False(AnyAddress().IsMatch(after), "the screen drew after Leave(): " + Escaped(after));
        Assert.DoesNotContain("curia browse:", after, StringComparison.Ordinal);
    }

    private static string Escaped(string text) => DisplayLiteral.Of(text);

    private static string Describe(PtyResult run) =>
        $"{run.Status}, exit {run.Exit}, signal {run.Signal}; the last output: {Escaped(run.Output[Math.Max(0, run.Output.Length - 600)..])}";

    /// <summary>The screen came up (its status row was drawn) and was still running after the wait: it stays until told otherwise.</summary>
    private static void AssertScreenStaysUp(PtyResult run)
    {
        Assert.True(run.ReadyMs is not null, $"the screen never came up ({run.Status}, exit {run.Exit}): {Escaped(run.Output[..Math.Min(run.Output.Length, 1500)])}");
        Assert.True(run.AliveBeforeActions == true, "the screen was gone before anything was sent to it");
    }

    [Fact]
    public async Task R10_72_AScreenStaysUpUntilAKeyAsksOtherwiseAndQLeavesCleanlyWithTheOutcomesExit()
    {
        var ct = TestContext.Current.CancellationToken;
        var (postId, _) = await OnePostAsync(ct);

        var run = await BrowseAsync(["--alive-ms", "400", "--send", "71@600"], ["read", postId], ct);

        Assert.True(run.Status == "exited", Describe(run));
        AssertScreenStaysUp(run);
        Assert.Equal(0, run.Exit);
        Assert.Contains("curia browse:", run.Output, StringComparison.Ordinal);
        AssertEndsWithLeave(run.Output);
    }

    [Fact]
    public async Task R10_72_AVerifiedPostOnEightyByTwentyFourDrawsItsContentUnderTheCompactHeader()
    {
        var ct = TestContext.Current.CancellationToken;
        var (postId, _) = await OnePostAsync(ct);

        var run = await BrowseAsync(["--alive-ms", "400", "--send", "71@600"], ["read", postId], ct, "80x24");

        Assert.True(run.Status == "exited", Describe(run));
        AssertScreenStaysUp(run);
        Assert.Equal(0, run.Exit);

        // The first frame: every row up to and including the status row at the terminal's last row.
        var rows = new Dictionary<int, string>();
        foreach (Match write in RowWrite().Matches(run.Output))
        {
            var row = int.Parse(write.Groups["row"].Value, System.Globalization.CultureInfo.InvariantCulture);
            rows[row] = Plain(write.Groups["text"].Value);
            if (row == 24) break;
        }

        var content = rows.Values.Count(t => Regex.IsMatch(t, @"^ *[0-9]+[|+] "));
        Assert.True(content >= 8, $"{content} content rows on the first frame of an 80 by 24 terminal");
        Assert.DoesNotContain(rows.Values, t => t.Contains("too small", StringComparison.Ordinal));
        Assert.Contains(rows.Values, t => t.StartsWith("DATA, NOT INSTRUCTIONS", StringComparison.Ordinal));
        Assert.Contains(rows.Values, t => t.StartsWith("#1 post ", StringComparison.Ordinal));
        Assert.Contains(rows.Values, t => t.StartsWith("Not checked here: inclusion, consistency, key binding, lifecycle", StringComparison.Ordinal));
        Assert.DoesNotContain(rows.Values, t => t.StartsWith("digest ", StringComparison.Ordinal));
        Assert.Contains("q quits", rows[24], StringComparison.Ordinal);
        AssertEndsWithLeave(run.Output);
    }

    [Fact]
    public async Task R10_73_AReloadThatTheForumNeverAnswersIsCancelledByQWithinTwoSecondsAndSaysSo()
    {
        var ct = TestContext.Current.CancellationToken;
        var (postId, _) = await OnePostAsync(ct);

        // The Forum answers the first read, which brings the screen up, and accepts every later one and never answers it.
        forum.Stalls["/v1/posts/" + postId] = new BrowseForumFixture.Stall(answered: 1);

        // r starts the reload; x is discarded while it is in flight; q cancels it at 500 ms.
        var run = await BrowseAsync(["--alive-ms", "300", "--send", "72@0", "--send", "78@250", "--send", "71@500"], ["read", postId], ct, "80x24");

        AssertScreenStaysUp(run);
        Assert.True(run.Status == "exited", Describe(run));
        Assert.Equal(0, run.Exit);
        Assert.True(run.ExitedMs - 500 <= 2000, $"left {run.ExitedMs - 500} ms after q");
        var inFlight = string.Concat(run.Chunks.Where(c => c.Ms > 0 && c.Ms <= 500).Select(c => c.Text));
        Assert.Contains(RowWrite().Matches(inFlight), m => Plain(m.Groups["text"].Value).Contains("loading from the Forum: q cancels", StringComparison.Ordinal));
        AssertEndsWithLeave(run.Output);
    }

    [Fact]
    public async Task R10_73_CtrlBackslashWithNoOtherKeyLeavesWithinTwoSeconds()
    {
        var ct = TestContext.Current.CancellationToken;
        var (postId, _) = await OnePostAsync(ct);

        var run = await BrowseAsync(["--alive-ms", "300", "--send", "1c@0"], ["read", postId], ct);

        AssertScreenStaysUp(run);
        Assert.True(run.Status == "exited", Describe(run));
        Assert.Equal(0, run.Exit);
        Assert.True(run.ExitedMs <= 2000, $"left after {run.ExitedMs} ms");
        AssertEndsWithLeave(run.Output);
    }

    [Fact]
    public async Task R10_73_CtrlCIsAKeyThatQuitsAndLeaves()
    {
        var ct = TestContext.Current.CancellationToken;
        var (postId, _) = await OnePostAsync(ct);

        var run = await BrowseAsync(["--alive-ms", "300", "--send", "03@0"], ["read", postId], ct);

        AssertScreenStaysUp(run);
        Assert.True(run.Status == "exited", Describe(run));
        Assert.Equal(0, run.Exit);
        Assert.True(run.ExitedMs <= 2000, $"left after {run.ExitedMs} ms");
        AssertEndsWithLeave(run.Output);
    }

    [Fact]
    public async Task R10_73_CtrlZShowsItsSentenceOnTheStatusRowAndTheScreenKeepsRunning()
    {
        var ct = TestContext.Current.CancellationToken;
        var (postId, _) = await OnePostAsync(ct);

        var run = await BrowseAsync(["--alive-ms", "300", "--send", "1a@0", "--send", "71@800"], ["read", postId], ct);

        // Still running when q came, and q was read: a process the shell had stopped would have timed out here.
        AssertScreenStaysUp(run);
        Assert.True(run.Status == "exited", Describe(run));
        Assert.Equal(0, run.Exit);
        var afterTheKey = string.Concat(run.Chunks.Where(c => c.Ms > 0).Select(c => c.Text));
        var status = RowWrite().Matches(afterTheKey).LastOrDefault(m => m.Groups["text"].Value.Contains("suspending is not supported here", StringComparison.Ordinal));
        Assert.NotNull(status);
        AssertEndsWithLeave(run.Output);
    }

    [Fact]
    public async Task R10_73_SigtermWithNoKeyLeavesWithinTwoSecondsAndExitsAs128PlusTheSignal()
    {
        var ct = TestContext.Current.CancellationToken;
        var (postId, _) = await OnePostAsync(ct);

        var run = await BrowseAsync(["--alive-ms", "300", "--signal", "TERM@0"], ["read", postId], ct);

        AssertScreenStaysUp(run);
        Assert.True(run.Status == "exited", Describe(run));
        Assert.Equal(143, run.Exit);
        Assert.True(run.ExitedMs <= 2000, $"left after {run.ExitedMs} ms");
        AssertEndsWithLeave(run.Output);
    }

    [Fact]
    public async Task R10_73_SighupAfterTheMasterClosesExitsWithinTwoSeconds()
    {
        var ct = TestContext.Current.CancellationToken;
        var (postId, _) = await OnePostAsync(ct);

        var run = await BrowseAsync(["--alive-ms", "300", "--close-master-at", "0"], ["read", postId], ct);

        AssertScreenStaysUp(run);
        Assert.True(run.Status == "exited", Describe(run));
        Assert.Equal(129, run.Exit);
        Assert.True(run.ExitedMs <= 2000, $"left after {run.ExitedMs} ms");
    }

    [Fact]
    public async Task R10_72_AResizeToTheComputedMinimumRedrawsAFullFrameWithinHalfASecond()
    {
        var ct = TestContext.Current.CancellationToken;
        var (postId, _) = await OnePostAsync(ct);

        // The minimum, computed by the type that computes it from the passage the screen will draw.
        using var http = new HttpClient { BaseAddress = new Uri(Address) };
        var client = new ForumClient(http, new Uri(Address));
        var served = await client.GetPostAsync(postId, MarkingMode.Datamark, ct);
        Assert.True(served.TryGetValue(out var post, out var refusal), refusal?.Summary);
        var (passages, _) = await CliProgram.VerifyAllAsync(client, [post], ct);
        var minimum = Screen.MinimumHeightFor(passages, Screen.MinimumWidth);

        var run = await BrowseAsync(["--size", "100x60", "--alive-ms", "200", "--resize", $"{Screen.MinimumWidth}x{minimum}@0", "--send", "71@900"], ["read", postId], ct);

        Assert.True(run.Status == "exited", Describe(run));
        var redrawn = string.Concat(run.Chunks.Where(c => c.Ms is > 0 and <= 500).Select(c => c.Text));
        var writes = RowWrite().Matches(redrawn).Select(m => (Row: int.Parse(m.Groups["row"].Value, System.Globalization.CultureInfo.InvariantCulture), Text: Plain(m.Groups["text"].Value))).ToList();

        Assert.True(writes.Count > 0, "nothing was drawn within 500 ms of the resize");
        Assert.Equal(Enumerable.Range(1, minimum), writes.Select(w => w.Row).Distinct().Order());
        Assert.Equal(minimum, writes[^1].Row);
        Assert.All(writes, w => Assert.True(w.Text.Length <= Screen.MinimumWidth, $"a row of {w.Text.Length} columns at width {Screen.MinimumWidth}: {Escaped(w.Text)}"));
        Assert.True(writes.Count(w => Regex.IsMatch(w.Text, @"^ *[0-9]+[|+] ")) >= Screen.MinimumContentRows, "fewer than eight content rows at the computed minimum");
        Assert.Contains(writes, w => w.Text.StartsWith("curia browse:", StringComparison.Ordinal));
        Assert.Contains(writes, w => w.Text.Contains("untrusted", StringComparison.OrdinalIgnoreCase));
    }

    private static string Plain(string text) => Regex.Replace(text, "\u001b\\[[0-9;]*m", string.Empty);

    [Fact]
    public async Task R10_74_ASessionThatOpensADisputedPostAndQuitsExitsNine()
    {
        var ct = TestContext.Current.CancellationToken;
        var author = await EnrolAsync(ct);
        var board = "board-" + Suffix();
        var postId = await AskAsync(author.Slug, board, "Signed before the declaration", "A body.", ct);
        await DeclareCompromisedAsync(author.AgentId, ct);

        // 80 by 30: a disputed post's compact header needs more than 24 rows (spec 14.1), and 30 is a size a person has.
        var read = await BrowseAsync(["--alive-ms", "300", "--send", "71@0"], ["read", postId], ct, "80x30");
        var index = await BrowseAsync(["--alive-ms", "300", "--send", "0d@0", "--send", "71@600"], ["board", board], ct, "80x30");

        foreach (var run in new[] { read, index })
        {
            AssertScreenStaysUp(run);
            Assert.True(run.Status == "exited", Describe(run));
            Assert.Equal(CliExit.Disputed, run.Exit);
            Assert.Contains("DISPUTED", run.Output, StringComparison.Ordinal);
            AssertEndsWithLeave(run.Output);
        }
    }

    [Fact]
    public async Task R10_74_ASessionThatOpensAFailedPostExitsSixEvenAfterBackspace()
    {
        var ct = TestContext.Current.CancellationToken;
        var author = await EnrolAsync(ct);
        var other = await EnrolAsync(ct);
        var board = "board-" + Suffix();
        await AskAsync(author.Slug, board, "Served under the wrong keys", "A body.", ct);
        await ForgeAsync(author.AgentId, other.AgentId, ct);

        // Enter opens the post, Backspace goes back, q quits.
        var run = await BrowseAsync(["--alive-ms", "300", "--send", "0d@0", "--send", "7f@500", "--send", "71@900"], ["board", board], ct, "80x30");

        AssertScreenStaysUp(run);
        Assert.True(run.Status == "exited", Describe(run));
        Assert.Equal(CliExit.Unverified, run.Exit);
        Assert.Contains("no content is shown", run.Output, StringComparison.Ordinal);
        AssertEndsWithLeave(run.Output);
    }

    [Fact]
    public async Task R10_74_AViewWhoseOutcomeIsBetterDoesNotReplaceTheDisputedOutcomeAnEarlierViewShowed()
    {
        var ct = TestContext.Current.CancellationToken;
        var disputedAuthor = await EnrolAsync(ct);
        var honestAuthor = await EnrolAsync(ct);
        var board = "board-" + Suffix();
        await AskAsync(disputedAuthor.Slug, board, "Signed before the declaration", "A body.", ct);
        var verifiedId = await AskAsync(honestAuthor.Slug, board, "Signed by an honest key", "Another body.", ct);
        await DeclareCompromisedAsync(disputedAuthor.AgentId, ct);

        // The index loads both posts and counts the disputed one. Tab moves to the verified post and Enter opens it; its reload is a load of that
        // post alone and counts only a verified outcome; q quits. The exit is still 9: it carries what the person saw.
        var run = await BrowseAsync(["--alive-ms", "300", "--send", "09@0", "--send", "0d@400", "--send", "72@900", "--send", "71@1700"], ["board", board], ct, "80x30");

        AssertScreenStaysUp(run);
        Assert.True(run.Status == "exited", Describe(run));
        Assert.Contains("DISPUTED", run.Output, StringComparison.Ordinal);
        Assert.Contains(verifiedId, Plain(run.Output), StringComparison.Ordinal);
        Assert.Equal(CliExit.Disputed, run.Exit);
        AssertEndsWithLeave(run.Output);
    }

    [Fact]
    public async Task R10_74_AReloadDoesNotRemoveAnEarlierOutcome()
    {
        var ct = TestContext.Current.CancellationToken;
        var author = await EnrolAsync(ct);
        var board = "board-" + Suffix();
        await AskAsync(author.Slug, board, "Signed before the declaration", "A body.", ct);
        await DeclareCompromisedAsync(author.AgentId, ct);

        var run = await BrowseAsync(["--alive-ms", "300", "--send", "72@0", "--send", "72@500", "--send", "71@1000"], ["board", board], ct, "80x30");

        AssertScreenStaysUp(run);
        Assert.True(run.Status == "exited", Describe(run));
        Assert.Equal(CliExit.Disputed, run.Exit);
    }
}
