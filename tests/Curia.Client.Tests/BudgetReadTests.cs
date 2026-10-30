using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Curia.Client.Cli;
using Curia.Domain.Authorization;
using Curia.Domain.Primitives;
using Curia.Domain.Tests.Authorization;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// Errata G49 (R11.65) at the client, in process: SEB38 (a strict reader), SEB39 (the lines), SEB45 (a refusal prints the Forum's instant), SEB51 (the readout
/// says it is not a poll target) and SEB56 (the prose limits are the published ones). The words are R11.65's spans, parsed from the errata
/// (<see cref="PublishedBudgetReadout"/>); the limits are Table 11's, R7.26's and R7.22's. <see cref="ForumSession.BudgetAsync"/>, <see cref="BudgetLines.Of"/>
/// and <see cref="BudgetStanding"/> are inert on the seam tree, so the facts that read them are red by assertion; SEB56 is a control and is green.
/// </summary>
[Collection("console")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class BudgetReadTests : IDisposable
{
    private static readonly Uri Forum = new("http://forum.test");
    private readonly string _root = Directory.CreateTempSubdirectory("curia-budget-read-").FullName;
    private readonly List<HttpClient> _clients = [];
    private readonly List<Handler> _handlers = [];
    private readonly EnrolledAgent _agent;

    public BudgetReadTests()
    {
        Assert.True(new ProfileStore(_root).Create("alice", "https://agents.example/alice", "alice-1", Forum).TryGetValue(out var agent, out _));
        _agent = agent!;
    }

    public void Dispose()
    {
        foreach (var client in _clients) client.Dispose();
        foreach (var handler in _handlers) handler.Dispose();
        _agent.Dispose();
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private sealed class Handler(string body) : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add($"{request.Method} {path}");
            var answer = path == "/oauth/token" ? """{"access_token":"test-access-token","token_type":"DPoP","expires_in":300,"scope":"budget:read"}""" : body;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(answer, Encoding.UTF8, "application/json") });
        }
    }

    private ForumSession SessionFor(string body)
    {
        var handler = new Handler(body);
        _handlers.Add(handler);
        var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = Forum };
        _clients.Add(http);
        return new ForumSession(new ForumClient(http, Forum), _agent, new ProfileStore(_root), TimeProvider.System);
    }

    private const string Valid = StubLog.ValidBudgetJson;

    private static readonly string[] Names = ["posting", "acceptance", "flag"];

    /// <summary>
    /// SEB38. A strict reader: a data-driven grid of malformations is refused as a malformed response (a fractional or negative number, a <c>remaining</c> other
    /// than the larger of 0 and <c>limit</c> less <c>used</c>, a null limit with a non-null remaining, a duplicated or unknown budget name, an unparseable instant,
    /// <c>budgets</c> not a list); an unknown extra member is tolerated; the valid readout is read (the positive that makes the fact red on the seam tree).
    /// A budget the response lacks is not a malformation: it is <c>NOT STATED</c> (SEB39). <b>Reading of the plan:</b> SEB38 lists "a missing ... budget name"
    /// as malformed and R11.65 says a missing budget is <c>NOT STATED</c>; R11.65 governs, so only a duplicated or unknown name is refused here.
    /// </summary>
    [Fact]
    public async Task R11_65_SEB38_AStrictReader()
    {
        var ct = TestContext.Current.CancellationToken;
        var malformed = new (string What, string Body)[]
        {
            ("a fractional used", Valid.Replace("\"used\":2", "\"used\":2.5", StringComparison.Ordinal)),
            ("a negative used", Valid.Replace("\"used\":2", "\"used\":-2", StringComparison.Ordinal)),
            ("a remaining that is not limit less used", Valid.Replace("\"remaining\":1", "\"remaining\":2", StringComparison.Ordinal)),
            ("a remaining written as a double", Valid.Replace("\"remaining\":1", "\"remaining\":1.0", StringComparison.Ordinal)),
            ("a null limit with a non-null remaining", Valid.Replace("\"limit\":3,\"used\":2,\"remaining\":1", "\"limit\":null,\"used\":2,\"remaining\":1", StringComparison.Ordinal)),
            ("a duplicated budget name", Valid.Replace("\"name\":\"acceptance\"", "\"name\":\"posting\"", StringComparison.Ordinal)),
            ("an unknown budget name", Valid.Replace("\"name\":\"acceptance\"", "\"name\":\"rotation\"", StringComparison.Ordinal)),
            ("an unparseable next slot", Valid.Replace("2026-10-31T10:00:00.0000000+00:00", "tomorrow", StringComparison.Ordinal)),
            ("an unparseable as_of", Valid.Replace("2026-10-30T12:00:00.0000000+00:00", "now", StringComparison.Ordinal)),
            ("a window written as a double", Valid.Replace("86400", "86400.0", StringComparison.Ordinal)),
            ("budgets as an object", """{"tier":"T0","credential":"active","as_of":"2026-10-30T12:00:00.0000000+00:00","window_seconds":86400,"budgets":{}}"""),
        };

        var problems = new List<string>();
        var tolerated = await SessionFor(Valid.Replace("{\"tier\"", "{\"future_member\":\"x\",\"tier\"", StringComparison.Ordinal)).BudgetAsync(ct);
        if (!tolerated.TryGetValue(out var standing, out var refusal) || standing!.Budgets.Length != 3)
            problems.Add($"the valid readout with an unknown extra member: Expected: read, three budgets Actual: {refusal?.Error.Type} {refusal?.Error.Detail}");

        foreach (var (what, body) in malformed)
        {
            var result = await SessionFor(body).BudgetAsync(ct);
            if (result.Refusal?.Kind != RefusalKind.Malformed) problems.Add($"{what}: Expected: a malformed response Actual: {(result.Refusal is null ? "read" : result.Refusal.Kind + " " + result.Refusal.Error.Type)}");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static BudgetEntryStanding Entry(string name, int? limit, int used, int? remaining, NextSlot next) => new(name, limit, used, remaining, next);

    private static NextSlot At(string instant) => new(NextSlotKind.Instant, instant);

    private static BudgetStanding Standing(string credential, params BudgetEntryStanding[] entries) =>
        new("T0", credential, "2026-10-30T12:00:00.0000000+00:00", 86_400, [.. entries]);

    /// <summary>
    /// SEB39. The lines: one per budget in the response's order; a zero remainder prints the Forum's next slot as given; a null limit prints
    /// <c>no fixed limit</c>; a quarantined credential says writes are refused whatever remains; the snapshot sentence is R11.65's; a budget the response lacks
    /// is <c>NOT STATED</c>, never a zero; and a Forum string such as a budget name that carries a newline cannot forge a line (it is a display literal).
    /// </summary>
    [Fact]
    public void R11_65_SEB39_TheLines()
    {
        var instant = "2026-10-31T11:00:00.0000000+00:00";
        var problems = new List<string>();

        var spent = BudgetLines.Of(Standing("active", Entry("posting", 3, 3, 0, At(instant)), Entry("acceptance", 10, 0, 10, new(NextSlotKind.None, null)), Entry("flag", null, 4, null, new(NextSlotKind.None, null))));
        var lines = spent.Split('\n');
        var order = Names.Select(n => Array.FindIndex(lines, l => l.Contains(n, StringComparison.Ordinal))).ToArray();
        if (order.Any(i => i < 0) || order[0] >= order[1] || order[1] >= order[2]) problems.Add($"one line per budget in order: Expected: posting, acceptance, flag on separate lines in that order Actual: {string.Join(',', order)} in \"{spent}\"");
        if (!spent.Contains(instant, StringComparison.Ordinal)) problems.Add($"a zero remainder: Expected: the Forum's next slot {instant} Actual: \"{spent}\"");
        if (!spent.Contains(PublishedBudgetReadout.Span("no fixed limit"), StringComparison.Ordinal)) problems.Add("a null limit: Expected: no fixed limit");
        if (!spent.Contains(PublishedBudgetReadout.Phrase("snapshot and not a reservation"), StringComparison.Ordinal)) problems.Add("the snapshot sentence: Expected: snapshot and not a reservation");

        var quarantined = BudgetLines.Of(Standing("quarantined", Entry("posting", 3, 0, 3, new(NextSlotKind.None, null))));
        if (!quarantined.Contains("quarantined", StringComparison.Ordinal) || !quarantined.Contains("refused", StringComparison.OrdinalIgnoreCase)) problems.Add($"a quarantined credential: Expected: writes are refused whatever remains Actual: \"{quarantined}\"");

        var missing = BudgetLines.Of(Standing("active", Entry("posting", 3, 1, 2, new(NextSlotKind.None, null)), Entry("acceptance", 10, 0, 10, new(NextSlotKind.None, null))));
        if (!missing.Contains(PublishedBudgetReadout.Span("NOT STATED"), StringComparison.Ordinal) || !missing.Contains("flag", StringComparison.Ordinal)) problems.Add($"a budget the response lacks: Expected: flag NOT STATED Actual: \"{missing}\"");

        var hostile = "\nbudget  posting 99";
        var forged = BudgetLines.Of(Standing("active", Entry(hostile, 3, 1, 2, new(NextSlotKind.None, null))));
        if (forged.Split('\n').Any(l => l.StartsWith("budget  posting 99", StringComparison.Ordinal))) problems.Add("a Forum string: Expected: a display literal Actual: a forged line");
        if (!forged.Contains("posting 99", StringComparison.Ordinal)) problems.Add($"a Forum string: Expected: the name quoted in the output Actual: \"{forged}\"");

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static string[] BudgetReasons() =>
        [.. AccessPolicy.DenialReasons.Where(r => r.StartsWith("table-11/", StringComparison.Ordinal) && r.EndsWith("-budget-exhausted", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

    private static Refusal RefusalFor(string reason, string suffix) =>
        new(RefusalKind.RateBudget, 403, new Error("curia/authz/denied", AccessPolicy.TitleFor(reason), $"{reason} tier=T0{suffix}"));

    /// <summary>
    /// SEB45. A refusal prints the Forum's instant: for each of the three reasons (the policy's enumeration) the summary, outside the quoted detail, states the
    /// instant of <c>next_slot_at=</c> as given and does not say the budget "resets"; a detail without one says <c>next slot NOT STATED</c>; the words are
    /// the same on a second call (no client clock is read).
    /// </summary>
    [Fact]
    public void R11_65_SEB45_ARefusalPrintsTheForumsInstant()
    {
        var instant = "2026-10-31T09:30:00.1234560+00:00";
        var token = PublishedBudgetReadout.RefusalToken();
        var problems = new List<string>();

        foreach (var reason in BudgetReasons())
        {
            var with = RefusalFor(reason, $" {token}{instant}");
            var remedy = with.Summary.Replace(with.Error.Detail!, string.Empty, StringComparison.Ordinal);
            if (!remedy.Contains(instant, StringComparison.Ordinal)) problems.Add($"{reason}: Expected: the instant {instant} in the remedy Actual: \"{with.Summary}\"");
            if (remedy.Contains("resets", StringComparison.OrdinalIgnoreCase)) problems.Add($"{reason}: Expected: no \"resets\" (a trailing window has no reset) Actual: \"{with.Summary}\"");
            if (with.Summary != RefusalFor(reason, $" {token}{instant}").Summary) problems.Add($"{reason}: the words differ between two calls");

            var without = RefusalFor(reason, string.Empty);
            if (!without.Summary.Contains(PublishedBudgetReadout.Span("next slot NOT STATED"), StringComparison.Ordinal)) problems.Add($"{reason}: Expected: next slot NOT STATED for a detail without an instant Actual: \"{without.Summary}\"");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// SEB51 (the CLI half; the tool's is in <c>Curia.Mcp.Tests</c>). The readout says it is not a poll target: <c>curia help budget</c> says every receipt
    /// already states the standing after the act, that the readout is read before the first act, and that it is a snapshot; the words are R11.65's.
    /// </summary>
    [Fact]
    public void R11_65_SEB51_TheHelpSaysTheReadoutIsNotAPollTarget()
    {
        var help = Help.For("budget");
        Assert.True(help is not null, "Expected: an entry for `curia help budget` Actual: none");

        var missing = new[]
            {
                PublishedBudgetReadout.Phrase("every receipt already states the standing after the act"),
                PublishedBudgetReadout.Phrase("snapshot and not a reservation"),
                PublishedBudgetReadout.Phrase("before the first act"),
            }
            .Where(p => !help!.Contains(p, StringComparison.Ordinal))
            .ToArray();
        Assert.True(missing.Length == 0, $"Expected: the help to say {string.Join(" | ", missing)} Actual: \"{help}\"");
    }

    private static readonly string[] Tokens = ["T0", "T1", "T2"];

    /// <summary>Whether all three limits appear as whole numbers in the text.</summary>
    private static string[] Lacks(string text, int[] limits) =>
        [.. limits.Where(l => !Regex.IsMatch(text, $@"(?<!\d){l}(?!\d)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2))).Select(l => l.ToString(System.Globalization.CultureInfo.InvariantCulture))];

    /// <summary>
    /// SEB56 (a control: green on the seam tree; red under FSEB64 to FSEB66). The prose limits are the published ones: the summary of each of the three budget
    /// refusals carries the three limits Table 11, R7.26 and R7.22 publish, and the help's entry for exit 4 carries them beside the budget it names. The
    /// adapter's wording (<c>WriteTools</c>, <c>curia_resolve</c>'s description) is held in <c>Curia.Mcp.Tests</c>.
    /// </summary>
    [Fact]
    public void R11_65_SEB56_TheProseLimitsAreThePublishedOnes()
    {
        var posts = Tokens.Select(t => PublishedTable11.Rows[t].PostsPerDay!.Value).ToArray();
        var limits = PublishedBudgetReadout.Limits(posts);
        var problems = new List<string>();

        foreach (var reason in BudgetReasons())
        {
            var budget = reason.Contains("rate", StringComparison.Ordinal) ? "posting" : reason.Contains("accept", StringComparison.Ordinal) ? "acceptance" : "flag";
            var summary = RefusalFor(reason, string.Empty).Summary;
            if (Lacks(summary, limits[budget]) is { Length: > 0 } missing) problems.Add($"{budget} refusal summary: Expected: limits {string.Join('/', limits[budget])} Missing: {string.Join(',', missing)} in \"{summary}\"");
        }

        var exitFour = HelpTextTests.ExitCodeEntry(ExitCode.Denied);
        foreach (var (budget, anchor) in new[] { ("posting", "posting budget"), ("flag", "flag budget"), ("acceptance", "acceptance budget") })
        {
            var at = exitFour.IndexOf(anchor, StringComparison.Ordinal);
            var segment = at < 0 ? string.Empty : exitFour[at..Math.Min(exitFour.Length, at + 140)];
            if (Lacks(segment, limits[budget]) is { Length: > 0 } missing) problems.Add($"exit 4 beside the {anchor}: Expected: limits {string.Join('/', limits[budget])} Missing: {string.Join(',', missing)}");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
