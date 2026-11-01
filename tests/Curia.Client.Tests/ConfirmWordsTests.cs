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

    /// <summary>G53's store sentence while the errata still holds it, otherwise <see langword="null"/> (the span is a pointer, as the build leaves it).</summary>
    private static string? OldStoreSentence()
    {
        // G53's paragraph after the build holds a pointer, not the sentence: a surviving "may or may not have been recorded" span there is the old sentence.
        var paragraph = PublishedConfirmWords.Paragraph("R11.73 words");
        const string Marker = "If the request was a write it may or may not have been recorded";
        return paragraph.Contains(Marker, StringComparison.Ordinal) ? paragraph : null;
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
    /// AFC17. The sentence, on every surface of the command. A <c>503 curia/store/unavailable</c> is printed with R11.76's sentence; the help of every verb that writes
    /// (derived from the dispatch) carries it; no surface carries G53's sentence beside it; G53's span is a pointer after the build; the resolve, flag and other-kind
    /// clauses stand verbatim in what is printed.
    /// </summary>
    [Fact]
    public async Task R11_76_AFC17_TheStoreSentenceIsR1176sOnEverySurfaceOfTheCommand()
    {
        var sentence = Fold(PublishedConfirmWords.StoreSentence);
        var old = OldStoreSentence() is { } g53 ? Fold(g53) : null;
        var printed = await PrintedAsync(
            f => f.Answer = (_, _) => (PublishedStoreFaults.UnavailableStatus, $"{{\"type\":\"{PublishedStoreFaults.StoreUnavailableType}\",\"title\":\"{PublishedStoreFaults.StoreUnavailableTitle}\",\"detail\":null}}"),
            "budget", "--agent", "alice");
        var verbs = WritingVerbs();
        var withoutIt = verbs.Where(v => !Fold(Help.For(v) ?? string.Empty).Contains(sentence, StringComparison.Ordinal)).ToArray();
        var withOld = old is null ? [] : verbs.Where(v => Fold(Help.For(v) ?? string.Empty).Contains(old, StringComparison.Ordinal)).ToArray();
        var clauses = new[] { PublishedClientWords.ResolveReplayPhrase, PublishedClientWords.FlagRepeatPhrase, PublishedClientWords.OtherKindClause };
        var missingClauses = clauses.Where(c => !printed.Contains(Fold(c), StringComparison.Ordinal)).ToArray();

        var expected = "the refusal prints R11.76's sentence; the help of every verb that writes carries it; G53's sentence is on no surface and its span is a pointer; the resolve, flag and other-kind clauses are printed";
        var problems = new List<string>();
        if (!printed.Contains(sentence, StringComparison.Ordinal)) problems.Add("the refusal lacks \"has not confirmed that the act was recorded\"");
        if (withoutIt.Length > 0) problems.Add($"the help of {withoutIt.Length} of {verbs.Length} writing verbs lacks the sentence ({string.Join(' ', withoutIt)})");
        if (old is not null) problems.Add($"G53's span still holds a store sentence{(withOld.Length > 0 ? $", carried by the help of {withOld.Length} verbs" : string.Empty)}");
        if (missingClauses.Length > 0) problems.Add($"{missingClauses.Length} of the 3 clauses are not printed");
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
