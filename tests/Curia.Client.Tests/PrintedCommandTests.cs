using System.Diagnostics.CodeAnalysis;
using Curia.Client.Cli;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// CWK51's unit half (errata G59, R11.82 (b); plan ruling 6): <see cref="PrintedCommand.AsAgent"/> is the one definition of "this command acts as an agent". Each probe is in its failing form: on the
/// seam it returns the command unchanged.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class PrintedCommandTests
{
    /// <summary>CWK51 (unit). A known profile is appended as a shell word, an unknown one as <c>&lt;name&gt;</c>, and a slug that is not a shell word is withheld in the form <c>Hints</c> uses (R10.65). Predicted red: <c>printed "curia budget" with no agent</c>.</summary>
    [Fact]
    public void R11_82_CWK51_AsAgentAppendsTheAgentAsAShellWordOrWithholdsIt()
    {
        var problems = new List<string>();
        foreach (var command in new[] { "curia budget", "curia flags", "curia acts", "curia acts --discard sha256:" + new string('a', 64), "curia resend sha256:" + new string('b', 64) })
        {
            var known = PrintedCommand.AsAgent(command, "a-one");
            if (known != command + " --agent 'a-one'") problems.Add($"printed \"{known}\" with no agent (expected \"{command} --agent 'a-one'\")");
            var unknown = PrintedCommand.AsAgent(command, null);
            if (unknown != command + " --agent <name>") problems.Add($"unknown profile: printed \"{unknown}\", expected \"{command} --agent <name>\"");
            var odd = PrintedCommand.AsAgent(command, "a b");
            if (!odd.Contains(Hints.Withheld, StringComparison.Ordinal) || odd.Contains("--agent 'a b'", StringComparison.Ordinal)) problems.Add($"a slug that is not a shell word: printed \"{odd}\", expected it withheld");
        }

        Assert.True(problems.Count == 0, "Expected: the agent as a shell word, <name> where unknown, withheld where not a word. Actual: " + string.Join("; ", problems));
    }
}
