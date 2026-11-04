using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// CWK6, CWK11 (the comment), CWK16, CWK33 and CWK52 (the third reading) (errata G59, R11.82, R6.61; plan trap 118 and trap 150): the name floors of the agent resolver and of the retained head's
/// verification. <b>Every probe here binds a name and not a behaviour, and says so</b>: the claims are held by the client probes (CWK1 and CWK8 run the verbs, CWK25 the plants); a
/// scan finds a site that is past its reach (a call built by concatenation). Self-contained, as each file of this suite is.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class AgentResolverFenceTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Could not find repo root (a 'src' directory) above " + AppContext.BaseDirectory);
    }

    private static string[] Lines(string relative) => File.ReadAllLines(Path.Combine(FindRepoRoot(), relative));

    private static IEnumerable<(string Relative, int Line, string Text)> Code(string directory)
    {
        var repo = FindRepoRoot();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(repo, directory), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(repo, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal)) continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*')) continue;
                yield return (relative, i + 1, lines[i]);
            }
        }
    }

    /// <summary>
    /// CWK6. One resolver, by name (a floor): <c>Slugs()</c> is called in <c>Curia.Client.Cli</c> by <c>AgentResolver</c> and by <c>ListAgents</c> and nowhere else. The claim that no verb acts as a
    /// profile nobody named is CWK1. On the seam the 13 sites call the resolver, so this is green (a control); the unmodified tree had <c>Slugs()</c> at 13 sites.
    /// </summary>
    [Fact]
    public void R11_82_CWK6_SlugsIsCalledOnlyByTheResolverAndTheAgentsListing()
    {
        var problems = new List<string>();
        var lines = Lines("src/Curia.Client.Cli/Program.cs");
        var inProgram = Code("src/Curia.Client.Cli").Where(c => c.Relative.EndsWith("Program.cs", StringComparison.Ordinal) && c.Text.Contains("Slugs()", StringComparison.Ordinal)).ToArray();
        var elsewhere = Code("src/Curia.Client.Cli").Where(c => !c.Relative.EndsWith("Program.cs", StringComparison.Ordinal) && !c.Relative.EndsWith("AgentResolver.cs", StringComparison.Ordinal) && c.Text.Contains("Slugs()", StringComparison.Ordinal));
        foreach (var e in elsewhere) problems.Add($"Slugs() used in {e.Relative}:{e.Line}");

        if (inProgram.Length != 1) problems.Add($"Slugs() used at {inProgram.Length} sites in Program.cs, expected 1 (ListAgents)");
        else if (!string.Concat(lines.Skip(Math.Max(0, inProgram[0].Line - 12)).Take(12)).Contains("ListAgents", StringComparison.Ordinal)) problems.Add($"the one Slugs() in Program.cs (line {inProgram[0].Line}) is not in ListAgents");

        Red.None(problems, "Slugs() called by AgentResolver and ListAgents only");
    }

    /// <summary>
    /// CWK16. No fallback anywhere, by name (a floor): <c>Curia.Mcp</c> does not reference <c>Slugs</c> at all, and <c>Curia.Client.Cli</c> references it in two files only (<c>AgentResolver.cs</c> and
    /// <c>Program.cs</c>). Green on the seam (a control); red under FCWK13.
    /// </summary>
    [Fact]
    public void R11_82_CWK16_NoFallbackAgentIsReachableByName()
    {
        var problems = new List<string>();
        foreach (var hit in Code("src/Curia.Mcp").Where(c => Regex.IsMatch(c.Text, @"\bSlugs\b", RegexOptions.CultureInvariant)))
            problems.Add($"Slugs referenced in {hit.Relative}:{hit.Line}");

        var files = Code("src/Curia.Client.Cli").Where(c => Regex.IsMatch(c.Text, @"\bSlugs\b", RegexOptions.CultureInvariant)).Select(c => Path.GetFileName(c.Relative)).Distinct().Order(StringComparer.Ordinal).ToArray();
        if (!files.SequenceEqual(["AgentResolver.cs", "Program.cs"])) problems.Add($"Slugs referenced in {string.Join(", ", files)}, expected AgentResolver.cs and Program.cs");
        Red.None(problems, "Curia.Mcp never references Slugs; Curia.Client.Cli references it in AgentResolver.cs and Program.cs only");
    }

    /// <summary>
    /// CWK11 (the comment). <c>McpConfiguration.cs</c>'s comment names no CLI behaviour: it does not say the CLI takes "the first profile found" or that a server acting as whichever identity sorts first
    /// "is what the CLI does". A comment that promises a mechanism is held by a probe (trap 78); the behaviour (no agent, no write tool) is the Mcp suite's. Predicted red: the comment says it.
    /// </summary>
    [Fact]
    public void R11_82_CWK11_TheConfigurationCommentDoesNotDescribeAFallbackTheCliHas()
    {
        var text = string.Join('\n', Lines("src/Curia.Mcp/McpConfiguration.cs"));
        var problems = new List<string>();
        if (Regex.IsMatch(text, @"first profile found", RegexOptions.CultureInvariant)) problems.Add("the comment says the CLI's fallback is \"first profile found\"");
        if (Regex.IsMatch(text, @"which is what\s+///\s*the CLI does", RegexOptions.CultureInvariant)) problems.Add("the comment says a first-profile fallback \"is what the CLI does\"");
        Red.None(problems, "McpConfiguration.cs names no first-profile fallback as the CLI's behaviour");
    }

    /// <summary>
    /// CWK33. The structure, by name (a floor): <c>ActaCheck.HeadSignature</c> is called from exactly two sites in <c>PostVerifier.cs</c> (<c>AnchorAsync</c> and <c>CompareAndRetainAsync</c>), neither
    /// inside a loop. The order (the retained head verified before the comparison) is CWK25's claim, by plants whose comparison would fail; the count of verifications is Task 5's recorded
    /// measurement (no port exists to count through, trap 150). Predicted red: <c>HeadSignature called from 1 site</c>.
    /// </summary>
    [Fact]
    public void R6_61_CWK33_HeadSignatureIsCalledFromTwoSitesAndNeitherIsInALoop()
    {
        var lines = Lines("src/Curia.Client/PostVerifier.cs");
        var sites = Enumerable.Range(0, lines.Length).Where(i => lines[i].Contains("ActaCheck.HeadSignature(", StringComparison.Ordinal) && !lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal) && !lines[i].TrimStart().StartsWith('*')).ToArray();
        var problems = new List<string>();
        if (sites.Length != 2) problems.Add($"HeadSignature called from {sites.Length} site(s), expected 2 (AnchorAsync and CompareAndRetainAsync)");
        foreach (var site in sites)
        {
            var indent = lines[site].Length - lines[site].TrimStart().Length;
            for (var j = site - 1; j >= 0 && indent > 4; j--)
            {
                var here = lines[j].Length - lines[j].TrimStart().Length;
                if (lines[j].Trim().Length == 0 || here >= indent) continue;
                if (Regex.IsMatch(lines[j].TrimStart(), @"^(for|foreach|while)\b", RegexOptions.CultureInvariant)) problems.Add($"the call at PostVerifier.cs:{site + 1} is inside a loop (line {j + 1})");
                indent = here;
            }
        }

        Red.None(problems, "two call sites, neither in a loop");
    }

    /// <summary>
    /// CWK52 (the third reading). The 13 sites of <c>Program.cs</c> that resolve the agent serve the 19 verbs of R11.82 (a): 13 calls into <c>AgentResolver</c>, the 11 single-verb sites naming their verb
    /// as a literal, the two shared sites (<c>PostAsync</c> and <c>SignalAsync</c>) taking it as a variable and dispatched by 5 and 3 verbs; the verbs so counted equal the errata's list. A name floor;
    /// green on the seam, red under FCWK42.
    /// </summary>
    [Fact]
    public void R11_82_CWK52_TheThirteenSitesServeTheNineteenVerbsOfTheRequirement()
    {
        var program = string.Join('\n', Lines("src/Curia.Client.Cli/Program.cs"));
        var sites = SiteCall().Matches(program);
        var problems = new List<string>();
        if (sites.Count != 13) problems.Add($"{sites.Count} resolver call sites in Program.cs, expected 13");

        var literals = sites.Select(m => m.Groups[1].Value).Where(v => v.StartsWith('"')).Select(v => v.Trim('"')).ToList();
        var shared = sites.Select(m => m.Groups[1].Value).Where(v => v == "verb").Count();
        if (shared != 2) problems.Add($"{shared} shared sites taking the verb as a variable, expected 2");

        var posted = PostDispatch().Matches(program).Select(m => m.Groups[1].Value).ToList();
        var signalled = SignalDispatch().Matches(program).Select(m => m.Groups[1].Value).ToList();
        if (posted.Count != 5) problems.Add($"{posted.Count} verbs dispatched to PostAsync, expected 5");
        if (signalled.Count != 3) problems.Add($"{signalled.Count} verbs dispatched to SignalAsync, expected 3");

        var verbs = literals.Concat(posted).Concat(signalled).Order(StringComparer.Ordinal).ToArray();
        if (verbs.Length != 19) problems.Add($"the sites serve {verbs.Length} verbs, expected 19");
        var required = PublishedAgentWords.IdentityVerbs.Order(StringComparer.Ordinal).ToArray();
        foreach (var verb in verbs.Except(required, StringComparer.Ordinal)) problems.Add($"\"{verb}\" is served by a site and is not in R11.82 (a)");
        foreach (var verb in required.Except(verbs, StringComparer.Ordinal)) problems.Add($"\"{verb}\" is in R11.82 (a) and no site serves it");
        Red.None(problems, "13 sites serving the 19 verbs of R11.82 (a)");
    }

    /// <summary>
    /// CWK55 (the left-out sites, by name). The sites plan ruling 23 leaves out because the line they print is not a command to run for an agent are given no flag: <c>AnswerStanding</c>'s
    /// <c>curia resolve</c> (addressed to the thread's asker), the <c>curia inbox ...</c> continuation of <c>Program.cs</c> (whose ellipsis stands for the arguments given, <c>--agent</c> among them), and
    /// <c>StoreWording.PostPassage</c> (shared word for word with the helps and tool descriptions, which document a verb). A floor by name: the runtime probe (<c>PrintedCommandRunTests</c>) reaches only the damaged
    /// line's bare <c>curia resend</c>. A control on the seam; red under FCWK39.
    /// </summary>
    [Fact]
    public void R11_82_CWK55_TheLeftOutSitesCarryNoAgentFlag()
    {
        var problems = new List<string>();
        var sites = new (string File, string Needle)[]
        {
            ("src/Curia.Client/AnswerStanding.cs", "curia resolve"),
            ("src/Curia.Client.Cli/Program.cs", "curia inbox ..."),
            ("src/Curia.Client/StoreWording.cs", "resend <digest> (in curia-mcp"),
        };
        foreach (var (file, needle) in sites)
        {
            var hits = Lines(file).Select((text, i) => (text, i)).Where(l => l.text.Contains(needle, StringComparison.Ordinal)).ToArray();
            if (hits.Length == 0) problems.Add($"{file}: the left-out site \"{needle}\" is not found, so the floor holds nothing");
            foreach (var (text, i) in hits.Where(h => h.text.Contains("--agent", StringComparison.Ordinal))) problems.Add($"{file}:{i + 1} gives a left-out site a flag");
        }

        Red.None(problems, "the three left-out sites are found and carry no --agent");
    }

    /// <summary>What a red probe says: what was expected and what was found.</summary>
    private static class Red
    {
        internal static void None(List<string> problems, string expected)
        {
            if (problems.Count > 0)
                throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {string.Join("; ", problems)}");
        }
    }

    [GeneratedRegex(@"AgentResolver\.(?:Resolve|Refuse)\(args, store, (""[a-z]+""|verb)[,)]", RegexOptions.CultureInvariant)]
    private static partial Regex SiteCall();

    [GeneratedRegex(@"await PostAsync\(""([a-z]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex PostDispatch();

    [GeneratedRegex(@"await SignalAsync\(""([a-z]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex SignalDispatch();
}
