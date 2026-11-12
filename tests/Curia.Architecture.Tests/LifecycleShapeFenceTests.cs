using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Curia.Application.Credentials;
using Curia.Application.Projections;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// LRV11 (errata G68, rulings 2 and 36), as IL fences, to be run in Debug and in Release (D16: the IL differs between them). A consumer reads an identity's lifecycle only through the
/// verifier: nothing outside the verifier's own types calls <c>KeyLifecycle.Of</c> or <c>EnrollmentBinding.Find</c>, both are <see langword="internal"/>, no overload of
/// <c>DisputedPosts.Fold</c> takes a log that was not verified, and no decision reads a lifecycle by <c>ReadByAggregateAsync</c>, which sees rows the forward read does not (ruling 26 (b)).
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class LifecycleShapeFenceTests
{
    /// <summary>The assemblies built from <c>src/</c>, by name, read from the project directories: a different artifact from the type list a scan might carry.</summary>
    private static readonly string[] ShippedNames =
    [
        "Curia.Api", "Curia.Application", "Curia.AuthN", "Curia.Canon", "Curia.Canon.Sodium", "Curia.Client", "curia", "Curia.Domain",
        "Curia.Domain.Primitives", "Curia.Infrastructure", "curia-mcp", "curia-operator",
    ];

    /// <summary>The types of the verifier itself, the only callers of the two shape readers: the verifier, and the log it verifies.</summary>
    private static readonly string[] VerifierTypes = ["Curia.Application.Credentials.LifecycleVerifier", "Curia.Application.Credentials.VerifiedLog"];

    [Fact]
    public void LRV11_TheScanReadsTheShippedAssembliesByName()
    {
        var names = Shipped.ShippedAssemblies().Select(p => Path.GetFileNameWithoutExtension(p)!).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(ShippedNames.Order(StringComparer.Ordinal).ToArray(), names);
    }

    [Fact]
    public void LRV11_NoMethodOutsideTheVerifierCallsTheTwoShapeReaders()
    {
        var callers = new List<string>();
        var verifierCalls = 0;
        foreach (var path in Shipped.ShippedAssemblies())
        {
            foreach (var (callerType, callerMethod, calleeType, calleeMember) in IlCallSites.CallSites(path))
            {
                var shapeReader = (calleeType == "Curia.Application.Credentials.KeyLifecycle" && calleeMember == "Of")
                    || (calleeType == "Curia.Application.Credentials.EnrollmentBinding" && calleeMember == "Find");
                if (!shapeReader) continue;

                if (VerifierTypes.Contains(callerType, StringComparer.Ordinal)) verifierCalls++;
                else callers.Add($"{callerType}.{callerMethod} calls {calleeType.Split('.')[^1]}.{calleeMember}");
            }
        }

        Assert.True(verifierCalls > 0, "the verifier's own calls are found, so the scan is not vacuous");
        Assert.True(callers.Count == 0, $"Expected: no caller  Actual: {string.Join("; ", callers.Order(StringComparer.Ordinal))}");
    }

    [Fact]
    public void LRV11_TheTwoShapeReadersAreInternal()
    {
        var application = typeof(LifecycleVerifier).Assembly;
        var of = typeof(KeyLifecycle).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Single(m => m.Name == "Of");
        var find = application.GetType("Curia.Application.Credentials.EnrollmentBinding")!.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Single(m => m.Name == "Find");

        WholeText.Same("KeyLifecycle.Of internal; EnrollmentBinding.Find internal", $"KeyLifecycle.Of {(of.IsPublic ? "public" : "internal")}; EnrollmentBinding.Find {(find.IsPublic ? "public" : "internal")}");
    }

    [Fact]
    public void LRV11_NoOverloadOfDisputedPostsFoldTakesALogThatWasNotVerified()
    {
        var folds = typeof(DisputedPosts).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).Where(m => m.Name == "Fold").ToArray();

        Assert.NotEmpty(folds);
        Assert.All(folds, m => Assert.Equal(typeof(VerifiedLog), m.GetParameters()[0].ParameterType));
    }

    /// <summary>
    /// Ruling 36: <c>LogBoundKeys</c>, <c>KeyActs</c>, <c>EnrollIdentity</c> and <c>EnrollAgent</c> do not call <c>ReadByAggregateAsync</c>; the whole-log read is the only way a lifecycle
    /// is read, so that a row the whole-log read cannot see is honoured by nobody. Red on the seam, which keeps today's per-aggregate reads.
    /// </summary>
    [Fact]
    public void LRV11_NoLifecycleDecisionReadsAnAggregateByItself()
    {
        var application = Shipped.ShippedAssemblies().Single(p => Path.GetFileNameWithoutExtension(p) == "Curia.Application");
        string[] decisions = ["LogBoundKeys", "KeyActs", "EnrollIdentity", "EnrollAgent"];

        var sites = IlCallSites.CallSites(application)
            .Where(c => c.CalleeMember == "ReadByAggregateAsync" && decisions.Contains(c.CallerType.Split('.')[^1], StringComparer.Ordinal))
            .Select(c => $"{c.CallerType.Split('.')[^1]}.{c.CallerMethod}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(sites.Length == 0, $"Expected: no caller  Actual: {string.Join("; ", sites)} call IEventReader.ReadByAggregateAsync");
    }
}
