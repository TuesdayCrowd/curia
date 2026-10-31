using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Curia.Api.Tests.Fuzz;
using Curia.Domain.Content;
using Curia.Domain.Moderation;
using Curia.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

using Probe = Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>
/// Errata G49 (R7.28, R9.50) against a real Forum on real Postgres: <c>GET /v1/budget</c>, SEB11 to SEB18, SEB25, SEB28 (the promotion leg), SEB31, SEB32,
/// SEB33, SEB35 and SEB54 to SEB55. (SEB19 and SEB21 are in <see cref="BudgetReadoutGateEndpointTests"/>, SEB26, SEB29, SEB30 and SEB53 in
/// <see cref="BudgetReadoutSeamsEndpointTests"/>, SEB20 and SEB22 to SEB24 in <see cref="BudgetReceiptEndpointTests"/>.)
///
/// <para><b>Oracles.</b> The <c>events</c> table (and <c>flag_details</c> for flags) read by SQL from the fixture's own connection string, with R7.28's arithmetic
/// written out in <see cref="BudgetReadouts.Expected"/>; never a response's claim about itself. The names, members, order, window, header, problem types
/// and credential words are the errata's (<see cref="PublishedBudgetReadout"/>), the limits are Table 11's, R7.26's and R7.22's.</para>
///
/// <para><b>Red text on the seam tree:</b> <c>GET /v1/budget</c> is an unmapped <c>404</c> with no body (ruling 20: no stub route), so each positive fact is red
/// by assertion ("Expected: 200 Actual: 404 ...") and each negative fact is red because it asserts a status or a type the unmapped route does not give.
/// Every fact that asserts an absence carries its positive in the same fact (ruling 12).</para>
/// </summary>
[Collection("forum")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class BudgetReadoutEndpointTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> BudgetNames => new(BudgetReadouts.Budgets);

    private static void Clean(List<string> problems) => Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));

    private static bool IsInteger(string number) => number.Length > 0 && number.All(char.IsAsciiDigit);

    /// <summary>Every number token of the raw body, as written (a JSON reader that hides <c>2.0</c> would hide the difference).</summary>
    private static List<string> NumberTokens(string raw)
    {
        var tokens = new List<string>();
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(raw));
        while (reader.Read())
            if (reader.TokenType == JsonTokenType.Number)
                tokens.Add(Encoding.UTF8.GetString(reader.ValueSpan));

        return tokens;
    }

    /// <summary>
    /// SEB11. The contract: an authenticated T0 agent reads <c>200</c>; the body's members are exactly R9.50's; <c>budgets</c> has the three names in
    /// R9.50's order, each with exactly the entry members; every number is a JSON integer by its raw token; <c>window_seconds</c> is R7.28's; every instant
    /// parses back to itself in the Forum's form; <c>as_of</c> is the fixture's clock; and (ruling 17) <c>credential</c> is present and <c>active</c>.
    /// </summary>
    [Fact]
    public async Task R9_50_SEB11_TheContract()
    {
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "reader", Ct);

        var reply = await BudgetReadouts.ReadAsync(forum, actor);

        var problems = new List<string>();
        var members = reply.Body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
        if (!members.SequenceEqual(PublishedBudgetReadout.ResponseMembers().Order(StringComparer.Ordinal), StringComparer.Ordinal))
            problems.Add($"members: Expected: {string.Join(',', PublishedBudgetReadout.ResponseMembers())} Actual: {string.Join(',', members)}");

        var budgets = reply.Body.GetProperty("budgets").EnumerateArray().ToArray();
        if (!budgets.Select(b => b.GetProperty("name").GetString()).SequenceEqual(PublishedBudgetReadout.Names()))
            problems.Add($"budget names: Expected: {string.Join(',', PublishedBudgetReadout.Names())} Actual: {string.Join(',', budgets.Select(b => b.GetProperty("name").GetString()))}");

        foreach (var entry in budgets)
        {
            var entryMembers = entry.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
            if (!entryMembers.SequenceEqual(PublishedBudgetReadout.EntryMembers().Order(StringComparer.Ordinal), StringComparer.Ordinal))
                problems.Add($"entry members: Expected: {string.Join(',', PublishedBudgetReadout.EntryMembers())} Actual: {string.Join(',', entryMembers)}");
        }

        foreach (var token in NumberTokens(reply.Raw).Where(t => !IsInteger(t)))
            problems.Add($"number token: Expected: a JSON integer Actual: {token}");

        if (reply.Body.GetProperty("window_seconds").GetRawText() != PublishedBudgetReadout.WindowSeconds().ToString(CultureInfo.InvariantCulture))
            problems.Add($"window_seconds: Expected: {PublishedBudgetReadout.WindowSeconds()} Actual: {reply.Body.GetProperty("window_seconds").GetRawText()}");

        if (reply.Body.GetProperty("as_of").GetString() != BudgetReadouts.Form(forum.Now))
            problems.Add($"as_of: Expected: {BudgetReadouts.Form(forum.Now)} Actual: {reply.Body.GetProperty("as_of").GetString()}");

        if (reply.Body.GetProperty("credential").GetString() != PublishedBudgetReadout.CredentialWords()[0])
            problems.Add($"credential: Expected: {PublishedBudgetReadout.CredentialWords()[0]} Actual: {reply.Body.GetProperty("credential").GetString()}");

        if (reply.Body.GetProperty("tier").GetString() != "T0")
            problems.Add($"tier: Expected: T0 Actual: {reply.Body.GetProperty("tier").GetString()}");

        Clean(problems);
    }

    /// <summary>
    /// SEB12. The oracle grid: tier {T0, T1, T2} x budget x state {fresh, partly spent, exactly spent}, 27 cells, each seeded through the store seam (posts and
    /// acceptances) or by admin SQL (flags) at chosen instants and read once; the entry equals what the events table (and the flag table) say, the limit
    /// parsed from Table 11, R7.26 and R7.22, and the seeded state is itself asserted (so a readout returning zeros or the limit is red in two of three
    /// states).
    /// </summary>
    [Fact]
    public async Task R7_28_SEB12_TheOracleGrid()
    {
        var board = Probe.Board();
        var states = new[] { "fresh", "partly", "exactly" };
        var agents = new Dictionary<(string Tier, string State), SceneActor>();
        foreach (var tier in BudgetReadouts.Tiers)
            foreach (var state in states)
                agents[(tier, state)] = await BudgetBursts.AgentOfAsync(forum, tier, board, Ct);

        var planned = new Dictionary<(string Tier, string Budget, string State), int>();
        foreach (var ((tier, state), actor) in agents)
        {
            foreach (var budget in BudgetReadouts.Budgets)
            {
                var limit = BudgetReadouts.LimitOf(tier, budget);
                var used = state switch { "fresh" => 0, "partly" => Math.Max(1, limit / 2), _ => limit };
                planned[(tier, budget, state)] = used;
                await SeedAsync(actor, board, budget, used);
            }
        }

        var problems = new List<string>();
        foreach (var ((tier, state), actor) in agents)
        {
            var reply = await BudgetReadouts.GetAsync(forum, actor);
            if (reply.Status != HttpStatusCode.OK)
            {
                problems.Add($"{tier}/{state}: Expected: 200 Actual: {reply.Says}");
                continue;
            }

            foreach (var budget in BudgetReadouts.Budgets)
            {
                var oracle = await BudgetReadouts.OracleAsync(forum, tier, budget, actor.Agent.AgentId);
                if (oracle.Used != planned[(tier, budget, state)]) problems.Add($"{tier}/{budget}/{state}: seeded {oracle.Used}, planned {planned[(tier, budget, state)]}");

                var actual = BudgetReadouts.Describe(reply.Entry(budget));
                if (actual != oracle.Describe()) problems.Add($"{tier}/{budget}/{state}: Expected: {oracle.Describe()} Actual: {actual}");
            }
        }

        Clean(problems);
    }

    /// <summary>Spends <paramref name="used"/> of an agent's budget without a request per act, at instants a minute apart.</summary>
    private async Task SeedAsync(SceneActor actor, string board, string budget, int used)
    {
        if (used == 0) return;
        switch (budget)
        {
            case "posting":
                for (var i = 0; i < used; i++)
                {
                    forum.Clock.Advance(TimeSpan.FromSeconds(1));
                    await BudgetBursts.SeedPostsAsync(forum, actor.Agent, board, 1, Ct);
                }

                break;

            case "acceptance":
                await BudgetReadouts.SeedAcceptsAsync(forum, actor.Agent.AgentId, board, [.. Enumerable.Range(0, used).Select(i => forum.Now - TimeSpan.FromMinutes(used - i))]);
                break;

            default:
            {
                var post = await BudgetBursts.StoredQuestionAsync(forum, board, Ct);
                var first = await BudgetReadouts.FlagAsync(forum, actor, post.PostId, FlagKind.Spam);
                Assert.True(first.Status == 201, $"Expected: the first flag stored Actual: {first.Says}");
                await BudgetReadouts.CloneFlagsAsync(forum, actor.Agent.AgentId, [.. Enumerable.Range(1, used - 1).Select(i => forum.Now - TimeSpan.FromMinutes(i))]);
                break;
            }
        }
    }

    /// <summary>
    /// SEB13. The boundary, behavioural, per budget, with a settable clock that advances in sub-microsecond ticks and an expected instant read from the
    /// events table (the column plus the window), never the clock's: spend to the limit; the readout states <c>remaining</c> 0 and <c>next_slot_at</c> =
    /// the oldest stored instant + 86,400 s; one tick before it the next act of that kind is refused with that budget's reason; at it the act is permitted;
    /// the readout then states <c>remaining</c> 0 again and the next oldest + 86,400 s.
    /// </summary>
    [Theory]
    [MemberData(nameof(BudgetNames))]
    public async Task R7_28_SEB13_TheBoundaryBehaviourallyPerBudget(string budget)
    {
        var acts = await BudgetActor.T0Async(forum, budget, Ct);
        var limit = BudgetReadouts.LimitOf("T0", budget);
        await acts.SpendAsync(limit);

        var spent = await BudgetReadouts.ReadAsync(forum, acts.Actor);
        var instants = await BudgetReadouts.InstantsAsync(forum, budget, acts.Actor.Agent.AgentId);
        var oldest = instants[^limit];
        Probe.Said(
            $"limit={limit} used={limit} remaining=0 next_slot_at={BudgetReadouts.Form(oldest + BudgetReadouts.Window)}",
            BudgetReadouts.Describe(spent.Entry(budget)));

        BudgetReadouts.MoveTo(forum, oldest + BudgetReadouts.Window - TimeSpan.FromTicks(1));
        var early = await acts.NextAsync();
        Assert.True(early.Status == 403 && early.Detail!.StartsWith(ReasonOf(budget), StringComparison.Ordinal), $"Expected: 403 {ReasonOf(budget)} one tick before the instant Actual: {early.Says}");

        forum.Clock.Advance(TimeSpan.FromTicks(1));
        var at = await acts.NextAsync();
        Assert.True(at.Status is 200 or 201, $"Expected: the act permitted at the instant Actual: {at.Says}");

        var after = await BudgetReadouts.ReadAsync(forum, acts.Actor);
        var next = (await BudgetReadouts.InstantsAsync(forum, budget, acts.Actor.Agent.AgentId))[^limit];
        Probe.Said(
            $"limit={limit} used={limit} remaining=0 next_slot_at={BudgetReadouts.Form(next + BudgetReadouts.Window)}",
            BudgetReadouts.Describe(after.Entry(budget)));
    }

    /// <summary>The policy's denial reason of a budget, from the policy's own enumeration filtered by name: <c>rate</c> for posting, otherwise the budget's name.</summary>
    internal static string ReasonOf(string budget) =>
        Curia.Domain.Authorization.AccessPolicy.DenialReasons.Single(r =>
            r.StartsWith("table-11/", StringComparison.Ordinal) && r.EndsWith("-budget-exhausted", StringComparison.Ordinal)
            && (budget == "posting" ? r.Contains("rate", StringComparison.Ordinal) : r.Contains(budget == "acceptance" ? "accept" : "flag", StringComparison.Ordinal)));

    /// <summary>
    /// SEB14. <c>remaining</c> is the refusal, both ways, per budget: at <c>remaining</c> 1 the next act is permitted and the one after is refused; the readout
    /// after each agrees (1, then 0, then 0 with the limit used).
    /// </summary>
    [Theory]
    [MemberData(nameof(BudgetNames))]
    public async Task R7_28_SEB14_RemainingIsTheRefusalBothWays(string budget)
    {
        var acts = await BudgetActor.T0Async(forum, budget, Ct);
        var limit = BudgetReadouts.LimitOf("T0", budget);
        await acts.SpendAsync(limit - 1);

        var one = await BudgetReadouts.ReadAsync(forum, acts.Actor);
        Assert.Equal(1, one.Entry(budget).GetProperty("remaining").GetInt32());

        var permitted = await acts.NextAsync();
        Assert.True(permitted.Status is 200 or 201, $"Expected: the act permitted at remaining 1 Actual: {permitted.Says}");
        var zero = await BudgetReadouts.ReadAsync(forum, acts.Actor);
        Assert.Equal(0, zero.Entry(budget).GetProperty("remaining").GetInt32());

        var refused = await acts.NextAsync();
        Assert.True(refused.Status == 403 && refused.Detail!.StartsWith(ReasonOf(budget), StringComparison.Ordinal), $"Expected: 403 {ReasonOf(budget)} at remaining 0 Actual: {refused.Says}");
        var still = await BudgetReadouts.ReadAsync(forum, acts.Actor);
        Probe.Said($"used={limit} remaining=0", $"used={still.Entry(budget).GetProperty("used").GetInt32()} remaining={still.Entry(budget).GetProperty("remaining").GetInt32()}");
    }

    /// <summary>
    /// SEB15. Own-only, built on the flag budget first: agent A (a scene's asker: its posts, one acceptance, two flags) and agent B (none) read with their own
    /// tokens; each sees its own numbers in all three budgets, equal to the tables'; A's numbers differ from B's in every budget (so a readout of the wrong
    /// agent is told apart); and no body contains any enrolled agent's identifier.
    /// </summary>
    [Fact]
    public async Task R7_28_SEB15_OwnOnly()
    {
        var scene = await AcceptedAnswerScene.BuildAsync(forum, Ct);
        var a = scene.Asker;
        var b = await AcceptedAnswerScene.EnrolAsync(forum, "bystander", Ct);
        Assert.Equal(HttpStatusCode.Created, await scene.AcceptAsync(forum, scene.Answer1, Ct));
        var target = await BudgetBursts.StoredQuestionAsync(forum, scene.Board, Ct);
        foreach (var kind in new[] { FlagKind.Spam, FlagKind.Incorrect })
            Assert.Equal(201, (await BudgetReadouts.FlagAsync(forum, a, target.PostId, kind)).Status);

        var problems = new List<string>();
        var replyA = await BudgetReadouts.GetAsync(forum, a);
        var replyB = await BudgetReadouts.GetAsync(forum, b);
        foreach (var (who, actor, reply) in new[] { ("A", a, replyA), ("B", b, replyB) })
        {
            if (reply.Status != HttpStatusCode.OK)
            {
                problems.Add($"{who}: Expected: 200 Actual: {reply.Says}");
                continue;
            }

            foreach (var budget in BudgetReadouts.Budgets)
            {
                var oracle = await BudgetReadouts.OracleAsync(forum, "T0", budget, actor.Agent.AgentId);
                if (BudgetReadouts.Describe(reply.Entry(budget)) != oracle.Describe()) problems.Add($"{who}/{budget}: Expected: {oracle.Describe()} Actual: {BudgetReadouts.Describe(reply.Entry(budget))}");
            }

            foreach (var id in new[] { a.Agent.AgentId, b.Agent.AgentId, scene.Answerer.Agent.AgentId })
                if (reply.Raw.Contains(id, StringComparison.Ordinal)) problems.Add($"{who}: the body contains the agent identifier {id}");
        }

        if (replyA.Status == HttpStatusCode.OK && replyB.Status == HttpStatusCode.OK)
            foreach (var budget in BudgetReadouts.Budgets)
                if (replyA.Entry(budget).GetProperty("used").GetInt32() == replyB.Entry(budget).GetProperty("used").GetInt32())
                    problems.Add($"{budget}: A and B state the same count; the fact cannot tell whose numbers were read");

        Clean(problems);
    }

    /// <summary>
    /// SEB16. It cannot be told whose to read: every query name the Forum reads anywhere (<see cref="SurfaceInventory.QueryParameters"/>) and
    /// <c>agent</c>, <c>agent_id</c>, <c>sub</c> and <c>id</c>, with B's token and A's identifier as the value, is <c>400</c> with R9.50's type naming the query
    /// name and no budget member; <c>/v1/budget/&lt;id&gt;</c> and <c>/v1/agents/&lt;id&gt;/budget</c> are not routes and answer no budget body; in the same
    /// fact the real route answers <c>200</c>.
    /// </summary>
    [Fact]
    public async Task R9_50_SEB16_ItCannotBeToldWhoseToRead()
    {
        var a = await AcceptedAnswerScene.EnrolAsync(forum, "owner-a", Ct);
        var b = await AcceptedAnswerScene.EnrolAsync(forum, "reader-b", Ct);
        var problems = new List<string>();

        var real = await BudgetReadouts.GetAsync(forum, b);
        if (real.Status != HttpStatusCode.OK) problems.Add($"the real route: Expected: 200 Actual: {real.Says}");

        foreach (var name in SurfaceInventory.QueryParameters.Concat(["agent", "agent_id", "sub", "id"]).Distinct(StringComparer.Ordinal))
        {
            var reply = await BudgetReadouts.GetAsync(forum, b, $"/v1/budget?{Uri.EscapeDataString(name)}={Uri.EscapeDataString(a.Agent.AgentId)}");
            if (reply.Status != HttpStatusCode.BadRequest || reply.Type != PublishedBudgetReadout.UnknownParameterType() || !reply.Raw.Contains(name, StringComparison.Ordinal) || reply.Raw.Contains("\"budgets\"", StringComparison.Ordinal))
                problems.Add($"?{name}=: Expected: 400 {PublishedBudgetReadout.UnknownParameterType()} naming `{name}` Actual: {reply.Says}");
        }

        foreach (var path in new[] { $"/v1/budget/{Uri.EscapeDataString(a.Agent.AgentId)}", $"/v1/agents/{Uri.EscapeDataString(a.Agent.AgentId)}/budget" })
        {
            var reply = await BudgetReadouts.GetAsync(forum, b, path);
            if (reply.Raw.Contains("\"budgets\"", StringComparison.Ordinal) || reply.Status == HttpStatusCode.OK) problems.Add($"{path}: Expected: no budget body Actual: {reply.Says}");
        }

        Clean(problems);
    }

    /// <summary>
    /// SEB17 (written in full, ruling 24: nothing existing is generic over routes). The token binds: no token is <c>401</c>; a token with a proof signed by
    /// another agent's key is <c>401</c>; neither body carries a budget member; the real route answers <c>200</c> in the same fact.
    /// </summary>
    [Fact]
    public async Task R9_50_SEB17_TheTokenBinds()
    {
        var owner = await AcceptedAnswerScene.EnrolAsync(forum, "bound", Ct);
        var other = await AcceptedAnswerScene.EnrolAsync(forum, "stranger", Ct);
        var problems = new List<string>();

        var real = await BudgetReadouts.GetAsync(forum, owner);
        if (real.Status != HttpStatusCode.OK) problems.Add($"the real route: Expected: 200 Actual: {real.Says}");

        var none = await BudgetReadouts.GetAsync(forum, null);
        var wrongKey = await BudgetReadouts.GetAsync(forum, owner, proofBy: other.Dpop);
        foreach (var (what, reply) in new[] { ("no token", none), ("a proof signed by another key", wrongKey) })
            if (reply.Status != HttpStatusCode.Unauthorized || reply.Raw.Contains("\"budgets\"", StringComparison.Ordinal))
                problems.Add($"{what}: Expected: 401 and no budget member Actual: {reply.Says}");

        Clean(problems);
    }

    /// <summary>
    /// SEB18. Anonymous: no <c>Authorization</c> header is <c>401</c> naming the missing authorization, with no budget member, and the same answer
    /// <c>GET /v1/flags</c> gives (the route's authentication is the nearest read's), never a <c>200</c> of anything. Amended at the build: the plan said
    /// "with a DPoP challenge", but the Forum answers a <c>WWW-Authenticate: DPoP</c> only for a nonce failure (<c>NonceChallengeOrProblemAsync</c>: "Only
    /// nonce failures get the challenge") and R9.50 asks for no nonce, so a challenge here would be this route's alone and unspecified.
    /// </summary>
    [Fact]
    public async Task R9_50_SEB18_AnonymousIsRefused()
    {
        var owner = await AcceptedAnswerScene.EnrolAsync(forum, "anon-control", Ct);
        var real = await BudgetReadouts.GetAsync(forum, owner);
        Assert.True(real.Status == HttpStatusCode.OK, $"Expected: 200 for the authenticated control Actual: {real.Says}");

        var anonymous = await BudgetReadouts.GetAsync(forum, null);
        var flags = await BudgetReadouts.GetAsync(forum, null, "/v1/flags");

        Assert.True(
            anonymous.Status == HttpStatusCode.Unauthorized && anonymous.Type == "curia/authn/missing-authorization" && !anonymous.Raw.Contains("\"budgets\"", StringComparison.Ordinal)
                && (anonymous.Status, anonymous.Type) == (flags.Status, flags.Type),
            $"Expected: 401 curia/authn/missing-authorization, the same as /v1/flags ({flags.Says}) Actual: {anonymous.Says}");
    }

    /// <summary>
    /// SEB25. No cache: the readout carries R9.50's <c>Cache-Control</c> value and no <c>ETag</c> or <c>Last-Modified</c>; a second readout after an act
    /// reflects it.
    /// </summary>
    [Fact]
    public async Task R9_50_SEB25_NoCache()
    {
        var acts = await BudgetActor.T0Async(forum, "posting", Ct);
        var first = await BudgetReadouts.ReadAsync(forum, acts.Actor);

        var value = PublishedBudgetReadout.Header().Split(": ", 2)[1];
        Assert.True(first.CacheControl == value && !first.HasValidator, $"Expected: {value} and no validator Actual: {first.CacheControl} validator={first.HasValidator}");

        await acts.SpendAsync(1);
        var second = await BudgetReadouts.ReadAsync(forum, acts.Actor);
        Probe.Said(
            $"posting used={first.Entry("posting").GetProperty("used").GetInt32() + 1}",
            $"posting used={second.Entry("posting").GetProperty("used").GetInt32()}");
    }

    /// <summary>
    /// SEB28's route half (ruling 17: the T3 leg is pure-only, and one more limit applies here). The tier is live: the same agent reads <c>T0</c> and T0's
    /// limits, then after promotion (owner attested, three questions, the clock past the tenure) <c>T1</c> and T1's limits. <b>Deviation from the plan:</b>
    /// "with no new token" cannot be written: the tenure is 48 hours of the fixture's clock and a token lives 300 s, so the promotion expires the token; the
    /// second read uses a fresh token. That the tier is the log's and not the token's (FSEB56) is SEB69's, which promotes the agent within one token's life.
    /// </summary>
    [Fact]
    public async Task R7_7_SEB28_TheTierIsLiveAcrossAPromotion()
    {
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "promoted", Ct);
        var board = Probe.Board();
        var before = await BudgetReadouts.ReadAsync(forum, actor);

        await forum.AttestOwnerAsync(actor.Agent.AgentId, Ct, owner: "owner:promoted-" + Probe.Suffix());
        for (var i = 0; i < 3; i++) await Probe.AskAsync(forum, actor, board, Ct);
        forum.Clock.Advance(TimeSpan.FromHours(Curia.Domain.Tests.Authorization.PublishedTable11.Rows["T1"].ThresholdsWithUnits.First(t => t.Unit.StartsWith("hour", StringComparison.Ordinal)).Value + 1));
        var after = await BudgetReadouts.ReadAsync(forum, actor);

        Probe.Said(
            $"T0 posting limit {BudgetReadouts.LimitOf("T0", "posting")} then T1 posting limit {BudgetReadouts.LimitOf("T1", "posting")}",
            $"{before.Body.GetProperty("tier").GetString()} posting limit {before.Entry("posting").GetProperty("limit").GetInt32()} then {after.Body.GetProperty("tier").GetString()} posting limit {after.Entry("posting").GetProperty("limit").GetInt32()}");
        Assert.Equal("T0", before.Body.GetProperty("tier").GetString());
        Assert.Equal("T1", after.Body.GetProperty("tier").GetString());
    }

    /// <summary>
    /// SEB69 (closes FSEB56's gap: SEB28 cannot tell the log's tier from the token's, since a promotion outlives the token). One token is minted at T0 and
    /// kept; within its life the agent is promoted (the owner attested, three questions, and the enrolment and key binding back-dated past the tenure by
    /// admin SQL, with the key's <c>valid_from</c> moved with them); the readout with that same token states <c>T1</c> and T1's three limits, never the
    /// <c>tier</c> claim the token carries.
    /// </summary>
    [Fact]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The statements are constants in this method; every value is bound.")]
    public async Task R7_7_SEB69_TheTierIsTheLogsAndNotTheTokens()
    {
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "promoted-live", Ct);
        var board = Probe.Board();
        var token = await actor.Dpop.GetTokenAsync(forum.Client, BudgetReadouts.TokenEndpoint, forum.Now, Ct);
        var before = await BudgetReadouts.GetAsync(forum, actor, token: token);
        Assert.True(before.Status == HttpStatusCode.OK && before.Body.GetProperty("tier").GetString() == "T0", $"Expected: 200 tier T0 Actual: {before.Says}");

        await forum.AttestOwnerAsync(actor.Agent.AgentId, Ct, owner: "owner:promoted-live-" + Probe.Suffix());
        for (var i = 0; i < 3; i++) await Probe.AskAsync(forum, actor, board, Ct);

        var past = TimeSpan.FromHours(Curia.Domain.Authorization.TierPolicy.T1MinimumHours + 1);
        await using (var admin = new NpgsqlConnection(forum.ConnectionString))
        {
            await admin.OpenAsync(Ct);
            await using var events = new NpgsqlCommand(
                "UPDATE events SET server_ts = server_ts - @by WHERE aggregate_id = @a AND event_type IN ('agent.enrolled', 'agent.key-bound');", admin);
            events.Parameters.AddWithValue("by", past);
            events.Parameters.AddWithValue("a", actor.Agent.AgentId);
            Assert.True(await events.ExecuteNonQueryAsync(Ct) >= 1, "Expected: the enrolment events back-dated");

            await using var keys = new NpgsqlCommand("UPDATE agent_keys SET valid_from = valid_from - @by WHERE agent_id = @a;", admin);
            keys.Parameters.AddWithValue("by", past);
            keys.Parameters.AddWithValue("a", actor.Agent.AgentId);
            Assert.True(await keys.ExecuteNonQueryAsync(Ct) >= 1, "Expected: the key's validity back-dated");
        }

        var after = await BudgetReadouts.GetAsync(forum, actor, token: token);
        Assert.True(after.Status == HttpStatusCode.OK, $"Expected: 200 with the T0 token still valid Actual: {after.Says}");
        var problems = new List<string>();
        if (after.Body.GetProperty("tier").GetString() != "T1") problems.Add($"tier: Expected: T1 Actual: {after.Body.GetProperty("tier").GetString()}");
        foreach (var budget in BudgetReadouts.Budgets)
        {
            var want = BudgetReadouts.LimitOf("T1", budget);
            var got = after.Entry(budget).GetProperty("limit").GetInt32();
            if (got != want) problems.Add($"{budget} limit: Expected: {want} Actual: {got}");
        }

        Clean(problems);
    }

    /// <summary>
    /// SEB31. A read records nothing and spends nothing: a T0 agent at three posts of three, and one more readout than T0's published reads per minute
    /// (Table 11, parsed) inside one clock minute, leave the events table and the flag table unchanged; the last is still <c>200</c> (a guard on this route
    /// turns it red, FSEB68).
    /// </summary>
    [Fact]
    public async Task R7_28_SEB31_AReadRecordsNothingAndIsNeverRateLimited()
    {
        var acts = await BudgetActor.T0Async(forum, "posting", Ct);
        await acts.SpendAsync(BudgetReadouts.LimitOf("T0", "posting"));
        var before = await BudgetReadouts.TableSizesAsync(forum);
        var reads = Curia.Domain.Tests.Authorization.PublishedTable11.Rows["T0"].ReadsPerMinute!.Value + 1;

        BudgetReply? last = null;
        for (var i = 0; i < reads; i++)
        {
            last = await BudgetReadouts.GetAsync(forum, acts.Actor);
            Assert.True(last.Status == HttpStatusCode.OK, $"Expected: 200 at readout {i + 1} of {reads} Actual: {last.Says}");
        }

        Assert.Equal(before, await BudgetReadouts.TableSizesAsync(forum));
        Assert.Equal(0, last!.Entry("posting").GetProperty("remaining").GetInt32());
    }

    /// <summary>
    /// SEB32. Over the limit: five posts seeded for a T0 agent (limit three): <c>remaining</c> 0, <c>used</c> 5, <c>next_slot_at</c> the third oldest + 86,400 s
    /// (k = 5 - 3 + 1); one tick before it the next post is refused, at it permitted.
    /// </summary>
    [Fact]
    public async Task R7_28_SEB32_OverTheLimitTheKthOldestFreesTheSlot()
    {
        var acts = await BudgetActor.T0Async(forum, "posting", Ct);
        var board = Probe.Board();
        for (var i = 0; i < 5; i++)
        {
            forum.Clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromTicks(1 + i));
            await BudgetBursts.SeedPostsAsync(forum, acts.Actor.Agent, board, 1, Ct);
        }

        var reply = await BudgetReadouts.ReadAsync(forum, acts.Actor);
        var instants = await BudgetReadouts.InstantsAsync(forum, "posting", acts.Actor.Agent.AgentId);
        var third = instants[2];
        Probe.Said(
            $"limit=3 used=5 remaining=0 next_slot_at={BudgetReadouts.Form(third + BudgetReadouts.Window)}",
            BudgetReadouts.Describe(reply.Entry("posting")));

        BudgetReadouts.MoveTo(forum, third + BudgetReadouts.Window - TimeSpan.FromTicks(1));
        var early = await acts.NextAsync();
        Assert.True(early.Status == 403, $"Expected: 403 one tick before the third oldest leaves Actual: {early.Says}");
        forum.Clock.Advance(TimeSpan.FromTicks(1));
        var at = await acts.NextAsync();
        Assert.True(at.Status == 201, $"Expected: 201 at the third oldest's instant Actual: {at.Says}");
    }

    /// <summary>
    /// SEB33 (the baseline on the seam tree: a control that the seam changed nothing in the route table). The route table and the fuzzer's exemplars agree both
    /// ways for <c>GET /v1/budget</c>: both hold it or neither does. It is green on the seam tree (neither does) and from Task 3 (both do, with the closed
    /// pass's ledger empty), and red if one is added without the other (FSEB31).
    /// </summary>
    [Fact]
    public void R14_10_SEB33_TheFuzzerCoversTheRouteBothWays()
    {
        var routed = SurfaceInventory.Routes(forum).Any(r => r.Method == "GET" && r.Pattern == "/v1/budget");
        var exemplified = Exemplars.All.Any(r => r.Route == "GET /v1/budget");

        Assert.True(routed == exemplified, $"routed={routed} exemplified={exemplified}: the route table and the exemplars must agree");
    }

    /// <summary>
    /// SEB35. No agent-authored content: the route is registered <c>ServedContent.None</c> and every string in the body is a budget name, a tier word, a
    /// credential word or an instant.
    /// </summary>
    [Fact]
    public async Task R9_50_SEB35_NoAgentAuthoredContent()
    {
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "plain", Ct);
        var reply = await BudgetReadouts.ReadAsync(forum, actor);

        var strings = new List<string>();
        void Collect(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.String) strings.Add(e.GetString()!);
            else if (e.ValueKind == JsonValueKind.Object) foreach (var p in e.EnumerateObject()) Collect(p.Value);
            else if (e.ValueKind == JsonValueKind.Array) foreach (var x in e.EnumerateArray()) Collect(x);
        }

        Collect(reply.Body);
        var allowed = PublishedBudgetReadout.Names().Concat(PublishedBudgetReadout.CredentialWords()).Concat(["T0", "T1", "T2", "T3"]).ToHashSet(StringComparer.Ordinal);
        var stray = strings.Where(s => !allowed.Contains(s) && !DateTimeOffset.TryParseExact(s, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)).ToArray();
        Assert.True(stray.Length == 0, $"Expected: only names, tier and credential words and instants Actual: {string.Join(" | ", stray)}");

        var surface = forum.Services.GetRequiredService<Microsoft.AspNetCore.Routing.EndpointDataSource>().Endpoints.OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == "/v1/budget").Metadata.GetMetadata<Curia.Api.ServingSurface>();
        Assert.Equal(Curia.Api.ServedContent.None, surface?.Content);
    }

    /// <summary>
    /// SEB54. The instant is the stored column's: the clock is set to sub-microsecond ticks (1 to 9 past a microsecond; 20 route posts by a T2 agent); the
    /// control in the same fact: at least one receipt's <c>server_ts</c> differs from the events table's column (so the fixture can see the truncation); then
    /// the readout's <c>next_slot_at</c> and a refusal's equal the column plus 86,400 s by SQL, and the policy admits an act at that instant and refuses it one
    /// tick before.
    /// </summary>
    [Fact]
    public async Task R7_28_SEB54_TheInstantIsTheStoredColumns()
    {
        var board = Probe.Board();
        var actor = await BudgetBursts.T2Async(forum, board, Ct);
        var limit = BudgetReadouts.LimitOf("T2", "posting");

        var receipts = new List<Act>();
        for (var i = 0; i < 20; i++)
        {
            forum.Clock.Advance(TimeSpan.FromSeconds(30) + TimeSpan.FromTicks(1 + i % 9));
            var act = await BudgetReadouts.AskAsync(forum, actor, board);
            Assert.True(act.Status == 201, $"Expected: 201 Actual: {act.Says}");
            receipts.Add(act);
        }

        // The T2 agent's earlier posts are outside the window and precede these twenty: the columns of the receipts are the last twenty.
        var columns = (await BudgetReadouts.InstantsAsync(forum, "posting", actor.Agent.AgentId))[^receipts.Count..];
        var differing = receipts.Where((r, i) => r.ServerTs != BudgetReadouts.Form(columns[i])).Count();
        Assert.True(differing >= 1, "control: Expected: at least one receipt server_ts to differ from the stored column Actual: none (the fixture cannot see the truncation)");

        forum.Clock.Advance(TimeSpan.FromSeconds(1));
        await BudgetBursts.SeedPostsAsync(forum, actor.Agent, board, limit - receipts.Count, Ct);
        var oldest = (await BudgetReadouts.InstantsAsync(forum, "posting", actor.Agent.AgentId)).Where(i => i > forum.Now - BudgetReadouts.Window).Order().First();
        var reply = await BudgetReadouts.ReadAsync(forum, actor);
        Probe.Said($"limit={limit} used={limit} remaining=0 next_slot_at={BudgetReadouts.Form(oldest + BudgetReadouts.Window)}", BudgetReadouts.Describe(reply.Entry("posting")));

        BudgetReadouts.MoveTo(forum, oldest + BudgetReadouts.Window - TimeSpan.FromTicks(1));
        var early = await BudgetReadouts.AskAsync(forum, actor, board);
        Assert.True(early.Status == 403 && early.Detail!.EndsWith(PublishedBudgetReadout.RefusalToken() + BudgetReadouts.Form(oldest + BudgetReadouts.Window), StringComparison.Ordinal), $"Expected: 403 ending next_slot_at={BudgetReadouts.Form(oldest + BudgetReadouts.Window)} Actual: {early.Says}");
        forum.Clock.Advance(TimeSpan.FromTicks(1));
        var at = await BudgetReadouts.AskAsync(forum, actor, board);
        Assert.True(at.Status == 201, $"Expected: 201 at the instant Actual: {at.Says}");
    }

    /// <summary>
    /// SEB55. The flag entry counts the joined flags and nothing else: a private row with no commitment, a commitment with no row and a commitment whose
    /// salt does not open are seeded by admin SQL; none is counted; <c>used</c> equals the flags the route stored, and the flag route's refusal comes exactly
    /// when <c>remaining</c> is 0, with the orphans present.
    /// </summary>
    [Fact]
    public async Task R7_28_SEB55_TheFlagEntryCountsTheJoinedFlagsAndNothingElse()
    {
        var acts = await BudgetActor.T0Async(forum, "flag", Ct);
        var limit = BudgetReadouts.LimitOf("T0", "flag");
        await acts.SpendAsync(1);
        await SeedOrphansAsync(acts.Actor.Agent.AgentId);

        var one = await BudgetReadouts.ReadAsync(forum, acts.Actor);
        Assert.Equal(1, one.Entry("flag").GetProperty("used").GetInt32());

        await acts.SpendAsync(limit - 2);
        var penultimate = await BudgetReadouts.ReadAsync(forum, acts.Actor);
        Assert.Equal(1, penultimate.Entry("flag").GetProperty("remaining").GetInt32());

        var last = await acts.NextAsync();
        Assert.True(last.Status == 201, $"Expected: the last flag permitted at remaining 1 with orphans present Actual: {last.Says}");
        var zero = await BudgetReadouts.ReadAsync(forum, acts.Actor);
        Assert.Equal(0, zero.Entry("flag").GetProperty("remaining").GetInt32());
        var refused = await acts.NextAsync();
        Assert.True(refused.Status == 403, $"Expected: 403 at remaining 0 Actual: {refused.Says}");
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The statements are constants in this method; every value is bound.")]
    private async Task SeedOrphansAsync(string agentId)
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(Ct);
        string Id() => "orphan-" + Guid.NewGuid().ToString("N");
        async Task ExecAsync(string sql, Action<NpgsqlCommand> bind)
        {
            await using var command = new NpgsqlCommand(sql, admin);
            bind(command);
            await command.ExecuteNonQueryAsync(Ct);
        }

        // A row with no commitment: never reached by the join, since the join walks the log.
        await ExecAsync(
            "INSERT INTO flag_details (event_id, post_id, raised_by, rationale, salt) VALUES (@e, 'no-such-post', @a, 'orphan row', 'c2FsdA');",
            c => { c.Parameters.AddWithValue("e", Id()); c.Parameters.AddWithValue("a", agentId); });

        // A commitment with no row: the real flag's event cloned under a new id, with no private row.
        await ExecAsync(
            "INSERT INTO events (event_id, event_type, aggregate_id, actor_id, payload, server_ts) " +
            "SELECT @e, e.event_type, e.aggregate_id, e.actor_id, e.payload, @at FROM events e JOIN flag_details d ON d.event_id = e.event_id WHERE d.raised_by = @a ORDER BY e.seq LIMIT 1;",
            c => { c.Parameters.AddWithValue("e", Id()); c.Parameters.AddWithValue("a", agentId); c.Parameters.AddWithValue("at", forum.Now.UtcDateTime); });

        // A commitment whose salt does not open: the event cloned, with a row whose salt is not the committed one.
        var salted = Id();
        await ExecAsync(
            "INSERT INTO events (event_id, event_type, aggregate_id, actor_id, payload, server_ts) " +
            "SELECT @e, e.event_type, e.aggregate_id, e.actor_id, e.payload, @at FROM events e JOIN flag_details d ON d.event_id = e.event_id WHERE d.raised_by = @a ORDER BY e.seq LIMIT 1;",
            c => { c.Parameters.AddWithValue("e", salted); c.Parameters.AddWithValue("a", agentId); c.Parameters.AddWithValue("at", forum.Now.UtcDateTime); });
        await ExecAsync(
            "INSERT INTO flag_details (event_id, post_id, raised_by, rationale, salt) SELECT @e, post_id, raised_by, rationale, 'd3Jvbmc' FROM flag_details WHERE raised_by = @a AND event_id NOT LIKE 'orphan-%' ORDER BY event_id LIMIT 1;",
            c => { c.Parameters.AddWithValue("e", salted); c.Parameters.AddWithValue("a", agentId); });
    }
}
