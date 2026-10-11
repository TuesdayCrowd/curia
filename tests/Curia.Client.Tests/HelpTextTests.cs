using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using Curia.Client.Cli;
using Curia.Domain.Authorization;
using Curia.Domain.Serving;
using Curia.Domain.Tests.Authorization;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>What <c>curia help</c> publishes, read from the text it prints.</summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class HelpTextTests
{
    [Fact]
    public void R10_71_ExitCodeSixNamesTheSpanCheck()
    {
        var entry = ExitCodeEntry(6);

        Assert.Contains("R10.71", entry, StringComparison.Ordinal);
        Assert.Contains("content   FAILED:", entry, StringComparison.Ordinal);
        Assert.Contains("NOT A DELIMITED SPAN:", entry, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every line EXIT CODES 6 names as a failed span's marker is one a failed span's passage writes, and
    /// each is written only by the failure it names. The markers are taken from the help, not written
    /// here, so the help and the frame code check each other (FA29's target).
    /// </summary>
    [Fact]
    public void R10_71_EveryLineExitSixNamesIsOneAFailedSpanWrites()
    {
        var markers = Regex.Matches(ExitCodeEntry(6), "'([^']+:)'")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(
            markers.SetEquals(["content   FAILED:", "NOT A DELIMITED SPAN:"]),
            "EXIT CODES 6 names these markers: " + string.Join(" | ", markers));

        var verdict = new SignatureVerdict(true, "k", "d");
        var delimited = SpanCheckTests.Post(
            SpanCheckTests.Envelope("The words the author signed."),
            Datamarking.Render(SpanCheckTests.Envelope("zyzzyva"), MarkingMode.None),
            MarkingMode.None);
        var undelimited = delimited with { Rendered = "undelimited" };

        var passages = new Dictionary<string, Passage>(StringComparer.Ordinal)
        {
            ["delimited"] = new Passage(delimited, verdict),
            ["undelimited"] = new Passage(undelimited, verdict),
        };
        foreach (var passage in passages.Values)
            Assert.Equal(CheckOutcome.Failed, passage.Outcome);

        var owner = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["content   FAILED:"] = "delimited",
            ["NOT A DELIMITED SPAN:"] = "undelimited",
        };
        foreach (var marker in markers)
        {
            foreach (var (name, passage) in passages)
            {
                var writes = passage.Render().Split('\n').Any(l => l.TrimStart(' ').StartsWith(marker, StringComparison.Ordinal));
                Assert.True(
                    writes == (owner[marker] == name),
                    $"the {name} passage {(writes ? "writes" : "does not write")} a line beginning '{marker}'");
            }
        }
    }

    /// <summary>
    /// D37: the tier reminder names, as T0's, exactly the verbs for what Table 10 grants T0 beyond what it
    /// grants an anonymous reader, and none for what it grants only from T1. The table is parsed from the
    /// white paper, so the reminder is held to the published table and not to this file's idea of it
    /// (FA11's target).
    /// </summary>
    [Fact]
    public void D37_TheTierReminderListsWhatTable10GrantsT0()
    {
        var verbs = new Dictionary<(ResourceKind, ActionKind), string[]>
        {
            [(ResourceKind.Question, ActionKind.Create)] = ["ask"],
            [(ResourceKind.Comment, ActionKind.Create)] = ["comment"],
            [(ResourceKind.Revision, ActionKind.Create)] = ["revision"],
            [(ResourceKind.Answer, ActionKind.Accept)] = ["resolve"],
            [(ResourceKind.Flag, ActionKind.Raise)] = ["flag"],
            [(ResourceKind.Flag, ActionKind.List)] = ["flags"],
            [(ResourceKind.Answer, ActionKind.Create)] = ["answer"],
            [(ResourceKind.Vote, ActionKind.Cast)] = ["endorse"],
            [(ResourceKind.Verification, ActionKind.Submit)] = ["reproduce", "contradict"],
        };

        static bool Grants(Table10Cell cell) => cell is Table10Cell.Allowed or Table10Cell.RateLimited;

        // T0's grants beyond an anonymous reader's: what an enrolled agent may do that reading is not.
        var t0 = PublishedTable10.Rows
            .Where(r => Grants(r.Value[PrincipalTier.T0]) && !Grants(r.Value[PrincipalTier.Anonymous]))
            .Select(r => r.Key)
            .ToList();
        var t1Only = PublishedTable10.Rows
            .Where(r => Grants(r.Value[PrincipalTier.T1]) && !Grants(r.Value[PrincipalTier.T0]))
            .Select(r => r.Key)
            .ToList();

        // Non-vacuity: the table yields grants, and the map names a verb for every one of them.
        Assert.NotEmpty(t0);
        Assert.NotEmpty(t1Only);
        foreach (var grant in t0.Concat(t1Only))
            Assert.True(verbs.ContainsKey(grant), "no verb mapped for Table 10's " + PublishedTable10.Describe(grant, PrincipalTier.T0));

        var reminder = Help.TierReminder;
        var may = reminder.IndexOf("may:", StringComparison.Ordinal);
        var needs = reminder.IndexOf("needs T1", StringComparison.Ordinal);
        Assert.True(may >= 0 && needs > may, "the reminder no longer reads 'may:' ... 'needs T1'");

        // T0's list: from 'may:' to the start of the line that says what needs T1.
        var end = reminder.LastIndexOf('\n', needs);
        Assert.True(end > may, "the reminder's T0 list and its T1 line are one line");
        var t0Words = Regex.Split(reminder[(may + "may:".Length)..end], "[^a-z]+")
            .ToHashSet(StringComparer.Ordinal);

        foreach (var grant in t0)
            foreach (var verb in verbs[grant])
                Assert.True(t0Words.Contains(verb), $"the reminder does not name '{verb}' among T0's actions");
        foreach (var grant in t1Only)
            foreach (var verb in verbs[grant])
                Assert.False(t0Words.Contains(verb), $"the reminder names '{verb}', which needs T1, among T0's actions");

        Assert.DoesNotContain("nothing else", reminder, StringComparison.Ordinal);
    }

    /// <summary>
    /// D37: <c>curia whoami</c> prints the reminder and then said its last sentence again. Across the
    /// reminder and <c>WhoAmI</c>'s own lines, read from the source that prints them, the tier endpoint's
    /// absence is stated once.
    /// </summary>
    [Fact]
    public void D37_WhoamiStatesTheTierEndpointsAbsenceOnce()
    {
        const string sentence = "no endpoint that reports";
        var whoami = MethodSource(SourceText("src/Curia.Client.Cli/Program.cs"), "private static int WhoAmI(");
        Assert.Contains("Help.TierReminder", whoami, StringComparison.Ordinal);

        Assert.Equal(1, Occurrences(Help.TierReminder + "\n" + whoami, sentence));
    }

    /// <summary>
    /// D37: the inbox prints a post id and an author per line (<c>InboxAsync</c>), and its banner said
    /// "These are titles". It now says what the lines are.
    /// </summary>
    [Fact]
    public void D37_TheInboxBannerSaysWhatItsLinesAre()
    {
        var inbox = MethodSource(SourceText("src/Curia.Client.Cli/Program.cs"), "private static async Task<int> InboxAsync(");
        Assert.Contains("Output.Line($\"{post.PostId}   {post.Provenance.Author}\");", inbox, StringComparison.Ordinal);

        Assert.DoesNotContain("title", Help.InboxBanner, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("post id", Help.InboxBanner, StringComparison.Ordinal);
        Assert.Contains("author", Help.InboxBanner, StringComparison.Ordinal);

        // The Forum assigns a post's id at ingest (R8.3); only the author was chosen by another agent.
        Assert.Contains("assigned by the Forum", Help.InboxBanner, StringComparison.Ordinal);
        Assert.DoesNotContain("Both values were chosen", Help.InboxBanner, StringComparison.Ordinal);
    }

    /// <summary>
    /// D37: <c>search</c> fuses a lexical and a vector channel (the banner it prints says so), and its
    /// usage line said "Lexical only" while <c>SearchBanner</c>'s comment said the vector half did not exist.
    /// </summary>
    [Fact]
    public void D37_SearchIsDescribedAsHybrid()
    {
        Assert.Contains("vector", Help.SearchBanner, StringComparison.Ordinal);

        Assert.DoesNotContain("Lexical only", Help.Usage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("only the lexical half exists", SourceText("src/Curia.Client.Cli/Help.cs"), StringComparison.Ordinal);
    }

    /// <summary>
    /// D37: a post newer than the latest signed head could not be checked, and the detail said a later
    /// head would cover it without saying who signs one. It names the operator's command, as the no-head
    /// case already did; <c>curia_verify</c> reads the same detail.
    /// </summary>
    [Fact]
    public async Task D37_VerifyPastTheHeadNamesTheRemedy()
    {
        var home = Directory.CreateTempSubdirectory("curia-d37-").FullName;
        try
        {
            using var log = new StubLog();
            log.HeadTreeSize = log.PostIndex;

            var result = await new PostVerifier(log.Client(), new HeadStore(home))
                .VerifyAsync(StubLog.PostId, MarkingMode.None, TestContext.Current.CancellationToken);
            Assert.True(result.TryGetValue(out var verification, out var refusal), refusal?.Summary);

            Assert.Equal(CheckOutcome.CouldNotCheck, verification!.Inclusion.Outcome);
            Assert.Contains("not covered by the signed head", verification.Inclusion.Detail, StringComparison.Ordinal);
            Assert.Contains("curia-operator sign-head", verification.Inclusion.Detail, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>
    /// D37: a first verification has no retained head to compare with, so its consistency is COULD NOT
    /// BE CHECKED and it exits 0 by design. The verifier is run to show both, and EXIT CODES 0 says so.
    /// </summary>
    [Fact]
    public async Task D37_ExitCodesSayAFirstVerificationExitsZero()
    {
        var home = Directory.CreateTempSubdirectory("curia-d37-").FullName;
        try
        {
            using var log = new StubLog();
            var result = await new PostVerifier(log.Client(), new HeadStore(home))
                .VerifyAsync(StubLog.PostId, MarkingMode.None, TestContext.Current.CancellationToken);
            Assert.True(result.TryGetValue(out var verification, out var refusal), refusal?.Summary);

            Assert.Equal(CheckOutcome.CouldNotCheck, verification!.Consistency.Outcome);
            Assert.True(verification.Consistency.NothingToCompare, verification.Consistency.Detail);
            Assert.Equal(ExitCode.Ok, ExitCode.ForOutcomes(verification.Overall));

            var printed = verification.Consistency.Describe;
            var marker = printed[..printed.IndexOf(':', StringComparison.Ordinal)];

            var entry = ExitCodeEntry(ExitCode.Ok);
            Assert.Contains("first verification", entry, StringComparison.Ordinal);
            Assert.Contains("R6.53", entry, StringComparison.Ordinal);
            Assert.Contains(marker, entry, StringComparison.Ordinal);

            // Every other consistency check not run exits 8, and entry 8 says so.
            var fault = ExitCodeEntry(ExitCode.ForumFault);
            Assert.Contains("could not be run", fault, StringComparison.Ordinal);
            Assert.Contains("consistency check", fault, StringComparison.Ordinal);
            Assert.Contains("R6.53", fault, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>
    /// D37: every verb <c>Program.Main</c> dispatches answers <c>--help</c> and <c>-h</c> with its own
    /// usage, and only when the flag is the verb's first argument; a verb Main does not dispatch has none
    /// (FA12's target, which is why the unknown verb is asserted: a <c>For</c> that answered everything
    /// would pass the rest).
    ///
    /// <para><b>Its own usage, held to its own command.</b> The verbs and the method each calls are read
    /// from <c>Main</c>'s dispatch, and each method's <c>args.Unknown</c> list from its source: a
    /// different artifact from the help. A verb's usage is not the whole of <see cref="Help.Usage"/>
    /// (i), begins with the verb (ii), names no other verb's command (iii), names only flags its method
    /// accepts (iv), and, where the method serves that verb alone, names every one of them (v) (FA41's
    /// and FA42's target).</para>
    /// </summary>
    [Fact]
    public void D37_EveryVerbAnswersHelp()
    {
        var program = SourceText("src/Curia.Client.Cli/Program.cs");
        var main = MethodSource(program, "private static async Task<int> Main(");
        var dispatch = main[main.IndexOf("return command switch", StringComparison.Ordinal)..];
        dispatch = dispatch[..dispatch.IndexOf("_ =>", StringComparison.Ordinal)];

        // Each arm: the whole quoted alternation, then the method it calls.
        var arms = Regex.Matches(dispatch, "((?:\"[a-z]+\"\\s*or\\s*)*\"[a-z]+\")\\s*=>\\s*(?:await\\s+)?([A-Za-z]+)\\(")
            .Select(m => (
                Verbs: Regex.Matches(m.Groups[1].Value, "\"([a-z]+)\"").Select(v => v.Groups[1].Value).ToList(),
                Method: m.Groups[2].Value))
            .ToList();
        var method = arms.SelectMany(a => a.Verbs.Select(v => (Verb: v, a.Method)))
            .ToDictionary(p => p.Verb, p => p.Method, StringComparer.Ordinal);
        var verbs = method.Keys.ToList();
        Assert.True(verbs.Count >= 20, "Main dispatches " + verbs.Count.ToString(CultureInfo.InvariantCulture) + " verbs: " + string.Join(' ', verbs));
        Assert.Contains("enrol", verbs);
        Assert.Contains("enroll", verbs);

        // How many verbs dispatch to each method; the enrol/enroll pair is one verb spelled two ways.
        static bool Pair(string a, string b) => (a, b) is ("enrol", "enroll") or ("enroll", "enrol");
        var servedBy = method.Where(p => p.Key != "enroll")
            .GroupBy(p => p.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var width = Help.Usage.Split('\n').Max(l => l.Length);
        var withList = new SortedSet<string>(StringComparer.Ordinal);
        var everyFlagNamed = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var verb in verbs)
        {
            var usage = Help.For(verb);
            Assert.True(usage is not null, $"'{verb}' has no help");
            Assert.Equal(usage, Program.HelpRequested([verb, "--help"]));
            Assert.Equal(usage, Program.HelpRequested([verb, "-h"]));

            // (i) its own usage, not the whole help.
            Assert.NotEqual(Help.Usage, usage);

            // (ii) it begins with the verb, as a word.
            Assert.True(
                verb == "enroll"
                    ? usage.StartsWith("curia enrol (or enroll)", StringComparison.Ordinal)
                    : Regex.IsMatch(usage, "^curia " + verb + "(?=[\\s(])"),
                $"'{verb}''s usage does not begin with its own command: {usage}");

            // (iii) no other verb's command.
            foreach (var other in verbs.Where(w => w != verb && !Pair(w, verb)))
            {
                Assert.False(
                    Regex.IsMatch(usage, "curia " + other + "(?=\\s)") || Regex.IsMatch(usage, "(?m)^curia " + other + "(?=[\\s(])"),
                    $"'{verb}''s usage names 'curia {other}': {usage}");
            }

            // Width: no line wider than the help's own.
            foreach (var line in usage.Split('\n'))
                Assert.True(line.Length <= width, $"'{verb}''s usage has a line of {line.Length} > {width}: {line}");

            // (iv) only flags its method accepts.
            var source = MethodSource(program, DeclarationOf(program, method[verb]));
            var lists = Regex.Matches(source, "args\\.Unknown\\(\\[(.*?)\\]\\)", RegexOptions.Singleline);
            Assert.True(lists.Count <= 1, $"{method[verb]} calls args.Unknown {lists.Count} times");
            if (lists.Count == 0) continue;

            withList.Add(method[verb]);
            var accepted = Regex.Matches(lists[0].Groups[1].Value, "\"([a-z][a-z-]*)\"")
                .Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.Ordinal);
            Assert.NotEmpty(accepted);

            foreach (var named in Regex.Matches(usage, "--([a-z][a-z-]*)").Select(m => m.Groups[1].Value).Where(n => n != "help"))
                Assert.True(accepted.Contains(named), $"'{verb}''s usage names --{named}, which {method[verb]} refuses (it accepts {string.Join(", ", accepted)})");

            // (v) where the method serves this verb alone, every flag it accepts.
            if (servedBy[method[verb]] != 1) continue;

            everyFlagNamed.Add(method[verb]);
            foreach (var name in accepted)
                Assert.True(
                    Regex.IsMatch(usage, "--" + name + "(?![a-z-])"),
                    $"'{verb}''s usage omits --{name}, which {method[verb]} accepts: {usage}");
        }

        // Non-vacuity: the methods with a list, and those held to name every flag, by name.
        Assert.True(
            withList.SetEquals(["EnrolAsync", "PostAsync", "SignalAsync", "RecheckAsync", "InboxAsync", "ResolveAsync", "SearchAsync", "FlagAsync", "FlagsAsync"]),
            "methods checked under (iv): " + string.Join(", ", withList));
        Assert.True(
            everyFlagNamed.SetEquals(["EnrolAsync", "RecheckAsync", "InboxAsync", "ResolveAsync", "SearchAsync", "FlagAsync", "FlagsAsync"]),
            "methods checked under (v): " + string.Join(", ", everyFlagNamed));

        Assert.Null(Program.HelpRequested(["ask", "--body", "-h"]));
        Assert.Null(Help.For("no-such-verb"));
    }

    /// <summary>A method's declaration in <paramref name="source"/>, up to its parameter list, found once.</summary>
    private static string DeclarationOf(string source, string method)
    {
        var found = Regex.Matches(source, "static [^\\n(]*\\b" + method + "\\(");
        Assert.True(found.Count == 1, $"{method} is declared {found.Count} times");
        return found[0].Value;
    }

    /// <summary>
    /// D37: the datamark is a private-use character most terminals draw as nothing, so datamarked output
    /// looks unmarked. The help names the character the marker is (read from <c>Datamarking</c>, not
    /// written here) and says that.
    /// </summary>
    [Fact]
    public void D37_TheHelpNamesTheDatamark()
    {
        var token = Datamarking.DefaultControlToken;
        Assert.Equal(1, token.Length);
        var name = "U+" + ((int)token[0]).ToString("X4", CultureInfo.InvariantCulture);

        Assert.Contains(name, Help.Usage, StringComparison.Ordinal);
        Assert.Contains("most terminals draw nothing", Help.Usage, StringComparison.OrdinalIgnoreCase);

        // R10.14 lets a Forum choose another private-use character: U+E000 is the default, and each
        // datamarked passage names the one it carries.
        Assert.Contains("by default", Help.Usage, StringComparison.Ordinal);
        Assert.Contains("token=U+", Help.Usage, StringComparison.Ordinal);
    }

    /// <summary>A repository file's text, found by walking up from the test binary to the solution.</summary>
    private static string SourceText(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curia.sln")))
            dir = dir.Parent;
        Assert.True(dir is not null, "Curia.sln not found above " + AppContext.BaseDirectory);
        return File.ReadAllText(Path.Combine(dir.FullName, relative));
    }

    /// <summary>A method's source: from its signature to the next member declared at the same indent.</summary>
    private static string MethodSource(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "no method declared as " + signature);
        Assert.Equal(-1, source.IndexOf(signature, start + 1, StringComparison.Ordinal));

        var next = Regex.Match(source[(start + signature.Length)..], "\n    (?:private|internal|public) ");
        return next.Success ? source.Substring(start, signature.Length + next.Index) : source[start..];
    }

    private static int Occurrences(string text, string value) =>
        Regex.Count(text, Regex.Escape(value), RegexOptions.IgnoreCase);

    /// <summary>
    /// The EXIT CODES entry for <paramref name="code"/>: its first line, beginning with the number, and
    /// every continuation line indented beneath it, up to the next entry or section.
    /// </summary>
    internal static string ExitCodeEntry(int code)
    {
        var lines = Help.Usage.Split('\n');
        var section = Array.FindIndex(lines, l => string.Equals(l.Trim(), "EXIT CODES", StringComparison.Ordinal));
        Assert.True(section >= 0, "the help text has no EXIT CODES section");

        var number = code.ToString(System.Globalization.CultureInfo.InvariantCulture) + "  ";
        var first = Array.FindIndex(lines, section + 1, l => l.TrimStart().StartsWith(number, StringComparison.Ordinal));
        Assert.True(first > section, $"EXIT CODES has no entry {code}");

        var indent = lines[first].Length - lines[first].TrimStart().Length;
        var entry = new List<string> { lines[first].Trim() };
        for (var i = first + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0 || line.Length - line.TrimStart().Length <= indent) break;
            entry.Add(line.Trim());
        }

        return string.Join(' ', entry);
    }
}
