using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Infrastructure.Lifecycle;
using Curia.OperatorTool;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// PR H (errata G67, R12.18): the operator's words are read against the operator's own <c>--help</c>, a different artifact from each: the runbook and the README
/// name only variables and verbs the tool names, and say the two keys are never in one environment.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class OperatorDocsTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;

        return dir?.FullName
            ?? throw new InvalidOperationException("Could not find repo root (a 'src' directory) above " + AppContext.BaseDirectory);
    }

    private static async Task<(int Exit, string Help)> HelpAsync()
    {
        var gate = EmbeddedLifecycleAppendGate.FromEmbedded("none").Match(g => g, e => throw new InvalidOperationException(e.Type));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await OperatorCommands.RunAsync(
            ["--help"], "Host=unused", TimeProvider.System, gate, OperatorCommands.LoadLogSigner, stdout, stderr, TestContext.Current.CancellationToken)
            .ConfigureAwait(false);
        return (exit, stdout.ToString());
    }

    [GeneratedRegex(@"\bCURIA_[A-Z_]+\b|\bCuria__[A-Za-z_]+\b")]
    private static partial Regex VariableName();

    [GeneratedRegex(@"curia-operator\s+([a-z][a-z-]*)")]
    private static partial Regex VerbName();

    /// <summary>The fenced block of the runbook that holds the <c>compromise</c> command.</summary>
    private static string CommandBlock(string runbook)
    {
        foreach (var block in Regex.Matches(runbook, "```\n(.*?)\n```", RegexOptions.Singleline, TimeSpan.FromSeconds(5)).Select(m => m.Groups[1].Value))
            if (block.Contains("curia-operator compromise", StringComparison.Ordinal))
                return block;

        throw new InvalidOperationException("the runbook has no fenced block holding 'curia-operator compromise'");
    }

    /// <summary>
    /// HSU32. The runbook names the declaration key's variable and the verb that publishes it; its command block holds no log key; it no longer says the declaration
    /// is signed with the log key or that the runbook must not be used; and every variable and verb it uses is one the tool's help names. Red on the seam: the runbook is unchanged.
    /// </summary>
    [Fact]
    public async Task R12_18_HSU32_TheRunbookNamesTheDeclarationKeyAndOnlyWhatTheToolNames()
    {
        var runbook = await File.ReadAllTextAsync(Path.Combine(RepoRoot(), "docs", "runbooks", "key-compromise.md"), TestContext.Current.CancellationToken).ConfigureAwait(true);
        var (_, help) = await HelpAsync().ConfigureAwait(true);

        var flat = Regex.Replace(runbook, @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(5));
        var problems = new List<string>();
        if (!runbook.Contains("CURIA_DECLARATION_SIGNING_KEY_PEM", StringComparison.Ordinal)) problems.Add("the runbook does not name CURIA_DECLARATION_SIGNING_KEY_PEM");
        if (!runbook.Contains("publish-declaration-key", StringComparison.Ordinal)) problems.Add("the runbook does not name publish-declaration-key");
        if (CommandBlock(runbook).Contains("CURIA_LOG_SIGNING_KEY_PEM", StringComparison.Ordinal)) problems.Add("the command block holds CURIA_LOG_SIGNING_KEY_PEM");
        if (flat.Contains("today the declaration is signed with the log key", StringComparison.Ordinal)) problems.Add("the runbook still says the declaration is signed with the log key");
        if (flat.Contains("stop and do not use this runbook", StringComparison.Ordinal)) problems.Add("the runbook still says to stop and not use it");

        // The variables of the operator's own command blocks only: the runbook's client commands name the client's variables (CURIA_FORUM), which this tool's help does not.
        var operatorBlocks = string.Join('\n', Regex.Matches(runbook, "```\n(.*?)\n```", RegexOptions.Singleline, TimeSpan.FromSeconds(5))
            .Select(m => m.Groups[1].Value)
            .Where(b => b.Contains("curia-operator", StringComparison.Ordinal)));
        foreach (var variable in VariableName().Matches(operatorBlocks).Select(m => m.Value).Distinct(StringComparer.Ordinal))
            if (!help.Contains(variable, StringComparison.Ordinal)) problems.Add($"the runbook's operator commands name {variable}, which --help does not");
        foreach (var verb in VerbName().Matches(runbook).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal))
            if (!help.Contains($"curia-operator {verb}", StringComparison.Ordinal)) problems.Add($"the runbook names the verb {verb}, which --help does not");

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// HSU33. <c>--help</c> lists <c>--if-grown</c>, <c>publish-declaration-key</c>, the environment variables and exit codes 5 and 6 with their words, and exits 0;
    /// the README's <c>sign-head</c> fence carries <c>--if-grown</c> and the sentence that the log key and the declaration key are never in one environment, each name
    /// checked against the help. Red on the seam: the help is unchanged.
    /// </summary>
    [Fact]
    public async Task R12_18_HSU33_TheHelpAndTheReadmeSayWhatTheToolDoes()
    {
        var (exit, help) = await HelpAsync().ConfigureAwait(true);
        var readme = await File.ReadAllTextAsync(Path.Combine(RepoRoot(), "README.md"), TestContext.Current.CancellationToken).ConfigureAwait(true);

        var problems = new List<string>();
        if (exit != 0) problems.Add($"--help exits {exit}, not 0");
        foreach (var needed in new[]
        {
            "--if-grown", "publish-declaration-key", "CURIA_EVENTS_POSTGRES", "CURIA_LOG_SIGNING_KEY_PEM", "CURIA_DECLARATION_SIGNING_KEY_PEM", "Curia__Lifecycle__Enabled",
        })
        {
            if (!help.Contains(needed, StringComparison.Ordinal)) problems.Add($"--help does not name {needed}");
        }

        foreach (var code in new[] { "5", "6" })
            if (!Regex.IsMatch(help, $@"^\s+{code}\s+\S", RegexOptions.Multiline, TimeSpan.FromSeconds(5))) problems.Add($"--help's EXIT CODES block has no line for {code}");

        var fence = Regex.Matches(readme, "```bash\n(.*?)\n```", RegexOptions.Singleline, TimeSpan.FromSeconds(5))
            .Select(m => m.Groups[1].Value)
            .FirstOrDefault(b => b.Contains("curia-operator sign-head", StringComparison.Ordinal));
        if (fence is null) problems.Add("the README has no fenced block holding 'curia-operator sign-head'");
        else if (!fence.Contains("--if-grown", StringComparison.Ordinal)) problems.Add("the README's sign-head block does not carry --if-grown");

        if (!Regex.IsMatch(Regex.Replace(readme, @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(5)), @"log key and the declaration key are never in one environment", RegexOptions.None, TimeSpan.FromSeconds(5)))
            problems.Add("the README does not say the log key and the declaration key are never in one environment");

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }
}
