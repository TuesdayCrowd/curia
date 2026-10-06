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
/// the derivation to the host both ways.
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
                if (pointer.StartsWith("/envelope/", StringComparison.Ordinal)) envelope.Add(pointer["/envelope/".Length..].Split('/')[0]);
                else if (pointer.Length > 1) body.Add(pointer[1..].Split('/')[0]);
            }
        }

        var missing = SurfaceInventory.EnvelopeMembers().Where(m => !envelope.Contains(m)).Select(m => "envelope: " + m)
            .Concat(SurfaceInventory.BodyMembers(forum).Where(m => !body.Contains(m.Name)).Select(m => $"{m.Method} {m.Pattern}: {m.Name}"))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Assert.True(SurfaceInventory.BodyMembers(forum).Count > 0, "no handler binds a body with named members; the reflection is wrong");
        Assert.True(missing.Count == 0, "members no exemplar carries as a part:\n" + string.Join('\n', missing));
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
