using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Curia.Api.Tests.Fuzz;
using Curia.Client;
using Curia.Domain.Content;
using Curia.Tests.Shared;
using Npgsql;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// Errata G39 (R9.40, R11.51) against a real Forum on a real socket: the real <c>curia</c> command in a child process, as
/// <see cref="CliRevisionEndToEndTests"/> runs it. A revision of a post that has a correction, or that another agent wrote, is refused before anything
/// is signed whatever the agent's budget; a recheck over a batch with a typo in it exits the code the errata names after every line is printed.
///
/// <para><b>Oracles.</b> The <c>events</c> table by SQL (the revision events whose signed <c>prev</c> is the original's digest, and the posts an agent
/// made), the ids and digests the test's own receipts returned, a counting proxy in front of the Forum for the number of batch requests, and the exit
/// number parsed from the errata (<see cref="PublishedCliRules"/>). Never the verb's report of itself.</para>
///
/// <para><b>A stated confound.</b> A fork costs its author three posts (the original and two revisions), which is a T0 agent's whole day, so on a tree
/// where the verb still signs, the fourth post is refused by the Forum's budget (exit 4) and not accepted (exit 0). Both are red against exit 1 and both
/// leave the log as it was; the probes assert the exit and the log, not which refusal came first.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class CliGuardEndToEndTests(KestrelForumFixture forum) : IClassFixture<KestrelForumFixture>, IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("curia-cli-guard-").FullName;

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Address => forum.Client.BaseAddress!.GetLeftPart(UriPartial.Authority);

    [GeneratedRegex(@"^(?<key>posted|digest)\s+""?(?<value>[^\s""]+)", RegexOptions.Multiline)]
    private static partial Regex Receipt();

    /// <summary>Three questions on nothing in common, so the Forum's near-duplicate check (R8.18) refuses none of them.</summary>
    private static readonly (string Title, string Body)[] Unrelated =
    [
        ("Certificate pinning", "How should a mobile client rotate a pinned leaf certificate without bricking installed apps?"),
        ("Postgres vacuum", "Why does autovacuum fall behind on a table with heavy updates and long running transactions?"),
        ("Cache invalidation", "What is a safe strategy for invalidating a read-through cache after a write to the backing store?"),
    ];

    /// <summary>Runs <c>curia &lt;args&gt;</c> from this suite's output, with the profile store under a temporary home.</summary>
    private async Task<(int Exit, string Stdout, string Stderr)> CuriaAsync(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "curia.dll"));
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["CURIA_CLIENT_HOME"] = _home;

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(Ct);
        var stderr = process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        return (process.ExitCode, await stdout, await stderr);
    }

    private static (string PostId, string Digest) ReadReceipt(string stdout)
    {
        var found = Receipt().Matches(stdout).ToDictionary(m => m.Groups["key"].Value, m => m.Groups["value"].Value);
        return (found["posted"], found["digest"]);
    }

    private static string AgentId(string slug) => $"urn:curia:agent:{slug}";

    private async Task<string> EnrolAsync(string stem)
    {
        // The CLI stamps real time on what it signs; the fixture's clock is fixed, and R6.32 refuses the future.
        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now);
        var slug = $"{stem}-{Guid.NewGuid().ToString("N")[..8]}";
        var enrolled = await CuriaAsync("enrol", "--agent", slug, "--forum", Address);
        Assert.True(enrolled.Exit == 0, enrolled.Stdout + enrolled.Stderr);
        return slug;
    }

    private async Task<(string PostId, string Digest, string Board)> AskAsync(string slug, string title, string body, string? board = null)
    {
        board ??= "g39-" + Guid.NewGuid().ToString("N")[..8];
        var unique = Guid.NewGuid().ToString("N")[..10];
        var asked = await CuriaAsync("ask", "--agent", slug, "--board", board, "--title", $"{title} {unique}", "--body", $"{body} ({unique})", "--forum", Address);
        Assert.True(asked.Exit == 0, asked.Stdout + asked.Stderr);
        var (postId, digest) = ReadReceipt(asked.Stdout);
        return (postId, digest, board);
    }

    private async Task<(int Exit, string Stdout, string Stderr)> ReviseAsync(string slug, string board, string parent, string body) =>
        await CuriaAsync("revision", "--agent", slug, "--board", board, "--parent", parent, "--body", body, "--forum", Address);

    /// <summary>The revisions the log holds whose signed <c>prev</c> is <paramref name="digest"/>: counted from the table.</summary>
    private async Task<long> RevisionsOfAsync(string digest)
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM events WHERE event_type = 'post.accepted' AND payload->>'kind' = 'revision' " +
            "AND (payload->>'canonical')::jsonb->>'prev' = @digest",
            admin);
        command.Parameters.AddWithValue("digest", digest);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture);
    }

    /// <summary>The posts the log holds whose author is <paramref name="agentId"/>: counted from the table.</summary>
    private async Task<long> PostsByAsync(string agentId)
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM events WHERE event_type = 'post.accepted' AND payload->>'author' = @author",
            admin);
        command.Parameters.AddWithValue("author", agentId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// PF17: a second revision of a post that already has one is refused and nothing is sent. The log holds one revision event whose signed
    /// <c>prev</c> is the original's digest (counted by SQL), the refusal names the first revision's post id from its receipt, and a recheck of the
    /// original does not say FORKED.
    /// </summary>
    [Fact]
    public async Task R11_51_ASecondRevisionOfARevisedPostIsRefusedAndNothingIsSent()
    {
        var slug = await EnrolAsync("reviser");
        var (original, digest, board) = await AskAsync(slug, "Retry policy", "I retry a flaky call three times with a fixed one second delay. Is exponential backoff with jitter better?");
        var first = await ReviseAsync(slug, board, original, "A corrected question.");
        Assert.True(first.Exit == 0, first.Stdout + first.Stderr);
        var (revisionId, _) = ReadReceipt(first.Stdout);
        Assert.Equal(1, await RevisionsOfAsync(digest));

        var second = await ReviseAsync(slug, board, original, "A second corrected question.");

        Assert.Equal(PublishedCliRules.R1151Exit(), second.Exit);
        Assert.Contains(revisionId, second.Stderr, StringComparison.Ordinal);
        Assert.Equal(1, await RevisionsOfAsync(digest));
        var recheck = await CuriaAsync("recheck", digest, "--forum", Address);
        Assert.DoesNotContain("FORKED", recheck.Stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// PF17b: one <c>curia revision</c> reads the post it revises once. The Forum (seen through the counting proxy) answers exactly one
    /// <c>GET /v1/posts/{id}</c> of that post, so the guards judge the read that was verified and not a second one.
    /// </summary>
    [Fact]
    public async Task R11_51_ARevisionReadsThePostItRevisesOnce()
    {
        var slug = await EnrolAsync("onceread");
        var (original, _, board) = await AskAsync(slug, "Cache sizing", "How should a read-through cache be sized against the working set of a service that serves mostly recent items?");
        using var proxy = CountingProxy.In(forum.Client.BaseAddress!);

        var revised = await CuriaAsync("revision", "--agent", slug, "--board", board, "--parent", original, "--body", "A corrected cache question.", "--forum", proxy.Address);

        Assert.True(revised.Exit == 0, revised.Stdout + revised.Stderr);
        Assert.Equal(1, proxy.ReadsOf(original));
    }

    /// <summary>
    /// PF18: a fork the harness made (a second revision signed in-process through the client library, which has no guard, as a hand-signed envelope
    /// would be) is named with both revisions and FORKED when the verb is asked to revise the original again. Nothing is added to the log.
    /// </summary>
    [Fact]
    public async Task R11_51_AForkTheHarnessMadeIsNamedWithBothRevisions()
    {
        var slug = await EnrolAsync("forked");
        var (original, digest, board) = await AskAsync(slug, "Queue depth", "How does queue depth interact with consumer concurrency limits in a worker pool?");
        var first = await ReviseAsync(slug, board, original, "A first correction of the queue question.");
        Assert.True(first.Exit == 0, first.Stdout + first.Stderr);
        var (firstId, _) = ReadReceipt(first.Stdout);

        var secondId = await SignForkedRevisionAsync(slug, board, original, digest);
        Assert.Equal(2, await RevisionsOfAsync(digest));
        var posts = await PostsByAsync(AgentId(slug));

        var refused = await ReviseAsync(slug, board, original, "A third correction that must not be sent.");

        Assert.Equal(PublishedCliRules.R1151Exit(), refused.Exit);
        Assert.Contains(firstId, refused.Stderr, StringComparison.Ordinal);
        Assert.Contains(secondId, refused.Stderr, StringComparison.Ordinal);
        Assert.Contains("FORKED", refused.Stderr, StringComparison.Ordinal);
        Assert.Equal(2, await RevisionsOfAsync(digest));
        Assert.Equal(posts, await PostsByAsync(AgentId(slug)));
    }

    /// <summary>Signs a revision of <paramref name="digest"/> as the CLI's own agent through the client library and submits it: the Forum accepts a fork (Table 6).</summary>
    private async Task<string> SignForkedRevisionAsync(string slug, string board, string parent, string digest)
    {
        Assert.True(new ProfileStore(_home).Load(slug).TryGetValue(out var agent, out var loadError), loadError?.Detail);
        using (agent)
        {
            var draft = new PostDraft { Kind = PostKind.Revision, Board = board, Body = "A second correction, signed by the harness.", Parent = parent, Prev = digest };
            Assert.True(SubmissionBuilder.Build(agent!, draft, DateTimeOffset.UtcNow).TryGetValue(out var submission, out var buildError), buildError?.Detail);
            var client = new ForumClient(forum.Client, forum.Client.BaseAddress!);
            var session = new ForumSession(client, agent!, new ProfileStore(_home), forum.Clock);
            var posted = await session.SubmitAsync(submission!.Wire, Ct);
            Assert.True(posted.TryGetValue(out var receipt, out var refusal), refusal?.Summary);
            return receipt!.PostId;
        }
    }

    /// <summary>
    /// PF19: a stranger is told the post is not theirs whatever their budget: a free one and a spent one both exit 1, the stderr says who wrote it and
    /// holds neither the tier row (table-10) nor the budget row (table-11), and the stranger's post count, from SQL, does not move.
    /// </summary>
    [Theory]
    [InlineData("free")]
    [InlineData("spent")]
    public async Task R11_51_AStrangerIsToldItIsNotTheirPostWhateverTheirBudget(string budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        var author = await EnrolAsync("owner");
        var (post, _, board) = await AskAsync(author, "Worker pools", "How does queue depth interact with consumer concurrency limits in a worker pool?");
        var stranger = await EnrolAsync("stranger");
        if (budget == "spent")
        {
            foreach (var (title, body) in Unrelated) await AskAsync(stranger, title, body);
            Assert.Equal(Unrelated.Length, await PostsByAsync(AgentId(stranger)));
        }

        var before = await PostsByAsync(AgentId(stranger));

        var refused = await ReviseAsync(stranger, board, post, "A rewrite of someone else's question.");

        Assert.Equal(PublishedCliRules.R1151Exit(), refused.Exit);
        Assert.Contains("written by", refused.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("table-10", refused.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("table-11", refused.Stderr, StringComparison.Ordinal);
        Assert.Equal(before, await PostsByAsync(AgentId(stranger)));
    }

    /// <summary>PF20, the control: a first revision of the agent's own post posts, and its receipt carries the supersession line, with the digest line as it was.</summary>
    [Fact]
    public async Task R11_51_AFirstRevisionPostsAndItsReceiptCarriesTheSupersessionLine()
    {
        var slug = await EnrolAsync("first");
        var (original, _, board) = await AskAsync(slug, "Postgres vacuum", "Why does autovacuum fall behind on a table with heavy updates and long running transactions?");

        var revised = await ReviseAsync(slug, board, original, "A corrected question about autovacuum.");

        Assert.True(revised.Exit == 0, revised.Stdout + revised.Stderr);
        Assert.Contains("supersession none was reported", revised.Stdout, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^digest\s+sha256:[0-9a-f]{64}   \(computed here\)$", revised.Stdout);
    }

    /// <summary>
    /// PF21: two real digests and a typo. The verb prints a line for each, exits the code the errata names, and the one batch request reached the Forum
    /// (counted by a proxy in front of it, since an in-process Forum keeps no access log).
    /// </summary>
    [Fact]
    public async Task R9_40_CuriaRecheckOfAMixedBatchExitsThreeWithEveryLine()
    {
        var slug = await EnrolAsync("mixed");
        var (_, first, _) = await AskAsync(slug, "Certificate pinning", Unrelated[0].Body);
        var (_, second, _) = await AskAsync(slug, "Postgres vacuum", Unrelated[1].Body);
        using var proxy = CountingProxy.In(forum.Client.BaseAddress!);

        var rechecked = await CuriaAsync("recheck", first, "nope", second, "--forum", proxy.Address);

        Assert.Equal(PublishedCliRules.R940Exit(), rechecked.Exit);
        var lines = rechecked.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Contains(first, lines[0], StringComparison.Ordinal);
        Assert.Contains("digests[1]", lines[1], StringComparison.Ordinal);
        Assert.Contains(second, lines[2], StringComparison.Ordinal);
        Assert.Equal(1, proxy.BatchRequests);
    }

    /// <summary>PF22, the control: sixty-five digests (the cap is 64) are refused whole, exit 3, no state line, and the refusal on standard error.</summary>
    [Fact]
    public async Task R9_40_CuriaRecheckOfSixtyFiveDigestsExitsThreeWithNoLine()
    {
        var digest = "sha256:" + new string('a', 64);

        var rechecked = await CuriaAsync(["recheck", .. Enumerable.Repeat(digest, 65), "--forum", Address]);

        Assert.Equal(PublishedCliRules.R940Exit(), rechecked.Exit);
        string[] states = ["current", "superseded", "withheld", "unknown", "malformed", "disputed", "FAILED"];
        Assert.DoesNotContain(rechecked.Stdout.Split('\n'), l => states.Any(w => l.StartsWith(w, StringComparison.Ordinal)));
        Assert.Contains("batch-too-large", rechecked.Stderr, StringComparison.Ordinal);
    }

    /// <summary>A loopback forwarder that counts the batch requests it relays: the oracle for "one request was sent".</summary>
    private sealed class CountingProxy : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly HttpClient _upstream = new();
        private readonly Uri _target;
        private readonly CancellationTokenSource _stop = new();
        private int _batchRequests;
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _gets = new();

        private CountingProxy(Uri target, int port)
        {
            _target = target;
            Address = $"http://{IPAddress.Loopback}:{port}";
            _listener.Prefixes.Add(Address + "/");
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        internal string Address { get; }

        internal int BatchRequests => Volatile.Read(ref _batchRequests);

        /// <summary>How many <c>GET /v1/posts/{id}</c> requests for <paramref name="postId"/> the proxy relayed.</summary>
        internal int ReadsOf(string postId) => _gets.Count(path => string.Equals(path, "/v1/posts/" + Uri.EscapeDataString(postId), StringComparison.Ordinal));

        internal static CountingProxy In(Uri target)
        {
            return new CountingProxy(target, FreePort());
        }

        private static int FreePort()
        {
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)probe.LocalEndPoint!).Port;
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A relay failure ends that exchange only; the test's own assertions then see the missing answer.")]
        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                try
                {
                    var request = context.Request;
                    if (string.Equals(request.HttpMethod, "GET", StringComparison.Ordinal)) _gets.Enqueue(request.Url!.AbsolutePath);
                    if (string.Equals(request.HttpMethod, "POST", StringComparison.Ordinal)
                        && string.Equals(request.Url!.AbsolutePath, "/v1/posts/batch", StringComparison.Ordinal))
                        Interlocked.Increment(ref _batchRequests);

                    using var forwarded = new HttpRequestMessage(new HttpMethod(request.HttpMethod), new Uri(_target, request.Url!.PathAndQuery));
                    foreach (var name in new[] { "Authorization", "DPoP", "If-None-Match" })
                        if (request.Headers[name] is { } value) forwarded.Headers.TryAddWithoutValidation(name, value);
                    forwarded.Headers.Host = request.UserHostName; // a DPoP proof is signed over the URL the client used, so the Forum must rebuild that one
                    using var body = new MemoryStream();
                    await request.InputStream.CopyToAsync(body);
                    if (body.Length > 0)
                    {
                        forwarded.Content = new ByteArrayContent(body.ToArray());
                        if (request.ContentType is { } type) forwarded.Content.Headers.TryAddWithoutValidation("Content-Type", type);
                    }

                    using var answer = await _upstream.SendAsync(forwarded);
                    var bytes = await answer.Content.ReadAsByteArrayAsync();
                    context.Response.StatusCode = (int)answer.StatusCode;
                    context.Response.ContentType = answer.Content.Headers.ContentType?.ToString();
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                }
                catch (Exception e) when (e is HttpRequestException or HttpListenerException or IOException or InvalidOperationException)
                {
                    context.Response.StatusCode = 502;
                }
                finally
                {
                    context.Response.Close();
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
            _upstream.Dispose();
            _stop.Dispose();
        }
    }
}
