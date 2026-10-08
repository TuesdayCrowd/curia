using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Curia.Api.Tests.Fuzz;

/// <summary>
/// The fuzzer's fixture: the real Forum, with a recorder of every header and query parameter the host
/// reads (Hardin's item 9). The recorder is shared with every fixture a row runs on of its own, so it
/// records across the whole closed pass.
/// </summary>
public sealed class FuzzForumFixture : ForumFixture
{
    public FuzzForumFixture()
        : this(new HeaderReadRecorder())
    {
    }

    internal FuzzForumFixture(HeaderReadRecorder recorder) => Recorder = recorder;

    internal HeaderReadRecorder Recorder { get; }

    protected override void ConfigureFuzz(IServiceCollection services) => services.AddSingleton<IStartupFilter>(Recorder);
}

/// <summary>One failed answer, as the failure line, the ledger and <c>CURIA_FUZZ_FAILURES</c> name it.</summary>
internal sealed record FuzzFailure(
    string Route, string Variant, string Part, string Variation, string Copy, int Status, string ProblemType, long ElapsedMs, string Reason)
{
    internal string Method => Route[..Route.IndexOf(' ', StringComparison.Ordinal)];

    internal string Pattern => Route[(Route.IndexOf(' ', StringComparison.Ordinal) + 1)..];

    internal bool Budget => Reason.StartsWith(Oracle.OverBudget, StringComparison.Ordinal);

    internal string Line =>
        $"{Status} {Method} {Pattern} [{Variant}] {Part} {Variation} {Copy}: {Reason} ({ElapsedMs.ToString(CultureInfo.InvariantCulture)} ms)";
}

/// <summary>
/// The closed pass (R14.10; spec §4.10): every route's exemplars, sent plain, then once per (part,
/// variation, copy) of <see cref="Mutator.Plan"/>, then plain again, in three groups, a row that
/// creates posts and <c>accept</c> each on a fixture of its own; then the coverage of reads, the ledger,
/// and a host restarted over each fuzzed log.
/// </summary>
internal static class FuzzRun
{
    private const string TokenEndpoint = FuzzContext.TokenEndpoint;
    private const string PostsUrl = RequestModel.Origin + "/v1/posts";

    /// <summary>
    /// An agent enrolled through the route and raised to T1 as Table 11 raises one: its owner
    /// attested (R4.30), three questions asked, and 49 hours on the fixture's clock. T1 because a tier
    /// may do everything a lesser one may, so its requests reach every handler a T0 agent's reach, and
    /// those a T0 agent is refused before. Moved from <c>RequestSurfaceTests</c>; the owner is a
    /// parameter, since a vote is refused between two agents under one owner (R8.40).
    /// </summary>
    internal static async Task<(ForumAgent Agent, DpopClient Dpop, string Token)> EnrolledAtT1Async(
        ForumFixture forum, HttpClient client, CancellationToken ct, string owner = "owner:test")
    {
        ArgumentNullException.ThrowIfNull(forum);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agent = ForumAgent.Create("https://agents.example/surface-" + suffix, "surface-" + suffix);
        var (dpop, _) = await agent.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);
        await forum.AttestOwnerAsync(agent.AgentId, ct, owner);

        for (var i = 0; i < 3; i++)
        {
            using var asked = await dpop.PostAsync(
                client,
                PostsUrl,
                await dpop.GetTokenAsync(client, TokenEndpoint, forum.Now, ct),
                agent.SignQuestion("surface-" + suffix, "A warm-up question " + Guid.NewGuid().ToString("N"), "Warm-up " + i, forum.Now),
                forum.Now,
                ct);
            Assert.Equal(HttpStatusCode.Created, asked.StatusCode);
        }

