using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using Curia.Application.Projections;
using Curia.Domain.Serving;
using Curia.OperatorTool;
using Npgsql;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R10.59 end to end: the operator's out-of-band verbs against the real Forum's database, through
/// <see cref="OperatorCommands.RunAsync"/> — the whole path an operator runs, minus reading one
/// environment variable.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class OperatorModerationTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    private sealed record Party(ForumAgent Agent, DpopClient Dpop, string Token);

    private async Task<(int Exit, string Out, string Err)> RunAsync(string[] args, CancellationToken ct)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await OperatorCommands.RunAsync(args, forum.ConnectionString, forum.Clock, stdout, stderr, ct);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private async Task<Party> PartyAsync(HttpClient client, string stem, CancellationToken ct)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agent = ForumAgent.Create($"https://agents.example/{stem}-{suffix}", $"{stem}-{suffix}");
        var (dpop, token) = await agent.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);
        return new Party(agent, dpop, token);
    }

    private async Task<(string PostId, string Digest)> AskAsync(HttpClient client, Party author, CancellationToken ct)
    {
        var board = "op-" + Guid.NewGuid().ToString("N")[..8];
        using var asked = await author.Dpop.PostAsync(
            client, PostsUrl, author.Token,
            author.Agent.SignQuestion(board, "How does JCS order object members?", "Ordering " + Guid.NewGuid().ToString("N")[..8], forum.Now),
            forum.Now, ct);
        Assert.Equal(HttpStatusCode.Created, asked.StatusCode);

        using var receipt = JsonDocument.Parse(await asked.Content.ReadAsStringAsync(ct));
        return (receipt.RootElement.GetProperty("post_id").GetString()!, receipt.RootElement.GetProperty("digest").GetString()!);
    }

    private async Task FlagAsync(HttpClient client, Party raiser, string postId, string kind, string rationale, CancellationToken ct)
    {
        using var raised = await raiser.Dpop.PostAsync(
            client, $"http://localhost/v1/posts/{postId}/flags", raiser.Token,
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { kind, rationale })),
            forum.Now, ct, contentType: "application/json");
        Assert.Equal(HttpStatusCode.Created, raised.StatusCode);
    }

    private static string[] Moderate(string postId, string effect, string category = "spam", string reason = "Reviewed: advertising.") =>
        ["moderate", "--post", postId, "--category", category, "--effect", effect, "--reason", reason, "--by", "reviewer"];

    /// <summary>How many entries the log serves, counted the way any reader counts them.</summary>
    private static async Task<long> LogSizeAsync(HttpClient client, CancellationToken ct)
    {
        for (long i = 0; i < 100_000; i++)
        {
            using var entry = await client.GetAsync(new Uri($"/v1/log/entries/{i}", UriKind.Relative), ct);
            if (entry.StatusCode == HttpStatusCode.NotFound) return i;
        }

        throw new InvalidOperationException("the log did not end within 100,000 entries");
    }

    /// <summary>
    /// Waits until <paramref name="sessions"/> sessions of this database wait for a lock on
    /// <c>flag_details</c>, or until a run has ended without waiting, which the caller's assertions
    /// then report.
    /// </summary>
    private static async Task WaitUntilQueuedAsync(NpgsqlDataSource db, int sessions, Task[] runs, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3_000; attempt++)
        {
            if (runs.Any(r => r.IsCompleted)) return;

            await using var waiting = db.CreateCommand(
                "SELECT count(*) FROM pg_locks WHERE locktype = 'relation' AND NOT granted " +
                "AND database = (SELECT oid FROM pg_database WHERE datname = current_database()) " +
                "AND relation = 'flag_details'::regclass");
            if ((long)(await waiting.ExecuteScalarAsync(ct))! >= sessions) return;

            await Task.Delay(TimeSpan.FromMilliseconds(10), ct);
        }

        throw new TimeoutException($"{sessions} runs did not reach the private store within 30 s");
    }

    /// <summary>R10.59, R10.60, R6.25: the post stops being served, and the record — public, in the log — names who and why, and the digest.</summary>
    [Fact]
    public async Task R10_59_TheOperatorWithholdsAPostOutOfBandAndTheRecordIsPublic()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var author = await PartyAsync(client, "op-author", ct);
        var (postId, digest) = await AskAsync(client, author, ct);

        var (exit, stdout, stderr) = await RunAsync(Moderate(postId, "withhold"), ct);

        Assert.True(exit == ExitCode.Ok, stderr);
        Assert.Contains($"moderated    {postId}", stdout, StringComparison.Ordinal);
        Assert.Contains($"digest       {digest}", stdout, StringComparison.Ordinal);
        Assert.Contains("adjudicates  0", stdout, StringComparison.Ordinal);
        Assert.Contains("by           operator:reviewer", stdout, StringComparison.Ordinal);

        using (var read = await client.GetAsync(new Uri($"/v1/posts/{postId}", UriKind.Relative), ct))
            Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);

        string? record = null;
        for (long i = 0; i < 100_000; i++)
        {
            using var entry = await client.GetAsync(new Uri($"/v1/log/entries/{i}", UriKind.Relative), ct);
            if (entry.StatusCode == HttpStatusCode.NotFound) break;

            var body = await entry.Content.ReadAsStringAsync(ct);
            if (body.Contains("\"moderation.applied\"", StringComparison.Ordinal) && body.Contains(postId, StringComparison.Ordinal))
                record = body;
        }

        Assert.True(record is not null, "no moderation record for the post in the log");
        Assert.Contains("\"actor_id\":\"operator:reviewer\"", record!, StringComparison.Ordinal);
        Assert.Contains("\"moderator\":\"human\"", record, StringComparison.Ordinal);
        Assert.Contains($"\"digest\":\"{digest}\"", record, StringComparison.Ordinal);
    }

    /// <summary>R10.60, R10.61: the record names the flag, and the listing shows it open before and upheld after.</summary>
    [Fact]
    public async Task R10_60_TheRecordNamesTheFlagAndTheListingShowsItUpheld()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var author = await PartyAsync(client, "op-author", ct);
        var raiser = await PartyAsync(client, "op-raiser", ct);
        var (postId, _) = await AskAsync(client, author, ct);
        await FlagAsync(client, raiser, postId, "spam", "Advertising, not a question.", ct);

        var (listExit, before, listErr) = await RunAsync(["flags", "--post", postId], ct);
        Assert.True(listExit == ExitCode.Ok, listErr);
        Assert.Contains("state      open", before, StringComparison.Ordinal);
        Assert.Contains("1 flag(s)", before, StringComparison.Ordinal);

        // Judging content needs no identity: the raiser is listed only under --raisers (R10.62).
        Assert.DoesNotContain("raised_by", before, StringComparison.Ordinal);
        Assert.DoesNotContain(raiser.Agent.AgentId, before, StringComparison.Ordinal);
        var (raisersExit, withRaisers, raisersErr) = await RunAsync(["flags", "--post", postId, "--raisers"], ct);
        Assert.True(raisersExit == ExitCode.Ok, raisersErr);
        Assert.Contains($"raised_by  {raiser.Agent.AgentId}", withRaisers, StringComparison.Ordinal);

        var flagId = before.Split('\n').Single(l => l.StartsWith("flag       ", StringComparison.Ordinal))["flag       ".Length..].Trim();

        var (exit, stdout, stderr) = await RunAsync(Moderate(postId, "withhold"), ct);
        Assert.True(exit == ExitCode.Ok, stderr);
        Assert.Contains($"adjudicates  1: {flagId}", stdout, StringComparison.Ordinal);

        var (_, after, _) = await RunAsync(["flags", "--post", postId], ct);
        Assert.Contains("state      upheld", after, StringComparison.Ordinal);

        var (_, open, _) = await RunAsync(["flags", "--post", postId, "--open"], ct);
        Assert.Contains("0 flag(s)", open, StringComparison.Ordinal);
    }

    /// <summary>
    /// Review Focus 5. R10.44 requires a rationale served under <c>moderation</c>|<c>list</c> be
    /// delimited and marked — the operator's reviewer may be a model — and a terminal must not
    /// interpret an escape sequence or a bidi override someone typed into a flag.
    /// </summary>
    [Fact]
    public async Task R10_62_TheListingMarksTheRationaleAndEscapesControlCharacters()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var author = await PartyAsync(client, "op-author", ct);
        var raiser = await PartyAsync(client, "op-raiser", ct);
        var (postId, _) = await AskAsync(client, author, ct);
        await FlagAsync(client, raiser, postId, "injection", "Spam.\u001b[31m red \u202e reversed", ct);

        var (exit, stdout, stderr) = await RunAsync(["flags", "--post", postId], ct);

        Assert.True(exit == ExitCode.Ok, stderr);
        Assert.Contains(Datamarking.OpenDelimiter, stdout, StringComparison.Ordinal);
        Assert.Contains(Datamarking.CloseDelimiter, stdout, StringComparison.Ordinal);
        Assert.Contains(Datamarking.DefaultControlToken, stdout, StringComparison.Ordinal);
        Assert.Contains("\\u001B", stdout, StringComparison.Ordinal);
        Assert.Contains("\\u202E", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("\u202e", stdout, StringComparison.Ordinal);
    }

    /// <summary>R10.39 counts records: the same record twice is refused by name, and the post stays as the first left it.</summary>
    [Fact]
    public async Task R10_39_ARepeatedRecordIsRefusedByName()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var author = await PartyAsync(client, "op-author", ct);
        var (postId, _) = await AskAsync(client, author, ct);

        Assert.Equal(ExitCode.Ok, (await RunAsync(Moderate(postId, "withhold"), ct)).Exit);

        var (exit, _, stderr) = await RunAsync(Moderate(postId, "withhold"), ct);
        Assert.Equal(ExitCode.Refused, exit);
        Assert.Contains("curia/moderation/no-op", stderr, StringComparison.Ordinal);
    }

    /// <summary>R10.60: the reason lands in a public leaf, so a credential in it is refused, never echoed, and nothing changes.</summary>
    [Fact]
    public async Task R10_60_ACredentialInTheReasonIsRefusedAndThePostStaysServed()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var author = await PartyAsync(client, "op-author", ct);
        var (postId, _) = await AskAsync(client, author, ct);

        var (exit, _, stderr) = await RunAsync(Moderate(postId, "withhold", "credential_leak", "It leaks AKIAIOSFODNN7EXAMPLE."), ct);

        Assert.Equal(ExitCode.Refused, exit);
        Assert.Contains("curia/moderation/rationale-rejected", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("AKIA", stderr, StringComparison.Ordinal);

        using var read = await client.GetAsync(new Uri($"/v1/posts/{postId}", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    /// <summary>
    /// A usage error is refused before anything is written. It is aimed at a real, servable post, so
    /// a verb that defaulted the missing category would have withheld it: the log would grow and the
    /// post would stop being served. Against a post that does not exist, "writes nothing" would hold
    /// whatever the verb did.
    /// </summary>
    [Fact]
    public async Task AMissingCategoryIsAUsageErrorAndWritesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var author = await PartyAsync(client, "op-author", ct);
        var (postId, _) = await AskAsync(client, author, ct);
        var before = await LogSizeAsync(client, ct);

        var (exit, _, stderr) = await RunAsync(["moderate", "--post", postId, "--effect", "withhold", "--reason", "r", "--by", "x"], ct);

        Assert.Equal(ExitCode.Usage, exit);
        Assert.Contains("--category is required", stderr, StringComparison.Ordinal);
        Assert.Equal(before, await LogSizeAsync(client, ct));

        using var read = await client.GetAsync(new Uri($"/v1/posts/{postId}", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    /// <summary>
    /// A blank value is a missing one, refused as usage before anything is written. Without that,
    /// <c>--post " "</c> reached the writer's argument guard and threw; <c>--by " "</c> was recorded
    /// as <c>operator: </c>, a permanent public leaf naming no one (R10.59); and <c>--reason " "</c>
    /// was refused by the writer instead of the verb. Each is aimed at a real, servable post, as
    /// <see cref="AMissingCategoryIsAUsageErrorAndWritesNothing"/> is, so a verb that let the blank
    /// through would grow the log.
    /// </summary>
    [Theory]
    [InlineData("post")]
    [InlineData("by")]
    [InlineData("reason")]
    public async Task ABlankValueIsAUsageErrorAndWritesNothing(string blank)
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var author = await PartyAsync(client, "op-author", ct);
        var (postId, _) = await AskAsync(client, author, ct);
        var before = await LogSizeAsync(client, ct);

        var args = Moderate(postId, "withhold");
        args[Array.IndexOf(args, "--" + blank) + 1] = " ";
        var (exit, _, stderr) = await RunAsync(args, ct);

        Assert.Equal(ExitCode.Usage, exit);
        Assert.Contains($"--{blank} is required", stderr, StringComparison.Ordinal);
        Assert.Equal(before, await LogSizeAsync(client, ct));

        using var read = await client.GetAsync(new Uri($"/v1/posts/{postId}", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    /// <summary>
    /// R10.39 counts records, so two operators recording one decision at once must leave one record.
    /// <c>ApplyModeration</c> decides on one read of the log, and the store refuses the append that
    /// read no longer describes; the verb then tells that operator to re-read the post and re-run.
    /// The guidance names no flag, raiser or rationale (R10.27, R10.28).
    ///
    /// <para>Both runs are held at the private store, which the writer reads after the log and before
    /// it appends, until both are waiting there, so both decide on the same view of the log. The lock
    /// is the database owner's; the Forum's role holds no grant that could take it.</para>
    /// </summary>
    [Fact]
    public async Task R10_39_OfTwoRecordsDecidedOnOneViewOneLandsAndTheOtherIsToldToReRead()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var author = await PartyAsync(client, "op-author", ct);
        var raiser = await PartyAsync(client, "op-raiser", ct);
        var (postId, _) = await AskAsync(client, author, ct);
        const string rationale = "Advertising, not a question.";
        await FlagAsync(client, raiser, postId, "spam", rationale, ct);

        (int Exit, string Out, string Err)[] runs;
        await using (var db = NpgsqlDataSource.Create(forum.ConnectionString))
        await using (var holder = await db.OpenConnectionAsync(ct))
        await using (var hold = await holder.BeginTransactionAsync(ct))
        {
            await using (var lockTable = new NpgsqlCommand("LOCK TABLE flag_details IN ACCESS EXCLUSIVE MODE", holder, hold))
                await lockTable.ExecuteNonQueryAsync(ct);

            Task<(int Exit, string Out, string Err)>[] pending =
                [RunAsync(Moderate(postId, "withhold"), ct), RunAsync(Moderate(postId, "withhold"), ct)];
            await WaitUntilQueuedAsync(db, pending.Length, pending, ct);
            await hold.RollbackAsync(ct);
            runs = await Task.WhenAll(pending);
        }

        var landed = Assert.Single(runs, r => r.Exit == ExitCode.Ok);
        var overtaken = Assert.Single(runs, r => r.Exit == ExitCode.Refused);
        Assert.Contains("curia/domain/concurrency-conflict", overtaken.Err, StringComparison.Ordinal);
        Assert.Contains("nothing was written", overtaken.Err, StringComparison.Ordinal);
        Assert.Contains("Re-read the post", overtaken.Err, StringComparison.Ordinal);

        var flagId = landed.Out.Split('\n').Single(l => l.StartsWith("adjudicates  1: ", StringComparison.Ordinal))["adjudicates  1: ".Length..].Trim();
        Assert.DoesNotContain(flagId, overtaken.Err, StringComparison.Ordinal);
        Assert.DoesNotContain(raiser.Agent.AgentId, overtaken.Err, StringComparison.Ordinal);
        Assert.DoesNotContain(rationale, overtaken.Err, StringComparison.Ordinal);

        var records = 0;
        for (long i = 0; i < 100_000; i++)
        {
            using var entry = await client.GetAsync(new Uri($"/v1/log/entries/{i}", UriKind.Relative), ct);
            if (entry.StatusCode == HttpStatusCode.NotFound) break;

            var body = await entry.Content.ReadAsStringAsync(ct);
            if (body.Contains("\"moderation.applied\"", StringComparison.Ordinal) && body.Contains(postId, StringComparison.Ordinal))
                records++;
        }

        Assert.Equal(1, records);
    }

    /// <summary>
    /// R11.31's shape, for the join R10.62 creates. A private row rewritten or lost after the fact no
    /// longer opens its entry's commitment, so the directory skips the flag; the listing is the one
    /// place an operator sees that join, and it counts each skip by reason rather than reading as a
    /// Forum where the flag was never raised. The count names no raiser and repeats no text. A
    /// skipped flag cannot be attributed to a post, so under <c>--post</c> the listing says the
    /// counts cover every post.
    ///
    /// <para>Both changes are the database owner's: R11.6's grant refuses the Forum's role
    /// <c>UPDATE</c> and <c>DELETE</c> on the table.</para>
    /// </summary>
    [Fact]
    public async Task R10_62_ARewrittenOrLostPrivateRowIsCountedInTheListing()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var author = await PartyAsync(client, "op-author", ct);
        var raiser = await PartyAsync(client, "op-raiser", ct);
        var (postId, _) = await AskAsync(client, author, ct);
        await FlagAsync(client, raiser, postId, "spam", "Advertising, not a question.", ct);
        await FlagAsync(client, raiser, postId, "incorrect", "The premise is false.", ct);

        await using (var db = NpgsqlDataSource.Create(forum.ConnectionString))
        {
            await using (var rewrite = db.CreateCommand(
                "UPDATE flag_details SET rationale = 'Rewritten after the fact.' WHERE post_id = @post AND rationale = 'Advertising, not a question.'"))
            {
                rewrite.Parameters.AddWithValue("post", postId);
                Assert.Equal(1, await rewrite.ExecuteNonQueryAsync(ct));
            }

            await using (var lose = db.CreateCommand(
                "DELETE FROM flag_details WHERE post_id = @post AND rationale = 'The premise is false.'"))
            {
                lose.Parameters.AddWithValue("post", postId);
                Assert.Equal(1, await lose.ExecuteNonQueryAsync(ct));
            }
        }

        var (exit, stdout, stderr) = await RunAsync(["flags", "--post", postId], ct);

        Assert.True(exit == ExitCode.Ok, stderr);
        Assert.Contains("note       the skipped counts below cover every post, not only --post", stdout, StringComparison.Ordinal);
        Assert.Contains($"skipped    {FlagDirectory.SkippedCommitmentMismatch}: 1", stdout, StringComparison.Ordinal);
        Assert.Contains($"skipped    {FlagDirectory.SkippedNoDetail}: 1", stdout, StringComparison.Ordinal);
        Assert.Contains("0 flag(s)", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("Rewritten after the fact.", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(raiser.Agent.AgentId, stdout, StringComparison.Ordinal);
    }
}
