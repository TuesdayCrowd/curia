#pragma warning disable CA1305, CA1308, CA1707, CA1822, CA1031, CA2007, CA1861, CA1859, CA1062, CA1307, CA1310, CA1002, CA1819
using System.Net;
using System.Text.Json;
using Xunit;

using Probe = Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>
/// PR N2 (errata G70, R4.43, R6.67), Task 1: the probes that forge an operator act as the application role, as <c>ForgedLifecycleRowsTests</c> forges a lifecycle row, and read what a reader, a token or a writer then
/// says. OAS1 to OAS6, OAS25, OAS31 and OAS33 to OAS37, OAS39 (Amendment 1 adds 34 to 39). Every probe reads its harm beside a positive control read by the same observer; a probe that reads a denial asserts the type and
/// the reason (ruling 17), because a spent budget is the same type as a tier denial. They are red on the tree because the Forum folds both act types by shape; OAS39 is a characterization of an open defect and is green by design.
/// </summary>
[Collection("forum")]
public sealed class OperatorActsForgeryTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OperatorActsApi Api => new(forum);

    private static string Denied => "403 curia/authz/denied (reason table-10/denied)";

    /// <summary>The first post's <c>owner_verified</c> and, with it, the answer's outcome: the observer every OAS1 reading shares.</summary>
    private async Task<string> AnswerAndStandingAsync(OperatorActsApi api, OperatorActsApi.Actor who, string board, string target, string firstQuestion)
    {
        var answer = await api.AnswerAsync(who, board, target, Ct);
        var prov = await api.ProvenanceAsync(firstQuestion, Ct);
        return $"{OperatorActsApi.Said(answer)}; owner_verified={prov.OwnerVerified?.ToString().ToLowerInvariant()}";
    }

    // ---- OAS1 ----------------------------------------------------------------------------------

    [Fact]
    public async Task OAS1_TheComposedForgeryReachesNoTier()
    {
        var api = Api;
        var board = OperatorActsApi.Board();
        var asker = await api.GenuineAsync("asker", Ct);
        var target = await api.QuestionByAsync(asker, board, Ct);

        // Control, the genuine path: an attestation, three questions, an aged enrolment: the answer is accepted and the envelope says verified.
        var genuine = await api.GenuineAsync("genuine", Ct);
        await api.GenuineAttest(genuine.Agent.AgentId, Ct, "owner:genuine-" + OperatorActsApi.Suffix());
        var first = (await api.AskAsync(genuine, board, Ct)).PostId;
        for (var i = 0; i < 2; i++) Assert.Equal(201, (await api.AskAsync(genuine, board, Ct)).Status);
        forum.Clock.Advance(TimeSpan.FromHours(49));
        WholeText.Same("201 (accepted); owner_verified=true", await AnswerAndStandingAsync(api, genuine, board, target, first));

        // The forgery: a backdated enrolment root, the statement-less key pair, a forged attestation and three questions, as the application role alone.
        var forged = await api.ForgeAsync("forged", TimeSpan.FromHours(72), attest: true, "owner:forged-" + OperatorActsApi.Suffix(), Ct);
        var forgedFirst = (await api.AskAsync(forged, board, Ct)).PostId;
        for (var i = 0; i < 2; i++) await api.AskAsync(forged, board, Ct);
        var before = await api.EventCountAsync(Ct);
        var actual = await AnswerAndStandingAsync(api, forged, board, target, forgedFirst);
        WholeText.Same($"{Denied}; owner_verified=false", actual);
        Assert.Equal(before, await api.EventCountAsync(Ct));
    }

    // ---- OAS2 ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("human", "withhold")]
    [InlineData("automated", "quarantine")]
    [InlineData("delegated_agent", "withhold")]
    public async Task OAS2_AForgedWithholdWithholdsNothing(string moderator, string effect)
    {
        var api = Api;
        // Control: the genuine withhold of another post withholds it (the observer can see a withholding).
        var other = await api.QuestionAsync("genuine-other", Ct);
        await api.GenuineWithhold(other.Id, Ct);
        WholeText.Same("404 curia/posts/withheld", await PostStatusAsync(api, other.Id));

        var victim = await api.QuestionAsync("victim", Ct);
        WholeText.Same("200; search=1", $"{await PostStatusAsync(api, victim.Id)}; search={await api.SearchCountAsync(victim.Words, victim.Board, Ct)}");
        var skippedBefore = await api.SkippedRowsAsync(Ct);
        await api.ForgeModerationAsync(victim.Id, victim.Digest, effect, Ct, moderator: moderator);
        var actual = $"{await PostStatusAsync(api, victim.Id)}; search={await api.SearchCountAsync(victim.Words, victim.Board, Ct)}";
        WholeText.Same("200; search=1", actual);
        // Task 3d (ruling 86 (c)): the forged row was read and skipped, so the served post is not for want of a row the Forum never saw. For the automated row this is the whole characterization of the arm.
        Assert.Equal(skippedBefore + 1, await api.SkippedRowsAsync(Ct));
    }

    private static async Task<string> PostStatusAsync(OperatorActsApi api, string id)
    {
        using var r = await api.Http().GetAsync(new Uri($"/v1/posts/{id}", UriKind.Relative), Ct);
        return r.StatusCode == HttpStatusCode.OK ? "200" : $"{(int)r.StatusCode} {OperatorActsApi.Read(r.StatusCode, await r.Content.ReadAsStringAsync(Ct)).Type}";
    }

    // ---- OAS3 ----------------------------------------------------------------------------------

    [Fact]
    public async Task OAS3_AForgedRestoreReleasesNothing()
    {
        var api = Api;
        var post = await api.QuestionAsync("withheld", Ct);
        await api.GenuineWithhold(post.Id, Ct);
        WholeText.Same("404 curia/posts/withheld", await PostStatusAsync(api, post.Id)); // control: the genuine withhold holds
        await api.ForgeModerationAsync(post.Id, post.Digest, "restore", Ct);
        WholeText.Same("404 curia/posts/withheld", await PostStatusAsync(api, post.Id));
    }

    // ---- OAS4 ----------------------------------------------------------------------------------

    [Fact]
    public async Task OAS4_AForgedUpheldFlagDemotesNoOne()
    {
        var api = Api;
        var board = OperatorActsApi.Board();
        var asker = await api.GenuineAsync("asker", Ct);
        var target = await api.QuestionByAsync(asker, board, Ct);
        var demotee = await api.GenuineAsync("demotee", Ct);
        await api.GenuineAttest(demotee.Agent.AgentId, Ct, "owner:d-" + OperatorActsApi.Suffix());
        var first = await api.AskAsync(demotee, board, Ct);
        for (var i = 0; i < 2; i++) Assert.Equal(201, (await api.AskAsync(demotee, board, Ct)).Status);
        forum.Clock.Advance(TimeSpan.FromHours(49));
        var flagger = await api.GenuineAsync("flagger", Ct);
        var flagId = await api.RaiseFlagAsync(flagger, first.PostId, Ct);

        // Control, same observer: with the flag only raised the agent is T1 (accepted) and the flag is open.
        var openState = await api.FlagStateAsync(first.PostId, Ct);
        WholeText.Same("answer 201 (accepted)", "answer " + OperatorActsApi.Said(await api.AnswerAsync(demotee, board, target, Ct)));
        Assert.Contains("open", openState, StringComparison.Ordinal);

        await api.ForgeModerationAsync(first.PostId, first.Digest, "withhold", Ct, adjudicates: [flagId]);
        var answer = OperatorActsApi.Said(await api.AnswerAsync(demotee, board, target, Ct));
        var state = await api.FlagStateAsync(first.PostId, Ct);
        OperatorActsApi.Log($"OAS4 flag state before {openState}; after the forged record {state}");
        WholeText.Same($"answer 201 (accepted); flag {openState}", $"answer {answer}; flag {state}");
    }

    // ---- OAS5 ----------------------------------------------------------------------------------

    [Fact]
    public async Task OAS5_AForgedFirstAttestationDoesNotBindAnOwner()
    {
        var api = Api;
        // Control: an attestation of an agent no forged row touched is recorded.
        var clean = await api.GenuineAsync("clean", Ct);
        WholeText.Same("recorded", await api.GenuineAttestResultAsync(clean.Agent.AgentId, "owner:alice", Ct));

        var poisoned = await api.GenuineAsync("poisoned", Ct);
        await api.ForgeAttestationNowAsync(poisoned.Agent.AgentId, "owner:bob", Ct, verified: false);
        var before = await api.EventCountAsync(Ct);
        var outcome = await api.GenuineAttestResultAsync(poisoned.Agent.AgentId, "owner:alice", Ct);
        WholeText.Same("recorded", outcome);
        Assert.Equal(before + 1, await api.EventCountAsync(Ct));
    }

    // ---- OAS6 ----------------------------------------------------------------------------------

    [Fact]
    public async Task OAS6_AForgedOwnerChangesNoVerification()
    {
        var api = Api;
        var scene = await AcceptedAnswerScene.BuildAsync(forum, Ct);
        var board = scene.Board;

        async Task<string> VoteAsync(OperatorActsApi.Actor a, ScenePost target) =>
            OperatorActsApi.Said(await api.PostRawAsync(a, a.Agent.SignVote(board, target.Digest, forum.Now), Ct));
        static OperatorActsApi.Actor Conv(SceneActor a) => new(a.Agent, a.Dpop);

        // Control: two genuine T1 voters under distinct genuine owners make the answer V1.
        var g1 = Conv(await AcceptedAnswerScene.T1Async(forum, board, Ct));
        var g2 = Conv(await AcceptedAnswerScene.T1Async(forum, board, Ct));
        Assert.Equal("201 (accepted)", await VoteAsync(g1, scene.Answer1));
        Assert.Equal("201 (accepted)", await VoteAsync(g2, scene.Answer1));
        WholeText.Same("V1", await api.LevelAsync(scene.Answer1.PostId, Ct));

        async Task<OperatorActsApi.Actor> ForgedT1Async(string stem, string owner)
        {
            var f = await api.ForgeAsync(stem, TimeSpan.FromHours(72), attest: true, owner, Ct);
            for (var i = 0; i < 3; i++) await api.AskAsync(f, board, Ct);
            return f;
        }

        // Leg 1: two agents, no person, forged distinct owners.
        var f1 = await ForgedT1Async("forged-1", "owner:fake-1-" + OperatorActsApi.Suffix());
        var f2 = await ForgedT1Async("forged-2", "owner:fake-2-" + OperatorActsApi.Suffix());
        await VoteAsync(f1, scene.Answer2);
        await VoteAsync(f2, scene.Answer2);
        var level = await api.LevelAsync(scene.Answer2.PostId, Ct);

        // Leg 2: a voter pre-poisoned with the author's owner is refused (same-owner); with no forgery it votes.
        var author = await api.ProvenanceAsync(scene.Answer1.PostId, Ct);
        var legit = await api.GenuineAsync("legit-voter", Ct);

        // Task 3b (T3bF7, a probe that did not go red): the forged row names the author's owner and comes FIRST (the first binding is immutable, R4.1, so a raw reader keeps it), then the voter's own genuine attestation
        // (another owner) is recorded, which the writer does because the forged row is skipped (OAS5). The voter is T1 on its own attestation; a consumer that reads the forged owner refuses its vote as the author's.
        // Without a genuine attestation the voter is T0 and "with no forgery it votes" is false of any T0 voter.
        await api.ForgeAttestationNowAsync(legit.Agent.AgentId, author.Owner ?? "owner:none", Ct);
        await api.GenuineAttest(legit.Agent.AgentId, Ct, "owner:legit-" + OperatorActsApi.Suffix());
        for (var i = 0; i < 3; i++) await api.AskAsync(legit, board, Ct);
        forum.Clock.Advance(TimeSpan.FromHours(49));
        var legitVote = await VoteAsync(legit, scene.Answer2);

        WholeText.Same("V0; 201 (accepted)", $"{level}; {legitVote}");

        // Leg 3 (T3bF8, a probe that did not go red): two voters, each pre-poisoned with the same forged owner (first) and then genuinely attested under owners of their own, vote on a third answer: it is V1,
        // as the two genuine voters of the control made one V1. A reader that takes the owners from the forged rows sees one owner for both and says V0.
        var third = await api.PostRawAsync(Conv(scene.Answerer), scene.Answerer.Agent.SignAnswer(board, "The third way " + Probe.Unrelated(), scene.Question.PostId, forum.Now), Ct);
        Assert.Equal(201, third.Status);
        var sharedForgedOwner = "owner:shared-forged-" + OperatorActsApi.Suffix();
        var poisoned = new List<OperatorActsApi.Actor>();
        for (var n = 0; n < 2; n++)
        {
            var voter = await api.GenuineAsync("poisoned-voter-" + n, Ct);
            await api.ForgeAttestationNowAsync(voter.Agent.AgentId, sharedForgedOwner, Ct);
            await api.GenuineAttest(voter.Agent.AgentId, Ct, "owner:own-" + n + "-" + OperatorActsApi.Suffix());
            for (var i = 0; i < 3; i++) await api.AskAsync(voter, board, Ct);
            poisoned.Add(voter);
        }

        forum.Clock.Advance(TimeSpan.FromHours(49));
        foreach (var voter in poisoned)
            Assert.Equal("201 (accepted)", OperatorActsApi.Said(await api.PostRawAsync(voter, voter.Agent.SignVote(board, third.Digest, forum.Now), Ct)));
        WholeText.Same("V1", await api.LevelAsync(third.PostId, Ct));
    }

    // ---- OAS25 (owner and served leg), OAS33 ---------------------------------------------------

    [Fact]
    public async Task OAS25_RowsWithNoStatementAreSkipped()
    {
        var api = Api;
        var agent = await api.GenuineAsync("unsigned", Ct);
        var asked = await api.AskAsync(agent, OperatorActsApi.Board(), Ct);
        await api.ForgeAttestationNowAsync(agent.Agent.AgentId, "owner:alice", Ct);
        var post = await api.QuestionAsync("unsigned-post", Ct);
        await api.ForgeModerationAsync(post.Id, post.Digest, "withhold", Ct);
        var prov = await api.ProvenanceAsync(asked.PostId, Ct);
        WholeText.Same(
            "owner none; verified false; post served 200",
            $"owner {(prov.Owner is null ? "none" : prov.Owner)}; verified {prov.OwnerVerified?.ToString().ToLowerInvariant()}; post served {await PostStatusAsync(api, post.Id)}");
    }

    public static TheoryData<string, string> MalformedActPayloads => new()
    {
        { "agent.owner-attested", "\"not an object\"" },
        { "agent.owner-attested", "[1,2,3]" },
        { "agent.owner-attested", "{\"owner_id\":7}" },
        { "agent.owner-attested", "{\"statement\":5,\"authorization\":{}}" },
        { "moderation.applied", "\"not an object\"" },
        { "moderation.applied", "{\"effect\":[],\"moderator\":null}" },
        { "moderation.applied", "{\"statement\":\"{}\",\"authorization\":\"x\"}" },
    };

    [Theory]
    [MemberData(nameof(MalformedActPayloads))]
    public async Task OAS33_NoRequestIsRefusedForAnActsContent(string type, string payload)
    {
        var api = Api;
        var board = OperatorActsApi.Board();
        var agent = await api.GenuineAsync("untouched", Ct);
        var post = await api.QuestionAsync("served", Ct);
        var aggregate = type == "agent.owner-attested" ? agent.Agent.AgentId : post.Id;
        await api.InsertRowAsync(type, aggregate, payload, "operator:ops", forum.Now, Ct);

        var token = await api.TokenAsync(agent, Ct);
        var ask = await api.AskAsync(agent, board, Ct);
        var keySet = await api.GetStatusAsync($"/v1/jwks?agent={Uri.EscapeDataString(agent.Agent.AgentId)}", Ct);
        var served = await PostStatusAsync(api, post.Id);
        WholeText.Same(
            "token 200; ask 201; key set 200; post 200",
            $"token {token.Reply.Status}; ask {ask.Status}; key set {keySet}; post {served}");
    }

    // ---- OAS33c: the vectors' skipped rows, as a request meets them (Task 3b, ruling 80 (b)) ------------------

    private static string OperatorActsVectors()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curia.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "conformance", "operator-acts");
    }

    /// <summary>The cases of <c>conformance/operator-acts/</c> whose <c>expected.json</c> has at least one skipped act row, each with the log indices of those rows: parsed from the files, never listed here.</summary>
    private static List<(string Case, int[] SkippedIndices)> CasesWithSkippedRows() =>
        [.. Directory.GetDirectories(OperatorActsVectors()).Order(StringComparer.Ordinal).Select(d =>
        {
            using var expected = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(d, "expected.json")));
            var skipped = expected.RootElement.GetProperty("rows").EnumerateArray()
                .Where(r => r.GetProperty("verdict").GetString() == "skipped").Select(r => r.GetProperty("log_index").GetInt32()).ToArray();
            return (Path.GetFileName(d), skipped);
        }).Where(c => c.skipped.Length > 0)];

    public static TheoryData<string> CasesWithSkipped()
    {
        var data = new TheoryData<string>();
        foreach (var (name, _) in CasesWithSkippedRows()) data.Add(name);
        return data;
    }

    /// <summary>
    /// OAS33c: for every case of the vectors that has a skipped act row (61 today, 63 rows), that case's skipped rows are appended onto a live agent's or a live post's own stream (the aggregate and every
    /// occurrence of the case's subject id in the row, statement text included, replaced by the live id), as the application role; then a token request is 200, an ask is 201, the key set is 200 and the served post
    /// is 200, as they were before the rows. The vacuity guard (ruling 80 (b)): the case count equals the count parsed from the files, and the Forum's own audit lists exactly as many more skipped rows after the
    /// insert as were inserted, so a probe whose rows the Forum never reads cannot pass.
    /// </summary>
    [Theory]
    [MemberData(nameof(CasesWithSkipped))]
    public async Task OAS33c_NoRequestIsRefusedForTheSkippedRowsOfTheVectors(string caseName)
    {
        ArgumentNullException.ThrowIfNull(caseName);
        var api = Api;
        var cases = CasesWithSkippedRows();
        Assert.Equal(cases.Count, CasesWithSkipped().Count);
        var skippedIndices = cases.Single(c => c.Case == caseName).SkippedIndices;
        Assert.NotEmpty(skippedIndices);

        var board = OperatorActsApi.Board();
        var agent = await api.GenuineAsync("c33c", Ct);
        var post = await api.QuestionAsync("c33c-post", Ct);

        async Task<string> ObserveAsync(bool ask) =>
            $"token {(await api.TokenAsync(agent, Ct)).Reply.Status}; ask {(ask ? (await api.AskAsync(agent, board, Ct)).Status : 201)}; key set {await api.GetStatusAsync($"/v1/jwks?agent={Uri.EscapeDataString(agent.Agent.AgentId)}", Ct)}; post {await PostStatusAsync(api, post.Id)}";
        WholeText.Same("token 200; ask 201; key set 200; post 200", await ObserveAsync(ask: false));

        var listedBefore = await api.SkippedRowsAsync(Ct);
        foreach (var index in skippedIndices)
        {
            using var entryDoc = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(OperatorActsVectors(), caseName, "entries", $"{index}.json"), Ct));
            var entry = entryDoc.RootElement.GetProperty("entry");
            var type = entry.GetProperty("event_type").GetString()!;
            var subject = entry.GetProperty("aggregate_id").GetString()!;
            var live = type == "agent.owner-attested" ? agent.Agent.AgentId : post.Id;
            var payload = entry.GetProperty("payload").GetRawText().Replace(subject, live, StringComparison.Ordinal);
            await api.InsertRowAsync(type, live, payload, entry.GetProperty("actor_id").GetString()!, forum.Now, Ct);
        }

        Assert.Equal(listedBefore + skippedIndices.Length, await api.SkippedRowsAsync(Ct));
        WholeText.Same("token 200; ask 201; key set 200; post 200", await ObserveAsync(ask: true));
    }

    // ---- OAS34 to OAS36 -------------------------------------------------------------------------

    [Fact]
    public async Task OAS34_TheTokensTierClaimReadsHonouredActs()
    {
        var api = Api;
        var board = OperatorActsApi.Board();
        var control = await api.GenuineT1Async("genuine-t1", board, Ct);
        WholeText.Same("T1", (await api.TokenAsync(control, Ct)).Tier ?? "no token");

        var forged = await api.ForgeAsync("forged", TimeSpan.FromHours(72), attest: true, "owner:forged-" + OperatorActsApi.Suffix(), Ct);
        for (var i = 0; i < 3; i++) await api.AskAsync(forged, board, Ct);
        var token = await api.TokenAsync(forged, Ct);
        WholeText.Same("tier=T0", $"tier={token.Tier ?? "no token: " + token.Reply.Words}");
    }

    [Fact]
    public async Task OAS35_TheReannouncementDoesNotSayOwnerVerifiedForAForgedAttestation()
    {
        var api = Api;
        var genuine = await api.GenuineAsync("attested", Ct);
        await api.GenuineAttest(genuine.Agent.AgentId, Ct, "owner:genuine-" + OperatorActsApi.Suffix());
        WholeText.Same("201 True", await api.EnrollBodyMemberAsync(genuine.Agent, "owner_verified", Ct));

        var forged = await api.ForgeAsync("forged", TimeSpan.FromHours(72), attest: true, "owner:forged-" + OperatorActsApi.Suffix(), Ct);
        WholeText.Same("201 False", await api.EnrollBodyMemberAsync(forged.Agent, "owner_verified", Ct));
    }

    [Fact]
    public async Task OAS36_AForgedWithholdFreesNoDuplicate()
    {
        var api = Api;
        var board = OperatorActsApi.Board();
        const string body = "How do I rotate the signing key of a long running service without dropping in flight requests?";
        var title = "Rotating a signing key " + OperatorActsApi.Suffix();

        async Task<(OperatorActsApi.Reply Original, string Digest)> OriginalAsync()
        {
            var o = await api.GenuineAsync("original", Ct);
            var r = await api.PostRawAsync(o, o.Agent.SignQuestion(board, body, title, forum.Now), Ct);
            Assert.Equal(201, r.Status);
            return (r, r.Digest);
        }

        // Task 3d (ruling 90, H-sim): the duplicate is the original's text under another author and nonce. It was the body plus " Please advise.", whose cosine to the original under the test adapter (hashed
        // n-grams, 256 buckets) depends on the random title suffix and fell under R8.18's 0.94 for about one suffix in thirteen, so the Forum was right to answer 201 and the probe failed intermittently.
        // The same text is a duplicate whatever the suffix and whatever the embedder; the near-duplicate band is DedupeEndpointTests'.
        async Task<OperatorActsApi.Reply> DuplicateAsync(OperatorActsApi.Actor? enrolled = null)
        {
            var d = enrolled ?? await api.GenuineAsync("dup", Ct);
            return await api.PostRawAsync(d, d.Agent.SignQuestion(board, body, title, forum.Now), Ct);
        }

        // Control: the genuine withhold of the original frees the duplicate (201); while the original is served the duplicate is refused (409).
        var genuine = await OriginalAsync();
        var control = await DuplicateAsync();
        WholeText.Same("409 curia/posts/duplicate-question", $"{control.Status} {control.Type}");
        await api.GenuineWithhold(genuine.Original.PostId, Ct);
        Assert.Equal(201, (await DuplicateAsync()).Status);

        // The forgery: another original, withheld by a forged row; the duplicate must still be refused.
        board = OperatorActsApi.Board();
        var forgedOriginal = await OriginalAsync();
        await api.ForgeModerationAsync(forgedOriginal.Original.PostId, forgedOriginal.Digest, "withhold", Ct);
        // Task 3b (ruling 79 (b), a count that moves by rows the arrangement itself appends): the duplicate's author is enrolled first, so that the count compares the refused request alone.
        var duplicateAuthor = await api.GenuineAsync("dup", Ct);
        var before = await api.EventCountAsync(Ct);
        var dup = await DuplicateAsync(duplicateAuthor);
        WholeText.Same("409 curia/posts/duplicate-question", $"{dup.Status} {dup.Type}");
        Assert.Equal(before, await api.EventCountAsync(Ct));
    }

    // ---- OAS39 (characterization, green by design) ----------------------------------------------

    /// <summary>
    /// OAS39, a characterization of OPEN defects, green on the tree and green after N2 by design (Amendment 1, ruling 28): the unsigned enrolment root is not an operator act, and two effects of it stand as filed.
    /// Register item 293 (tenure and squatting: a backdated root buys the 48 hours; a root planted before an identity enrols squats it, <c>409</c>). Item 299 (a second <c>agent.enrolled</c> row) is closed by errata G80
    /// (R4.48): the standing reads only the honoured root, and SRS12 is where its probe lives. The fix of either remaining effect must edit this probe; it is asserted in the layer where the fix would land (the token
    /// endpoint and the enrolment route, through the Forum). It is not counted among the probes that must be red.
    /// </summary>
    [Fact]
    public async Task OAS39_TheUnsignedRootsTwoLimitsAreAsFiled()
    {
        var api = Api;

        // (ii) a root planted before the identity enrols: its genuine enrolment is 409 (item 293, squatting).
        var squatted = ForumAgent.Create($"https://agents.example/planted-{OperatorActsApi.Suffix()}", "k-" + OperatorActsApi.Suffix());
        var planted = $"{{\"agent_id\":\"{squatted.AgentId}\",\"kid\":\"other\",\"reason\":\"Enrollment accepted: agent key registered with the Registrar\"}}";
        await api.InsertRowAsync("agent.enrolled", squatted.AgentId, planted, squatted.AgentId, forum.Now - TimeSpan.FromHours(72), Ct);
        WholeText.Same("409 curia/enroll/already-enrolled", (await api.EnrollBodyMemberAsync(squatted, "none", Ct)));

        // (iii) a backdated root: the enrolment instant served is the planted instant (item 293, tenure).
        var backdated = await api.ForgeAsync("backdated", TimeSpan.FromHours(72), attest: false, "owner:none", Ct);
        var instant = (forum.Now - TimeSpan.FromHours(72)).UtcDateTime;
        var served = await api.EnrollBodyMemberAsync(backdated.Agent, "enrolled_at", Ct);
        Assert.StartsWith("201 ", served, StringComparison.Ordinal);
        Assert.True(DateTimeOffset.Parse(served[4..], System.Globalization.CultureInfo.InvariantCulture).UtcDateTime == instant, $"enrolled_at served {served}, planted {instant:o}");
    }

    // ---- OAS20 (the Api half) -------------------------------------------------------------------

    [Fact]
    public async Task OAS20_TheLogKeySetCarriesOperatorKeys()
    {
        var api = Api;

        // Task 4 (a literal that moves by the one log.operator-key event): the fixture publishes its own operator key before its first signed act, and which test of this class meets the log first
        // is not this probe's to know, so the key is published here and the member is expected to carry that one entry (it was an empty array while no writer signed).
        await forum.OperatorActsKeyAsync(Ct);
        var body = await api.GetBodyAsync("/v1/log/jwks", Ct);
        var json = body[(body.IndexOf(' ') + 1)..];
        using var doc = JsonDocument.Parse(json);
        var present = doc.RootElement.TryGetProperty("operator_keys", out var keys);
        var actual = !present ? "operator_keys member absent" : keys.ValueKind == JsonValueKind.Array && keys.GetArrayLength() == 1 ? "operator_keys present (one entry)" : "operator_keys present (" + keys + ")";
        WholeText.Same("operator_keys present (one entry)", actual);
        _ = Probe.Unrelated();
    }
}
