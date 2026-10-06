using System.Diagnostics.CodeAnalysis;
using Curia.Domain.Content;
using Xunit;

namespace Curia.Api.Tests.Fuzz;

/// <summary>
/// The fuzz classes' collection: each keeps its own class fixture, and none runs beside any other
/// test (spec §4.10).
/// </summary>
[CollectionDefinition("fuzz", DisableParallelization = true)]
public sealed class RequestFuzzGroup;

/// <summary>
/// R14.10 (errata G18): a fuzzer derived from the route registrations varies one part of a request
/// at a time over a closed published set, with an oracle and a ledger. The scope is derived -- routes
/// from the host's <c>EndpointDataSource</c>, parts by walking each exemplar -- and these facts hold
/// the derivation to the host: query reads both ways and by route; header reads by name.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("fuzz")]
public sealed class RequestFuzzClosedTests(FuzzForumFixture forum) : IClassFixture<FuzzForumFixture>
{
    [Fact]
    public void R14_10_EveryRegisteredRouteHasAnExemplarAndEveryExemplarARoute()
    {
        var routes = SurfaceInventory.Routes(forum).Select(r => $"{r.Method} {r.Pattern}").ToHashSet(StringComparer.Ordinal);
        var rows = Exemplars.All.Select(r => r.Route).ToHashSet(StringComparer.Ordinal);

        var missing = routes.Except(rows).Order(StringComparer.Ordinal).ToList();
        var stale = rows.Except(routes).Order(StringComparer.Ordinal).ToList();
        Assert.True(routes.Count > 0, "the host registers no route; the derivation is wrong");
        Assert.True(
            missing.Count == 0 && stale.Count == 0,
            "routes with no exemplar:\n" + string.Join('\n', missing) + "\nexemplars of no route:\n" + string.Join('\n', stale));
    }

    [Fact]
    public void R14_10_EveryPostKindHasAnExemplar()
    {
        var variants = Exemplars.All.Where(r => r.Route == "POST /v1/posts").Select(r => r.Variant).ToHashSet(StringComparer.Ordinal);
        var missing = Enum.GetValues<PostKind>().Select(PostKinds.Wire).Where(k => !variants.Contains(k)).ToList();
        Assert.True(missing.Count == 0, "post kinds with no POST /v1/posts exemplar: " + string.Join(", ", missing));
    }

