using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.Application.Retrieval;
using Curia.Domain;
using Curia.Domain.Search;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// Register item 286 (errata G71, R9.65), SQW7: nothing that holds a log can wait. In the shipped <c>Curia.Application</c> assembly (a different artifact from the source it constrains) no method of
/// <see cref="HybridSearch"/> that takes the log it decides on (a <see cref="VerifiedLog"/> or a list of events) returns a <see cref="Task"/> or <see cref="ValueTask"/>, no method of
/// <see cref="DuplicateCheck"/> that takes a corpus folded from a log does, and <c>HybridSearch.Search</c> and <c>DuplicateCheck.Assess</c> take no <see cref="CancellationToken"/>. A method that takes
/// the log and can wait is the order R9.65 forbids by construction: it reads first and waits second.
///
/// <para><b>What this does not hold.</b> It holds the Application layer's methods, not the route's order: a route that reads the log and then calls <c>PrepareAsync</c> compiles (G71, non-property (e)),
/// and SQW1 to SQW3 and SQW9 carry that. It is run in Debug and in Release (D16): the IL differs and the rule must hold in both.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class WaitThenReadFenceTests
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static Assembly Application => typeof(IEventStore).Assembly;

    private static bool Waits(Type returnType) =>
        returnType == typeof(Task) || returnType == typeof(ValueTask)
        || (returnType.IsGenericType && (returnType.GetGenericTypeDefinition() == typeof(Task<>) || returnType.GetGenericTypeDefinition() == typeof(ValueTask<>)));

    private static string Name(Type type) =>
        type.IsGenericType ? $"{type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)]}<{string.Join(", ", type.GetGenericArguments().Select(Name))}>" : type.Name;

    private static string Describe(MethodBase method) =>
        $"{method.DeclaringType!.Name}.{method.Name}({string.Join(", ", method.GetParameters().Select(p => Name(p.ParameterType)))})";

    private static bool TakesALog(MethodInfo method) =>
        method.GetParameters().Any(p => p.ParameterType == typeof(VerifiedLog) || p.ParameterType == typeof(IReadOnlyList<AppendedEvent>));

    private static bool TakesACorpus(MethodInfo method) =>
        method.GetParameters().Any(p => p.ParameterType == typeof(ImmutableArray<SearchablePost>));

    private static MethodInfo[] MethodsOf(string typeName) =>
        Application.GetType(typeName, throwOnError: true)!.GetMethods(All);

    [Fact]
    public void R9_65_SQW7_NoMethodOfHybridSearchThatTakesTheLogReturnsATask()
    {
        var offenders = MethodsOf("Curia.Application.Retrieval.HybridSearch")
            .Where(m => TakesALog(m) && Waits(m.ReturnType))
            .Select(m => $"{Describe(m)} returns {Name(m.ReturnType)}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(offenders.Length == 0, "a method that holds the log can wait: " + string.Join("; ", offenders));
    }

    [Fact]
    public void R9_65_SQW7_NoMethodOfDuplicateCheckThatTakesACorpusReturnsATask()
    {
        var offenders = MethodsOf("Curia.Application.Retrieval.DuplicateCheck")
            .Where(m => TakesACorpus(m) && Waits(m.ReturnType))
            .Select(m => $"{Describe(m)} returns {Name(m.ReturnType)}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(offenders.Length == 0, "a method that holds the corpus can wait: " + string.Join("; ", offenders));
    }

    /// <summary>The synchronous remainders exist, take the log or the corpus, take no token and return no task; and the waiting halves take no log, so the fence above is not vacuous.</summary>
    [Fact]
    public void R9_65_SQW7_TheSynchronousStepsExistAndTakeNoTokenAndThePreparingStepsTakeNoLog()
    {
        var search = MethodsOf("Curia.Application.Retrieval.HybridSearch").Where(m => m.Name == "Search").ToArray();
        var assess = MethodsOf("Curia.Application.Retrieval.DuplicateCheck").Where(m => m.Name == "Assess").ToArray();
        var preparing = MethodsOf("Curia.Application.Retrieval.HybridSearch").Concat(MethodsOf("Curia.Application.Retrieval.DuplicateCheck")).Where(m => m.Name == "PrepareAsync").ToArray();

        var said = new List<string>();
        if (search.Length != 1) said.Add($"HybridSearch.Search: {search.Length} found, expected 1");
        if (assess.Length != 1) said.Add($"DuplicateCheck.Assess: {assess.Length} found, expected 1");
        if (preparing.Length != 2) said.Add($"PrepareAsync: {preparing.Length} found, expected 1 on each");
        foreach (var m in search.Concat(assess))
        {
            if (m.GetParameters().Any(p => p.ParameterType == typeof(CancellationToken))) said.Add($"{Describe(m)} takes a CancellationToken");
            if (Waits(m.ReturnType)) said.Add($"{Describe(m)} returns {Name(m.ReturnType)}");
        }

        foreach (var m in preparing)
        {
            if (TakesALog(m) || TakesACorpus(m)) said.Add($"{Describe(m)} takes the log or a corpus");
            if (!Waits(m.ReturnType)) said.Add($"{Describe(m)} does not return a task, so nothing in it can wait");
        }

        Assert.True(said.Count == 0, string.Join("; ", said));
    }
}
