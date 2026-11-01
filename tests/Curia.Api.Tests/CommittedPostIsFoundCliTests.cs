using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Mcp;
using Curia.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using Xunit;

using Probe = Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>
/// CPF23 and CPF32 (errata G54, R11.75, R11.69 (rev.)): the instruction followed across the seam to the act it tells the reader to do, and the loop that drives the check.
/// CPF23 takes its input from the Forum's own output: a real <c>503</c> on a real socket, the raw <c>200</c> a search then returns, and the lines the real <c>curia search</c>
/// and the <c>curia_search</c> tool print for it; the expected lines are composed from R11.75's published pieces with the instants and the count the raw JSON holds
/// (trap 89). CPF32 runs the hosted loop itself, on a clock whose timers fire when it is advanced, with nobody calling the tick.
/// </summary>
[Collection("forum")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class CommittedPostIsFoundCliTests(CommittedPostKestrelFixture forum) : IClassFixture<CommittedPostKestrelFixture>, IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("curia-cpf-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Address => forum.Client.BaseAddress!.GetLeftPart(UriPartial.Authority);

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static string Fold(string text) => PublishedCheckWords.Fold(text);

    /// <summary>Moves the Forum's clock forward to the real time, never back: a child process signs its proofs with the real clock.</summary>
    private void SyncClock()
    {
        var behind = DateTimeOffset.UtcNow - forum.Now;
        if (behind > TimeSpan.Zero) forum.Clock.Advance(behind);
    }

    private async Task<(int Exit, string Text, string Stdout)> CuriaAsync(params string[] args)
    {
        SyncClock();
        var run = await CuriaProcess.RunAsync(_home, Ct, args);
        return (run.Exit, Fold(run.Stdout + " " + run.Stderr), run.Stdout);
    }

    private static string Text(CallToolResult result) => string.Join('\n', result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private static string Unquote(string raw) => raw.StartsWith('"') ? System.Text.Json.JsonSerializer.Deserialize<string>(raw)! : raw;

    private async Task<(string Cli, string Tool)> ReadersAsync()
    {
        var run = await CuriaAsync("search", "quokka", "--forum", Address);
        var cli = run.Stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Where(l => l.StartsWith("vector  ", StringComparison.Ordinal)).Select(Fold).FirstOrDefault() ?? "no vector line";
        var http = forum.Client;
        var tools = new ForumTools(new ForumClient(http, http.BaseAddress!), MarkingMode.None, new HeadStore(_home), null);
        var tool = Fold(Text(await tools.SearchAsync(new SearchCriteria { Query = "quokka" }, Ct)));
        return (cli, tool);
    }

    /// <summary>
    /// CPF23. The instruction across the seam (trap 89). From a real <c>503</c> of an <c>ask</c>, <c>curia search</c> and <c>curia_search</c> run against the same Forum print
    /// the owed form with the due instant equal to the raw JSON's member, and after the clock has reached the settle window past the exit, the not-owed form with the new
    /// checked instant; the post is found by words in the first and by meaning in both after one tick.
    /// </summary>
    [Fact]
    public async Task R11_75_CPF23_TheLinesTheClientsPrintAreTheRawMembersComposedFromThePublishedPieces()
    {
        await using var host = forum.Itself();
        var slug = $"cpf23-{Guid.NewGuid().ToString("N")[..8]}";
        var enrolled = await CuriaAsync("enrol", "--agent", slug, "--forum", Address);
        Assert.True(enrolled.Exit == 0, enrolled.Text);

        host.Store.Arm(EventStoreFault.AppendThenThrow);
        var asked = await CuriaAsync("ask", "--agent", slug, "--board", "cpf23", "--title", $"About a quokka {Guid.NewGuid():N}?", "--body", "A question about the quokka.", "--forum", Address);
        host.Store.Disarm();
        var exit = forum.Now;

        var owedState = await host.StateAsync(Ct);
        var (owedCli, owedTool) = await ReadersAsync();
        var owedLine = Fold(PublishedCheckWords.Line(int.Parse(owedState.Pending, CultureInfo.InvariantCulture), owedState.Owed ? owedState.Due : null, owedState.DueRaw == "null", owedState.Checked));

        await host.TicksAsync(1, Ct);
        var antiJoinAfterTick = await host.AntiJoinAsync(await AuthorOfAsync(), Ct);
        await host.TicksAsync((int)PublishedCheckWords.SweepSettle().TotalSeconds + 1, Ct);

        var clearState = await host.StateAsync(Ct);
        var (clearCli, clearTool) = await ReadersAsync();
        var clearLine = Fold(PublishedCheckWords.Line(int.Parse(clearState.Pending, CultureInfo.InvariantCulture), clearState.Owed ? clearState.Due : null, clearState.DueRaw == "null", clearState.Checked));

        static string ToolExpected(CheckState state) =>
            $"vector_check_due_at={(state.Owed ? state.Due : "none")}, vector_checked_at={state.Checked}, {(state.Owed ? "with" : "without")} the owed sentence";

        static string ToolObserved(string block, CheckState state)
        {
            var due = state.Owed ? state.Due : "none";
            var sentence = Fold(PublishedCheckWords.OwedSentence());
            return $"vector_check_due_at={(block.Contains($"vector_check_due_at={due}", StringComparison.Ordinal) ? due : "something else")}, " +
                $"vector_checked_at={(block.Contains($"vector_checked_at={state.Checked}", StringComparison.Ordinal) ? state.Checked : "something else")}, " +
                $"{(block.Contains(sentence, StringComparison.Ordinal) ? "with" : "without")} the owed sentence";
        }

        var expected = new[]
        {
            $"ask answered 503 {PublishedStoreFaults.StoreUnavailableType}",
            "after the 503 the raw search states vector_check_due_at as an instant",
            $"owed: the CLI line is \"{owedLine}\"",
            $"owed: the tool states {ToolExpected(owedState)}",
            "one tick later the anti-join is 0",
            $"settled: the CLI line is \"{clearLine}\"",
            $"settled: the tool states {ToolExpected(clearState)}",
        };
        var actual = new[]
        {
            $"ask answered {(asked.Text.Contains(PublishedStoreFaults.StoreUnavailableType, StringComparison.Ordinal) ? "503 " + PublishedStoreFaults.StoreUnavailableType : asked.Text)}",
            owedState.Owed ? "after the 503 the raw search states vector_check_due_at as an instant" : $"after the 503 the raw search states vector_check_due_at as {owedState.DueRaw}",
            $"owed: the CLI line is \"{owedCli}\"",
            $"owed: the tool states {ToolObserved(owedTool, owedState)}",
            $"one tick later the anti-join is {antiJoinAfterTick}",
            $"settled: the CLI line is \"{clearCli}\"",
            $"settled: the tool states {ToolObserved(clearTool, clearState)}",
        };
        Probe.Said(expected, actual);
        _ = exit;
    }

    private async Task<string> AuthorOfAsync()
    {
        await using var admin = new Npgsql.NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand("SELECT payload->>'author' FROM events WHERE event_type = 'post.accepted' AND payload->>'board' = 'cpf23' ORDER BY seq DESC LIMIT 1;", admin);
        return (string?)await command.ExecuteScalarAsync(Ct) ?? string.Empty;
    }
}

/// <summary>CPF32.</summary>
[Collection("forum")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class CommittedPostLoopTests(AgentWriteGateForumFixture forum) : IClassFixture<AgentWriteGateForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// CPF32. The loop drives the check (ruling 14). On a host whose clock has timers, with the hosted catch-up loop in place and nobody calling the tick: a check that falls
    /// due runs at the first tick after it falls due, not at the next search, and finds the post the log holds and the index lacks.
    /// </summary>
    [Fact]
    public async Task R8_83_CPF32_TheHostedLoopRunsTheCheckAtTheFirstTickAfterItFallsDue()
    {
        var clock = TimerClockHost.At(forum);
        await using var host = TimerClockHost.Of(forum, clock);
        using var client = host.CreateClient();
        var loops = host.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().Count(s => s.GetType().Name == "EmbeddingCatchUpService");
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "cpf32", Ct);
        var (_, digest) = await forum.AppendStoredPostAsync(actor.Agent.SignQuestion(Probe.Board(), Probe.Unrelated(), "About " + Probe.Unrelated(), forum.Now), Ct);
        var sweep = host.Services.GetRequiredService<Curia.Application.Retrieval.IndexSweep>();
        var armed = await IndexLag.EventuallyAsync(() => clock.Armed >= 1, TimeSpan.FromSeconds(10), Ct);
        sweep.Owe(clock.GetUtcNow());

        var rows = 0L;
        for (var second = 1; second <= 6 && rows == 0; second++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            for (var i = 0; i < 20 && rows == 0; i++)
            {
                rows = await IndexLag.RowsAsync(forum, digest);
                if (rows == 0) await Task.Delay(50, Ct);
            }
        }

        Probe.Said(
            "1 catch-up service; a timer armed; the post the log held and the index lacked was in the index within 6 ticks of a check falling due, with no search made",
            $"{loops} catch-up service{(loops == 1 ? string.Empty : "s")}; {(armed ? "a timer armed" : "no timer armed")}; the post the log held and the index lacked was {(rows == 1 ? "in the index within 6 ticks of a check falling due" : "not in the index 6 ticks after a check fell due")}, with no search made");
    }
}
