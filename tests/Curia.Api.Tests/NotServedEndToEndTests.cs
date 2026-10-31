using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Api.Tests.Fuzz;
using Curia.Canon.Json;
using Curia.Mcp;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// PR 3 (errata G50, R10.91), Task 0's probe RLW12 against a real Forum on a real socket, over the three ways the Forum stops serving a post: a human withhold, a
/// human quarantine and an automated quarantine (made as <c>ForumFixture.QuarantineAsAutomatedAsync</c> makes it; the host has no producer of one, M5). For each,
/// <c>curia_read</c> of the original states R10.91's pointer beside the revision's digest and again beside the same digest in the text-that-stands clause of the
/// same line; <c>curia_recheck</c> of that digest says <c>withheld</c> (the pointer is true in every state); the thread's <c>posts=</c> is the served members; no
/// output names the unserved post's id or counts what the thread does not list; and restoring it removes the line.
///
/// <para><b>Oracles.</b> The Forum's own answer to <c>recheck</c>, in the same fact as the pointer (trap 57: one word, three predicates); the members the thread holds
/// by the construction (a question and a revision, one of them not served); the id the scan looks for is the receipt's. The definition sentence is G50's.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class NotServedEndToEndTests(KestrelForumFixture forum) : IClassFixture<KestrelForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex(@"\b\d+ (?:withheld|unserved|not served|hidden)\b|(?:withheld|unserved|hidden)\s*[=:]\s*\d", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex ACountOfWhatIsNotListed();

    private static string Block(CallToolResult result, int i) => Assert.IsType<TextContentBlock>(result.Content[i]).Text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static bool EveryMentionHasTheWordsBeside(string text, string digest, string words)
    {
        var literal = DisplayLiteral.Of(digest);
        var at = text.IndexOf(literal, StringComparison.Ordinal);
        if (at < 0) return false;
        while (at >= 0)
        {
            var end = at + literal.Length;
            var found = text[end..Math.Min(text.Length, end + 80 + words.Length)].IndexOf(words, StringComparison.Ordinal);
            if (found < 0 || found > 80) return false;
            at = text.IndexOf(literal, end, StringComparison.Ordinal);
        }

        return true;
    }

    private static int Count(string text, string needle)
    {
        var n = 0;
        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    /// <summary>RLW12. A real withhold, end to end, over a human withhold, a human quarantine and an automated quarantine.</summary>
    [Fact]
    public async Task R10_91_RLW12_ARealWithholdIsSaidInTheDefinitionsWordsInEveryState()
    {
        var sentence = PublishedReadersLastWords.DefinitionSentence();
        using var scene = new ReadersLastWordsScene(forum);
        var board = "board-" + ReadersLastWordsScene.Suffix();
        var asker = await scene.T1Async("asker", board);

        // The fixture's restore is the human moderator's writer, which refuses to lift a record that is not a human one (no-op), and the host has no producer or
        // lifter of an automated quarantine (Task 0, M5): the automated state is read withheld and never restored here.
        var states = new (string Name, Func<RlwPost, Task> Apply, bool Restorable)[]
        {
            ("a human withhold", p => forum.WithholdAsync(p.Id, Ct), true),
            ("a human quarantine", p => forum.QuarantineAsync(p.Id, Ct), true),
            ("an automated quarantine", p => forum.QuarantineAsAutomatedAsync(p.Id, p.Digest, Ct), false),
        };

        var problems = new List<string>();
        foreach (var (name, apply, restorable) in states)
        {
            var word = "finch" + ReadersLastWordsScene.Suffix();
            var chain = await scene.QuestionWithRevisionsAsync(asker, board, word, 1);
            var (question, revision) = (chain[0], chain[1]);
            await apply(revision);

            var read = await asker.Tools.ReadAsync(question.Id, Ct);
            var first = Block(read, 0);
            var revisionLine = first.Split('\n').FirstOrDefault(l => l.StartsWith("revision", StringComparison.Ordinal)) ?? string.Empty;
            if (Count(revisionLine, sentence) < 2 || !EveryMentionHasTheWordsBeside(revisionLine, revision.Digest, sentence))
                problems.Add($"{name}: Expected: the pointer beside the digest and again beside it in the text-that-stands clause Actual: {revisionLine}");
            if (!first.Contains("posts=1", StringComparison.Ordinal)) problems.Add($"{name}: Expected: posts=1 (the served members) Actual: {first}");

            var rechecked = ReadersLastWordsScene.Flatten(await asker.Tools.RecheckAsync([revision.Digest], Ct));
            if (!rechecked.Contains("withheld", StringComparison.Ordinal)) problems.Add($"{name}: the pointer is false: recheck of the digest answers {rechecked.Trim()}");

            var searched = await asker.Tools.SearchAsync(new SearchCriteria { Query = word, Board = board }, Ct);
            var verified = await asker.Tools.VerifyAsync(question.Id, null, Ct);
            var thread = await scene.CuriaAsync("thread", question.Id, "--agent", asker.Slug, "--forum", scene.Address);
            foreach (var (form, text) in new[] { ("curia_read", ReadersLastWordsScene.Flatten(read)), ("curia_search", ReadersLastWordsScene.Flatten(searched)), ("curia_verify", ReadersLastWordsScene.Flatten(verified)), ("curia thread", thread.Stdout) })
            {
                if (text.Contains(revision.Id, StringComparison.Ordinal)) problems.Add($"{name}: {form} names the unserved post's id");
                if (ACountOfWhatIsNotListed().IsMatch(text)) problems.Add($"{name}: {form} counts what the thread does not list: {ACountOfWhatIsNotListed().Match(text).Value}");
            }

            if (thread.Exit != 0 || !thread.Stdout.Contains(sentence, StringComparison.Ordinal)) problems.Add($"{name}: curia thread: Expected: exit 0 and the pointer Actual: exit {thread.Exit} {thread.Stdout.Trim()}");

            if (!restorable) continue;
            await forum.RestoreAsync(revision.Id, Ct);
            var restored = Block(await asker.Tools.ReadAsync(question.Id, Ct), 0);
            if (restored.Contains("not served", StringComparison.Ordinal) || restored.Contains(sentence, StringComparison.Ordinal)) problems.Add($"{name}: restoring it left the line: {restored}");
            if (!restored.Contains("posts=2", StringComparison.Ordinal)) problems.Add($"{name}: restoring it did not return the member: {restored}");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
