using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// CWK62 (the command line's half), CWK63, CWK64 and CWK65 (errata G59, R6.61, R10.95, R11.82, R11.83; plan Amendment 3, rulings 42 to 46): the words of the <c>unknown</c> line on the command line,
/// the retained head's could-not-be-checked words naming <c>head.json</c> and the person who can remove it, and the withheld form of a printed command closing its code span before the parenthetical.
/// Every expected word is read from the errata (<see cref="PublishedAgentWords"/>); the files are read from the filesystem and the spans from the real output. The <c>curia-mcp</c> halves are
/// <c>UnknownLineSurfacesTests</c> in the Mcp suite.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Each host is a using declaration; the analyzer loses the ownership across the awaits.")]
[Collection("console")]
public sealed class AmendmentThreeWordsTests
{
    private const string Asked = "sha256:" + "e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1";

    private static string Fold(string text) => Regex.Replace(text, @"\s+", " ", RegexOptions.CultureInvariant).Trim();

    private static string Shorten(string text) => text.Length <= 160 ? text.Trim() : text[..160].Trim() + "...";

    // ----- CWK62 (the command line) -----

    /// <summary>
    /// CWK62 (command line). <c>curia recheck</c> of a digest the Forum does not know ends, before the placeholder's sentence, with the published sentence naming <c>curia_resend</c> as what a
    /// <c>curia-mcp</c> session configured as that agent has; it prints the placeholder's sentence; it does not print the old sentence that claimed it of any <c>curia-mcp</c>; and it is not the read-only
    /// session's line. Predicted red: the published sentence is not printed (the old one is).
    /// </summary>
    [Fact]
    public async Task R10_95_CWK62_TheCommandLinesUnknownLineNamesCuriaResendAsWhatAnAgentConfiguredSessionHas()
    {
        var problems = new List<string>();
        using var host = new AgentHost(PrintedScenarios.Printer);
        host.OnOther = request => request.RequestUri!.AbsolutePath.StartsWith("/v1/posts/batch", StringComparison.Ordinal)
            ? ScriptedForum.Json(200, "{\"items\":[" + StubLog.BatchItem(Asked, "unknown") + "]}")
            : ScriptedForum.Problem(404, "curia/posts/not-found", "No such post");
        var run = await host.RunAsync("recheck", Asked, "--forum", AgentHost.ForumAddress.ToString());
        var text = run.Text;

        var sentence = Fold(PublishedAgentWords.McpUnknownSentence);
        if (!text.Contains(sentence, StringComparison.Ordinal)) problems.Add($"the published sentence \"{sentence}\" is not printed (\"{Shorten(text)}\")");
        var placeholder = text.IndexOf(Fold(PublishedAgentWords.PlaceholderSentence), StringComparison.Ordinal);
        if (placeholder < 0) problems.Add("the placeholder's sentence is not printed");
        else if (text.IndexOf(sentence, StringComparison.Ordinal) is var at and >= 0 && at > placeholder) problems.Add("the published sentence comes after the placeholder's sentence, not before it");
        if (text.Contains("In `curia-mcp`, `curia_resend` sends that act again.", StringComparison.Ordinal)) problems.Add("the old sentence, which claims curia_resend of any curia-mcp, is still printed");
        if (text.Contains("this session cannot send it again", StringComparison.Ordinal)) problems.Add("the read-only session's line is printed on the command line");

        Red.None(problems, "the command line's unknown line ends with the published curia-mcp sentence, then the placeholder's sentence, and is not the read-only line");
    }

    // ----- the verification host -----

    private sealed record Verified(int Exit, string Text, string Directory);

    private static async Task<Verified> VerifyAsync(StubLog log, StubLogListener listener)
    {
        var run = await CliHome.RunInAsync(log.Store.Root, "verify", StubLog.PostId, "--forum", listener.Address.ToString());
        return new Verified(run.Exit, Fold(run.Stdout + " " + run.Stderr), new HeadStore(log.Store.Root).DirectoryFor(listener.Address));
    }

