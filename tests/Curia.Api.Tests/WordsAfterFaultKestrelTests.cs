using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using Curia.Client;
using Curia.Domain.Authorization;
using Curia.Domain.Serving;
using Curia.Mcp;
using Curia.OperatorTool;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using Npgsql;
using Xunit;
using CliExit = Curia.Client.Cli.ExitCode;

namespace Curia.Api.Tests;

/// <summary>
/// The Forum on Kestrel (Development) with the decorated store and a home for the profiles the probes enrol: the real <c>curia</c> and the real <c>curia-mcp</c>
/// tools act against it, and the events table is the oracle (errata G53, the plan's ruling 2). One answerer at T1 is made once, when the fixture starts and before
/// any probe has moved the Forum's clock to the real time, so that its enrolment is 49 hours old (the tenure T1 needs) and every thread has an answer to accept.
/// </summary>
public sealed class WordsAfterFaultFixture : FaultableForumFixture, IAsyncLifetime
{
    /// <summary>The answerer's slug: enrolled, attested, warmed up and past the T1 tenure.</summary>
    internal const string Answerer = "waf-answerer";

    public WordsAfterFaultFixture() => UseKestrel(0);

    /// <summary>The profile store the child <c>curia</c> processes and the in-process tools share.</summary>
    internal string Home { get; } = Directory.CreateTempSubdirectory("curia-waf-").FullName;

    /// <summary>Moves the Forum's clock forward to the real time, never back: a child process signs its proofs with the real clock.</summary>
    internal void SyncClock()
    {
        var behind = DateTimeOffset.UtcNow - Now;
        if (behind > TimeSpan.Zero) Clock.Advance(behind);
    }

    async ValueTask IAsyncLifetime.InitializeAsync()
    {
        await base.InitializeAsync();

        // As if enrolled 49 hours ago, through the reference client's own writer (as StoreFaultStatusQuoTests does): the clock is put 49 hours short of the real
        // time, the answerer enrols and asks its three questions, and the clock then catches up.
        var tenure = TimeSpan.FromHours(TierPolicy.T1MinimumHours + 1);
        var behind = DateTimeOffset.UtcNow - Now - tenure;
        if (behind > TimeSpan.Zero) Clock.Advance(behind);

        var store = new ProfileStore(Home);
        Assert.True(store.Create(Answerer, "https://agents.example/waf-answerer", "waf-answerer-k", Client.BaseAddress!).TryGetValue(out var agent, out var created), created?.Detail);
        using (agent)
        {
            var client = new ForumClient(Client, Client.BaseAddress!);
            Assert.True((await client.EnrolAsync(agent!, CancellationToken.None)).TryGetValue(out _, out var refusal), refusal?.Summary);
            await AttestOwnerAsync(agent!.Profile.AgentId, CancellationToken.None, owner: "owner:waf-answerer");

            var session = new ForumSession(client, agent, store, Clock);
            var tools = new ForumTools(client, MarkingMode.Datamark, new HeadStore(Home), new ForumWriter(agent, session, Clock));
            var board = "waf-warmup-" + Guid.NewGuid().ToString("N")[..8];
            for (var i = 0; i < TierPolicy.T1MinimumCleanQuestions; i++)
                await tools.AskAsync(board, $"Warmup {i} {Guid.NewGuid():N}", $"warmup body {i} {Guid.NewGuid():N}", null, null, CancellationToken.None);
        }

        SyncClock();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        Directory.Delete(Home, recursive: true);
    }
}