    [Fact]
    public async Task R14_10_EveryQueryParameterARouteReadsHasAnExemplarValue()
    {
        var ct = TestContext.Current.CancellationToken;
        using var context = FuzzContext.Offline(forum);
        var keys = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var row in Exemplars.All)
        {
            var model = await row.Build(context, ct);
            if (!keys.TryGetValue(row.Route, out var set)) keys[row.Route] = set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (name, _) in model.Query) set.Add(name);
        }

        var bound = SurfaceInventory.BoundQuery(forum);
        Assert.True(bound.Count > 0, "no handler binds a query parameter, so this fact checked nothing; the reflection is wrong");

        var missing = bound.Concat(SurfaceInventory.RequestReadQuery)
            .Where(q => !keys.TryGetValue($"{q.Method} {q.Pattern}", out var set) || !set.Contains(q.Name))
            .Select(q => $"{q.Method} {q.Pattern} reads '{q.Name}', which its exemplar does not send")
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Assert.True(missing.Count == 0, "query parameters with no exemplar value:\n" + string.Join('\n', missing));
    }

    [Fact]
    public async Task R14_10_EveryEnvelopeMemberAndBodyPropertyIsAPart()
    {
        var ct = TestContext.Current.CancellationToken;
        using var context = FuzzContext.Offline(forum);
        var envelope = new HashSet<string>(StringComparer.Ordinal);
        var body = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in Exemplars.All)
        {
            var model = await row.Build(context, ct);
            foreach (var part in model.Parts().Where(p => p.Kind == PartKind.Json))
            {
                var pointer = part.Address["json:".Length..];
                if (pointer.StartsWith("/envelope/", StringComparison.Ordinal))
                {
                    var segments = pointer["/envelope/".Length..].Split('/')
                        .Select(s => s.Length > 0 && s.All(char.IsAsciiDigit) ? "*" : s)
                        .ToArray();
                    for (var i = 1; i <= segments.Length; i++) envelope.Add(string.Join('/', segments[..i]));
                }
                else if (pointer.Length > 1) body.Add(pointer[1..].Split('/')[0]);
            }
        }

        var missing = SurfaceInventory.EnvelopeMembers().Where(m => !envelope.Contains(m)).Select(m => "envelope: " + m)
            .Concat(SurfaceInventory.BodyMembers(forum).Where(m => !body.Contains(m.Name)).Select(m => $"{m.Method} {m.Pattern}: {m.Name}"))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Assert.True(SurfaceInventory.BodyMembers(forum).Count > 0, "no handler binds a body with named members; the reflection is wrong");
        Assert.True(SurfaceInventory.EnvelopeMembers().Any(m => m.Contains("/*/", StringComparison.Ordinal)), "the reflection found no nested envelope member; it is wrong");
        Assert.True(missing.Count == 0, "members no exemplar carries as a part:\n" + string.Join('\n', missing));
    }

    /// <summary>
    /// Spec §4.10: a variation whose envelope has no canonical form has no re-signed copy. Rendering
    /// one throws, and the pass counts it superseded by the unre-signed copy, which is sent.
    /// </summary>
    [Fact]
    public async Task R14_10_AnEnvelopeThatCannotBeCanonicalizedHasNoReSignedCopy()
    {
        var ct = TestContext.Current.CancellationToken;
        using var context = FuzzContext.Offline(forum);
        var row = Exemplars.All.Single(r => r.Route == "POST /v1/posts" && r.Variant == "question");
        var model = await row.Build(context, ct);
        var part = model.Parts().Single(p => p.Address == "json:/envelope/body");
        var variation = Variations.Closed.Single(v => v.Id == "nul-raw");
        var value = variation.Make(part, model.ValueOf(part));

        Assert.Throws<NotReSignableException>(() => model.Render(part, value, CopyKind.ReSigned));
        using var unsigned = model.Render(part, value, CopyKind.Unsigned);
        Assert.NotNull(unsigned);
    }

    /// <summary>
    /// Spec §4.10, clause 5: a superseded copy sits outside the unsent ceiling, so the set of
    /// variations that may be superseded is bounded here against a list stated by hand, not against
    /// FuzzRun's own constant (review of 0277b97). Over the seven POST /v1/posts rows: 520 in all,
    /// 72 each for nul-raw, lone-high, lone-high-raw, lone-low, lone-low-raw, bad-utf8 and overlong,
    /// 9 for 1e400, and 7 for removed of the json:/envelope root.
    /// </summary>
    [Fact]
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A copy no request can be built for is unsent, which clause 5 bounds elsewhere; this fact counts only superseded copies.")]
    public async Task R14_10_OnlyAVariationWithNoCanonicalFormIsSuperseded()
    {
        var ct = TestContext.Current.CancellationToken;
        using var context = FuzzContext.Offline(forum);
        var rows = Exemplars.All.Where(r => r.Route == "POST /v1/posts").ToList();
        Assert.True(rows.Count == 7, $"expected the seven POST /v1/posts rows, found {rows.Count}");

        var superseded = new List<(string Variant, string Address, string Variation, bool Allowed)>();
        foreach (var row in rows)
        {
            var model = await row.Build(context, ct);
            var signed = ((JsonBody)model.Body!).SignedPointer!;
            foreach (var (part, variation, copy) in Mutator.Plan(model))
            {
                if (copy != CopyKind.ReSigned) continue;
                var value = variation.Make(part, model.ValueOf(part));
                try
                {
                    using var request = model.Render(part, value, copy);
                }
                catch (NotReSignableException)
                {
                    superseded.Add((row.Variant, part.Address, variation.Id, FuzzRun.MayBeSuperseded(part, variation, signed)));
                }
                catch (Exception)
                {
                    // Unsent (no request can be built); not superseded, and clause 5's ceiling holds it.
                }
            }
        }

        var stated = new HashSet<string>(StringComparer.Ordinal) { "nul-raw", "lone-high", "lone-high-raw", "lone-low", "lone-low-raw", "bad-utf8", "overlong", "1e400" };
        var onMembers = superseded.Where(s => s.Address != "json:/envelope").Select(s => s.Variation).ToHashSet(StringComparer.Ordinal);
        var onRoot = superseded.Where(s => s.Address == "json:/envelope").ToList();
        var problems = new List<string>();
        problems.AddRange(onMembers.Except(stated).Order(StringComparer.Ordinal).Select(v => $"superseded, and not in the stated list: {v}"));
        problems.AddRange(stated.Except(onMembers).Order(StringComparer.Ordinal).Select(v => $"in the stated list, and never superseded: {v}"));
        problems.AddRange(onRoot.Where(s => s.Variation != "removed").Select(s => $"superseded on the root other than by removed: {s.Variant} {s.Variation}"));
        problems.AddRange(rows.Where(r => onRoot.Count(s => s.Variant == r.Variant && s.Variation == "removed") != 1)
            .Select(r => $"removed of json:/envelope is not superseded exactly once on {r.Variant}"));
        problems.AddRange(superseded.Where(s => !s.Allowed).Select(s => $"FuzzRun.MayBeSuperseded refuses {s.Variant} {s.Address} {s.Variation}"));
        if (superseded.Count != 520)
        {
            problems.Add($"{superseded.Count} superseded, not 520: " + string.Join(", ", superseded.GroupBy(s => s.Variation)
                .OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Key} {g.Count()}")));
        }

        Assert.True(problems.Count == 0, "superseded copies outside the bound:\n" + string.Join('\n', problems));
    }

    /// <summary>
    /// Spec §4.10: a clause-4 failure is an exemplar defect and never a ledger row, and a budget
    /// failure (D32) never one either; a variation's server fault may be.
    /// </summary>
    [Fact]
    public void R14_10_AnExemplarFailureIsNeverLedgerable()
    {
        Assert.False(FuzzRun.Ledgerable(new FuzzFailure(
            "GET /health", "plain", "exemplar", "first", "plain", 409, "about:blank", 1, "the exemplar was not answered 2xx (clause 4): about:blank")));
        Assert.True(FuzzRun.Ledgerable(new FuzzFailure(
            "GET /health", "plain", "query:none", "nul", "plain", 500, "about:blank", 1, "a server fault")));
    }

    /// <summary>
    /// The closed pass (spec §4.10), and in the same fact its second phase,
    /// <c>R14_10_AHostOverTheFuzzedLogStartsAndServesWhatItAccepted</c>: after each fixture's pass a host
    /// started over its log answers <c>GET /health</c> and every post the run created. Inside it too,
    /// the coverage of every header and query parameter the host read across the whole pass.
    /// </summary>
    [Fact]
    public async Task R14_10_NoVariationOfAnyPartIsAServerFaultAProblemlessRefusalOrOverBudget()
    {
        var ct = TestContext.Current.CancellationToken;
        var outcome = await FuzzRun.ClosedAsync(forum, ct);

        Assert.True(outcome.Failures.Count == 0, $"{outcome.Failures.Count} failures:\n" + string.Join('\n', outcome.Failures));
    }
}
