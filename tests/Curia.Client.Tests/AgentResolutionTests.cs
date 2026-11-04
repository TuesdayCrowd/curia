#pragma warning disable CA1416 // the kept-act directories are mode 0700 (R11.79): the probes set Unix modes, as PrivateFiles does

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using Curia.Canon.Json;
using Curia.Client.Cli;
using Curia.Tests.Shared;
using CsCheck;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// CWK1 to CWK5, CWK7, CWK12, CWK13, CWK52 and CWK53 (errata G59, R11.82): which agent <c>curia</c> acts as, and the refusal that names it. Run at the real command, in process
/// (<see cref="AgentHost"/>), on hosts of one, none and several profiles. Each probe is in its failing form: on the seam the 19 identity verbs still act as the ordinal-first profile.
/// The oracle is the recording Forum's request list and a fingerprint of every profile's directory, never an exit code alone (trap 146); the verb set is read three ways that do not
/// derive from each other (the top-level help, the errata, the code's sites). A host of several profiles holds <c>"B-two"</c> and <c>"a-one"</c>: ordinal order puts <c>B</c> first and
/// culture order puts <c>a</c> first, and each is spelled exactly as it was created (plan ruling 29).
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Each host is a using declaration; the analyzer loses the ownership across the awaits.")]
[Collection("console")]
public sealed partial class AgentResolutionTests
{
    private static readonly string[] TwoProfiles = ["B-two", "a-one"];

    /// <summary>The verbs of the top-level help's sections WRITING, YOURS and YOUR KEYS, and <c>whoami</c> (IDENTITY), parsed from the help text (a different artifact from the resolver).</summary>
    internal static string[] VerbsOfTheHelp()
    {
        var verbs = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        string? section = null;
        foreach (var line in Help.Usage.Split('\n'))
        {
            if (HelpSection().Match(line) is { Success: true } heading) section = heading.Groups[1].Value;
            var m = HelpVerb().Match(line);
            if (!m.Success) continue;
            var verb = m.Groups[1].Value;
            if (section is "WRITING" or "YOURS" or "YOUR KEYS" || (section == "IDENTITY" && verb == "whoami"))
            {
                verbs.Add(verb);
                counts[section!] = counts.GetValueOrDefault(section!) + 1;
            }
        }

        Assert.Equal(8, counts["WRITING"]);
        Assert.Equal(7, counts["YOURS"]);
        Assert.Equal(3, counts["YOUR KEYS"]);
        Assert.Equal(1, counts["IDENTITY"]);
        return [.. verbs];
    }

