using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// R10.71 on <c>curia</c>'s posting path: a refusal to a submission goes through
/// <c>Program.RefusedAsync</c>, which verifies a duplicate refusal's answers before any of them is
/// written. The call site is in <c>SendDraftAsync</c>, behind a live submission no test drives, so the
/// wiring is held in IL (FA26's target).
///
/// <para>Keyed on the calling method, not only its type: <see cref="IlCallSites.CallSites"/> reports an
/// async method's state machine as the outermost type, so every method of <c>Program</c> shares one
/// <c>CallerType</c>, and a fact keyed on the type alone would pass while <c>SendDraftAsync</c> went back
/// to <c>Output.Fail</c>. Non-vacuous: the set of methods that submit is asserted to be exactly
/// <c>SendDraftAsync</c>, so a new submitting method fails here until it is held too.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class RefusalPathTests
{
    [Fact]
    public void R10_71_ThePostingPathSendsItsRefusalThroughRefusedAsync()
    {
        // This configuration's own build of curia, beside the test assembly (Shipped's rule).
        var path = Path.Combine(AppContext.BaseDirectory, "curia.dll");
        Assert.True(File.Exists(path), $"curia.dll is not in {AppContext.BaseDirectory}");

        var sites = IlCallSites.CallSites(path);

        var submitting = sites
            .Where(s => s.CalleeType == "Curia.Client.ForumSession" && s.CalleeMember == "SubmitAsync")
            .Select(s => (s.CallerType, s.CallerMethod))
            .ToHashSet();

        Assert.True(
            submitting.SetEquals([("Curia.Client.Cli.Program", "SendDraftAsync")]),
            "the methods that call ForumSession.SubmitAsync are not exactly Program.SendDraftAsync: "
            + string.Join(", ", submitting.Select(s => $"{s.CallerType}.{s.CallerMethod}")));

        var missing = submitting
            .Where(caller => !sites.Any(s =>
                s.CallerType == caller.CallerType
                && s.CallerMethod == caller.CallerMethod
                && s.CalleeType == "Curia.Client.Cli.Program"
                && s.CalleeMember == "RefusedAsync"))
            .Select(s => $"{s.CallerType}.{s.CallerMethod}")
            .ToList();
        Assert.True(
            missing.Count == 0,
            "a posting path does not send its refusal through Program.RefusedAsync, so a duplicate refusal's answers would be written unverified or not at all: "
            + string.Join(", ", missing));
    }
}
