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

    /// <summary>The type a call site's caller is declared in, the state machine of an async method folded into its method's own type (<c>Type+&lt;Method&gt;d__3</c> is <c>Type</c>).</summary>
    private static string OwningType(string callerType)
    {
        var plus = callerType.IndexOf("+<", StringComparison.Ordinal);
        return plus < 0 ? callerType : callerType[..plus];
    }

    /// <summary>
    /// Every type in a shipped assembly that calls <c>IEventReader.ReadByAggregateAsync</c> (Amendment 7, ruling 88), each with the reason it reads one aggregate and why that is not a lifecycle decision: a
    /// lifecycle is read from the whole log, because a by-aggregate read sees rows the forward read does not (ruling 26 (b)); none of these reads an identity's key lifecycle. A caller that is not here is red, and a row
    /// whose type no longer calls is red ("stale row").
    /// </summary>
    private static readonly (string Type, string Reason)[] ByAggregateReaders =
    [
        ("Curia.Application.Credentials.AttestOwner", "folds one agent's owner standing (agent.owner-attested) to refuse a second owner: owner standing is not a key lifecycle and no key is read or bound by it"),
        ("Curia.Application.Ingest.ParentLookup", "reads a post's own stream for the post.accepted that names it, to find a reply's parent: no lifecycle row is read"),
        ("Curia.Application.Moderation.AcceptAnswer", "reads a thread root's stream for the answer that stands accepted (R8.68, R8.71), conditioning its append on the version it read: a thread, not an identity"),
        ("Curia.Application.Moderation.RaiseFlag", "reads a post's stream for the flags it holds (R10.70, one flag of a type per raiser per post) and the post's own facts: a post, not an identity"),
    ];

    /// <summary>The decision classes that read a lifecycle: each is asserted to exist in the scanned IL and to read the whole log, so that renaming one cannot make this fence go quiet (the mirror of <c>verifierCalls &gt; 0</c>).</summary>
    private static readonly string[] LifecycleDecisions =
    [
        "Curia.Application.Credentials.LogBoundKeys", "Curia.Application.Credentials.KeyActs", "Curia.Application.Credentials.EnrollIdentity", "Curia.Application.Credentials.EnrollAgent",
    ];

    /// <summary>
    /// Ruling 36, as Amendment 7's ruling 88 states it: the set of types that call <c>ReadByAggregateAsync</c> over all twelve shipped assemblies is the pinned list, so no lifecycle decision reads an aggregate by
    /// itself and a new caller is a decision read in review; and each of <c>LogBoundKeys</c>, <c>KeyActs</c>, <c>EnrollIdentity</c> and <c>EnrollAgent</c> is found and reads the whole log (<c>ReadAllAsync</c>).
    /// </summary>
    [Fact]
    public void LRV11_TheTypesThatReadAnAggregateByItselfAreTheListedOnesAndNoLifecycleDecision()
    {
        var sites = Shipped.ShippedAssemblies().SelectMany(path => IlCallSites.CallSites(path)).ToArray();
        var byAggregate = sites.Where(c => c.CalleeMember == "ReadByAggregateAsync").Select(c => OwningType(c.CallerType)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var listed = ByAggregateReaders.Select(r => r.Type).Order(StringComparer.Ordinal).ToArray();

        Assert.All(ByAggregateReaders, r => Assert.False(string.IsNullOrWhiteSpace(r.Reason), $"{r.Type} has no reason"));
        var unlisted = byAggregate.Except(listed, StringComparer.Ordinal).ToArray();
        var stale = listed.Except(byAggregate, StringComparer.Ordinal).ToArray();
        Assert.True(
            unlisted.Length == 0 && stale.Length == 0,
            $"Expected: the listed callers  Actual: unlisted [{string.Join(", ", unlisted)}]; stale rows [{string.Join(", ", stale)}]");

        foreach (var decision in LifecycleDecisions)
        {
            var calls = sites.Where(c => OwningType(c.CallerType) == decision).ToArray();
            Assert.True(calls.Length > 0, $"{decision} is not found in the scanned IL, so this fence would go quiet");
            Assert.True(calls.Any(c => c.CalleeMember == "ReadAllAsync"), $"{decision} does not read the whole log (ReadAllAsync)");
            Assert.DoesNotContain(decision, byAggregate);
        }
    }
}
