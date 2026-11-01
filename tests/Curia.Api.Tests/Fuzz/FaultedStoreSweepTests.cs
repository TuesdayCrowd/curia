using System.Diagnostics.CodeAnalysis;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Api.Tests.Fuzz;

/// <summary>
/// ULG1 to ULG3, ULG34 (errata G52, R9.52, R8.82, R11.71): every exemplar of every route sent while a store fails, on the in-process host (Development, the
/// owner's connection; the fixture decorates each store and the test arms it after seeding). The routes come from the host's route table, as the request fuzzer's
/// do (R14.10), so a new route that reads a store is covered by default, and one that reads none must be listed with a reason. Each fact prints every route
/// that was answered other than the entry says, and the answer, so its red names the routes and what they said. On the seam tree the host answers an
/// unhandled exception as ASP.NET's developer page (<c>500 text/plain</c>, the exception's text as the body; Task 0's M9) and a failed <c>Result</c> as it
/// always has; the assertions are the same in Production.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("fuzz")]
public sealed class FaultedStoreSweepTests(FaultableForumFixture forum) : IClassFixture<FaultableForumFixture>
{
    private enum Expect { Healthy, LogUnreadable, StoreUnavailable, TokenUnavailable }

    private static string Describe(Expect expect) => expect switch
    {
        Expect.Healthy => "2xx",
        Expect.LogUnreadable => $"503 {PublishedStoreFaults.LogUnreadableType} (a problem document, null detail)",
        Expect.StoreUnavailable => $"503 {PublishedStoreFaults.StoreUnavailableType} (a problem document, null detail)",
        Expect.TokenUnavailable => $"{PublishedStoreFaults.TokenStatus} {PublishedStoreFaults.TokenCode}",
        _ => throw new ArgumentOutOfRangeException(nameof(expect), expect, null),
    };

    private static bool Meets(SweptReply swept, Expect expect) => expect switch
    {
        Expect.Healthy => swept.Reply.Threw is null && swept.Reply.Status is >= 200 and <= 299,
        Expect.LogUnreadable => FaultSweep.IsProblem(swept.Reply, PublishedStoreFaults.UnavailableStatus, PublishedStoreFaults.LogUnreadableType),
        Expect.StoreUnavailable => FaultSweep.IsProblem(swept.Reply, PublishedStoreFaults.UnavailableStatus, PublishedStoreFaults.StoreUnavailableType),
        Expect.TokenUnavailable => FaultSweep.IsOAuthError(swept.Reply, PublishedStoreFaults.TokenStatus, PublishedStoreFaults.TokenCode),
        _ => throw new ArgumentOutOfRangeException(nameof(expect), expect, null),
    };

    /// <summary>What each store the ULG3 sweep faults is not reached by, each with its reason (hand-written, so a new route that reads the store is judged by default).</summary>
    private static (Func<SweptReply, bool> Matches, string Reason)[] NotReached(string store)
    {
        static Func<SweptReply, bool> Routes(params string[] routes) => s => routes.Contains(s.Route, StringComparer.Ordinal);

        static bool Acta(SweptReply s) => s.Route.StartsWith("GET /v1/log/", StringComparison.Ordinal);

        string[] anonymousReads =
        [
            "GET /v1/posts/{postId}", "GET /v1/threads/{rootPostId}", "GET /v1/boards/{board}/posts", "GET /v1/search", "POST /v1/posts/batch",
        ];
        string[] tokenReads = ["GET /v1/inbox", "GET /v1/flags", "GET /v1/budget", "GET /v1/posts/{postId}/flags"];

        var table = new List<(Func<SweptReply, bool> Matches, string Reason)>
        {
            (FaultSweep.ReadsNoStore, "it reads no store (FaultSweep.ReadsNoLog gives each its reason)"),
            (Acta, "the Acta's own reads are public: no token, no DPoP proof, and the key set they verify under is the log's"),
            (Routes(anonymousReads), "an anonymous read serves from the log: it carries no token and no proof, and reads no Registrar key"),
        };

        switch (store)
        {
            case "nonce":
                table.Add((Routes(["GET /v1/jwks", .. tokenReads, "POST /oauth/token", "POST /v1/agents"]),
                    "a DPoP nonce is asked of a write; a read, the token endpoint (it checks the proof, not a nonce) and enrolment (anonymous) are not asked one"));
                break;
            case "replay":
                table.Add((Routes("GET /v1/jwks", "POST /v1/agents"), "the key set is a public read and enrolment is anonymous: neither carries a proof or an assertion to remember"));
                break;
            default:
                table.Add((Routes(tokenReads), "a token-bearing read is authorised by the token and the log; it resolves no Registrar key"));
                table.Add((Routes("POST /v1/posts/{postId}/flags", "POST /v1/posts/{postId}/accept"),
                    "a flag and an acceptance carry no signed envelope, so no author key is resolved: the token and the log authorise them"));
                break;
        }

        return [.. table];
    }