        forum.Clock.Advance(TimeSpan.FromHours(49));
        return (agent, dpop, await dpop.GetTokenAsync(client, TokenEndpoint, forum.Now, ct));
    }

    /// <summary>
    /// A budget failure (D32) and any failure of an exemplar send (clause 4, and clauses 1-2 on an
    /// exemplar) are never ledger rows (spec §4.10).
    /// </summary>
    internal static bool Ledgerable(FuzzFailure failure) => !failure.Budget && !string.Equals(failure.Part, "exemplar", StringComparison.Ordinal);

    /// <summary>The variable naming the JSONL file every pass appends its failures to (spec §4.10, "Files").</summary>
    internal const string FailuresVariable = "CURIA_FUZZ_FAILURES";

    /// <summary>
    /// Appends one failure to the <see cref="FailuresVariable"/> file, when it is set, in the closed
    /// pass's schema: the random and Kestrel passes' failures reach it as the closed pass's do.
    /// </summary>
    internal static async Task AppendFailureAsync(
        string route, string variant, string part, string variation, string copy, int status, string problemType, long elapsedMs, bool ledgered, CancellationToken ct)
    {
        if (Environment.GetEnvironmentVariable(FailuresVariable) is not { } failures) return;
        await File.AppendAllTextAsync(failures, FailureRow(route, variant, part, variation, copy, status, problemType, elapsedMs, ledgered) + "\n", ct);
    }

    /// <summary>One line of the failures file; the schema exists only here.</summary>
    private static string FailureRow(
        string route, string variant, string part, string variation, string copy, int status, string problemType, long elapsedMs, bool ledgered) =>
        new JsonObject
        {
            ["route"] = route,
            ["variant"] = variant,
            ["part"] = part,
            ["variation"] = variation,
            ["copy"] = copy,
            ["status"] = status,
            ["problemType"] = problemType,
            ["elapsedMs"] = elapsedMs,
            ["ledgered"] = ledgered,
        }.ToJsonString();

    /// <summary>The variation ids that leave an envelope with no canonical form; see <see cref="MayBeSuperseded"/>.</summary>
    internal static readonly IReadOnlySet<string> NoCanonicalForm = new HashSet<string>(StringComparer.Ordinal) { "nul-raw", "lone-high", "lone-high-raw", "lone-low", "lone-low-raw", "bad-utf8", "overlong", "1e400" };

    /// <summary>
    /// Whether a re-signed copy may be superseded (clause 5): only a variation with no canonical form
    /// (a lone surrogate, escaped or raw, or a string that is not UTF-8, which have no NFC/JCS form;
    /// 1e400, which has no IEEE double), or removing the signed root itself, which leaves nothing to
    /// sign. A superseded copy sits outside the unsent ceiling, so any other one is a renderer
    /// regression moving a legitimate variation out of the signed-envelope test (review of 0277b97).
    /// </summary>
    internal static bool MayBeSuperseded(Part part, Variation variation, string signedPointer)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(variation);
        return NoCanonicalForm.Contains(variation.Id) || (variation.Id == "removed" && part.Address == "json:" + signedPointer);
    }

    /// <summary>The outcome of a closed pass.</summary>
    internal sealed record Outcome(IReadOnlyList<string> Failures, IReadOnlyList<FuzzFailure> Answered, IReadOnlyList<string> Plan);

    /// <summary>Runs the closed pass and returns every failure line, the ledgered ones excluded.</summary>
    internal static async Task<Outcome> ClosedAsync(FuzzForumFixture forum, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(forum);
        var pass = new Pass();
        var timings = Environment.GetEnvironmentVariable("CURIA_FUZZ_TIMINGS");
        // Appended, never truncated: the random and Kestrel passes write their slowest rows to the same file, in whatever order the classes run.
        if (timings is not null) await File.AppendAllTextAsync(timings, "route\tvariant\tindex\telapsed_ms\n", ct);

        using var main = await FuzzContext.SeedAsync(forum, ct);

        // The plan, per row and in total, before the first send; and every header an exemplar varies.
        var plan = new List<string>();
        var varied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sentQuery = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var total = 0;
        foreach (var row in Exemplars.All)
        {
            main.Prepared = null;
            var model = await row.Build(main, ct);
            var count = Mutator.Plan(model).Count();
            total += count;
            plan.Add($"{row.Route} [{row.Variant}]: {count.ToString(CultureInfo.InvariantCulture)} sends");
            foreach (var part in model.Parts().Where(p => p.Kind == PartKind.Header))
                varied.Add(part.Address["header:".Length..]);
            if (!sentQuery.TryGetValue(row.Route, out var sent)) sentQuery[row.Route] = sent = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (name, _) in model.Query) sent.Add(name);
        }

        plan.Add($"total: {total.ToString(CultureInfo.InvariantCulture)} sends");
        foreach (var line in plan) TestContext.Current.TestOutputHelper?.WriteLine(line);

        foreach (var group in Exemplars.All.GroupBy(r => r.Group).OrderBy(g => g.Key))
        {
            foreach (var row in group)
            {
                if (!row.OwnFixture)
                {
                    await pass.RunRowAsync(main, row, ct);
                    continue;
                }

                await OnFixtureOfItsOwnAsync(pass, forum.Recorder, row, ct);
            }

            if (group.Key == 2) await pass.RestartAsync(forum, pass.MainCreated, ct);
        }

        // Coverage of reads (Hardin's item 9), over the whole pass.
        foreach (var name in forum.Recorder.Headers.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!varied.Contains(name) && !SurfaceInventory.TransportHeaders.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
                pass.Lines.Add($"coverage: the header {name} is read, and no exemplar varies it and TransportHeaders does not list it");
        }

        foreach (var (name, _) in SurfaceInventory.TransportHeaders)
        {
            if (!forum.Recorder.Headers.Contains(name, StringComparer.OrdinalIgnoreCase))
                pass.Lines.Add($"coverage: TransportHeaders lists {name}, which nothing read during the pass (stale)");
        }

        foreach (var (method, pattern, name) in SurfaceInventory.RequestReadQuery)
        {
            if (!forum.Recorder.QueryReads.Contains(($"{method} {pattern}", name)))
                pass.Lines.Add($"coverage: RequestReadQuery lists {method} {pattern} {name}, which that route never read during the pass (stale)");
        }

        foreach (var (method, pattern, name, _) in SurfaceInventory.RefusedQuery)
        {
            if (!forum.Recorder.QueryReads.Contains(($"{method} {pattern}", name)))
                pass.Lines.Add($"coverage: RefusedQuery lists {method} {pattern} {name}, which that route never read during the pass (stale)");
        }

        // Both ways: every query parameter a route read is sent by some exemplar of that route, or listed as one it refuses.
        foreach (var (route, name) in forum.Recorder.QueryReads.OrderBy(r => r.Route, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.Ordinal))
        {
            if (route == HeaderReadRecorder.NoEndpoint)
                pass.Lines.Add($"coverage: the query parameter {name} was read with no endpoint matched");
            else if ((!sentQuery.TryGetValue(route, out var names) || !names.Contains(name))
                && !SurfaceInventory.RefusedQuery.Any(r => string.Equals($"{r.Method} {r.Pattern}", route, StringComparison.Ordinal) && string.Equals(r.Name, name, StringComparison.Ordinal)))
            {
                pass.Lines.Add($"coverage: {route} reads the query parameter {name}, and no exemplar of that route sends it");
            }
        }

        if (pass.SupersededTotal == 0)
            pass.Lines.Add("superseded: no re-signed copy was superseded in the whole pass, so no variation without a canonical form (a raw or escaped lone surrogate, invalid UTF-8, 1e400, or the signed root removed) reached a signed envelope");

        await pass.WriteFilesAsync(timings, ct);
        return new Outcome(pass.Judge(), pass.Failures, plan);
    }

    /// <summary>A row on a fixture of its own, seeded by the same steps, restarted over, and disposed after the row (spec §4.10).</summary>
    private static async Task OnFixtureOfItsOwnAsync(Pass pass, HeaderReadRecorder recorder, ExemplarRow row, CancellationToken ct)
    {
        await using var fixture = new FuzzForumFixture(recorder);
        await fixture.InitializeAsync();
        using var context = await FuzzContext.SeedAsync(fixture, ct);
        var created = await pass.RunRowAsync(context, row, ct);
        await pass.RestartAsync(fixture, created, ct);
    }

    /// <summary>What one closed pass observed.</summary>
    private sealed class Pass
    {
        internal List<FuzzFailure> Failures { get; } = [];

        internal List<string> Lines { get; } = [];

        internal List<string> MainCreated { get; } = [];

        internal int SupersededTotal { get; private set; }

        private readonly SortedDictionary<string, string> _answers = new(StringComparer.Ordinal);
        private readonly List<string> _timings = [];
        private readonly Dictionary<string, long> _slowest = new(StringComparer.Ordinal);

        /// <summary>One row: the exemplar, every planned variation, the exemplar again; then clause 5. Returns the post ids its 201s named.</summary>
        internal async Task<List<string>> RunRowAsync(FuzzContext context, ExemplarRow row, CancellationToken ct)
        {
            var state = new RowState();
            context.Prepared = null;
            var exemplar = await row.Build(context, ct);
            var plan = Mutator.Plan(exemplar).ToList();
            foreach (var (part, _, _) in plan)
                state.Planned[part.Address] = state.Planned.GetValueOrDefault(part.Address) + 1;

            try
            {
                await SendAsync(context, row, state, null, null, CopyKind.Plain, "first", ct);
                foreach (var (part, variation, copy) in plan)
                    await SendAsync(context, row, state, part, variation, copy, variation.Id, ct);
                await SendAsync(context, row, state, null, null, CopyKind.Plain, "last", ct);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Lines.Add($"{row.Route} [{row.Variant}]: the row stopped after {state.Sent.ToString(CultureInfo.InvariantCulture)} sends: {e.GetType().Name}: {e.Message}");
            }

            // Clause 5: sent plus unsent plus superseded is the plan; unsent at most a tenth of a part's sends; reach per part.
            if (state.Sent + state.UnsentTotal + state.SupersededTotal != plan.Count)
                Lines.Add($"{row.Route} [{row.Variant}]: sent {state.Sent.ToString(CultureInfo.InvariantCulture)} + unsent {state.UnsentTotal.ToString(CultureInfo.InvariantCulture)} + superseded {state.SupersededTotal.ToString(CultureInfo.InvariantCulture)} is not the {plan.Count.ToString(CultureInfo.InvariantCulture)} planned");

            foreach (var (address, planned) in state.Planned)
            {
                var unsent = state.Unsent.GetValueOrDefault(address);
                if (unsent * 10 > planned)
                    Lines.Add($"unsent: {row.Route} [{row.Variant}] {address}: {unsent.ToString(CultureInfo.InvariantCulture)} of {planned.ToString(CultureInfo.InvariantCulture)} sends could not be built");
                if (!state.Reached.Contains(address))
                    Lines.Add($"reach: {row.Route} [{row.Variant}] {address}: no re-signed or plain variation was answered 2xx or refused past the credential and the signature");
            }

            var line = new StringBuilder();
            for (var i = 0; i < state.Elapsed.Count; i++)
                line.Append(CultureInfo.InvariantCulture, $"{row.Route}\t{row.Variant}\t{i}\t{state.Elapsed[i]}\n");
            _timings.Add(line.ToString());
            if (Environment.GetEnvironmentVariable("CURIA_FUZZ_TIMINGS") is { } file)
                await File.AppendAllTextAsync(file, line.ToString(), ct);
            var slowest = state.Elapsed.Count == 0 ? 0 : state.Elapsed.Skip(1).DefaultIfEmpty(0).Max();
            _slowest[row.Route] = Math.Max(_slowest.GetValueOrDefault(row.Route), slowest);

            if (!row.OwnFixture) MainCreated.AddRange(state.Created);

            // A vote is never served (R8.55; PostKinds.IsServedToReaders, ForumEndpoints.cs:1816), so GET /v1/posts/{id} on one can never answer 200. The vote row's restart check asks only GET /health (spec §4.10 departs here; recorded in the plan's Task A1).
            return string.Equals(row.Variant, "vote", StringComparison.Ordinal) ? [] : state.Created;
        }

        [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Every exception a send throws is a failure line carrying its type (spec §4.10).")]
        [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The request is disposed by the using block that sends it; building it either returns it or throws.")]
        private async Task SendAsync(
            FuzzContext context, ExemplarRow row, RowState state, Part? part, Variation? variation, CopyKind copy, string label, CancellationToken ct)
        {
            if (row.Prepare is not null)
            {
                context.Forum.Clock.Advance(row.PrepareStep);
                await row.Prepare(context, ct);
            }

            if (row.CreatesPosts) context.Forum.Clock.Advance(TimeSpan.FromHours(1));

            var model = await row.Build(context, ct);
            var value = part is null ? null : variation!.Make(part, model.ValueOf(part));
            HttpRequestMessage request;
            try
            {
                request = model.Render(part, value, copy);
            }
            catch (NotReSignableException) when (part is not null && copy == CopyKind.ReSigned)
            {
                state.Superseded[part.Address] = state.Superseded.GetValueOrDefault(part.Address) + 1;
                state.SupersededTotal++;
                SupersededTotal++;
                if (!MayBeSuperseded(part, variation!, ((JsonBody)model.Body!).SignedPointer!))
                    Lines.Add($"superseded: {row.Route} [{row.Variant}] {part.Address} {variation!.Id} has no re-signed copy, and it is not a variation without a canonical form");
                return;
            }
            catch (Exception) when (part is not null)
            {
                state.Unsent[part.Address] = state.Unsent.GetValueOrDefault(part.Address) + 1;
                state.UnsentTotal++;
                return;
            }

            var address = part?.Address ?? "exemplar";
            var key = $"{row.Route} [{row.Variant}] {address} {label} {Copies.Wire(copy)}";
            // Warm-up is the first send of each ROW, not each route (spec §4.10 clause 3 says route): rows on a fixture of their own start a new host, whose first request is a cold start. Only the plain exemplar's first send is exempt; its last send and every variation stay budgeted. Recorded in the plan's Task A1.
            var warmUp = state.Elapsed.Count == 0;
            var stopwatch = Stopwatch.StartNew();
            int status;
            string body;
            string? thrown = null;
            using (request)
            {
                try
                {
                    using var response = await context.Client!.SendAsync(request, ct);
                    body = await response.Content.ReadAsStringAsync(ct);
                    status = (int)response.StatusCode;
                }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    thrown = e.GetType().Name;
                    body = string.Empty;
                    status = 0;
                }
            }

            stopwatch.Stop();
            var elapsed = stopwatch.Elapsed;
            var ms = (long)Math.Round(elapsed.TotalMilliseconds);
            state.Elapsed.Add(ms);
            if (part is not null) state.Sent++;

            var problemType = thrown ?? Oracle.ProblemType(row.Pattern, body);
            _answers[key] = $"{status.ToString(CultureInfo.InvariantCulture)} {problemType}".TrimEnd();

            var reason = thrown is null
                ? Oracle.Verdict(row.Pattern, (HttpStatusCode)status, body, elapsed, warmUp)
                : thrown is nameof(TaskCanceledException) ? Oracle.Budget(elapsed) + " (the client's 120 s timeout)" : "the send threw " + thrown;
            if (part is null && thrown is null && status is < 200 or > 299)
                reason = (reason is null ? string.Empty : reason + "; ") + "the exemplar was not answered 2xx (clause 4): " + problemType;

            if (reason is not null)
                Failures.Add(new FuzzFailure(row.Route, row.Variant, address, label, Copies.Wire(copy), status, problemType, ms, reason));

            // The rebinding (R14.10, D33): the re-signed copy of a variation of a proof's jwk, on a request
            // carrying a token, was rebound to the key it carries, so a refusal at the binding check is the
            // rebinding broken. A line of its own, never a failure the ledger could absorb (Task A5, case 3).
            if (part is not null && copy == CopyKind.ReSigned && thrown is null && IsProofJwk(part.Address) && model.Jws.ContainsKey("token")
                && string.Equals(Oracle.Slug(problemType), BindingMismatch, StringComparison.Ordinal))
                Lines.Add($"rebinding: the re-signed copy of {part.Address} {label} on {row.Route} was refused at the binding");

            if (part is not null && copy != CopyKind.Unsigned && thrown is null && Oracle.Reached((HttpStatusCode)status, problemType))
                state.Reached.Add(part.Address);

            // Only a submission creates a post: a flag's or an acceptance's 201 names a post it did not create.
            if (row.CreatesPosts && status == 201 && PostIdOf(body) is { } created) state.Created.Add(created);
        }

        private const string BindingMismatch = "curia/authn/binding-mismatch";

        private static bool IsProofJwk(string address) =>
            string.Equals(address, "jws:proof:header/jwk", StringComparison.Ordinal)
            || address.StartsWith("jws:proof:header/jwk/", StringComparison.Ordinal);

        private static string? PostIdOf(string body)
        {
            try
            {
                return JsonNode.Parse(body) is JsonObject o && o["post_id"] is JsonValue v && v.TryGetValue<string>(out var id) ? id : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>D24's shape (Hardin's item 2): a host started over the fuzzed log serves its health and every post the run created.</summary>
        internal async Task RestartAsync(ForumFixture fixture, IReadOnlyList<string> created, CancellationToken ct)
        {
            const string Name = "R14_10_AHostOverTheFuzzedLogStartsAndServesWhatItAccepted";
            await using var restarted = fixture.WithWebHostBuilder(_ => { });
            using var client = restarted.CreateClient();
            using (var health = await client.GetAsync(new Uri("/health", UriKind.Relative), ct))
            {
                if (health.StatusCode != HttpStatusCode.OK)
                    Lines.Add($"{Name}: GET /health answered {(int)health.StatusCode}");
            }

            foreach (var id in created.Distinct(StringComparer.Ordinal))
            {
                using var read = await client.GetAsync(new Uri("/v1/posts/" + Uri.EscapeDataString(id), UriKind.Relative), ct);
                if (read.StatusCode != HttpStatusCode.OK)
                    Lines.Add($"{Name}: GET /v1/posts/{id} answered {(int)read.StatusCode}");
            }
        }

        /// <summary>Failures not in the ledger, stale ledger rows, and every other line.</summary>
        internal List<string> Judge()
        {
            var lines = new List<string>();
            var matched = new HashSet<FaultRow>();
            foreach (var failure in Failures)
            {
                var row = Ledgered(failure);
                if (row is not null && !Ledgerable(failure))
                    lines.Add($"ledger: {row.Register} matches a {(failure.Budget ? "budget" : "exemplar")} failure, which may never be ledgered: {failure.Line}");
                else if (row is not null)
                    matched.Add(row);
                else
                    lines.Add(failure.Line);
            }

            foreach (var row in ExpectedFaults.Rows)
            {
                if (!Regex.IsMatch(row.Register, "^D33-[1-9][0-9]*$", RegexOptions.None, TimeSpan.FromSeconds(1)))
                    lines.Add($"ledger: {row.Register} is not D33-<n>");
                if (string.Equals(row.Part, "exemplar", StringComparison.Ordinal))
                    lines.Add($"ledger: {row.Register} names an exemplar send, which may never be ledgered");
                // A random-N row is the random pass's to judge stale (review of 9411deb).
                if (!ExpectedFaults.IsRandom(row) && !matched.Contains(row))
                    lines.Add($"ledger: {row.Register} ({row.Route} [{row.Variant}] {row.Part} {row.Variation} {row.Copy}) was not observed failing (stale)");
            }

            lines.AddRange(Lines);
            return lines;
        }

        private static FaultRow? Ledgered(FuzzFailure failure) =>
            ExpectedFaults.Rows.FirstOrDefault(r =>
                r.Route == failure.Route && r.Variant == failure.Variant && r.Part == failure.Part
                && r.Variation == failure.Variation && r.Copy == failure.Copy);

        internal async Task WriteFilesAsync(string? timings, CancellationToken ct)
        {
            if (timings is not null)
            {
                var slowest = new StringBuilder();
                foreach (var (route, ms) in _slowest.OrderBy(s => s.Key, StringComparer.Ordinal))
                    slowest.Append(CultureInfo.InvariantCulture, $"slowest\t{route}\t-\t{ms}\n");
                await File.AppendAllTextAsync(timings, slowest.ToString(), ct);
            }

            if (Environment.GetEnvironmentVariable(FailuresVariable) is { } failures)
            {
                var text = new StringBuilder();
                foreach (var f in Failures)
                {
                    text.Append(FailureRow(f.Route, f.Variant, f.Part, f.Variation, f.Copy, f.Status, f.ProblemType, f.ElapsedMs, Ledgerable(f) && Ledgered(f) is not null))
                        .Append('\n');
                }

                // Appended, never truncated: the random and Kestrel passes append to the same file, in whatever order the classes run, so whoever sets the variable deletes the file before the run.
                await File.AppendAllTextAsync(failures, text.ToString(), ct);
            }

            if (Environment.GetEnvironmentVariable("CURIA_FUZZ_ANSWERS") is { } answers)
            {
                var map = new JsonObject();
                foreach (var (key, answer) in _answers) map[key] = answer;
                await File.WriteAllTextAsync(answers, map.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct);
            }
        }
    }

    /// <summary>One row's counts.</summary>
    private sealed class RowState
    {
        internal Dictionary<string, int> Planned { get; } = new(StringComparer.Ordinal);

        internal Dictionary<string, int> Unsent { get; } = new(StringComparer.Ordinal);

        internal Dictionary<string, int> Superseded { get; } = new(StringComparer.Ordinal);

        internal HashSet<string> Reached { get; } = new(StringComparer.Ordinal);

        internal List<long> Elapsed { get; } = [];

        internal List<string> Created { get; } = [];

        internal int Sent { get; set; }

        internal int UnsentTotal { get; set; }

        internal int SupersededTotal { get; set; }
    }
}
