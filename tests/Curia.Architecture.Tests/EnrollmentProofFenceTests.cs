using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// EPP4 (errata G79, R4.46; the plan's ruling 5): <c>EnrollIdentity.EnrollAsync</c> has exactly one production caller, the route, and a caller that does not ask the proof's validator first fails the
/// build. The shape is CS-15's: the set of callers is read from the shipped assemblies' IL and held to a list whose every entry has a reason. A second caller of the use case is a second door to an
/// enrollment that shows no possession; the use case takes no proof (the proof is the route's, ruling 5) so nothing else stops one. The first fact is green on the tree (the route is the one caller
/// today); the second is red until the route calls the validator, and then holds the order's first half (the proof is asked before the use case is called) by the one thing IL can say: the same method
/// calls both.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class EnrollmentProofFenceTests
{
    private const string UseCase = "Curia.Application.Credentials.EnrollIdentity";
    private const string Validator = "Curia.AuthN.EnrollmentProofValidator";

    /// <summary>The methods that may call <c>EnrollIdentity.EnrollAsync</c> in shipped code, and why. A row is added only with its reason, in the pull request that adds the caller.</summary>
    private static readonly (string Caller, string Reason)[] IntendedCallers =
    [
        ("Curia.Api.ForumEndpoints.EnrollAsync", "the enrollment route: the only door to an enrollment, which asks the proof's validator before it calls the use case (R4.46)"),
    ];

    private static string[] CallersOf(string calleeType, string calleeMember) =>
        [.. Shipped.ShippedAssemblies()
            .SelectMany(path => IlCallSites.CallSites(path))
            .Where(s => s.CalleeType == calleeType && s.CalleeMember == calleeMember)
            .Select(s => $"{s.CallerType}.{s.CallerMethod}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    [Fact]
    public void R4_46_EPP4_TheOnlyProductionCallerOfTheEnrollmentUseCaseIsTheRoute()
    {
        Assert.All(IntendedCallers, row => Assert.False(string.IsNullOrWhiteSpace(row.Reason), $"{row.Caller} has no reason"));
        var callers = CallersOf(UseCase, "EnrollAsync");

        Assert.Equal([.. IntendedCallers.Select(r => r.Caller).Order(StringComparer.Ordinal)], callers);
    }

    [Fact]
    public void R4_46_EPP4_EveryCallerOfTheEnrollmentUseCaseAsksTheProofsValidatorToo()
    {
        var useCaseCallers = CallersOf(UseCase, "EnrollAsync");
        var validatorCallers = CallersOf(Validator, "ValidateAsync");

        var withoutProof = useCaseCallers.Except(validatorCallers, StringComparer.Ordinal).ToArray();
        Assert.True(
            withoutProof.Length == 0,
            $"these methods call EnrollIdentity.EnrollAsync and never ask EnrollmentProofValidator.ValidateAsync: {string.Join(", ", withoutProof)} (callers of the validator: [{string.Join(", ", validatorCallers)}])");
    }
}