    private async Task<IReadOnlyList<SweptReply>> SweepAsync(Action arm, CancellationToken ct)
    {
        using var context = await FuzzContext.SeedAsync(forum, ct);
        return await FaultSweep.RunAsync(forum, context, _ => true, arm, forum.Store.Disarm, ct);
    }

    private static void Judged(IReadOnlyList<SweptReply> swept, Func<SweptReply, Expect> expect, IEnumerable<string> tableProblems)
    {
        var problems = tableProblems.ToList();
        problems.AddRange(FaultSweep.RetryAfterProblems(swept));
        problems.AddRange(swept.Where(s => !Meets(s, expect(s))).Select(s => $"expected {Describe(expect(s))}, actual {s.Says}"));
        Assert.True(swept.Count > 0, "the sweep sent nothing");
        Assert.True(problems.Count == 0, $"{problems.Count} of {swept.Count} exemplars:\n" + FaultSweep.Lines(problems));
    }

    /// <summary>
    /// ULG1. The store throws <see cref="Curia.Application.Ports.LogUnreadableException"/> on every read, as the fixed event store does: every route that reads the
    /// log answers <c>503</c> <c>curia/log/unreadable</c> as a problem document with a null <c>detail</c>; the token endpoint answers RFC 6749's code; the listed
    /// routes read no store and answer <c>2xx</c>. An unlisted route that does not answer <c>503</c> fails, and so does a listed one that does.
    /// </summary>
    [Fact]
    public async Task R9_52_ULG1_EveryRouteThatReadsTheLogAnswersAnUnreadableLogAs503AProblemDocument()
    {
        var ct = TestContext.Current.CancellationToken;
        var swept = await SweepAsync(() => forum.Store.Arm(EventStoreFault.ThrowUnreadable), ct);

        Judged(
            swept,
            s => FaultSweep.ReadsNoStore(s) ? Expect.Healthy : FaultSweep.IsToken(s) ? Expect.TokenUnavailable : Expect.LogUnreadable,
            FaultSweep.TableProblems(forum));
    }

    /// <summary>
    /// ULG2, whole-log mode. The reader reports a failed <c>Result</c> for the whole-log read and serves a single aggregate: the empty-log fallback of
    /// <c>ReadEventsAsync</c> turns it into a denial or an empty list. Enrolment reads only the enrolling agent's own stream and stays healthy; everything else
    /// that reads the log is <c>503</c>, never <c>403</c> and never <c>200</c>.
    /// </summary>
    [Fact]
    public async Task R7_31_ULG2_AReaderThatReportsAFailedWholeLogReadNeverBecomesADenialOrAnEmptyList()
    {
        var ct = TestContext.Current.CancellationToken;
        var swept = await SweepAsync(() => forum.Store.Arm(EventStoreFault.ReportForward), ct);

        Judged(
            swept,
            s => FaultSweep.ReadsNoStore(s) || FaultSweep.IsEnrolment(s) ? Expect.Healthy : FaultSweep.IsToken(s) ? Expect.TokenUnavailable : Expect.LogUnreadable,
            FaultSweep.TableProblems(forum));
    }