/// <summary>
/// WAF1 to WAF3, WAF10 to WAF15, WAF18, WAF20, WAF21, WAF32, WAF34 and WAF35 (errata G53, R10.93, R11.73, R11.74): the words after a fault followed across the
/// seam to the act they instruct. A probe that follows an instruction takes its input from the instruction's own output: the digest it rechecks is parsed out of the
/// act line a real client printed after a real <c>503</c>, and "recorded" is the events table, never a status or a line. Every sentence, clause, phrase and fragment is
/// read from the errata (<see cref="PublishedClientWords"/>, <see cref="PublishedStoreFaults"/>), folded for whitespace. The commit of WAF32 is gated by the test and
/// never timed by a sleep.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed partial class WordsAfterFaultKestrelTests(WordsAfterFaultFixture forum) : IClassFixture<WordsAfterFaultFixture>, IDisposable
{
    private static readonly string LogKeyPem = LogSigningKey.GeneratePem();

    private readonly List<IDisposable> _owned = [];
    private readonly List<string> _homes = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Address => forum.Client.BaseAddress!.GetLeftPart(UriPartial.Authority);

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        foreach (var home in _homes) Directory.Delete(home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static string Fold(string text) => PublishedClientWords.Fold(text);

    [GeneratedRegex(@"^post\s+""?([^\s""]+)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PostLine();

    [GeneratedRegex(@"^posted\s+""?(?<id>[^\s""]+)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PostedLine();

    [GeneratedRegex(@"answered at (?<instant>\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ)", RegexOptions.CultureInvariant)]
    private static partial Regex AnsweredAt();

    [GeneratedRegex(@"log_index\s+(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex LogIndexLine();

    // ----- the harness -----

    private async Task<(int Exit, string Text)> CuriaAsync(params string[] args)
    {
        forum.SyncClock();
        var run = await CuriaProcess.RunAsync(forum.Home, Ct, args);
        return (run.Exit, Fold(run.Stdout + " " + run.Stderr));
    }

    private async Task<long> EventsAsync()
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM events;", admin);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture);
    }

    /// <summary>The act line as a pattern, read from the published words: the digest is whatever the line holds in the spelling <c>recheck</c> takes.</summary>
    private static Regex ActPattern() =>
        new(Regex.Escape(Fold(PublishedStoreFaults.ActSentence)).Replace("<digest>", "(?<digest>sha256:[0-9a-f]{64})", StringComparison.Ordinal), RegexOptions.CultureInvariant);

    private async Task<T> ArmedAsync<T>(EventStoreFault fault, Func<Task<T>> act)
    {
        forum.Store.Arm(fault);
        try
        {
            return await act();
        }
        finally
        {
            forum.Store.Disarm();
        }
    }

    private async Task<string> EnrolAsync(string stem)
    {
        var slug = $"{stem}-{Guid.NewGuid().ToString("N")[..8]}";
        var enrolled = await CuriaAsync("enrol", "--agent", slug, "--forum", Address);
        Assert.True(enrolled.Exit == 0, enrolled.Text);
        return slug;
    }

    private ForumTools ToolsFor(string slug, TimeProvider? clock = null, string? headHome = null)
    {
        var store = new ProfileStore(forum.Home);
        Assert.True(store.Load(slug).TryGetValue(out var agent, out var error), error?.Detail);
        _owned.Add(agent!);
        var client = new ForumClient(forum.Client, forum.Client.BaseAddress!);
        var session = new ForumSession(client, agent!, store, forum.Clock);
        return new ForumTools(client, MarkingMode.Datamark, new HeadStore(headHome ?? forum.Home), new ForumWriter(agent!, session, forum.Clock), clock);
    }

    private string NewHome()
    {
        var home = Directory.CreateTempSubdirectory("curia-waf-heads-").FullName;
        _homes.Add(home);
        return home;
    }

    private static string Text(CallToolResult result) => string.Join('\n', result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private static async Task<string> TextOfAsync(Func<Task<CallToolResult>> call)
    {
        try
        {
            var result = await call();
            return Fold((result.IsError == true ? "error: " : "") + Text(result));
        }
        catch (ModelContextProtocol.McpException refused)
        {
            return Fold("error: " + refused.Message);
        }
    }

    private sealed record Scene(string Asker, string Board, string Question, string AnswerA, string AnswerB);

    /// <summary>
    /// A question by a fresh T0 asker, and an answer to it by the T1 answerer (the asker is the only party who may accept one); a second answer when
    /// <paramref name="second"/>. The answerer's posting budget at T1 is 25 a day, so a probe asks for a second answer only where it needs one.
    /// </summary>
    private async Task<Scene> SceneAsync(bool second = false)
    {
        var answerer = ToolsFor(WordsAfterFaultFixture.Answerer);
        var asker = await EnrolAsync("waf-asker");
        var board = "waf-" + Guid.NewGuid().ToString("N")[..8];
        forum.SyncClock();
        var asked = await CuriaProcess.RunAsync(forum.Home, Ct, "ask", "--agent", asker, "--board", board, "--title", $"Which of two answers {Guid.NewGuid():N}?", "--body", "A question body.", "--forum", Address);
        Assert.True(asked.Exit == 0, asked.Stdout + asked.Stderr);
        var question = PostedLine().Match(asked.Stdout).Groups["id"].Value;

        string AnswerId(string text) => PostLine().Match(text) is { Success: true } m ? m.Groups[1].Value : throw new InvalidOperationException($"no post id in the answer receipt: {text}");
        var a = await TextOfAsync(() => answerer.AnswerAsync(question, $"The first answer {Guid.NewGuid():N}.", null, Ct));
        var b = second ? await TextOfAsync(() => answerer.AnswerAsync(question, $"The second answer {Guid.NewGuid():N}.", null, Ct)) : null;
        return new Scene(asker, board, question, AnswerId(a.Replace(" post ", "\npost ", StringComparison.Ordinal)), b is null ? string.Empty : AnswerId(b.Replace(" post ", "\npost ", StringComparison.Ordinal)));
    }

    private Task<(int Exit, string Text)> ResolveAsync(Scene scene, string answer) => CuriaAsync("resolve", answer, "--agent", scene.Asker, "--forum", Address);

    private Task<(int Exit, string Text)> FlagAsync(Scene scene, string post, string kind) =>
        CuriaAsync("flag", post, "--agent", scene.Asker, "--kind", kind, "--rationale", "a reason for the flag", "--forum", Address);

    private async Task<string> AskActAsync(string slug, EventStoreFault fault)
    {
        var asked = await ArmedAsync(fault, () => CuriaAsync("ask", "--agent", slug, "--board", "waf-act", "--title", $"An act {Guid.NewGuid():N}?", "--body", $"A body {Guid.NewGuid():N}", "--forum", Address));
        return asked.Text;
    }

    /// <summary>What the act line holds: the digest, or the problem with the text that was printed.</summary>
    private static string DigestOf(string printed, List<string> problems, string leg)
    {
        if (!printed.Contains(PublishedStoreFaults.StoreUnavailableType, StringComparison.Ordinal)) problems.Add($"{leg}: the act did not print {PublishedStoreFaults.StoreUnavailableType}: {printed}");
        var act = ActPattern().Match(printed);
        if (act.Success) return act.Groups["digest"].Value;
        problems.Add($"{leg}: the refusal prints no act line with a digest: {printed}");
        return string.Empty;
    }

    /// <summary>The R10.93 reading of an <c>unknown</c> line: its three clauses by their published starts, in order, and the wait.</summary>
    private static void CheckUnknown(string line, List<string> problems, string leg)
    {
        if (!line.StartsWith("unknown ", StringComparison.Ordinal)) problems.Add($"{leg}: the line does not begin with unknown: \"{line}\"");

        var starts = PublishedClientWords.UnknownClauses().Select(Fold).ToArray();
        var at = starts.Select(s => line.IndexOf(s, StringComparison.Ordinal)).ToArray();
        for (var i = 0; i < starts.Length; i++)
        {
            if (at[i] < 0) problems.Add($"{leg}: lacks \"{starts[i]}\"");
        }

        if (at.All(p => p >= 0) && !(at[0] < at[1] && at[1] < at[2])) problems.Add($"{leg}: the clauses are out of order ({string.Join(", ", at)})");
        if (!line.Contains(PublishedClientWords.Wait, StringComparison.Ordinal)) problems.Add($"{leg}: lacks the wait \"{PublishedClientWords.Wait}\"");
        if (!AnsweredAt().IsMatch(line)) problems.Add($"{leg}: states no instant in the published form");
    }

    private static string LineFor(string text, string digest)
    {
        var line = Regex.Match(text, @"(?<state>[a-z]+)\s+""?" + Regex.Escape(digest) + @"""?.*", RegexOptions.CultureInvariant);
        return line.Success ? line.Value : $"(no line names the digest) {text}";
    }

    // ----- Group A: the unknown line across the seam -----

    /// <summary>
    /// WAF1. Across the seam, an act that was not recorded, <c>curia</c>. <c>ask</c> under <c>ThrowOnAppend</c> prints <c>503 store/unavailable</c> and the act line; the
    /// digest is parsed from the printed act line; <c>recheck</c> of it prints a line that begins <c>unknown</c>, holds R10.93's three clauses in order, the wait and an
    /// instant; and the events table holds nothing.
    /// </summary>
    [Fact]
    public async Task R10_93_WAF1_AnActThatWasNotRecordedIsAnsweredUnknownWithTheResendClauseAndTheWait()
    {
        var problems = new List<string>();
        var slug = await EnrolAsync("waf1");
        var before = await EventsAsync();

        var asked = await AskActAsync(slug, EventStoreFault.ThrowOnAppend);
        var digest = DigestOf(asked, problems, "ask");
        var stored = await EventsAsync() - before;
        if (stored != 0) problems.Add($"ask: {stored} events were stored, expected 0");

        if (digest.Length > 0)
        {
            var rechecked = await CuriaAsync("recheck", digest, "--forum", Address);
            CheckUnknown(LineFor(rechecked.Text, digest), problems, "recheck");
        }

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    /// <summary>WAF2. The same through <c>curia-mcp</c>: <c>curia_ask</c>'s error text carries the act line, and <c>curia_recheck</c> of its digest answers the same <c>unknown</c> line.</summary>
    [Fact]
    public async Task R10_93_WAF2_TheSameThroughTheToolsAnActNotRecordedIsAnsweredUnknownWithTheResendClause()
    {
        var problems = new List<string>();
        var slug = await EnrolAsync("waf2");
        var tools = ToolsFor(slug);
        var before = await EventsAsync();

        var asked = await ArmedAsync(EventStoreFault.ThrowOnAppend, () => TextOfAsync(() => tools.AskAsync("waf-act", $"An act {Guid.NewGuid():N}?", $"A body {Guid.NewGuid():N}", null, null, Ct)));
        var digest = DigestOf(asked, problems, "curia_ask");
        var stored = await EventsAsync() - before;
        if (stored != 0) problems.Add($"curia_ask: {stored} events were stored, expected 0");

        if (digest.Length > 0)
        {
            var rechecked = await TextOfAsync(() => tools.RecheckAsync([digest], Ct));
            CheckUnknown(LineFor(rechecked, digest), problems, "curia_recheck");
        }

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    /// <summary>
    /// WAF3, a control (green on the seam tree; red under FWAF4). An act that was recorded: <c>AppendThenThrow</c> answers <c>503</c> and prints the act line; the
    /// events table holds the post once; <c>recheck</c> of the printed digest says <c>current</c> with the post id, and no clause of R10.93's <c>unknown</c> line is
    /// anywhere in it.
    /// </summary>
    [Fact]
    public async Task R10_93_WAF3_AnActThatWasRecordedIsCurrentAndCarriesNoResendClause()
    {
        var problems = new List<string>();
        var slug = await EnrolAsync("waf3");
        var before = await EventsAsync();

        var asked = await AskActAsync(slug, EventStoreFault.AppendThenThrow);
        var digest = DigestOf(asked, problems, "ask");
        var stored = await EventsAsync() - before;
        if (stored != 1) problems.Add($"ask: {stored} events were stored, expected 1");

        if (digest.Length > 0)
        {
            var rechecked = await CuriaAsync("recheck", digest, "--forum", Address);
            var line = LineFor(rechecked.Text, digest);
            if (!line.StartsWith("current ", StringComparison.Ordinal) || !line.Contains(" post ", StringComparison.Ordinal)) problems.Add($"recheck did not name the stored post: \"{line}\"");
            foreach (var start in PublishedClientWords.UnknownClauses().Select(Fold))
            {
                if (rechecked.Text.Contains(start, StringComparison.Ordinal)) problems.Add($"recheck of a recorded act carries \"{start}\"");
            }
        }

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    /// <summary>
    /// WAF32. A late commit, across the seam. The store throws the timeout-classified fault at once and commits when the test releases a gate: <c>ask</c> prints
    /// <c>503</c> and the act line; <c>recheck</c> of the parsed digest, before the release, prints <c>unknown</c> with the wait clause and the events table holds
    /// nothing; the test releases the gate and waits for the commit; the events table holds the post and <c>recheck</c> now says <c>current</c>. A line that said "may be
    /// sent again" inside the window would be false by the events table.
    /// </summary>
    [Fact]
    public async Task R10_93_WAF32_ALateCommitLandsAfterUnknownAndTheLineSaidItMight()
    {
        var problems = new List<string>();
        var slug = await EnrolAsync("waf32");
        var before = await EventsAsync();

        string asked;
        forum.Store.Arm(EventStoreFault.ThrowThenCommitWhenReleased);
        try
        {
            asked = (await CuriaAsync("ask", "--agent", slug, "--board", "waf-late", "--title", $"A late act {Guid.NewGuid():N}?", "--body", $"A body {Guid.NewGuid():N}", "--forum", Address)).Text;
            var digest = DigestOf(asked, problems, "ask");

            var early = await EventsAsync() - before;
            if (early != 0) problems.Add($"before the release: {early} events were stored, expected 0");

            if (digest.Length > 0)
            {
                var first = await CuriaAsync("recheck", digest, "--forum", Address);
                CheckUnknown(LineFor(first.Text, digest), problems, "before the release");

                forum.Store.ReleaseLateCommit();
                await forum.Store.LateCommitted.WaitAsync(TimeSpan.FromSeconds(30), Ct);

                var landed = await EventsAsync() - before;
                if (landed != 1) problems.Add($"after the release: {landed} events were stored, expected 1");
                var second = await CuriaAsync("recheck", digest, "--forum", Address);
                var line = LineFor(second.Text, digest);
                if (!line.StartsWith("current ", StringComparison.Ordinal)) problems.Add($"after the release: recheck says \"{line}\", not current");
            }
        }
        finally
        {
            forum.Store.ReleaseLateCommit();
            forum.Store.Disarm();
        }

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    /// <summary>
    /// WAF35. The instant, on both sides. <c>curia_recheck</c> built with a fake <see cref="TimeProvider"/> at two instants prints each (a <c>ForumTools</c> that ignores
    /// its clock is red); <c>curia recheck</c> prints an instant that lies, to the second, between the real clock read before the call and the one read after it
    /// (<c>Program.cs</c> holds no seam, so the probe brackets and does not compare for equality).
    /// </summary>
    [Fact]
    public async Task R10_93_WAF35_TheInstantIsTheReadersClockOnTheToolsAndTheCommand()
    {
        var problems = new List<string>();
        var digest = "sha256:" + new string('e', 64);
        var slug = await EnrolAsync("waf35");

        // The tools: a fake clock at two instants.
        foreach (var at in new[] { new DateTimeOffset(2026, 11, 1, 9, 30, 7, 912, TimeSpan.Zero), new DateTimeOffset(2027, 2, 3, 22, 4, 59, 1, TimeSpan.Zero) })
        {
            var tools = ToolsFor(slug, new ManualTimeProvider(at));
            var line = LineFor(await TextOfAsync(() => tools.RecheckAsync([digest], Ct)), digest);
            var expected = $"answered at {at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}";
            if (!line.Contains(expected, StringComparison.Ordinal)) problems.Add($"curia_recheck at {at:O}: lacks \"{expected}\"; printed \"{line}\"");
        }

        // The command: bracketed by the real clock.
        var floor = DateTimeOffset.UtcNow;
        var run = await CuriaAsync("recheck", digest, "--forum", Address);
        var ceiling = DateTimeOffset.UtcNow;
        if (AnsweredAt().Match(run.Text) is { Success: true } printed)
        {
            var instant = DateTimeOffset.Parse(printed.Groups["instant"].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            var lowest = new DateTimeOffset(floor.Year, floor.Month, floor.Day, floor.Hour, floor.Minute, floor.Second, TimeSpan.Zero);
            if (instant < lowest || instant > ceiling) problems.Add($"curia recheck printed {printed.Groups["instant"].Value}, outside the real clock's reads {lowest:O} and {ceiling:O}");
        }
        else
        {
            problems.Add($"curia recheck states no instant in the published form: \"{run.Text}\"");
        }

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    // ----- Group B: the repeat's reading -----

    private static bool Prints(string text, string phrase) => text.Contains(Fold(phrase), StringComparison.Ordinal);

    /// <summary>
    /// WAF10. <c>curia resolve</c>, the pair, both sides. The phrase is read from R11.73's pairs. A <c>resolve</c> that the store refused prints the store sentence, which
    /// holds the phrase; a recording <c>resolve</c> prints a receipt without it; the repeat of it prints it.
    /// </summary>
    [Fact]
    public async Task R11_73_WAF10_CuriaResolvePrintsThePhraseOnlyForTheRepeatAndTheSentenceHoldsIt()
    {
        var problems = new List<string>();
        var phrase = PublishedClientWords.ResolveReplayPhrase;
        var scene = await SceneAsync();
        var before = await EventsAsync();

        var refused = await ArmedAsync(EventStoreFault.ThrowOnAppend, () => ResolveAsync(scene, scene.AnswerA));
        if (!Prints(refused.Text, phrase)) problems.Add($"the store sentence lacks \"{phrase}\"; printed \"{refused.Text}\"");
        if (await EventsAsync() != before) problems.Add("the refused resolve stored an event");

        var recorded = await ResolveAsync(scene, scene.AnswerA);
        if (recorded.Exit != 0 || Prints(recorded.Text, phrase)) problems.Add($"a recording resolve printed the phrase or failed: exit {recorded.Exit}, \"{recorded.Text}\"");
        if (await EventsAsync() != before + 1) problems.Add("the recording resolve did not store exactly one event");

        var repeated = await ResolveAsync(scene, scene.AnswerA);
        if (repeated.Exit != 0 || !Prints(repeated.Text, phrase)) problems.Add($"the repeat did not print the phrase: exit {repeated.Exit}, \"{repeated.Text}\"");
        if (await EventsAsync() != before + 1) problems.Add("the repeat stored an event");

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    /// <summary>WAF11. <c>curia_resolve</c>, the same, both sides.</summary>
    [Fact]
    public async Task R11_73_WAF11_CuriaResolveToolPrintsThePhraseOnlyForTheRepeatAndTheSentenceHoldsIt()
    {
        var problems = new List<string>();
        var phrase = PublishedClientWords.ResolveReplayPhrase;
        var scene = await SceneAsync();
        var tools = ToolsFor(scene.Asker);
        var before = await EventsAsync();

        var refused = await ArmedAsync(EventStoreFault.ThrowOnAppend, () => TextOfAsync(() => tools.ResolveAsync(scene.AnswerA, Ct)));
        if (!Prints(refused, phrase)) problems.Add($"the store sentence lacks \"{phrase}\"; relayed \"{refused}\"");

        var recorded = await TextOfAsync(() => tools.ResolveAsync(scene.AnswerA, Ct));
        if (recorded.StartsWith("error", StringComparison.Ordinal) || Prints(recorded, phrase)) problems.Add($"a recording resolve printed the phrase or failed: \"{recorded}\"");
        if (await EventsAsync() != before + 1) problems.Add("the recording resolve did not store exactly one event");

        var repeated = await TextOfAsync(() => tools.ResolveAsync(scene.AnswerA, Ct));
        if (!Prints(repeated, phrase)) problems.Add($"the repeat did not print the phrase: \"{repeated}\"");
        if (await EventsAsync() != before + 1) problems.Add("the repeat stored an event");

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    /// <summary>
    /// WAF12. <c>curia flag</c> and <c>curia_flag</c>, the pair, both sides. A flag the store refused prints the store sentence, which holds the phrase; the repeat of a
    /// recorded flag is refused in the phrase; a flag that is recorded prints a receipt without it.
    /// </summary>
    [Fact]
    public async Task R11_73_WAF12_FlagPrintsThePhraseOnlyForTheRepeatAndTheSentenceHoldsIt()
    {
        var problems = new List<string>();
        var phrase = PublishedClientWords.FlagRepeatPhrase;
        var scene = await SceneAsync(second: true);
        var tools = ToolsFor(scene.Asker);

        // The command.
        var refused = await ArmedAsync(EventStoreFault.AppendThenThrow, () => FlagAsync(scene, scene.AnswerA, "spam"));
        if (!Prints(refused.Text, phrase)) problems.Add($"curia flag: the store sentence lacks \"{phrase}\"; printed \"{refused.Text}\"");
        var repeated = await FlagAsync(scene, scene.AnswerA, "spam");
        if (!Prints(repeated.Text, phrase)) problems.Add($"curia flag: the repeat of a recorded flag was not refused in the phrase: exit {repeated.Exit}, \"{repeated.Text}\"");
        var fresh = await FlagAsync(scene, scene.AnswerB, "spam");
        if (fresh.Exit != 0 || Prints(fresh.Text, phrase)) problems.Add($"curia flag: a recorded flag printed the phrase or failed: exit {fresh.Exit}, \"{fresh.Text}\"");

        // The tool.
        var toolRefused = await ArmedAsync(EventStoreFault.AppendThenThrow, () => TextOfAsync(() => tools.FlagAsync(scene.AnswerA, "duplicate", "a reason for the flag", Ct)));
        if (!Prints(toolRefused, phrase)) problems.Add($"curia_flag: the store sentence lacks \"{phrase}\"; relayed \"{toolRefused}\"");
        var toolRepeated = await TextOfAsync(() => tools.FlagAsync(scene.AnswerA, "duplicate", "a reason for the flag", Ct));
        if (!Prints(toolRepeated, phrase)) problems.Add($"curia_flag: the repeat of a recorded flag was not refused in the phrase: \"{toolRepeated}\"");
        var toolFresh = await TextOfAsync(() => tools.FlagAsync(scene.AnswerB, "duplicate", "a reason for the flag", Ct));
        if (toolFresh.StartsWith("error", StringComparison.Ordinal) || Prints(toolFresh, phrase)) problems.Add($"curia_flag: a recorded flag printed the phrase or failed: \"{toolFresh}\"");

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    /// <summary>
    /// WAF13. The reading is true, end to end, four legs. For a resolve and for a flag, under <c>AppendThenThrow</c> and under <c>ThrowOnAppend</c>: the first call is
    /// refused and prints the store sentence, which holds the phrase of the errata's pair; the repeat's print is compared with the pair; and the meaning the errata gives
    /// ("the first call was recorded") is compared with the events table: one event after a commit and its repeat, one event made by the repeat after no commit.
    /// </summary>
    [Fact]
    public async Task R11_73_WAF13_TheMeaningTheErrataGivesAgreesWithTheEventsTableInFourLegs()
    {
        var problems = new List<string>();
        var pairs = PublishedClientWords.ReadingPairs();

        foreach (var (verb, phrase, meaning) in new[] { ("resolve", pairs[0].Phrase, pairs[0].Meaning), ("flag", pairs[1].Phrase, pairs[1].Meaning) })
        {
            foreach (var (fault, committed) in new[] { (EventStoreFault.AppendThenThrow, true), (EventStoreFault.ThrowOnAppend, false) })
            {
                var leg = $"{verb} under {fault}";
                var scene = await SceneAsync();
                Task<(int Exit, string Text)> Call() => verb == "resolve" ? ResolveAsync(scene, scene.AnswerA) : FlagAsync(scene, scene.AnswerA, "spam");

                var before = await EventsAsync();
                var first = await ArmedAsync(fault, Call);
                var afterFault = await EventsAsync() - before;
                if (first.Exit != CliExit.ForumFault) problems.Add($"{leg}: the first call exited {first.Exit}, not {CliExit.ForumFault}");
                if (!first.Text.Contains(PublishedStoreFaults.StoreUnavailableType, StringComparison.Ordinal)) problems.Add($"{leg}: the first call did not answer {PublishedStoreFaults.StoreUnavailableType}: \"{first.Text}\"");
                if (!Prints(first.Text, phrase)) problems.Add($"{leg}: the store sentence lacks \"{phrase}\"");
                if (afterFault != (committed ? 1 : 0)) problems.Add($"{leg}: the faulted call stored {afterFault} events, expected {(committed ? 1 : 0)}");

                var repeat = await Call();
                var total = await EventsAsync() - before;
                var printedRecorded = Prints(repeat.Text, phrase);
                if (printedRecorded != committed) problems.Add($"{leg}: the repeat {(printedRecorded ? "printed" : "did not print")} \"{phrase}\" but the first call {(committed ? "was" : "was not")} recorded");

                // The errata's meaning ("the first call was recorded") against the events table, whichever way the repeat was read.
                var readAsRecorded = printedRecorded && meaning.Contains("recorded", StringComparison.Ordinal);
                if (readAsRecorded != (afterFault == 1)) problems.Add($"{leg}: read as {(readAsRecorded ? "recorded" : "not recorded")} while the events table holds {afterFault} event(s) from the first call");
                if (total != 1) problems.Add($"{leg}: {total} events in all after the repeat, expected exactly 1");
            }
        }

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    /// <summary>
    /// WAF14. The condition is stated and is true. Resolve B under <c>AppendThenThrow</c> (<c>503</c>, one event); resolve A (recorded, B replaced); resolve B again: the
    /// print is a move to B and lacks the replay phrase though the first call was recorded. The first call's printed sentence holds the condition R11.73 publishes
    /// (that no other call changed the thread's accepted answer between the two calls).
    /// </summary>
    [Fact]
    public async Task R11_73_WAF14_TheReadingHoldsOnlyIfNoOtherCallChangedTheAcceptedAnswer()
    {
        var problems = new List<string>();
        var scene = await SceneAsync(second: true);
        var before = await EventsAsync();

        var first = await ArmedAsync(EventStoreFault.AppendThenThrow, () => ResolveAsync(scene, scene.AnswerB));
        if (!Prints(first.Text, PublishedClientWords.ResolveCondition)) problems.Add($"the store sentence lacks \"{PublishedClientWords.ResolveCondition}\"; printed \"{first.Text}\"");
        if (await EventsAsync() - before != 1) problems.Add("the faulted resolve of B did not leave one event (the leg does not hold the case)");

        var other = await ResolveAsync(scene, scene.AnswerA);
        if (other.Exit != 0) problems.Add($"resolve A failed: \"{other.Text}\"");

        var again = await ResolveAsync(scene, scene.AnswerB);
        if (again.Exit != 0) problems.Add($"resolve B again failed: \"{again.Text}\"");
        if (Prints(again.Text, PublishedClientWords.ResolveReplayPhrase)) problems.Add("resolve B again printed the replay phrase though A replaced it in between");
        if (await EventsAsync() - before != 3) problems.Add($"{await EventsAsync() - before} events in all, expected 3 (B, then A, then B again)");

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    /// <summary>
    /// WAF34, a control (green on the seam tree; red under a Forum that appends before it checks). A replay of a standing answer never answers
    /// <c>store/unavailable</c>: with A accepted, resolve A under <c>ThrowOnAppend</c> and under <c>AppendThenThrow</c> answers <c>200</c> and the replay phrase both
    /// times, no <c>503</c>, and the events table is unchanged. If the Forum ever answers a replay so, the unreachable condition must come back into the sentence.
    /// </summary>
    [Fact]
    public async Task R11_73_WAF34_AReplayOfAStandingAnswerNeverAnswersStoreUnavailable()
    {
        var problems = new List<string>();
        var scene = await SceneAsync();
        var accepted = await ResolveAsync(scene, scene.AnswerA);
        Assert.True(accepted.Exit == 0, accepted.Text);
        var before = await EventsAsync();

        foreach (var fault in new[] { EventStoreFault.ThrowOnAppend, EventStoreFault.AppendThenThrow })
        {
            var replay = await ArmedAsync(fault, () => ResolveAsync(scene, scene.AnswerA));
            if (replay.Exit != 0) problems.Add($"{fault}: the replay exited {replay.Exit}: \"{replay.Text}\"");
            if (replay.Text.Contains(PublishedStoreFaults.StoreUnavailableType, StringComparison.Ordinal)) problems.Add($"{fault}: the replay answered {PublishedStoreFaults.StoreUnavailableType}");
            if (!Prints(replay.Text, PublishedClientWords.ResolveReplayPhrase)) problems.Add($"{fault}: the replay did not print the replay phrase: \"{replay.Text}\"");
        }

        if (await EventsAsync() != before) problems.Add("a replay changed the events table");
        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    /// <summary>
    /// WAF15. "Any other kind says nothing" is true. A flag repeated with the flag budget spent is refused for the budget with no "already raised" phrase (the budget
    /// wins), and the store sentence says an answer of any other kind says nothing of the first call.
    /// </summary>
    [Fact]
    public async Task R11_73_WAF15_ARefusalOfAnotherKindSaysNothingOfTheFirstCall()
    {
        var problems = new List<string>();
        var sentence = Fold(StoreWording.Sentence(StoreWording.StoreUnavailableType));
        if (!sentence.Contains(Fold(PublishedClientWords.OtherKindClause), StringComparison.Ordinal)) problems.Add($"the store sentence lacks \"{PublishedClientWords.OtherKindClause}\"");

        var scene = await SceneAsync(second: true);
        var tools = ToolsFor(scene.Asker);
        string[] kinds = ["injection", "credential_leak", "incorrect", "spam", "duplicate", "license_violation", "malicious_code"];
        var spent = 0;
        foreach (var (post, kind) in kinds.Select(k => (scene.AnswerA, k)).Concat(kinds.Take(3).Select(k => (scene.AnswerB, k))))
        {
            var flagged = await TextOfAsync(() => tools.FlagAsync(post, kind, "a reason for the flag", Ct));
            if (!flagged.StartsWith("error", StringComparison.Ordinal)) spent++;
        }

        if (spent != 10) problems.Add($"{spent} flags were raised, expected 10 to spend the budget");
        var repeat = await TextOfAsync(() => tools.FlagAsync(scene.AnswerA, "spam", "a reason for the flag", Ct));
        if (!repeat.Contains("flag-budget-exhausted", StringComparison.Ordinal)) problems.Add($"the repeat with the budget spent was not refused for the budget: \"{repeat}\"");
        if (Prints(repeat, PublishedClientWords.FlagRepeatPhrase)) problems.Add("the budget refusal says the flag was already raised");

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    // ----- Group C: the verification's lines against a real signed head -----

    private async Task SignHeadAsync()
    {
        forum.SyncClock();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await OperatorCommands.RunAsync(
            ["sign-head", "--by", "ops"], forum.ConnectionString, forum.Clock, forum.PermissiveGate(), OperatorCommands.LoadLogSigner, stdout, stderr, Ct, readLogSigningKeyPem: () => LogKeyPem);
        Assert.True(exit == 0, stderr.ToString());
    }

    /// <summary>T0 askers, enrolled before a head is signed (an enrolment is an event, and the probes want the post right after the head), three posts each.</summary>
    private async Task<Queue<string>> PostersAsync(int posts)
    {
        var queue = new Queue<string>();
        for (var made = 0; made < posts; made += 3)
        {
            var slug = await EnrolAsync("waf-poster");
            for (var i = 0; i < Math.Min(3, posts - made); i++) queue.Enqueue(slug);
        }

        return queue;
    }

    private async Task<string> PostQuestionAsync(Queue<string> posters)
    {
        forum.SyncClock();
        var asked = await CuriaProcess.RunAsync(forum.Home, Ct, "ask", "--agent", posters.Dequeue(), "--board", "waf-head", "--title", $"A question for the head {Guid.NewGuid():N}?", "--body", $"A body {Guid.NewGuid():N}", "--forum", Address);
        Assert.True(asked.Exit == 0, asked.Stdout + asked.Stderr);
        return PostedLine().Match(asked.Stdout).Groups["id"].Value;
    }

    private static async Task<string> VerifiedTextAsync(ForumTools tools, string postId)
    {
        _ = await tools.ReadAsync(postId, Ct);
        return Fold(Text(await tools.VerifyAsync(postId, null, Ct)));
    }

    /// <summary>
    /// WAF18. Concurrent, with overlap. The test takes the retained head's lock itself, starts fourteen <c>curia_verify</c> calls against a Forum with one head, waits
    /// until all fourteen have fetched the post's log entry (the last request before the lock), asserts that none has completed while it holds the lock, then releases
    /// it: exactly one line says no earlier head was retained; every other equal-head line states <c>retained at</c> the instant the file records, in a form that is not
    /// the seconds form (they all began before the head was stored), and none says "retained earlier".
    /// </summary>
    [Fact]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The counting handler owns the inner handler and is disposed with the client's lifetime by the using declaration.")]
    public async Task R11_74_WAF18_FourteenOverlappingVerificationsAllStateWhenTheHeadWasStored()
    {
        var problems = new List<string>();
        var postId = await PostQuestionAsync(await PostersAsync(1));
        await SignHeadAsync();

        var home = NewHome();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entryReads = 0;
        using var handler = new CountingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.StartsWith("/v1/log/entries/", StringComparison.Ordinal) && Interlocked.Increment(ref entryReads) >= 14) reached.TrySetResult();
        });
        using var http = new HttpClient(handler) { BaseAddress = forum.Client.BaseAddress };
        var client = new ForumClient(http, forum.Client.BaseAddress!);
        var tools = new ForumTools(client, MarkingMode.Datamark, new HeadStore(home), null);
        _ = await tools.ReadAsync(postId, Ct);
        entryReads = 0;

        var heads = new HeadStore(home);
        var held = await heads.LockAsync(forum.Client.BaseAddress!, TimeSpan.FromSeconds(30), TimeProvider.System, Ct);
        Assert.True(held.TryGetValue(out var lease, out var lockRefusal), lockRefusal?.Detail);

        var calls = Enumerable.Range(0, 14).Select(_ => Task.Run(async () => Fold(Text(await tools.VerifyAsync(postId, null, Ct))), Ct)).ToArray();
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        var completed = calls.Count(c => c.IsCompleted);
        if (completed != 0) problems.Add($"{completed} of 14 verifications completed while the lock was held, so the overlap is vacuous");
        lease!.Dispose();

        var texts = await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(120), Ct);
        var consistency = texts.Select(t => Regex.Match(t, @"consistency .*?(?= key |$)", RegexOptions.CultureInvariant).Value).ToArray();
        var none = consistency.Count(c => c.Contains("retained no earlier head", StringComparison.Ordinal));
        if (none != 1) problems.Add($"{none} lines say no earlier head was retained, expected exactly one");

        var retainedAt = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(heads.DirectoryFor(forum.Client.BaseAddress!), "head.json"), Ct))?["retained_at"]?.GetValue<string>();
        foreach (var line in consistency.Where(c => !c.Contains("retained no earlier head", StringComparison.Ordinal)))
        {
            if (line.Contains("retained earlier", StringComparison.Ordinal)) problems.Add($"a line says \"retained earlier\": \"{line}\"");
            else if (retainedAt is null || !line.Contains($"retained at {retainedAt}", StringComparison.Ordinal)) problems.Add($"a line lacks \"retained at {retainedAt ?? "(no retained_at in head.json)"}\": \"{line}\"");

            var seconds = Regex.Match(line, @"\(\d+ s before this check began", RegexOptions.CultureInvariant);
            if (seconds.Success) problems.Add($"a line carries the seconds form: \"{seconds.Value}\"");
        }

        Assert.True(problems.Count == 0, string.Join('\n', problems.Distinct(StringComparer.Ordinal).Take(8)) + (problems.Count > 8 ? $"\n... {problems.Count} problems in all" : string.Empty));
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The delegating handler owns its inner handler and disposes it with itself.")]
    private sealed class CountingHandler(Action<HttpRequestMessage> seen) : DelegatingHandler(new SocketsHttpHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            seen(request);
            return base.SendAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// WAF20. Past the head, at the first leaf and beyond it. A head is signed; the first post after it is at the leaf after the head's own event, a later one three
    /// leaves on. For each, <c>curia verify</c> exits 8 and both clients' inclusion line holds R11.74's two fragments with <c>&lt;m&gt;</c> equal to the Forum's own head
    /// route's tree size, and <c>curia_verify</c>'s closing holds the published cause with that tree size.
    /// </summary>
    [Fact]
    public async Task R11_74_WAF20_APostPastTheHeadNamesTheLeafTheTreeSizeAndTheCause()
    {
        var problems = new List<string>();
        var posters = await PostersAsync(4);
        await SignHeadAsync();
        var posts = new List<string>();
        for (var i = 0; i < 4; i++) posts.Add(await PostQuestionAsync(posters));

        var client = new ForumClient(forum.Client, forum.Client.BaseAddress!);
        var head = await client.GetLogHeadAsync(Ct);
        Assert.True(head.TryGetValue(out var served, out var headRefusal), headRefusal?.Summary);
        var size = served!.TreeSize.ToString(CultureInfo.InvariantCulture);

        var fragments = PublishedClientWords.InclusionFragments();
        foreach (var post in new[] { posts[0], posts[3] })
        {
            var tool = await VerifiedTextAsync(ToolsFor(WordsAfterFaultFixture.Answerer, headHome: NewHome()), post);
            var leaf = LogIndexLine().Match(tool).Groups[1].Value;
            var expectedLeaf = Fold(fragments[0].Replace("<n>", leaf, StringComparison.Ordinal).Replace("<m>", size, StringComparison.Ordinal));
            var closing = Fold(PublishedClientWords.PastHeadClosing.Replace("<n>", size, StringComparison.Ordinal));

            if (!tool.Contains(expectedLeaf, StringComparison.Ordinal)) problems.Add($"curia_verify of {post}: the inclusion line lacks \"{expectedLeaf}\"");
            if (!tool.Contains(Fold(fragments[1]), StringComparison.Ordinal)) problems.Add($"curia_verify of {post}: the inclusion line lacks \"{fragments[1]}\"");
            if (!tool.Contains(closing, StringComparison.Ordinal)) problems.Add($"curia_verify of {post}: lacks the closing \"{closing}\"");

            var cli = await CuriaAsync("verify", post, "--forum", Address);
            if (cli.Exit != CliExit.ForumFault) problems.Add($"curia verify of {post} exited {cli.Exit}, not {CliExit.ForumFault}");
            if (!cli.Text.Contains(expectedLeaf, StringComparison.Ordinal)) problems.Add($"curia verify of {post}: the inclusion line lacks \"{expectedLeaf}\"");
            if (!cli.Text.Contains(Fold(fragments[1]), StringComparison.Ordinal)) problems.Add($"curia verify of {post}: the inclusion line lacks \"{fragments[1]}\"");
        }

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    /// <summary>
    /// WAF21, a control (green on the seam tree). Both sides of the boundary. After the next head is signed the same post verifies (<c>VERIFIED.</c>, no cause words);
    /// and a post at the last covered leaf under its head (the head's own event follows it) is verified with no cause words either.
    /// </summary>
    [Fact]
    public async Task R11_74_WAF21_APostTheHeadCoversCarriesNoCauseWords()
    {
        var problems = new List<string>();
        const string Cause = "newer than the latest signed head";
        var posters = await PostersAsync(2);

        await SignHeadAsync();
        var past = await PostQuestionAsync(posters);
        var uncovered = await VerifiedTextAsync(ToolsFor(WordsAfterFaultFixture.Answerer, headHome: NewHome()), past);
        if (!uncovered.Contains(Cause, StringComparison.Ordinal) && !uncovered.Contains("is not covered by the signed head", StringComparison.Ordinal)) problems.Add("the post past the head says nothing of the head (the leg does not hold the case)");

        await SignHeadAsync();
        var covered = await VerifiedTextAsync(ToolsFor(WordsAfterFaultFixture.Answerer, headHome: NewHome()), past);
        if (!covered.Contains("VERIFIED.", StringComparison.Ordinal) || covered.Contains(Cause, StringComparison.Ordinal)) problems.Add($"after the next head: not VERIFIED, or cause words present: \"{covered}\"");

        var last = await PostQuestionAsync(posters);
        await SignHeadAsync();
        var lastCovered = await VerifiedTextAsync(ToolsFor(WordsAfterFaultFixture.Answerer, headHome: NewHome()), last);
        if (lastCovered.Contains(Cause, StringComparison.Ordinal) || !lastCovered.Contains("VERIFIED.", StringComparison.Ordinal)) problems.Add($"the last covered leaf: not VERIFIED, or cause words present: \"{lastCovered}\"");

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }
}
