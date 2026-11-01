using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using Curia.Client;
using Curia.Domain.Authorization;
using Curia.Domain.Content;
using Curia.Domain.Serving;
using Curia.Mcp;
using Curia.Tests.Shared;
using Npgsql;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// ULG4, ULG39 (errata G52, R11.71): the facts that need a real socket, on the Forum hosted on Kestrel (Development) with the decorated store. The plan's
/// client-parser clause of ULG4 is not asserted: the body being a problem document is what a parser reads.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed partial class StoreFaultKestrelTests(FaultableKestrelFixture forum) : IClassFixture<FaultableKestrelFixture>, IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _home = Directory.CreateTempSubdirectory("curia-store-fault-act-").FullName;

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    private static string Fold(string text) => Whitespace().Replace(text, " ");

    private async Task<(int Exit, string Text)> CuriaAsync(params string[] args)
    {
        var run = await CuriaProcess.RunAsync(_home, Ct, args);
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

    /// <summary>The state word of the line <c>curia recheck</c> printed for <paramref name="digest"/>, and the rest of that line.</summary>
    private static (string State, string Line) RecheckLine(string text, string digest)
    {
        var line = Regex.Match(text, @"(?<state>[a-z]+)\s+""?" + Regex.Escape(digest) + @"""?(?<rest>.*)", RegexOptions.CultureInvariant);
        return line.Success ? (line.Groups["state"].Value, line.Value) : ("no line names the digest", text);
    }

    /// <summary>
    /// ULG58. The act line is true, both sides, end to end, with the real <c>curia</c> against the Forum on Kestrel with the decorated store. (a) The append
    /// commits and then throws: <c>ask</c> prints <c>503 curia/store/unavailable</c> and a digest, one event is stored, and <c>recheck</c> of that digest names
    /// the post. (b) The append throws and commits nothing: <c>ask</c> prints a digest, no event is stored, and <c>recheck</c> of it answers <c>unknown</c>.
    /// </summary>
    [Fact]
    public async Task R11_72_ULG58_TheActLineIsTrueOnBothSidesOfACommit()
    {
        var address = forum.Client.BaseAddress!.GetLeftPart(UriPartial.Authority);
        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now);
        var enrolled = await CuriaAsync("enrol", "--agent", "acting", "--forum", address);
        Assert.True(enrolled.Exit == 0, enrolled.Text);

        var problems = new List<string>();
        foreach (var (leg, fault, stored) in new[] { ("a: committed then cut", EventStoreFault.AppendThenThrow, 1L), ("b: nothing committed", EventStoreFault.ThrowOnAppend, 0L) })
        {
            var before = await EventsAsync();
            (int Exit, string Text) asked;
            forum.Store.Arm(fault);
            try
            {
                asked = await CuriaAsync("ask", "--agent", "acting", "--board", "act-line", "--title", $"Leg {leg[0]} {Guid.NewGuid():N}", "--body", $"A body for leg {leg[0]} {Guid.NewGuid():N}", "--forum", address);
            }
            finally
            {
                forum.Store.Disarm();
            }

            var stored_ = await EventsAsync() - before;
            if (stored_ != stored) problems.Add($"{leg}: {stored_} events were stored, expected {stored}");
            if (!asked.Text.Contains(PublishedStoreFaults.StoreUnavailableType, StringComparison.Ordinal)) problems.Add($"{leg}: ask did not print {PublishedStoreFaults.StoreUnavailableType}: {asked.Text}");

            var act = ActPattern().Match(asked.Text);
            if (!act.Success)
            {
                problems.Add($"{leg}: lacks \"The act sent\" with a digest: {asked.Text}");
                continue;
            }

            var digest = act.Groups["digest"].Value;
            var rechecked = await CuriaAsync("recheck", digest, "--forum", address);
            var (state, line) = RecheckLine(rechecked.Text, digest);
            if (stored == 1 && (state != "current" || !line.Contains("post ", StringComparison.Ordinal) || line.Contains("no post id served", StringComparison.Ordinal)))
                problems.Add($"{leg}: recheck of the printed digest did not name the stored post: {rechecked.Text}");
            if (stored == 0 && state != "unknown") problems.Add($"{leg}: recheck of the printed digest of an act never stored answered {state}: {rechecked.Text}");
        }

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    private async Task<Reply> GetAsync(string path, CancellationToken token, string? probe = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (probe is not null) request.Headers.Add(FaultableForumFixture.ProbeHeader, probe);
        return await Reply.SendAsync(forum.Client, request, token);
    }

    /// <summary>
    /// ULG4. An exception nothing names, over the wire: <c>500</c> <c>curia/server/fault</c> as a JSON problem document with a body (and not a body-less answer
    /// that a client reports as an unreadable problem), no word of the exception in it.
    /// </summary>
    [Fact]
    public async Task R11_71_ULG4_AnUnclassifiedExceptionIsAProblemDocumentOverTheWire()
    {
        const string Sentinel = "kestrel-unclassified-bug";
        forum.Store.Arm(EventStoreFault.ThrowReads, () => StoreFaultKinds.Unclassified(Sentinel));
        Reply reply;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/boards/some-board/posts");
            reply = await Reply.SendAsync(forum.Client, request, Ct);
        }
        finally
        {
            forum.Store.Disarm();
        }

        var problems = new List<string>();
        if (!FaultSweep.IsProblem(reply, PublishedStoreFaults.FaultStatus, PublishedStoreFaults.FaultType))
            problems.Add($"expected {PublishedStoreFaults.FaultStatus} {PublishedStoreFaults.FaultType} as a problem document, actual {reply.Says}");
        else if (reply.Title != PublishedStoreFaults.FaultTitle)
            problems.Add($"expected the title \"{PublishedStoreFaults.FaultTitle}\", actual \"{reply.Title}\"");
        if (reply.Body.Contains(Sentinel, StringComparison.Ordinal)) problems.Add("the body carries the exception's text");

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    /// <summary>
    /// ULG39. The cancellation is the caller's: a request the client aborts mid-read is not logged as a store fault and is not answered. The positive is in the
    /// same fact: the same read with a fault served logs exactly one <c>StoreFault</c> event, so a Forum that logs nothing at all is red. The limb reads the
    /// log only after the host itself has finished with the aborted request (the host's own completion signal, bounded), so a Forum that logs after the store
    /// has thrown is seen, whatever the order the two threads run in; no event of the middleware's, and no server-fault line, may be among them.
    /// </summary>
    [Fact]
    public async Task R11_71_ULG39_ARequestTheClientAbortsMidReadIsNotAStoreFault()
    {
        var problems = new List<string>();

        // The positive: a fault the Forum serves is logged once as StoreFault.
        var mark = forum.Log.Mark();
        forum.Store.Arm(EventStoreFault.ThrowUnreadable);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/boards/some-board/posts");
            var served = await Reply.SendAsync(forum.Client, request, Ct);
            if (served.Status != PublishedStoreFaults.UnavailableStatus) problems.Add($"a served fault: expected {PublishedStoreFaults.UnavailableStatus}, actual {served.Says}");
        }
        finally
        {
            forum.Store.Disarm();
        }

        var servedFaults = forum.Log.Since(mark).Count(e => e.EventId == PublishedStoreFaults.EventId);
        if (servedFaults != 1) problems.Add($"a served fault logged {servedFaults} {PublishedStoreFaults.EventName} events, not one");

        // The limit: the client goes away while the Forum is reading.
        mark = forum.Log.Mark();
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        forum.Store.Arm(EventStoreFault.HangUntilCancelled);
        try
        {
            var probe = Guid.NewGuid().ToString("N");
            var sending = GetAsync("/v1/boards/some-board/posts", abort.Token, probe);
            await forum.Store.HangStarted.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            await abort.CancelAsync();
            await forum.Store.HangEnded.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            await forum.Completion(probe).WaitAsync(TimeSpan.FromSeconds(30), Ct);
            try
            {
                _ = await sending;
            }
            catch (OperationCanceledException)
            {
                // The client's own abort, which is the point: it is not an answer.
            }
        }
        finally
        {
            forum.Store.Disarm();
        }

        var abortedFaults = forum.Log.Since(mark)
            .Where(e => e.EventId == PublishedStoreFaults.EventId || e.EventId == PublishedStoreFaults.ServerFaultEventId || e.Category.EndsWith(nameof(StoreFaultsMiddleware), StringComparison.Ordinal))
            .ToList();
        if (abortedFaults.Count != 0)
            problems.Add($"{abortedFaults.Count} fault event(s) were logged for a cancelled request: {string.Join(", ", abortedFaults.Select(e => $"{e.EventId} {e.Name}"))}");

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }
}

/// <summary>
/// ULG59 (errata G52, non-property k, D99): the status quo that makes the act-line words necessary is pinned and not hidden. Through the real client two answers
/// with the same body and different envelopes are two <c>201</c>s and two stored posts, two comments likewise; the same signed bytes sent twice are <c>201</c> and
/// then <c>409 curia/ingest/already-recorded</c> (R8.78, the control). The pin flips when D99 closes.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class StoreFaultStatusQuoTests(FaultableKestrelFixture forum) : IClassFixture<FaultableKestrelFixture>, IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("curia-store-fault-pin-").FullName;
    private readonly List<IDisposable> _owned = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private async Task<long> EventsAsync()
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM events;", admin);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task R11_72_ULG59_TheSameBodyInTwoEnvelopesIsStoredTwiceAndTheSameBytesAreRefused()
    {
        var address = forum.Client.BaseAddress!.GetLeftPart(UriPartial.Authority);
        var tenure = TimeSpan.FromHours(TierPolicy.T1MinimumHours + 1);

        // An answerer enrolled and warmed up as if 49 hours ago, through the reference client's own writer (as CliParentEndToEndTests does).
        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now - tenure);
        var store = new ProfileStore(_home);
        Assert.True(store.Create("answerer", "https://agents.example/answerer", "answerer-k", forum.Client.BaseAddress!).TryGetValue(out var agent, out var created), created?.Detail);
        _owned.Add(agent!);
        var client = new ForumClient(forum.Client, forum.Client.BaseAddress!);
        Assert.True((await client.EnrolAsync(agent!, Ct)).TryGetValue(out _, out var refusal), refusal?.Summary);
        await forum.AttestOwnerAsync(agent!.Profile.AgentId, Ct, owner: "owner:answerer");

        var session = new ForumSession(client, agent, store, forum.Clock);
        var tools = new ForumTools(client, MarkingMode.Datamark, new HeadStore(_home), new ForumWriter(agent, session, forum.Clock));
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];
        for (var i = 0; i < TierPolicy.T1MinimumCleanQuestions; i++)
            await tools.AskAsync(board, $"Warmup {i} {Guid.NewGuid():N}", $"warmup body {i} {Guid.NewGuid():N}", null, null, Ct);

        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now);
        var asked = await CuriaProcess.RunAsync(_home, Ct, "enrol", "--agent", "asker-pin", "--forum", address);
        Assert.True(asked.Exit == 0, asked.Stdout + asked.Stderr);
        var question = await CuriaProcess.RunAsync(_home, Ct, "ask", "--agent", "asker-pin", "--board", board, "--title", "Which entry does this pin", "--body", "a question body", "--forum", address);
        Assert.True(question.Exit == 0, question.Stdout + question.Stderr);
        var questionId = Regex.Match(question.Stdout, @"^posted\s+""?([^\s""]+)", RegexOptions.Multiline | RegexOptions.CultureInvariant).Groups[1].Value;

        var problems = new List<string>();
        async Task Twice(string name, Func<string, Task<ModelContextProtocol.Protocol.CallToolResult>> send)
        {
            var before = await EventsAsync();
            var first = await send("The same body, twice.");
            forum.Clock.Advance(TimeSpan.FromSeconds(2));
            var second = await send("The same body, twice.");
            var stored = await EventsAsync() - before;
            if ((first.IsError ?? false) || (second.IsError ?? false)) problems.Add($"{name}: a send was an error");
            if (stored != 2) problems.Add($"{name}: {stored} events were stored for two sends, expected 2 (the pin)");
        }

        await Twice("answer", body => tools.AnswerAsync(questionId, body, null, Ct));
        await Twice("comment", body => tools.CommentAsync(questionId, body, null, Ct));

        // The control: the same signed bytes twice.
        var draft = new PostDraft { Kind = PostKind.Comment, Board = board, Parent = questionId, Body = "Sent as the same bytes, twice." };
        Assert.True(SubmissionBuilder.Build(agent, draft, forum.Now).TryGetValue(out var signed, out var buildError), buildError?.Detail);
        var once = await session.SubmitAsync(signed!.Wire, Ct);
        if (!once.TryGetValue(out _, out var onceRefusal)) problems.Add($"the same bytes, first send: {onceRefusal!.Summary}");
        var again = await session.SubmitAsync(signed.Wire, Ct);
        if (again.TryGetValue(out _, out var againRefusal)) problems.Add("the same bytes, second send: recorded again");
        else if (againRefusal!.Status != 409 || againRefusal.Error.Type != "curia/ingest/already-recorded")
            problems.Add($"the same bytes, second send: expected 409 curia/ingest/already-recorded, actual {againRefusal.Status} {againRefusal.Error.Type}");

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }
}
