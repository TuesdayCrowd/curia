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
/// <c>SendDraftAsync</c>, so a new submitting method fails here until it is held too. The submitting
/// method reads no member of the refusal, calls no <c>Output.FailDuplicate</c> and no <c>ExitCode.For</c>,
/// so whether a refusal reaches RefusedAsync cannot depend on the refusal; the names banned are asserted
/// to occur in RefusedAsync or elsewhere in curia.dll, so a misspelt name fails rather than bans
/// nothing.</para>
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

        // R10.71: the submitting method hands its refusal over whole. It reads no member of it, so it
        // cannot choose which refusals reach RefusedAsync (a 409 routed to Output.Fail would drop R8.19's
        // duplicate frame from `curia post` while the call above stayed in the IL), and it prints none.
        static bool Inspects((string CallerType, string CallerMethod, string CalleeType, string CalleeMember) s) =>
            s.CalleeType == "Curia.Client.Refusal"
            || (s.CalleeType == "Curia.Client.Cli.Output" && s.CalleeMember == "FailDuplicate")
            || (s.CalleeType == "Curia.Client.Cli.ExitCode" && s.CalleeMember == "For");

        // Non-vacuity: each name the rule bans is spelled as the IL spells it, or the rule bans nothing.
        Assert.Contains(sites, s => s.CallerMethod == "RefusedAsync" && s.CalleeType == "Curia.Client.Refusal" && s.CalleeMember == "get_AsDuplicate");
        Assert.Contains(sites, s => s.CallerMethod == "RefusedAsync" && s.CalleeType == "Curia.Client.Cli.Output" && s.CalleeMember == "FailDuplicate");
        Assert.Contains(sites, s => s.CalleeType == "Curia.Client.Cli.ExitCode" && s.CalleeMember == "For");

        var inspecting = sites
            .Where(s => submitting.Contains((s.CallerType, s.CallerMethod)) && Inspects(s))
            .Select(s => $"{s.CallerType}.{s.CallerMethod} -> {s.CalleeType}.{s.CalleeMember}")
            .ToList();
        Assert.True(
            inspecting.Count == 0,
            "a posting path reads or prints its refusal itself instead of handing it whole to Program.RefusedAsync, so it can route a duplicate refusal around the verified frame: "
            + string.Join(", ", inspecting));
    }
}
