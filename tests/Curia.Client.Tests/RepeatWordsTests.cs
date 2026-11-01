using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Client.Cli;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// WAF9's command half (errata G53, R11.73): the store sentence <c>curia</c> prints for <c>503 curia/store/unavailable</c> and carries in the help of every verb that
/// writes is the sentence of the errata's <c>R11.73 words</c> (<see cref="PublishedClientWords"/>), folded for whitespace and matched after the refusal's title and
/// type. <c>curia-mcp</c>'s half is in <c>Curia.Mcp.Tests</c> (<c>WordsAfterFaultToolTests</c>).
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("console")]
public sealed partial class RepeatWordsTests
{
    private static string Fold(string text) => PublishedClientWords.Fold(text);

    [GeneratedRegex("\"(?<verb>[a-z]+)\" => await (?:PostAsync|SignalAsync|ResolveAsync|FlagAsync)\\(", RegexOptions.CultureInvariant)]
    private static partial Regex Writers();

    /// <summary>
    /// WAF9. The store sentence equals R11.73 words: <c>curia</c> prints it after the title and type of a <c>503 curia/store/unavailable</c>, and the help of every verb
    /// that writes carries it. The verbs are derived from the dispatch in <c>Program.cs</c> (each whose arm calls <c>PostAsync</c>, <c>SignalAsync</c>,
    /// <c>ResolveAsync</c> or <c>FlagAsync</c>), not listed, and the count is written after, so a writing verb added without the sentence is red.
    /// </summary>
    [Fact]
    public async Task R11_73_WAF9_TheRefusalAndTheHelpOfEveryWritingVerbCarryThePublishedStoreSentence()
    {
        var sentence = Fold(PublishedClientWords.StoreSentence);
        var problems = new List<string>();

        using (var forum = new ListenerForum
        {
            Answer = (_, _) => (PublishedStoreFaults.UnavailableStatus, $"{{\"type\":\"{PublishedStoreFaults.StoreUnavailableType}\",\"title\":\"{PublishedStoreFaults.StoreUnavailableTitle}\",\"detail\":null}}"),
        })
        {
            using var home = new CliHome(forum.Address);
            var run = await home.RunAsync("budget", "--agent", "alice", "--forum", forum.Address.ToString());
            var text = Fold(run.Stdout + " " + run.Stderr);
            var title = text.IndexOf(Fold(PublishedStoreFaults.StoreUnavailableTitle), StringComparison.Ordinal);
            var at = text.IndexOf(sentence, StringComparison.Ordinal);
            if (at < 0) problems.Add($"the refusal lacks \"{sentence}\"");
            else if (title >= 0 && at < title) problems.Add("the sentence comes before the title");
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curia.sln"))) dir = dir.Parent;
        var program = await File.ReadAllTextAsync(Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Curia.sln not found"), "src", "Curia.Client.Cli", "Program.cs"), TestContext.Current.CancellationToken);
        var verbs = Writers().Matches(program).Select(m => m.Groups["verb"].Value).Order(StringComparer.Ordinal).ToArray();
        var missing = verbs.Where(v => !Fold(Help.For(v) ?? string.Empty).Contains(sentence, StringComparison.Ordinal)).ToArray();
        if (verbs.Length != 10) problems.Add($"{verbs.Length} verbs write ({string.Join(' ', verbs)}), expected 10");
        if (missing.Length > 0) problems.Add($"the sentence is absent from the help of {missing.Length} verb(s) ({string.Join(' ', missing)})");

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }
}