    private static SignedHeadDocument ServedHead(StubLog log)
    {
        var read = log.Client().GetLogHeadAsync(TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        Assert.True(read.TryGetValue(out var head, out var refusal), refusal?.Summary);
        return head!;
    }

    private static string Flip(string signature)
    {
        var middle = signature.Length / 2;
        return signature[..middle] + (signature[middle] == 'A' ? 'B' : 'A') + signature[(middle + 1)..];
    }

    /// <summary>The two states the words are written for, by name: the plant, and the paragraph its words are in.</summary>
    private static readonly (string Name, Func<SignedHeadDocument, SignedHeadDocument> Plant, string Words, string NoShell, string LockPhrase)[] Cases =
    [
        ("signature", h => h with { Signature = Flip(h.Signature) }, PublishedAgentWords.SignatureWords, "If you cannot run commands on this host, ask whoever runs it to remove head.json.", "(head.lock stays)"),
        ("key", h => h with { Kid = "unpublished-kid" }, PublishedAgentWords.KeyWords, "If you cannot run commands on this host, ask whoever runs it to copy and remove head.json.", "leave head.lock where it is"),
    ];

    /// <summary>
    /// CWK63 (command line). <c>head.json</c>, from the filesystem. A real <c>curia verify</c> over a retained head that does not verify, once per variant: the printed words are the published words; the
    /// file the sentence names exists in the printed directory, which holds also <c>head.lock</c>; the sentence names <c>head.json</c> and never <c>head.lock</c> as a thing to remove (each mention of
    /// <c>head.lock</c> is inside the lock phrase of its variant); then the words are followed: remove only the named file and verify again, which is a first read that is not could-not-be-checked, exit 0,
    /// with the lock file still present. Predicted red: <c>remove it</c> with no file named.
    /// </summary>
    [Fact]
    public async Task R11_83_CWK63_TheCouldNotBeCheckedWordsNameHeadJsonAndLeaveTheLock()
    {
        var problems = new List<string>();
        foreach (var (name, plant, words, _, lockPhrase) in Cases)
        {
            using var log = new StubLog(treeSize: 8, postIndex: 3);
            using var listener = new StubLogListener(log);
            var heads = new HeadStore(log.Store.Root);
            heads.Write(listener.Address, plant(ServedHead(log)), new DateTimeOffset(2026, 11, 1, 10, 30, 0, TimeSpan.Zero));

            var first = await VerifyAsync(log, listener);
            foreach (var stretch in PublishedAgentWords.Stretches(words))
                if (!first.Text.Contains(stretch, StringComparison.Ordinal)) problems.Add($"[{name}] the published words \"{Shorten(stretch)}\" are not printed");
            if (!first.Text.Contains(first.Directory, StringComparison.Ordinal)) problems.Add($"[{name}] the directory is not named");

            var files = Directory.EnumerateFiles(first.Directory).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
            if (!files.Contains("head.json") || !files.Contains("head.lock")) problems.Add($"[{name}] the printed directory holds {string.Join(", ", files)}, not head.json and head.lock");
            if (!first.Text.Contains("head.json", StringComparison.Ordinal)) problems.Add($"[{name}] head.json exists in the printed directory and the sentence does not name it");

            var line = first.Text[(first.Text.IndexOf("consistency COULD NOT BE CHECKED: the head this client retained", StringComparison.Ordinal) is var from and >= 0 ? from : 0)..];
            if ((Regex.IsMatch(line, @"\bremove it\b", RegexOptions.CultureInvariant) || line.Contains("copy it aside", StringComparison.Ordinal))) problems.Add($"[{name}] \"remove it\" or \"copy it aside\" is printed with a directory named, and the pronoun's antecedent is the directory");
            if (!line.Contains(lockPhrase, StringComparison.Ordinal)) problems.Add($"[{name}] \"{lockPhrase}\" is not printed");
            var withoutLockPhrase = line.Replace(lockPhrase, string.Empty, StringComparison.Ordinal);
            if (withoutLockPhrase.Contains("head.lock", StringComparison.Ordinal)) problems.Add($"[{name}] head.lock is named outside the phrase that leaves it");

            // The words, followed: remove only the named file, verify again.
            File.Delete(Path.Combine(first.Directory, "head.json"));
            var again = await VerifyAsync(log, listener);
            if (again.Text.Contains("COULD NOT BE CHECKED: the head this client retained", StringComparison.Ordinal)) problems.Add($"[{name}] after removing head.json the next verification still could not use the retained head");
            if (!again.Text.Contains("this client had retained no earlier head", StringComparison.Ordinal)) problems.Add($"[{name}] after removing head.json the next verification was not a first read (\"{Shorten(again.Text)}\")");
            if (!File.Exists(Path.Combine(first.Directory, "head.lock"))) problems.Add($"[{name}] head.lock is gone after the words were followed");
            if (!File.Exists(Path.Combine(first.Directory, "head.json"))) problems.Add($"[{name}] the first read did not retain the head the Forum serves");
        }

        Red.None(problems, "both variants print the published words, name head.json in the printed directory beside head.lock, never tell the reader to remove the lock, and removing only head.json makes the next verification a first read with the lock present");
    }

    /// <summary>
    /// CWK64 (command line). The no-shell sentence, both variants: <c>If you cannot run commands on this host, ask whoever runs it to remove head.json.</c> (signature) and <c>... to copy and remove
    /// head.json.</c> (key) in the printed line; absent from a first read, from a verified line and from the failed words. Predicted red: the sentence absent.
    /// </summary>
    [Fact]
    public async Task R11_83_CWK64_TheNoShellSentenceIsPrintedInBothVariantsAndOnlyThere()
    {
        var problems = new List<string>();
        foreach (var (name, plant, _, noShell, _) in Cases)
        {
            using var log = new StubLog(treeSize: 8, postIndex: 3);
            using var listener = new StubLogListener(log);
            var heads = new HeadStore(log.Store.Root);
            var first = await VerifyAsync(log, listener);
            if (first.Text.Contains("ask whoever runs it", StringComparison.Ordinal)) problems.Add($"[{name}] a first read prints the no-shell sentence");

            heads.Write(listener.Address, plant(ServedHead(log)), new DateTimeOffset(2026, 11, 1, 10, 30, 0, TimeSpan.Zero));
            var planted = await VerifyAsync(log, listener);
            if (!planted.Text.Contains(noShell, StringComparison.Ordinal)) problems.Add($"[{name}] the sentence \"{noShell}\" is not printed (\"{Shorten(planted.Text)}\")");
            var other = name == "signature" ? Cases[1].NoShell : Cases[0].NoShell;
            if (planted.Text.Contains(other, StringComparison.Ordinal) && !other.Contains(noShell, StringComparison.Ordinal) && !noShell.Contains(other, StringComparison.Ordinal)) problems.Add($"[{name}] the other variant's sentence is printed");
        }

        // The failed words and the verified words carry no such sentence: the sentence is for a reader who must clear the file, which only a head that could not be used asks for.
        using (var log = new StubLog(treeSize: 8, postIndex: 3))
        {
            var retained = ServedHead(log);
            var served = retained;
            var failed = RetainedHeadCheck.FailedEqualSizes(retained, null, served).Detail;
            var verified = RetainedHeadCheck.ServedExtends(retained, served).Detail;
            if (failed.Contains("ask whoever runs it", StringComparison.Ordinal)) problems.Add("the failed words carry the no-shell sentence");
            if (verified.Contains("ask whoever runs it", StringComparison.Ordinal)) problems.Add("the verified words carry the no-shell sentence");
        }

        Red.None(problems, "the no-shell sentence of each variant is printed in its could-not-be-checked line and in no other line");
    }

    // ----- CWK65 -----

    /// <summary>The problems with the code spans in <paramref name="text"/>: a span holding <c>curia agents</c>, <c>the name of the profile you ran as</c> or a parenthesis. Returns the spans that follow a withheld command.</summary>
    internal static List<string> SpanProblems(string scenario, string text, out List<string> withheld)
    {
        var problems = new List<string>();
        withheld = [];
        foreach (Match span in Regex.Matches(text, "`([^`]*)`", RegexOptions.CultureInvariant))
        {
            var inside = span.Groups[1].Value;
            if (inside.Contains("curia agents", StringComparison.Ordinal) || inside.Contains("the name of the profile you ran as", StringComparison.Ordinal) || inside.Contains('(', StringComparison.Ordinal) || inside.Contains(')', StringComparison.Ordinal))
                problems.Add($"[{scenario}] a code span holds a parenthetical or the withheld words: `{Shorten(inside)}`");
            var after = text[(span.Index + span.Length)..];
            if (after.StartsWith(", with ", StringComparison.Ordinal))
            {
                withheld.Add(inside);
                var published = Fold(PublishedAgentWords.WithheldForm);
                var tail = published[(published.IndexOf("<command>", StringComparison.Ordinal) + "<command>".Length + 1)..];
                if (!after.StartsWith(tail, StringComparison.Ordinal)) problems.Add($"[{scenario}] the text after the span is not the published \"{Shorten(tail)}\" (it is \"{Shorten(after)}\")");
            }
        }

        return problems;
    }

    /// <summary>
    /// CWK65. No span holds a parenthetical. The code spans extracted from the real output of every printer of the withheld form (the act line, the stop sentence, the listing footer, <c>whoami</c> and
    /// <c>enrol</c>), with a profile whose name holds a backtick, planted in a directory of its own: no span contains <c>curia agents</c>, <c>the name of the profile you ran as</c> or a parenthesis; the text
    /// after the span is the published tail; the span, copied and run on the host of several profiles, refuses exit 1 with nothing sent. <b>A negative control:</b> the form of plan ruling 37 fed to the same
    /// extraction is reported. Predicted red: <c>curia agents</c> found inside a span.
    /// </summary>
    [Fact]
    public async Task R11_82_CWK65_NoCodeSpanHoldsTheParentheticalOfAWithheldCommand()
    {
        var problems = new List<string>();

        // The control: ruling 37's form, one span with the parenthetical inside it, is reported by the same extraction.
        var old = SpanProblems("control", "send it again with `curia acts, with --agent and the name of the profile you ran as ('curia agents' lists the names; not written as a command: a value in it holds a character a shell could act on)`", out _);
        if (old.Count == 0) problems.Add("the extraction did not report ruling 37's form (the negative control)");

        const string Backticked = "a`b";
        const string EnrolledName = "c`d";
        using var host = new AgentHost(Backticked, "x-two");
        var runs = new List<(string Scenario, AgentRun Run)>();

        runs.Add(("whoami", await host.RunAsync("whoami", "--agent", Backticked)));
        host.OnOther = request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v1/agents"
            ? ScriptedForum.Json(201, "{\"agent_id\":\"" + AgentHost.AgentIdOf(EnrolledName) + "\",\"kid\":\"" + EnrolledName + "-1\",\"enrolled_at\":\"2026-01-01T00:00:00.0000000+00:00\",\"owner_verified\":false}")
            : ScriptedForum.Problem(404, "curia/posts/not-found", "No such post");
        runs.Add(("enrol", await host.RunAsync("enrol", "--agent", EnrolledName, "--agent-id", AgentHost.AgentIdOf(EnrolledName), "--forum", AgentHost.ForumAddress.ToString())));
        host.OnOther = null;
        runs.Add(("act line", await host.RunAsync("comment", "--board", "dotnet", "--parent", "01PARENT00000000000000000A", "--body", "A body.", "--agent", Backticked)));
        var (digest, _) = host.PlantAct(Backticked, attempts: 0, lastAnswer: null);
        runs.Add(("resend refusal", await host.RunAsync("resend", digest, "--agent", Backticked)));
        runs.Add(("stop sentence", await host.RunAsync("resend", digest, "--agent", Backticked)));
        runs.Add(("listing footer", await host.RunAsync("acts", "--agent", Backticked)));

        foreach (var (scenario, run) in runs)
        {
            // The profile's own name is printed raw by whoami and enrol, as a display literal; its backtick is the name's and not a span's, so the name is set aside before spans are paired.
            var printed = run.Text.Replace(Backticked, "SLUG", StringComparison.Ordinal).Replace(EnrolledName, "SLUG2", StringComparison.Ordinal);
            var found = SpanProblems(scenario, printed, out var withheld);
            problems.AddRange(found);
            if (withheld.Count == 0) problems.Add($"[{scenario}] no withheld command was printed for a profile whose name holds a backtick (\"{Shorten(printed)}\"), so the probe did not reach the form");
            foreach (var command in withheld.Distinct(StringComparer.Ordinal))
            {
                var argv = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (argv.Length < 2 || argv[0] != "curia") { problems.Add($"[{scenario}] the span `{command}` is not a curia command"); continue; }
                var ran = await host.RunAsync([.. argv[1..]]);
                if (ran.Exit != 1 || ran.Requests.Count != 0) problems.Add($"[{scenario}] the span `{command}`, run as printed on the host of several profiles, exit {ran.Exit} with {ran.Requests.Count} request(s), expected exit 1 and nothing sent");
            }
        }

        Red.None(problems, "no span holds a parenthetical, the text after a withheld span is the published tail, and the span run on the host of several profiles refuses exit 1 with nothing sent");
    }
}
