using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Client.Cli;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// AFC17 and AFC19 (errata G55, R11.76): the store sentence at the command. The real <c>curia</c> runs in a child process against a Forum on a loopback socket
/// (<see cref="ListenerForum"/>, <see cref="StubLogListener"/>) that answers <c>503</c> with each refusal. R11.76's sentence is read from the errata
/// (<see cref="PublishedConfirmWords"/>), G53's from <see cref="PublishedStoreFaults"/> (a pointer once the build edits its span, and then read as absent), the neighbours'
/// sentences and the act line from G52 and G53, all folded for whitespace. A probe is written in its failing form and is red on the seam by assertion.
/// </summary>
[Collection("console")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class ConfirmWordsTests
{
    private static string Fold(string text) => Whitespace().Replace(text, " ");

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex("\"(?<verb>[a-z]+)\" => await (?:PostAsync|SignalAsync|ResolveAsync|FlagAsync)\\(", RegexOptions.CultureInvariant)]
    private static partial Regex Writers();

    private static void Said(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    private static async Task<string> PrintedAsync(Action<ListenerForum> arrange, params string[] verbArgs)
    {
        using var forum = new ListenerForum();
        arrange(forum);
        using var home = new CliHome(forum.Address);
        var run = await home.RunAsync([.. verbArgs, "--forum", forum.Address.ToString()]);
        return Fold(run.Stdout + " " + run.Stderr);
    }

    private static string[] WritingVerbs()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curia.sln"))) dir = dir.Parent;
        var program = File.ReadAllText(Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Curia.sln not found"), "src", "Curia.Client.Cli", "Program.cs"));
        return [.. Writers().Matches(program).Select(m => m.Groups["verb"].Value).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// AFC17 (as moved to errata G58). The clause, on every surface of the command: a <c>503 curia/store/unavailable</c> to a read is printed with the clause of a request that is none of the
    /// acts (R11.80 clause words (other)), once; the help of every verb that writes (derived from the dispatch) carries the clause of its own act (a signed post's, a resolve's, a flag's);
    /// and no surface carries G55's sentence or its wait, which G58 replaced.
    /// </summary>
    [Fact]
    public async Task R11_76_AFC17_TheClauseOfEachActIsOnEverySurfaceOfTheCommand()
    {
        var (post, resolve, flag, other) = PublishedResendWords.Clauses();
        var printed = await PrintedAsync(
            f => f.Answer = (_, _) => (PublishedStoreFaults.UnavailableStatus, $"{{\"type\":\"{PublishedStoreFaults.StoreUnavailableType}\",\"title\":\"{PublishedStoreFaults.StoreUnavailableTitle}\",\"detail\":null}}"),
            "budget", "--agent", "alice");
        var verbs = WritingVerbs();
        string ClauseOf(string verb) => Fold(verb == "resolve" ? resolve : verb == "flag" ? flag : post);
        var withoutIt = verbs.Where(v => !Fold(Help.For(v) ?? string.Empty).Contains(ClauseOf(v), StringComparison.Ordinal)).ToArray();

        var expected = "the read's refusal prints the other clause once; the help of every verb that writes carries its act's clause; G55's sentence and its wait are on no surface";
        var problems = new List<string>();
        if (Regex.Count(printed, Regex.Escape(Fold(other))) != 1) problems.Add("the refusal does not hold the other clause once");
        if (withoutIt.Length > 0) problems.Add($"the help of {withoutIt.Length} of {verbs.Length} writing verbs lacks its act's clause ({string.Join(' ', withoutIt)})");
        foreach (var retired in new[] { "do not run the verb again to find out, because ask, answer and comment each sign", "seconds old" })
        {
            if (printed.Contains(retired, StringComparison.Ordinal)) problems.Add($"the refusal still holds \"{retired}\"");
            foreach (var verb in verbs)
                if (Fold(Help.For(verb) ?? string.Empty).Contains(retired, StringComparison.Ordinal)) problems.Add($"the help of {verb} still holds \"{retired}\"");
        }

        Said(expected, problems.Count == 0 ? expected : string.Join("; ", problems));
    }

    /// <summary>
    /// AFC19, controls. The other sentences and the act line are the published ones, byte for byte: <c>curia/log/unreadable</c>'s sentence, the token's, and the act line
    /// of a signed post answered <c>store/unavailable</c> with the digest recomputed from the bytes the stub received.
    /// </summary>
    [Fact]
    public async Task R11_76_AFC19_TheNeighbouringSentencesAndTheActLineAreThePublishedOnes()
    {
        var problems = new List<string>();

        var log = await PrintedAsync(
            f => f.Answer = (_, _) => (PublishedStoreFaults.UnavailableStatus, $"{{\"type\":\"{PublishedStoreFaults.LogUnreadableType}\",\"title\":\"{PublishedStoreFaults.LogUnreadableTitle}\",\"detail\":null}}"),
            "budget", "--agent", "alice");
        if (!log.Contains(Fold(PublishedStoreFaults.LogUnreadableSentence), StringComparison.Ordinal)) problems.Add("the log/unreadable sentence is not G52's");

        var token = await PrintedAsync(
            f => f.Token = () => (PublishedStoreFaults.TokenStatus, $"{{\"error\":\"{PublishedStoreFaults.TokenCode}\",\"error_description\":\"{PublishedStoreFaults.TokenDescription}\"}}"),
            "budget", "--agent", "alice");
        if (!token.Contains(Fold(PublishedStoreFaults.TokenSentence), StringComparison.Ordinal)) problems.Add("the token sentence is not G53's");

        using var stub = new StubLog { RefusesSubmissionsAs = StubIngestRefusal.StoreUnavailable };
        using var listener = new StubLogListener(stub);
        var run = await CliHome.RunInAsync(stub.Store.Root, "ask", "--agent", "alice", "--board", "b", "--title", "A title?", "--body", "A body.", "--forum", listener.Address.ToString());
        var text = Fold(run.Stdout + " " + run.Stderr);
        var digest = stub.WrittenBodies.Where(ReceivedAct.IsSubmission).Select(ReceivedAct.DigestOf).LastOrDefault();
        if (digest is null || !text.Contains(Fold(PublishedStoreFaults.ActLine(digest)), StringComparison.Ordinal)) problems.Add("the act line is not the published one for the digest received");

        Said("the log sentence, the token sentence and the act line are the published ones", problems.Count == 0 ? "the log sentence, the token sentence and the act line are the published ones" : string.Join("; ", problems));
    }
}