    [GeneratedRegex(@"^  curia (\w+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex HelpVerb();

    [GeneratedRegex(@"^([A-Z][A-Z ]*[A-Z])(?:\s{2,}|$)", RegexOptions.CultureInvariant)]
    private static partial Regex HelpSection();

    /// <summary>R11.82 words (several profiles) filled for <paramref name="verb"/> and <paramref name="slugs"/> (the first 16 as display literals, then the published tail).</summary>
    internal static string SeveralRefusal(string verb, IReadOnlyList<string> slugs)
    {
        var shown = string.Join(", ", slugs.Take(16).Select(DisplayLiteral.Of));
        var text = PublishedAgentWords.SeveralWords
            .Replace("<n>", slugs.Count.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("<verb>", verb, StringComparison.Ordinal);
        if (slugs.Count > 16)
        {
            var more = PublishedAgentWords.MoreWords.Replace("<m>", (slugs.Count - 16).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
            shown += more;
        }

        return text.Replace("<names>", shown, StringComparison.Ordinal);
    }

    internal static string BlankRefusal(string verb) => PublishedAgentWords.BlankWords.Replace("<verb>", verb, StringComparison.Ordinal);

    private static bool Holds(AgentRun run, string expected) => run.Text.Contains(CliRun.Fold(expected), StringComparison.Ordinal);

    // ----- CWK52 -----

    /// <summary>
    /// CWK52. The verb set, read two ways that do not derive from each other: the verbs of the top-level help's sections (19, asserted after the list) and the names R11.82 (a) lists in the
    /// errata (19); the sets are equal. The third reading, the code's sites, is the Architecture fence's (<c>AgentResolverFenceTests</c>). A control on the seam; red under FCWK42.
    /// </summary>
    [Fact]
    public void R11_82_CWK52_TheVerbsOfTheHelpAreTheVerbsOfTheRequirement()
    {
        var help = VerbsOfTheHelp();
        var errata = PublishedAgentWords.IdentityVerbs;
        Assert.Equal(19, help.Length);
        Assert.Equal(19, errata.Length);

        var problems = new List<string>();
        foreach (var verb in help.Except(errata, StringComparer.Ordinal)) problems.Add($"\"{verb}\" is in the help's sections and not in R11.82 (a)");
        foreach (var verb in errata.Except(help, StringComparer.Ordinal)) problems.Add($"\"{verb}\" is in R11.82 (a) and not in the help's sections");
        Red.None(problems, "the help's 19 identity verbs equal R11.82 (a)'s 19");
    }

    // ----- CWK1 -----

    /// <summary>
    /// CWK1. The refusal, every identity verb. A host of two profiles, no <c>--agent</c>, each of the 19 verbs: exit 1, the published several-profiles words with the verb and both profiles as
    /// display literals, <b>0 requests</b> at the recording Forum, every profile's directory hashing equal before and after; the positive in the same fact: <c>--agent B-two</c> acts as
    /// <c>B-two</c>. Predicted red: <c>expected exit 1, was 0 (acted as "B-two")</c> for <c>whoami</c>; a token request for the writes.
    /// </summary>
    [Fact]
    public async Task R11_82_CWK1_EveryIdentityVerbRefusesWithTwoProfilesAndNoFlag()
    {
        var verbs = VerbsOfTheHelp();
        Assert.Equal(19, verbs.Length);
        using var host = new AgentHost(TwoProfiles);
        var (digest, _) = host.PlantAct("B-two");
        host.PlantAct("a-one");

        var calls = IdentityVerbs.Calls(digest);
        Assert.Equal(19, calls.Count);
        Assert.True(verbs.OrderBy(v => v, StringComparer.Ordinal).SequenceEqual(calls.Select(c => c.Verb).OrderBy(v => v, StringComparer.Ordinal)), "the kit's verbs are the help's verbs");

        var problems = new List<string>();
        foreach (var (verb, args) in calls)
        {
            var before = host.Fingerprint();
            var run = await host.RunAsync([verb, .. args]);
            var found = new List<string>();
            if (run.Exit != 1) found.Add($"expected exit 1, was {run.Exit} (acted as {Join(run.ActedAs)})");
            if (!Holds(run, SeveralRefusal(verb, ["B-two", "a-one"]))) found.Add("not the published several-profiles words");
            if (run.Requests.Count != 0) found.Add($"{run.Requests.Count} request(s): {string.Join(", ", run.Requests.Select(r => r.Method + " " + r.Path))}");
            if (!host.Fingerprint().SequenceEqual(before)) found.Add("a profile's directory changed");
            if (found.Count > 0) problems.Add($"{verb}: {string.Join("; ", found)}");

            var named = await host.RunAsync([verb, .. args, "--agent", "B-two"]);
            if (Holds(named, SeveralRefusal(verb, ["B-two", "a-one"]))) problems.Add($"{verb} --agent B-two: refused though named");
            if (named.ActedAs.Contains("a-one")) problems.Add($"{verb} --agent B-two: acted as \"a-one\"");
        }

        Red.None(problems, "every verb refuses with the published words, 0 requests and every directory unchanged; --agent B-two acts as B-two");
    }

    private static string Join(IReadOnlyList<string> names) => names.Count == 0 ? "no one" : string.Join(", ", names.Select(DisplayLiteral.Of));

    // ----- CWK2, CWK3 -----

    /// <summary>CWK2. The sole-profile control: one profile, no flag, every identity verb acts as it and none refuses. Green on the seam; red under FCWK3.</summary>
    [Fact]
    public async Task R11_82_CWK2_OneProfileNoFlagActsAsIt()
    {
        using var host = new AgentHost("a-one");
        var (digest, _) = host.PlantAct("a-one");
        var problems = new List<string>();
        foreach (var (verb, args) in IdentityVerbs.Calls(digest))
        {
            var run = await host.RunAsync([verb, .. args]);
            if (run.Text.Contains("is required", StringComparison.Ordinal) || run.Text.Contains("--agent is blank", StringComparison.Ordinal)) problems.Add($"{verb}: refused ({run.Text})");
            if (run.ActedAs.Any(a => a != "a-one")) problems.Add($"{verb}: acted as {Join(run.ActedAs)}");
        }

        Red.None(problems, "every verb resolves the one profile and acts as it");
    }

    /// <summary>CWK3. No profile: each site keeps its words and exit (<c>whoami</c> 2, the rest 1). A control on the seam.</summary>
    [Fact]
    public async Task R11_82_CWK3_NoProfileKeepsEachSitesWordsAndExit()
    {
        using var host = new AgentHost();
        var problems = new List<string>();
        foreach (var (verb, args) in IdentityVerbs.Calls(IdentityVerbs.SomeDigest))
        {
            var run = await host.RunAsync([verb, .. args]);
            var (exit, words) = verb == "whoami" ? (2, "no agent enrolled") : (1, "(no agent is enrolled)");
            if (run.Exit != exit) problems.Add($"{verb}: exit {run.Exit}, expected {exit}");
            if (!run.Text.Contains(words, StringComparison.Ordinal)) problems.Add($"{verb}: lacks \"{words}\" ({run.Text})");
            if (run.Requests.Count != 0) problems.Add($"{verb}: {run.Requests.Count} request(s)");
        }

        Red.None(problems, "whoami exits 2 and the others 1, each with the site's own words, nothing sent");
    }

    // ----- CWK4 -----

    private static readonly string[][] BlankSpellings =
    [
        ["--agent", ""],
        ["--agent=", ""],
        ["--agent", " "],
        ["--agent=   ", ""],
    ];

    /// <summary>
    /// CWK4. A present blank, every identity verb, one refusal: each of <c>--agent ""</c>, <c>--agent=</c>, <c>--agent " "</c> and <c>--agent=</c> followed by spaces on each of the 19 verbs, with one
    /// profile and with several: exit 1, the published blank words with the verb named once, 0 requests, no exception text. The assertion is on the words and not on the exit alone
    /// (<c>acts</c> and <c>resend</c> exit 1 today). Predicted red: <c>whoami</c>, <c>comment</c>, <c>budget</c> exit 2 (<c>No enrolled agent by that name</c>); <c>acts</c> and <c>resend</c> exit 1
    /// with the wrong words, <c>resend</c> leaking <c>(Parameter 'slug')</c>.
    /// </summary>
    [Fact]
    public async Task R11_82_CWK4_ABlankAgentIsOneRefusalOnEveryVerb()
    {
        var problems = new List<string>();
        foreach (var names in new[] { new[] { "a-one" }, TwoProfiles })
        {
            using var host = new AgentHost(names);
            var (digest, _) = host.PlantAct(names[0]);
            foreach (var (verb, args) in IdentityVerbs.Calls(digest))
            {
                foreach (var spelling in BlankSpellings)
                {
                    var tail = spelling[1].Length == 0 && spelling[0].EndsWith('=') ? [spelling[0]] : spelling;
                    var run = await host.RunAsync([verb, .. args, .. tail]);
                    var found = new List<string>();
                    if (run.Exit != 1) found.Add($"exit {run.Exit}");
                    if (!Holds(run, BlankRefusal(verb))) found.Add($"not the blank words (\"{Shorten(run.Stderr)}\")");
                    if (run.Requests.Count != 0) found.Add($"{run.Requests.Count} request(s)");
                    if (run.Stderr.Contains("Parameter '", StringComparison.Ordinal) || run.Stderr.Contains("ArgumentException", StringComparison.Ordinal)) found.Add("exception text in stderr");
                    if (found.Count > 0) problems.Add($"{names.Length} profile(s), {verb} {string.Join(' ', tail)}: {string.Join("; ", found)}");
                }
            }
        }

        Red.None(problems, "exit 1, the published blank words with the verb named once, 0 requests, no exception text, for every verb, spelling and host size");
    }

    private static string Shorten(string text) => text.Length <= 120 ? text.Trim() : text[..120].Trim() + "...";

    // ----- CWK5 -----

    /// <summary>The read verbs and the arguments that carry each to its request: they take <c>--agent</c> only to pick a Forum and must work with no flag on a host of any size.</summary>
    private static readonly string[][] ReadCalls =
    [
        ["read", "01READ0000000000000000000A", "--forum", "http://forum.test"],
        ["thread", "01READ0000000000000000000A", "--forum", "http://forum.test"],
        ["board", "dotnet", "--forum", "http://forum.test"],
        ["search", "term", "--forum", "http://forum.test"],
        ["verify", "01READ0000000000000000000A", "--forum", "http://forum.test"],
        ["recheck", "sha256:" + "d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1d1", "--forum", "http://forum.test"],
        ["contract", "--forum", "http://forum.test"],
        ["browse", "read", "01READ0000000000000000000A", "--forum", "http://forum.test"],
    ];

    /// <summary>
    /// CWK5. The read verbs are untouched: with no flag on a host of three profiles each answers as on a host of one (the same exit and the same requests), asks for no token and never
    /// says an agent is required. A control on the seam; red under FCWK5.
    /// </summary>
    [Fact]
    public async Task R11_82_CWK5_TheReadVerbsNeedNoAgentOnAnyHost()
    {
        using var one = new AgentHost("a-one");
        using var three = new AgentHost("B-two", "a-one", "c-three");
        var problems = new List<string>();
        foreach (var call in ReadCalls)
        {
            var alone = await one.RunAsync(call);
            var many = await three.RunAsync(call);
            if (alone.Exit != many.Exit) problems.Add($"{call[0]}: exit {many.Exit} on three profiles, {alone.Exit} on one");
            var requests = (AgentRun r) => string.Join(",", r.Requests.Select(q => q.Method + " " + q.Path));
            if (requests(alone) != requests(many)) problems.Add($"{call[0]}: requests differ ({requests(many)} against {requests(alone)})");
            if (many.Requests.Any(r => r.Path == "/oauth/token")) problems.Add($"{call[0]}: asked for a token");
            if (many.Text.Contains("is required", StringComparison.Ordinal) || many.Text.Contains("--agent is blank", StringComparison.Ordinal)) problems.Add($"{call[0]}: refused for an agent");
            if (alone.Requests.Count == 0 && call[0] != "browse") problems.Add($"{call[0]}: the control made no request, so it proves nothing");
        }

        Red.None(problems, "each read verb answers the same on three profiles as on one, with no token and no refusal");
    }

    // ----- CWK7 -----

    private sealed record ProfileSet(string[] Names, string[] Classes);

    /// <summary>
    /// CWK7. The refusal's words over profile sets: 2 to 40 names (exactly 16 and 17 among them), planted as directories another process could have made (quotes, backslashes, non-ASCII and
    /// control characters in the names, unique by an ASCII index so no two collide on a case-insensitive volume): every name a display literal, at most 16 named and then the published
    /// tail, the verb named once, <c>Nothing was sent</c> once. Each class is asserted reached (<c>NOT REACHED</c>). Predicted red: <c>no refusal</c>.
    /// </summary>
    [Fact]
    public async Task R11_82_CWK7_TheRefusalNamesTheProfilesAsDisplayLiterals()
    {
        var sizes = new[] { 2, 3, 15, 16, 17, 18, 40 };
        var exotic = new[] { "plain", "q\"uote", "back\\slash", "caf" + (char)0xE9, "ctl" + (char)1 + "x", "sp ace", "tab" + (char)9 + "x", "new" + (char)0x2028 + "line" };
        var gen = Gen.Select(Gen.Int[0, sizes.Length - 1], Gen.Int[0, exotic.Length - 1], Gen.Int[0, 1000], (s, e, seed) => (Size: sizes[s], Exotic: e, Seed: seed));

        var reached = new HashSet<string>(StringComparer.Ordinal);
        var problems = new List<string>();
        var set = new List<ProfileSet>();
        gen.Sample(draw =>
        {
            var names = Enumerable.Range(0, draw.Size).Select(i => $"n{i:D2}-" + exotic[(draw.Exotic + i) % exotic.Length]).ToArray();
            lock (set) set.Add(new ProfileSet(names, [])); // threads: 1 below; the lock is belt and braces for a shared collection
            return true;
        }, iter: 40, threads: 1);

        // Every size is walked too, so each class is reached whatever the sampler drew.
        foreach (var size in sizes)
            set.Add(new ProfileSet([.. Enumerable.Range(0, size).Select(i => $"n{i:D2}-" + exotic[i % exotic.Length])], []));

        foreach (var profiles in set.DistinctBy(p => p.Names.Length * 1_000_000 + string.Concat(p.Names).GetHashCode(StringComparison.Ordinal)).Take(60))
        {
            using var host = new AgentHost();
            foreach (var name in profiles.Names) Plant(host, name);
            var verb = "budget";
            var run = await host.RunAsync(verb);
            var slugs = host.Store.Slugs().ToArray();
            var expected = SeveralRefusal(verb, slugs);
            Mark(reached, slugs);
            if (!Holds(run, expected)) problems.Add($"{slugs.Length} profile(s): no refusal ({Shorten(run.Text)})");
            if (slugs.Length > 16 && Holds(run, DisplayLiteral.Of(slugs[16]))) problems.Add($"{slugs.Length} profiles: the 17th name was printed");
            if (Regex.Count(run.Stderr, @"'curia budget'", RegexOptions.CultureInvariant) != 1) problems.Add($"{slugs.Length} profile(s): the verb is not named once");
            if (Regex.Count(run.Stderr, @"Nothing was sent", RegexOptions.CultureInvariant) != 1) problems.Add($"{slugs.Length} profile(s): \"Nothing was sent\" is not said once");
        }

        foreach (var cls in new[] { "exactly 16", "exactly 17", "over 16", "quote", "backslash", "non-ASCII", "control" })
            if (!reached.Contains(cls)) problems.Add($"NOT REACHED: {cls}");

        Red.None(problems, "every name a display literal, at most 16 named then the tail, the verb and 'Nothing was sent' once; every class reached");
    }

    private static void Mark(HashSet<string> reached, string[] slugs)
    {
        if (slugs.Length == 16) reached.Add("exactly 16");
        if (slugs.Length == 17) reached.Add("exactly 17");
        if (slugs.Length > 16) reached.Add("over 16");
        if (slugs.Any(s => s.Contains('"', StringComparison.Ordinal))) reached.Add("quote");
        if (slugs.Any(s => s.Contains('\\', StringComparison.Ordinal))) reached.Add("backslash");
        if (slugs.Any(s => s.Any(c => c > 127))) reached.Add("non-ASCII");
        if (slugs.Any(s => s.Any(char.IsControl))) reached.Add("control");
    }

    /// <summary>A directory another process planted: an identity file in a directory of that name, which is all <c>ProfileStore.Slugs</c> reads.</summary>
    private static void Plant(AgentHost host, string name)
    {
        var directory = host.Store.DirectoryFor(name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "identity.json"), "{}");
    }

    // ----- CWK12 -----

    /// <summary>
    /// CWK12. The usage lines. The four verbs whose usage prints <c>[--agent &lt;name&gt;]</c> (<c>whoami</c>, <c>budget</c>, <c>resend</c>, <c>acts</c>) gain the sentence "required when this host
    /// holds more than one profile"; the lines that already require it are unchanged; the top-level help states the rule once. Predicted red: <c>usage line of "budget" lacks the sentence</c>.
    /// </summary>
    [Fact]
    public async Task R11_82_CWK12_TheOptionalUsageLinesStateWhenTheFlagIsRequired()
    {
        const string sentence = "required when this host holds more than one profile";
        using var host = new AgentHost("a-one");
        var problems = new List<string>();
        foreach (var verb in new[] { "whoami", "budget", "resend", "acts" })
        {
            var help = await host.RunAsync(verb, "--help");
            if (!help.Text.Contains("[--agent <name>]", StringComparison.Ordinal)) problems.Add($"{verb}: the control has no optional usage line ({Shorten(help.Text)})");
            if (!help.Text.Contains(sentence, StringComparison.Ordinal)) problems.Add($"usage line of \"{verb}\" lacks the sentence");
        }

        foreach (var verb in new[] { "ask", "comment", "inbox", "flags", "keys", "rotate", "retire" })
        {
            var help = await host.RunAsync(verb, "--help");
            if (help.Text.Contains("[--agent <name>]", StringComparison.Ordinal)) problems.Add($"{verb}: a required usage line became optional");
        }

        var top = await host.RunAsync("help");
        var states = Regex.Count(top.Stdout, Regex.Escape(sentence), RegexOptions.CultureInvariant);
        if (states != 1) problems.Add($"the top-level help states the rule {states} time(s), expected once");
        Red.None(problems, "the four optional usage lines carry the sentence, the required ones are unchanged, the top-level help states the rule once");
    }

    // ----- CWK13 -----

    /// <summary>
    /// CWK13. The refusal happens first: on a host of two profiles, one registered key held by an external signer that counts its signatures, both kept-act directories made read-only, a
    /// <c>comment</c> with no flag: the refusal is made, the Forum saw nothing, the signer signed nothing, and no profile directory changed. Predicted red: <c>a token request was sent</c>.
    /// </summary>
    [Fact]
    public async Task R11_82_CWK13_TheRefusalIsMadeBeforeAnythingIsDone()
    {
        using var signer = TestSigner.Create("held-1");
        using var host = new AgentHost("a-one");
        host.AddHeld("B-two", signer);
        foreach (var name in host.Names)
        {
            var acts = Path.Combine(host.Store.DirectoryFor(name), "acts");
            Directory.CreateDirectory(acts);
            File.SetUnixFileMode(acts, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        var before = host.Fingerprint();
        var problems = new List<string>();
        try
        {
            var run = await host.RunAsync("comment", "--board", "dotnet", "--parent", "01PARENT00000000000000000A", "--body", "A body.");
            if (!Holds(run, SeveralRefusal("comment", ["B-two", "a-one"]))) problems.Add($"no refusal (exit {run.Exit}, acted as {Join(run.ActedAs)})");
            if (run.Requests.Any(r => r.Path == "/oauth/token")) problems.Add("a token request was sent");
            if (run.Requests.Count != 0) problems.Add($"{run.Requests.Count} request(s) in all");
            if (signer.Signatures != 0) problems.Add($"the signer signed {signer.Signatures} time(s)");
            if (!host.Fingerprint().SequenceEqual(before)) problems.Add("a profile's directory changed");
        }
        finally
        {
            foreach (var name in host.Names)
                File.SetUnixFileMode(Path.Combine(host.Store.DirectoryFor(name), "acts"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Red.None(problems, "the refusal, no token request, no request, no signature, no directory changed");
    }

    // ----- CWK53 -----

    /// <summary>
    /// CWK53. A valueless <c>--agent</c> (ruling 24): as the last token on <c>comment</c>, <c>budget</c> and <c>whoami</c>, with one profile and with several, the flag is present and blank:
    /// exit 1, the blank words, 0 requests, nothing touched; the positive in the same fact: <c>--agent a-one</c> as the last two tokens acts as <c>a-one</c>. Predicted red (by reading;
    /// this run records it): the flag reads as absent, so the verb acts as the first profile.
    /// </summary>
    [Fact]
    public async Task R11_82_CWK53_AValuelessAgentFlagIsABlankAndNotAnAbsence()
    {
        var problems = new List<string>();
        var comment = new[] { "comment", "--board", "dotnet", "--parent", "01PARENT00000000000000000A", "--body", "A body." };
        foreach (var names in new[] { new[] { "a-one" }, TwoProfiles })
        {
            using var host = new AgentHost(names);
            host.PlantAct(names[0]);
            foreach (var call in new[] { comment, ["budget"], ["whoami"] })
            {
                var before = host.Fingerprint();
                var run = await host.RunAsync([.. call, "--agent"]);
                var found = new List<string>();
                if (run.Exit != 1) found.Add($"exit {run.Exit} (acted as {Join(run.ActedAs)})");
                if (!Holds(run, BlankRefusal(call[0]))) found.Add($"not the blank words (\"{Shorten(run.Text)}\")");
                if (run.Requests.Count != 0) found.Add($"{run.Requests.Count} request(s)");
                if (!host.Fingerprint().SequenceEqual(before)) found.Add("a profile's directory changed");
                if (found.Count > 0) problems.Add($"{names.Length} profile(s), {call[0]} --agent: {string.Join("; ", found)}");

                var named = await host.RunAsync([.. call, "--agent", "a-one"]);
                if (Holds(named, BlankRefusal(call[0]))) problems.Add($"{call[0]} --agent a-one: refused as blank though named");
            }
        }

        Red.None(problems, "a trailing --agent is refused as blank with the published words, nothing sent or touched; --agent a-one still acts as a-one");
    }
}
