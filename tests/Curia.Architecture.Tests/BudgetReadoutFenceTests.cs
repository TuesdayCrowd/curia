using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Curia.Application.Projections;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// Errata G49 as fences, SEB34, SEB36 and SEB37, over the IL of <c>Curia.Api</c>: the readout's route refuses a query name by the one engine, the budget is
/// counted by one function, and the handler holds no gate. <c>BudgetQueryParameters</c> and the handler (<c>ForumEndpoints.GetBudgetAsync</c>, the name the
/// plan's Task 3 gives it) do not exist on the seam tree, so SEB34 and SEB37 are written in their final form and are red by assertion ("Expected: ... Actual:
/// absent"); SEB36 is green on the seam tree (a control: the seam moved the counts) and red under FSEB10 and FSEB54.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class BudgetReadoutFenceTests
{
    private const string Endpoints = "Curia.Api.ForumEndpoints";
    private const string Handler = "GetBudgetAsync";

    private static readonly string[] UpperName = ["AGENT"];

    private static string ApiDll => Path.Combine(AppContext.BaseDirectory, "Curia.Api.dll");

    private static void Said(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    /// <summary>
    /// SEB34. One engine, no copy: <c>BudgetQueryParameters.Check</c> calls <c>QueryParameterRefusal.Check</c> (an IL fence) with an empty honoured set,
    /// and a query name in any case (<c>AGENT</c>) is refused while no name at all is not.
    /// </summary>
    [Fact]
    public void R9_50_SEB34_TheQueryRefusalIsTheSharedEngineWithAnEmptySet()
    {
        var type = typeof(Curia.Api.QueryParameterRefusal).Assembly.GetType("Curia.Api.BudgetQueryParameters");
        Assert.True(type is not null, "Expected: the type Curia.Api.BudgetQueryParameters Actual: absent");

        var sites = IlCallSites.CallSites(ApiDll)
            .Where(s => s.CallerType == "Curia.Api.BudgetQueryParameters" && s.CallerMethod == "Check")
            .Select(s => $"{s.CalleeType}.{s.CalleeMember}")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var honoured = (System.Collections.IEnumerable)type!.GetProperty("Honoured")!.GetValue(null)!;
        var check = type.GetMethod("Check")!;
        var upper = check.Invoke(null, [UpperName]);
        var none = check.Invoke(null, [Array.Empty<string>()]);

        Said(
            "calls Curia.Api.QueryParameterRefusal.Check; honours 0 names; refuses AGENT; accepts no names",
            $"calls {string.Join(", ", sites)}; honours {honoured.Cast<object>().Count()} names; {(upper is null ? "accepts AGENT" : "refuses AGENT")}; {(none is null ? "accepts no names" : "refuses no names")}");
    }

    /// <summary>
    /// The reasoned rows of SEB36: a method of <c>Curia.Api</c> that builds an <c>AuthorizationRequest</c> and spends no budget, with why. A method not in this
    /// list that builds one must call <c>BudgetReadout</c> (the one counting function), directly or through a method of <c>Curia.Api</c> it calls.
    /// </summary>
    private static readonly (string Method, string Reason)[] BuildsARequestWithoutACount =
    [
        ("Curia.Api.ForumEndpoints.AnonymousReadAllowedAsync", "asks the policy whether an anonymous read of a post or a board is allowed; a read spends no budget and no count is handed to the policy"),
        ("Curia.Api.ForumEndpoints.AuthorizeFlagListAsync", "authorizes the flag/list read of an agent's own records; a read spends no budget (Task 3 shares its preamble with the budget readout, which counts through BudgetReadout and so is not a row)"),
        ("Curia.Api.ForumEndpoints.AuthorizeKeyActAsync", "authorizes key/rotate and key/retire; the rotation budget is a fourth counted budget outside this entry (G49 decision 11b) and is not counted through the policy's three"),
        ("Curia.Api.ForumEndpoints.InboxAsync", "authorizes the inbox read; a read spends no budget"),
    ];

    /// <summary>
    /// SEB36. One counting function: <c>Curia.Api</c> has no method named <c>*InBudgetWindow</c>; <c>AcceptanceProjector.CountBy</c> is gone; and every
    /// method of <c>Curia.Api</c> that constructs an <c>AuthorizationRequest</c> is either a reasoned row of a method that spends no budget or calls
    /// <c>BudgetReadout</c> in its call closure (by <see cref="IlCallSites"/>).
    /// </summary>
    [Fact]
    public void R7_28_SEB36_OneCountingFunction()
    {
        var sites = IlCallSites.CallSites(ApiDll);
        var methods = typeof(Curia.Api.QueryParameterRefusal).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Select(m => m.Name)
            .Where(n => n.EndsWith("InBudgetWindow", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var countBy = typeof(AcceptanceProjector).GetMethod("CountBy");

        var calls = sites.GroupBy(s => $"{s.CallerType}.{s.CallerMethod}")
            .ToDictionary(g => g.Key, g => g.Select(s => $"{s.CalleeType}.{s.CalleeMember}").Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        bool CallsReadout(string method, HashSet<string> seen)
        {
            if (!seen.Add(method) || !calls.TryGetValue(method, out var callees)) return false;
            return callees.Any(c => c.StartsWith("Curia.Application.Budget.BudgetReadout.", StringComparison.Ordinal)) || callees.Any(c => CallsReadout(c, seen));
        }

        var builders = sites
            .Where(s => s.CalleeType == "Curia.Domain.Authorization.AuthorizationRequest" && s.CalleeMember == ".ctor")
            .Select(s => $"{s.CallerType}.{s.CallerMethod}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var uncounted = builders.Where(b => !CallsReadout(b, []) && !BuildsARequestWithoutACount.Any(r => r.Method == b && r.Reason.Length > 0)).ToArray();

        Said(
            "no *InBudgetWindow method; CountBy gone; every request-building method counts through BudgetReadout or is a reasoned row",
            $"{(methods.Length == 0 ? "no *InBudgetWindow method" : "methods " + string.Join(", ", methods))}; {(countBy is null ? "CountBy gone" : "CountBy present")}; {(uncounted.Length == 0 ? "every request-building method counts through BudgetReadout or is a reasoned row" : "uncounted: " + string.Join(", ", uncounted))}");
    }

    /// <summary>
    /// SEB37. The handler holds no gate: <c>ForumEndpoints.GetBudgetAsync</c> exists, and neither it nor any method of <c>Curia.Api</c> in its call closure
    /// calls <c>IAgentWriteGate</c>, <c>IFlagRaiserGate</c> or <c>IRevisionGate</c>.
    /// </summary>
    [Fact]
    public void R7_28_SEB37_TheHandlerHoldsNoGate()
    {
        var sites = IlCallSites.CallSites(ApiDll);
        var calls = sites.GroupBy(s => $"{s.CallerType}.{s.CallerMethod}")
            .ToDictionary(g => g.Key, g => g.Select(s => $"{s.CalleeType}.{s.CalleeMember}").Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var root = $"{Endpoints}.{Handler}";
        Assert.True(calls.ContainsKey(root), $"Expected: a handler {root} Actual: absent");

        string[] gates = ["Curia.Application.Ports.IAgentWriteGate.", "Curia.Application.Ports.IFlagRaiserGate.", "Curia.Application.Ports.IRevisionGate."];
        var closure = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>([root]);
        var entered = new List<string>();
        while (queue.Count > 0)
        {
            var method = queue.Dequeue();
            if (!closure.Add(method) || !calls.TryGetValue(method, out var callees)) continue;
            entered.AddRange(callees.Where(c => gates.Any(g => c.StartsWith(g, StringComparison.Ordinal))).Select(c => $"{method} -> {c}"));
            foreach (var callee in callees.Where(calls.ContainsKey)) queue.Enqueue(callee);
        }

        Said("the handler's closure calls no gate", entered.Count == 0 ? "the handler's closure calls no gate" : "calls " + string.Join("; ", entered));
    }
}