    /// <summary>ULG2, every-read mode: the validator's own mapping. Enrolment reads a stream too, so it is <c>503</c> with the rest.</summary>
    [Fact]
    public async Task R9_52_ULG2_AReaderThatReportsEveryReadFailedIs503ForEveryRouteThatReadsTheLog()
    {
        var ct = TestContext.Current.CancellationToken;
        var swept = await SweepAsync(() => forum.Store.Arm(EventStoreFault.ReportAll), ct);

        Judged(
            swept,
            s => FaultSweep.ReadsNoStore(s) ? Expect.Healthy : FaultSweep.IsToken(s) ? Expect.TokenUnavailable : Expect.LogUnreadable,
            FaultSweep.TableProblems(forum));
    }

    /// <summary>
    /// ULG2, append mode (ULG34 is carried here): the reader reports a failed <c>Result</c> for the append. Every route that appends answers <c>503</c>
    /// <c>curia/store/unavailable</c>, including the sites that answered a use case's failure <c>500</c> whatever its type (enrolment, PERSIST, the accept
    /// record) and the flag route's <c>400</c>; every route that only reads is healthy.
    /// </summary>
    [Fact]
    public async Task R8_82_ULG2_ULG34_AReaderThatReportsAFailedAppendIs503StoreUnavailableForEveryRouteThatAppends()
    {
        var ct = TestContext.Current.CancellationToken;
        var swept = await SweepAsync(() => forum.Store.Arm(EventStoreFault.ReportAppend), ct);

        Judged(swept, s => FaultSweep.Appends(s) ? Expect.StoreUnavailable : Expect.Healthy, FaultSweep.TableProblems(forum));
    }

    /// <summary>
    /// ULG3. A driver fault of a store that is not the log: the DPoP nonce store, the replay cache and the Registrar's key store, one at a time. Whatever route it
    /// reaches answers <c>503</c> <c>curia/store/unavailable</c> as a problem document (the token endpoint: its OAuth code); none answers <c>500</c>, and each
    /// fault reaches at least one route, so a sweep that reached nothing fails.
    /// </summary>
    [Theory]
    [InlineData("nonce")]
    [InlineData("replay")]
    [InlineData("keys")]
    public async Task R8_82_ULG3_ADriverFaultOfAStoreThatIsNotTheLogIs503StoreUnavailable(string store)
    {
        var ct = TestContext.Current.CancellationToken;
        Action arm = store switch
        {
            "nonce" => forum.Nonces.Arm,
            "replay" => forum.Replay.Arm,
            _ => forum.Keys.Arm,
        };
        Action disarm = store switch
        {
            "nonce" => forum.Nonces.Disarm,
            "replay" => forum.Replay.Disarm,
            _ => forum.Keys.Disarm,
        };

        using var context = await FuzzContext.SeedAsync(forum, ct);
        var swept = await FaultSweep.RunAsync(forum, context, _ => true, arm, disarm, ct);

        // Every exemplar is judged as ULG1 judges it, not only the replies that threw or were 5xx: a fault of this store that a handler turned into a 401, a 403,
        // a 404 or an empty 200 is the failure R7.31 forbids, and it must be seen. An exemplar answers the 503 of this fault unless the table says why this store
        // is not reached on its path (then it must not be a 5xx at all).
        var notReached = NotReached(store);
        var problems = FaultSweep.RetryAfterProblems(swept).ToList();
        var reached = 0;
        foreach (var s in swept)
        {
            if (notReached.Where(r => r.Matches(s)).Select(r => r.Reason).FirstOrDefault() is { } reason)
            {
                if (s.Reply.Threw is not null || s.Reply.Status >= 500) problems.Add($"{s.Name}: listed as not reaching the {store} store ({reason}) but answered {s.Reply.Says}");
                continue;
            }

            var expect = FaultSweep.IsToken(s) ? Expect.TokenUnavailable : Expect.StoreUnavailable;
            if (Meets(s, expect)) reached++;
            else problems.Add($"expected {Describe(expect)}, actual {s.Says}");
        }

        if (reached == 0) problems.Add($"the {store} fault reached no route: every exemplar was answered without its 503");

        Assert.True(problems.Count == 0, $"{problems.Count} problems over {swept.Count} exemplars:\n" + FaultSweep.Lines(problems));
    }
}
