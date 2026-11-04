using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// CWK62 (the <c>curia-mcp</c> half), CWK63 and CWK64 (the <c>curia_verify</c> half), CWK66 (errata G59, R6.61, R10.95, R11.83; plan Amendment 3, rulings 42 to 45): the <c>unknown</c> line is composed for
/// the surface that prints it, the could-not-be-checked words name <c>head.json</c> and the person who can remove it, and no result of a session names a tool the session does not register. Every
/// expected word is read from the errata; the command line's half is <c>AmendmentThreeWordsTests</c> in the Client suite.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class UnknownLineSurfacesTests : IDisposable
{
    private const string UnknownDigest = "sha256:" + "ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12";

    private readonly StubLog _log = new(treeSize: 8, postIndex: 3);
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-surfaces-").FullName;

    public void Dispose()
    {
        _log.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static string Fold(string text) => Regex.Replace(text, @"\s+", " ", RegexOptions.CultureInvariant).Trim();

    private static string Shorten(string text) => text.Length <= 160 ? text.Trim() : text[..160].Trim() + "...";

    private static void Red(List<string> problems)
    {
        if (problems.Count > 0) throw new Xunit.Sdk.XunitException("Actual:   " + string.Join("; ", problems));
    }

    private static string TextOf(CallToolResult result) => Fold(string.Join('\n', result.Content.OfType<TextContentBlock>().Select(b => b.Text)));

    private ForumTools ToolsWithAgent(out EnrolledAgent agent)
    {
        Assert.True(_log.Store.Load("alice").TryGetValue(out var loaded, out var error), error?.Detail);
        agent = loaded!;
        var writer = new ForumWriter(agent, new ForumSession(_log.Client(), agent, _log.Store, TimeProvider.System), TimeProvider.System);
        return new ForumTools(_log.Client(), MarkingMode.None, new HeadStore(_home), writer);
    }

    private ForumTools ToolsWithoutAgent() => new(_log.Client(), MarkingMode.None, new HeadStore(_home));

    private static async Task<string> UnknownLineAsync(ForumTools tools) => TextOf(await tools.RecheckAsync([UnknownDigest], TestContext.Current.CancellationToken));

    private static readonly Regex ToolName = new(@"curia_[a-z]+", RegexOptions.CultureInvariant);

    /// <summary>
    /// CWK62 (<c>curia-mcp</c>). The same digest on a session with an agent and on a session with none: the agent's line ends with the published sentence and has no placeholder's sentence; the read-only line
    /// holds the published words (the digest in place of <c>&lt;digest&gt;</c>), says <c>this session cannot send it again</c>, names <c>curia_resend</c> only as what an agent-configured session has and
    /// holds no sentence that offers it; the two lines are unequal; and the <c>curia_recheck</c> description names the same tools the read-only line does. Predicted red: the read-only line holds
    /// <c>curia_resend sends that act again</c>.
    /// </summary>
    [Fact]
    public async Task R10_95_CWK62_TheUnknownLineIsComposedForTheSurfaceThatPrintsIt()
    {
        var problems = new List<string>();
        var sentence = Fold(PublishedAgentWords.McpUnknownSentence);
        var placeholder = Fold(PublishedAgentWords.PlaceholderSentence);

        var configured = ToolsWithAgent(out var agent);
        string agentLine;
        using (agent) agentLine = await UnknownLineAsync(configured);
        var readOnlyLine = await UnknownLineAsync(ToolsWithoutAgent());

        if (!agentLine.Contains(sentence, StringComparison.Ordinal)) problems.Add($"agent: the published sentence is not printed (\"{Shorten(agentLine)}\")");
        if (agentLine.Contains(placeholder, StringComparison.Ordinal)) problems.Add("agent: the placeholder's sentence is printed beside a profile that is known");
        if (agentLine.Contains("In `curia-mcp`, `curia_resend` sends that act again.", StringComparison.Ordinal)) problems.Add("agent: the old sentence is printed");

        var expected = Fold(PublishedAgentWords.ReadOnlyUnknownWords.Replace("<digest>", UnknownDigest, StringComparison.Ordinal));
        if (!readOnlyLine.Contains(expected, StringComparison.Ordinal)) problems.Add($"read-only: the published words \"{Shorten(expected)}\" are not printed (\"{Shorten(readOnlyLine)}\")");
        if (!readOnlyLine.Contains("this session cannot send it again", StringComparison.Ordinal)) problems.Add("read-only: \"this session cannot send it again\" is not printed");
        if (readOnlyLine.Contains("sends that act again", StringComparison.Ordinal)) problems.Add("read-only: a sentence offers curia_resend (\"sends that act again\")");
        if (readOnlyLine.Contains("send exactly that act again with `curia resend", StringComparison.Ordinal)) problems.Add("read-only: the agent line's command sentence is printed");
        if (!readOnlyLine.Contains(placeholder, StringComparison.Ordinal)) problems.Add("read-only: the placeholder's sentence is not printed");
        if (string.Equals(agentLine, readOnlyLine, StringComparison.Ordinal)) problems.Add("the agent line and the read-only line are the same text");

        // The description is one definition of the same words: it names the tools the read-only line names, and says where the session offers curia_resend.
        await using var session = await McpProtocolSession.StartAsync(ToolsWithoutAgent(), null);
        var description = Fold((await session.ListAsync()).Single(t => t.Name == "curia_recheck").Description ?? string.Empty);
        const string DescribedSentence = "send exactly that act again with curia_resend where this session offers it (a session with an agent configured), and otherwise whoever runs the host does with `curia resend <digest> --agent <name>`";
        if (!description.Contains(DescribedSentence, StringComparison.Ordinal)) problems.Add($"the curia_recheck description does not say \"{DescribedSentence}\"");
        var lineTools = ToolName.Matches(PublishedAgentWords.ReadOnlyUnknownWords).Select(m => m.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var from = description.IndexOf("'unknown' names no post", StringComparison.Ordinal);
        var to = from < 0 ? -1 : description.IndexOf("drop the citation.", from, StringComparison.Ordinal);
        if (to < 0) problems.Add("the curia_recheck description has no 'unknown' sentence ending \"drop the citation.\"");
        else
        {
            var describedTools = ToolName.Matches(description[from..to]).Select(m => m.Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (!describedTools.SequenceEqual(lineTools)) problems.Add($"the description's 'unknown' sentence names {string.Join(", ", describedTools)}; the read-only line names {string.Join(", ", lineTools)}");
        }

        Red(problems);
    }

    private SignedHeadDocument ServedHead()
    {
        var read = _log.Client().GetLogHeadAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        Assert.True(read.TryGetValue(out var head, out var refusal), refusal?.Summary);
        return head!;
    }

    private static string Flip(string signature)
    {
        var middle = signature.Length / 2;
        return signature[..middle] + (signature[middle] == 'A' ? 'B' : 'A') + signature[(middle + 1)..];
    }

    private static readonly (string Name, Func<SignedHeadDocument, SignedHeadDocument> Plant, string Words, string NoShell, string LockPhrase)[] Cases =
    [
        ("signature", h => h with { Signature = Flip(h.Signature) }, PublishedAgentWords.SignatureWords, "If you cannot run commands on this host, ask whoever runs it to remove head.json.", "(head.lock stays)"),
        ("key", h => h with { Kid = "unpublished-kid" }, PublishedAgentWords.KeyWords, "If you cannot run commands on this host, ask whoever runs it to copy and remove head.json.", "leave head.lock where it is"),
    ];

    /// <summary>
    /// CWK63 and CWK64 (<c>curia_verify</c>). A retained head that does not verify, once per variant: the printed words are the published words, <c>head.json</c> exists in the printed directory beside
    /// <c>head.lock</c>, the sentence names the file and never the lock as a thing to remove, the no-shell sentence of its variant is printed and the other's is not; the words are followed (remove only
    /// <c>head.json</c>, verify again): a first read with the lock still present, and no no-shell sentence. Predicted red: <c>remove it</c> with no file named; the sentence absent.
    /// </summary>
    [Fact]
    public async Task R11_83_CWK63_CWK64_TheVerifyToolNamesHeadJsonLeavesTheLockAndSaysWhoCanRemoveIt()
    {
        var problems = new List<string>();
        var heads = new HeadStore(_home);
        var directory = heads.DirectoryFor(StubLog.Forum);
        foreach (var (name, plant, words, noShell, lockPhrase) in Cases)
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            var tools = ToolsWithoutAgent();

            var firstRead = TextOf(await tools.VerifyAsync(StubLog.PostId, null, TestContext.Current.CancellationToken));
            if (firstRead.Contains("ask whoever runs it", StringComparison.Ordinal)) problems.Add($"[{name}] a first read prints the no-shell sentence");

            heads.Write(StubLog.Forum, plant(ServedHead()), new DateTimeOffset(2026, 11, 1, 10, 30, 0, TimeSpan.Zero));
            var text = TextOf(await tools.VerifyAsync(StubLog.PostId, null, TestContext.Current.CancellationToken));
            foreach (var stretch in PublishedAgentWords.Stretches(words))
                if (!text.Contains(stretch, StringComparison.Ordinal)) problems.Add($"[{name}] the published words \"{Shorten(stretch)}\" are not printed");
            if (!text.Contains(directory, StringComparison.Ordinal)) problems.Add($"[{name}] the directory is not named");
            if (!text.Contains(noShell, StringComparison.Ordinal)) problems.Add($"[{name}] the no-shell sentence \"{noShell}\" is not printed");
            var other = name == "signature" ? Cases[1].NoShell : Cases[0].NoShell;
            if (!other.Contains(noShell, StringComparison.Ordinal) && !noShell.Contains(other, StringComparison.Ordinal) && text.Contains(other, StringComparison.Ordinal)) problems.Add($"[{name}] the other variant's sentence is printed");

            var files = Directory.EnumerateFiles(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
            if (!files.Contains("head.json") || !files.Contains("head.lock")) problems.Add($"[{name}] the printed directory holds {string.Join(", ", files)}, not head.json and head.lock");
            if (!text.Contains("head.json", StringComparison.Ordinal)) problems.Add($"[{name}] head.json exists in the printed directory and the sentence does not name it");
            if ((Regex.IsMatch(text, @"\bremove it\b", RegexOptions.CultureInvariant) || text.Contains("copy it aside", StringComparison.Ordinal))) problems.Add($"[{name}] \"remove it\" or \"copy it aside\" is printed with a directory named");
            if (!text.Contains(lockPhrase, StringComparison.Ordinal)) problems.Add($"[{name}] \"{lockPhrase}\" is not printed");
            if (text.Replace(lockPhrase, string.Empty, StringComparison.Ordinal).Contains("head.lock", StringComparison.Ordinal)) problems.Add($"[{name}] head.lock is named outside the phrase that leaves it");

            File.Delete(Path.Combine(directory, "head.json"));
            var again = TextOf(await tools.VerifyAsync(StubLog.PostId, null, TestContext.Current.CancellationToken));
            if (again.Contains("COULD NOT BE CHECKED: the head this client retained", StringComparison.Ordinal)) problems.Add($"[{name}] after removing head.json the next verification still could not use the retained head");
            if (!again.Contains("this client had retained no earlier head", StringComparison.Ordinal)) problems.Add($"[{name}] after removing head.json the next verification was not a first read (\"{Shorten(again)}\")");
            if (again.Contains("ask whoever runs it", StringComparison.Ordinal)) problems.Add($"[{name}] a verified line prints the no-shell sentence");
            if (!File.Exists(Path.Combine(directory, "head.lock"))) problems.Add($"[{name}] head.lock is gone after the words were followed");
            if (!File.Exists(Path.Combine(directory, "head.json"))) problems.Add($"[{name}] the first read did not retain the head the Forum serves");
        }

        Red(problems);
    }

    /// <summary>Phrases that introduce a tool name the session does not register: what an agent-configured session has, what this one is without, or where the session offers it.</summary>
    private static readonly string[] Introducers = ["a session configured as that agent", "where this session offers it", "and offers no"];

    /// <summary>Tool names that are legitimately named and not offered, with the reason. None today; the count is asserted.</summary>
    private static readonly (string Name, string Reason)[] ReasonedRows = [];

    private static IEnumerable<string> Unoffered(string text, string where, HashSet<string> offered)
    {
        foreach (Match name in ToolName.Matches(text))
        {
            if (offered.Contains(name.Value)) continue;
            if (ReasonedRows.Any(r => r.Name == name.Value)) continue;
            var window = text[Math.Max(0, name.Index - 140)..Math.Min(text.Length, name.Index + name.Length + 140)];
            if (Introducers.Any(i => window.Contains(i, StringComparison.Ordinal))) continue;
            yield return $"{where} names {name.Value}, which this session does not register, outside the words that introduce it";
        }
    }

    /// <summary>
    /// CWK66. No result names a tool the session lacks. A <c>curia-mcp</c> session with no agent: the text of each state of <c>curia_recheck</c> (current, superseded, withheld, unknown, malformed),
    /// <c>curia_verify</c> (a could-not-be-checked), <c>curia_search</c> and <c>curia_read</c>, and the <c>curia_recheck</c> description, is scanned for <c>curia_&lt;name&gt;</c>: every name is in that session's
    /// <c>tools/list</c> or is introduced by the words that say an agent-configured session has it. The same scan with an agent configured must also pass. Predicted red: <c>curia_resend</c> named in a
    /// read-only result.
    /// </summary>
    [Fact]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The agent is disposed by the using statement that follows the branch.")]
    public async Task R11_82_CWK66_NoResultNamesAToolTheSessionDoesNotRegister()
    {
        Assert.Empty(ReasonedRows);
        var problems = new List<string>();

        foreach (var identity in new[] { false, true })
        {
            var label = identity ? "agent" : "read-only";
            EnrolledAgent? agent = null;
            ForumTools tools;
            if (identity) tools = ToolsWithAgent(out agent);
            else tools = ToolsWithoutAgent();
            using (agent)
            {
                await using var session = await McpProtocolSession.StartAsync(tools, identity ? agent!.Profile.AgentId : null);
                var listed = await session.ListAsync();
                var offered = listed.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
                if (identity != offered.Contains("curia_resend")) problems.Add($"[{label}] the control failed: curia_resend registered is {offered.Contains("curia_resend")}");

                var honest = _log.Submission.PrefixedDigest;
                _log.BatchAnswer = _ =>
                [
                    StubLog.BatchItem(honest, "current"),
                    StubLog.BatchItem(UnknownDigest, "superseded", [honest]),
                    StubLog.BatchItem("sha256:" + new string('1', 64), "withheld"),
                    StubLog.BatchItem("sha256:" + new string('2', 64), "unknown"),
                    StubLog.BatchItem(null, "malformed"),
                ];
                var recheck = await session.CallAsync("curia_recheck", new() { ["digests"] = new[] { honest, UnknownDigest, "sha256:" + new string('1', 64), "sha256:" + new string('2', 64), "nonsense" } });
                problems.AddRange(Unoffered(Fold(recheck.Text), $"[{label}] curia_recheck", offered));
                _log.BatchAnswer = null;
                problems.AddRange(Unoffered(Fold((await session.CallAsync("curia_recheck", new() { ["digests"] = new[] { UnknownDigest } })).Text), $"[{label}] curia_recheck (unknown)", offered));

                var heads = new HeadStore(_home);
                if (Directory.Exists(heads.DirectoryFor(StubLog.Forum))) Directory.Delete(heads.DirectoryFor(StubLog.Forum), recursive: true);
                heads.Write(StubLog.Forum, ServedHead() with { Signature = Flip(ServedHead().Signature) }, new DateTimeOffset(2026, 11, 1, 10, 30, 0, TimeSpan.Zero));
                var verify = await session.CallAsync("curia_verify", new() { ["postId"] = StubLog.PostId });
                if (!verify.Text.Contains("COULD NOT BE CHECKED: the head this client retained", StringComparison.Ordinal)) problems.Add($"[{label}] the control failed: curia_verify did not print a could-not-be-checked");
                problems.AddRange(Unoffered(Fold(verify.Text), $"[{label}] curia_verify", offered));
                problems.AddRange(Unoffered(Fold((await session.CallAsync("curia_search", new() { ["criteria"] = new Dictionary<string, object?> { ["query"] = "anything" } })).Text), $"[{label}] curia_search", offered));
                problems.AddRange(Unoffered(Fold((await session.CallAsync("curia_read", new() { ["postId"] = StubLog.PostId })).Text), $"[{label}] curia_read", offered));
                foreach (var tool in listed.Where(t => t.Name == "curia_recheck"))
                    problems.AddRange(Unoffered(Fold(tool.Description ?? string.Empty), $"[{label}] the curia_recheck description", offered));
            }
        }

        Red(problems);
    }
}
