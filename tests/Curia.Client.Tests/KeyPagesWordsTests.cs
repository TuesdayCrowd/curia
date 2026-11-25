using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Curia.Client.Cli;
using Curia.Tests.Shared;
using Xunit;
using Fault = Curia.Client.Tests.KeyActDouble.LifecycleFault;

namespace Curia.Client.Tests;

/// <summary>
/// WRD1 and WRD2 (errata G77, R11.105, register item 327): the first line of <c>curia keys</c> gives the form of <c>curia verify</c>, and the form is the verb's own. The oracles are the errata's spans (read at test time),
/// <c>Help.For("verify")</c> and the double's request log; the form is then run as printed, with the sighting's flag as the control. Written against the unmodified tree: the recorded red text of each is in the task record.
/// </summary>
[Collection("console")]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Each rig is a using declaration; the analyzer loses the ownership across the awaits.")]
public sealed class KeyPagesBannerTests
{
    private const string PostId = "01HZZZZZZZZZZZZZZZZZZZZZZZ";

    private static string FirstLineOf(string text) => text.Split('\n')[0].TrimEnd('\r');

    /// <summary>WRD1: the first line of <c>curia keys</c> over an unremarkable key set is the errata's, by equality. Red on the tree: <c>... checked by curia verify, anchors a key's binding</c> (no form).</summary>
    [Fact]
    public async Task WRD1_TheFirstLineOfCuriaKeysIsTheErratas()
    {
        using var rig = KeyActRig.Create();

        var (exit, stdout, stderr) = await SigningKeyNotLiveKit.CliAsync(rig, "keys", "--agent", SigningKeyNotLiveKit.Slug);

        SigningKeyNotLiveKit.Red($"exit 0; first line: {KeyPagesWords.FirstLine()}", $"exit {exit}; first line: {FirstLineOf(stdout)}", stdout + "\n" + stderr);
    }

    /// <summary>
    /// WRD2a: the form the banner names is the verb's own: the line <c>curia keys</c> prints holds the first line of <c>Help.For("verify")</c> (two artifacts agreeing; the errata's span is not what is compared with the
    /// help, so a probe that read both from the errata would not be a restatement of itself). Red on the tree: the banner holds <c>curia verify,</c> and no form.
    /// </summary>
    [Fact]
    public async Task WRD2a_TheBannersFormIsTheFormOfCuriaVerifyHelp()
    {
        using var rig = KeyActRig.Create();
        var form = (Help.For("verify") ?? "<no help>").Split('\n')[0].Trim();

        var (_, stdout, _) = await SigningKeyNotLiveKit.CliAsync(rig, "keys", "--agent", SigningKeyNotLiveKit.Slug);
        var line = FirstLineOf(stdout);

        SigningKeyNotLiveKit.Red($"the first line of curia keys holds \"{form}\": yes", $"the first line of curia keys holds \"{form}\": {(line.Contains(form, StringComparison.Ordinal) ? "yes" : "no")}", $"first line: {line}\n{stdout}");
    }

    /// <summary>
    /// WRD2b (Amendment 1 ruling 3): the overview <c>curia help</c> prints gives the same form for <c>verify</c>, on the first line of its entry, as the errata's span and as <c>Help.For("verify")</c>. Red on the tree: the
    /// entry's first line is <c>curia verify &lt;post-id&gt;     Check a post locally: ...</c>.
    /// </summary>
    [Fact]
    public void WRD2b_TheOverviewsVerifyEntryBeginsWithTheForm()
    {
        var published = KeyPagesWords.OverviewEntryForm();
        var helpForm = (Help.For("verify") ?? "<no help>").Split('\n')[0].Trim();
        var entries = Help.Usage.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("curia verify <post-id>", StringComparison.Ordinal)).ToArray();

        Assert.True(entries.Length == 1, $"the overview holds {entries.Length} lines that begin \"curia verify <post-id>\", expected one: {string.Join(" | ", entries)}");
        WholeText.Same($"the errata's form and curia verify --help's agree: {(published == helpForm ? "yes" : "no")}; the overview's entry begins with it alone: {published}", $"the errata's form and curia verify --help's agree: {(published == helpForm ? "yes" : "no")}; the overview's entry begins with it alone: {entries[0]}");
    }

    /// <summary>WRD2c (control, K9): <c>curia verify --help</c>, run, prints the form as its first line, so a reader who follows the banner to the verb's help finds it. Green on the tree.</summary>
    [Fact]
    public async Task WRD2c_CuriaVerifyHelpPrintsTheFormFirst_Control()
    {
        using var rig = KeyActRig.Create();

        var (exit, stdout, stderr) = await SigningKeyNotLiveKit.CliAsync(rig, "verify", "--help");

        SigningKeyNotLiveKit.Red($"exit 0; first line: {KeyPagesWords.OverviewEntryForm()}", $"exit {exit}; first line: {FirstLineOf(stdout)}", stdout + "\n" + stderr);
    }

    /// <summary>
    /// WRD2d: the form, run as printed, is accepted: <c>curia verify &lt;post id&gt;</c> and <c>curia verify --forum &lt;url&gt; &lt;post id&gt;</c> are not refused as unknown flags or as usage (exit not 1) and the double sees the
    /// request for the post. Green on the tree (the form is the verb's); it is the property the banner's new words rely on.
    /// </summary>
    [Fact]
    public async Task WRD2d_TheFormRunsAsPrinted_Control()
    {
        using var rig = KeyActRig.Create();

        var plain = await SigningKeyNotLiveKit.CliAsync(rig, "verify", PostId);
        var plainSeen = rig.Double.Requests.Count(r => string.Equals(r, $"GET /v1/posts/{PostId}", StringComparison.Ordinal));
        var withForum = await SigningKeyNotLiveKit.CliAsync(rig, "verify", "--forum", KeyActDouble.Forum.ToString(), PostId);
        var seen = rig.Double.Requests.Count(r => string.Equals(r, $"GET /v1/posts/{PostId}", StringComparison.Ordinal));

        string Row(int exit, string stderr) => $"exit {(exit == 1 ? "1 (refused as usage)" : "not 1")}; unknown flag: {(stderr.Contains("unknown flag", StringComparison.Ordinal) ? "yes" : "no")}";
        SigningKeyNotLiveKit.Red(
            "curia verify <id>: exit not 1; unknown flag: no; the double saw the request for the post 1 time(s); curia verify --forum <url> <id>: exit not 1; unknown flag: no; the double saw it 2 time(s) in all",
            $"curia verify <id>: {Row(plain.Exit, plain.Stderr)}; the double saw the request for the post {plainSeen} time(s); curia verify --forum <url> <id>: {Row(withForum.Exit, withForum.Stderr)}; the double saw it {seen} time(s) in all",
            plain.Stdout + plain.Stderr + "\n---\n" + withForum.Stdout + withForum.Stderr);
    }

    /// <summary>
    /// WRD2e (control, the sighting reproduced): <c>curia verify --agent a &lt;id&gt;</c>, the line readers guessed, is refused: exit 1, <c>unknown flag "--agent"</c>, and nothing reached the double. The banner's omission of the form is
    /// the measured cause of the guess and not a story. Green on the tree; it carries information only through a verb that takes <c>--agent</c> (which would make the banner's form wrong).
    /// </summary>
    [Fact]
    public async Task WRD2e_VerifyTakesNoAgentFlag_Control()
    {
        using var rig = KeyActRig.Create();

        var (exit, stdout, stderr) = await SigningKeyNotLiveKit.CliAsync(rig, "verify", "--agent", SigningKeyNotLiveKit.Slug, PostId);

        SigningKeyNotLiveKit.Red(
            "exit 1; unknown flag \"--agent\": yes; requests the double saw: 0",
            $"exit {exit}; unknown flag \"--agent\": {(stderr.Contains("unknown flag \"--agent\"", StringComparison.Ordinal) ? "yes" : "no")}; requests the double saw: {rig.Double.Requests.Count}",
            stdout + "\n" + stderr);
    }
}

/// <summary>
/// WRD3 and WRD4 (errata G77, R11.105, R11.99 words (rev.), register item 329 (a) and (c)): the first-failure paragraph carries the exit-8 sentences and names <c>curia keys --agent &lt;slug&gt;</c> (WRD3, red on the tree), and
/// what those sentences claim is true of what a run does (WRD4, the oracle the double's request log and the profile's files, never a verb's words). Written against the unmodified tree.
/// </summary>
[Collection("console")]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Each rig is a using declaration; the analyzer loses the ownership across the awaits.")]
public sealed class KeyPagesExitEightTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Sent = UnheardScene.Sent;

    public static TheoryData<string, string> FailureRows() =>
        new() { { "rotate", "503" }, { "rotate", "unheard" }, { "retire", "503" }, { "retire", "unheard" } };

    private static async Task<UnheardScene> SceneAsync(string act) =>
        act == "rotate" ? UnheardScene.Fresh(Sent) : await UnheardScene.PendingRetirementAsync(Sent, Ct, pending: false);

    private static void Fail(UnheardScene scene, string how)
    {
        if (how == "503") scene.Rig.Double.LifecycleAnswers.Enqueue((HttpStatusCode.ServiceUnavailable, KeyActDouble.Problem("curia/store/unavailable", "store unavailable")));
        else scene.Rig.Double.LifecycleFaults.Enqueue(Fault.AnswerLost);
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(UnheardScene scene, string act)
    {
        scene.Rig.Reload();   // each command is a new process: it loads the profile as the last one left it
        return act == "rotate" ? await scene.RotateAsync(Ct) : await scene.RetireAsync(Ct);
    }

    /// <summary>
    /// WRD3a: after a failure the client did not hear an answer to (a <c>503</c> and an answer lost, a rotation and a retirement) the first-failure paragraph carries both of the errata's exit-8 sentences and names the
    /// command that reads the key set as <c>curia keys --agent a</c>, on stderr. Red on the tree: <c>the exit-8 sentences: not said; ... curia keys --agent a ...: not said</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(FailureRows))]
    public async Task WRD3a_TheParagraphSaysWhatExitEightIsAndNamesCuriaKeysWithItsForm(string act, string how)
    {
        using var scene = await SceneAsync(act);
        Fail(scene, how);

        var (exit, stdout, stderr) = await RunAsync(scene, act);

        var addition = KeyPagesWords.Addition(act == "rotate");
        var keysSentence = $"curia keys --agent {scene.Rig.Slug} reads the same key set and writes nothing.";
        string Said(string text) => PublishedClientWords.Says(stderr, text) ? "said" : "not said";
        SigningKeyNotLiveKit.Red(
            $"exit 8; the exit-8 sentences: said; \"{keysSentence}\": said",
            $"exit {exit}; the exit-8 sentences: {Said(addition)}; \"{keysSentence}\": {Said(keysSentence)}",
            $"--- the sentences sought ---\n{addition}\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}");
    }

    /// <summary>
    /// WRD3b (an errata self-agreement check; no code under test): the two words the sentences name, <c>refused</c> and <c>dropped</c>, are the first words of the <c>Refused inside the window</c> and <c>Dropped</c> spans of
    /// R11.99 words, so a change to the word of one without the other is red (R11.99 words (rev.): "a change to one without the other is a defect"). Green when written; it goes red when either copy is edited in the errata.
    /// </summary>
    [Theory]
    [InlineData(true, "refused", "Refused inside the window, a rotation")]
    [InlineData(false, "refused", "Refused inside the window, a retirement")]
    [InlineData(true, "dropped", "Dropped, a rotation")]
    [InlineData(false, "dropped", "Dropped, a retirement")]
    public void WRD3b_TheWordsTheSentencesNameAreTheFirstWordsOfThePublishedLines(bool rotation, string named, string label)
    {
        var addition = KeyPagesWords.Addition(rotation);
        var word = KeyPagesWords.LineWord(label);

        WholeText.Same(
            $"the span's first word is \"{named}\" and the addition says \"a {word} line\"",
            $"the span's first word is \"{word}\" and the addition {(addition.Contains($"a {word} line", StringComparison.Ordinal) ? "says" : "does not say")} \"a {word} line\"");
    }

    /// <summary>
    /// WRD3c: a slug that is not a shell word as R10.65 writes one (one that begins with a quotation mark; a slug with a space is a shell word, which the command quotes, as `curia rotate`'s does) leaves the keys command unprinted, and the paragraph says where the value is instead. Hand literal for the
    /// words the paragraph says in its place: the errata publishes no words for this form (R11.99 words (rev.): "the paragraph says where the value is"), so the probe holds the property it does publish, that no
    /// <c>curia keys --agent</c> command is printed and a withheld marker is, and that the sentence it ends is still the keys sentence. Red on the tree: <c>a withheld marker: not there</c>.
    /// </summary>
    [Fact]
    public async Task WRD3c_ASlugThatIsNotAShellWordLeavesTheKeysCommandUnprinted()
    {
        var clock = new ManualTimeProvider(Sent);
        using var rig = KeyActRig.Create(slug: "\"ab", clock: clock);
        rig.Double.LifecycleAnswers.Enqueue((HttpStatusCode.ServiceUnavailable, KeyActDouble.Problem("curia/store/unavailable", "store unavailable")));

        var (exit, stdout, stderr) = await KeyActRig.CaptureAsync(() => Program.RotateKeyAsync(rig.Client, rig.Session, rig.Agent, rig.Store, null, Ct));

        var folded = PublishedClientWords.Fold(stderr);
        string Is(bool value) => value ? "yes" : "no";
        SigningKeyNotLiveKit.Red(
            "exit 8; a curia keys --agent command printed: no; a withheld marker (\"not written as a command\"): yes; the sentence ends reads the same key set and writes nothing.: yes",
            $"exit {exit}; a curia keys --agent command printed: {Is(folded.Contains("curia keys --agent", StringComparison.Ordinal))}; a withheld marker (\"not written as a command\"): {Is(folded.Contains("not written as a command", StringComparison.Ordinal))}; "
            + $"the sentence ends reads the same key set and writes nothing.: {Is(folded.Contains("reads the same key set and writes nothing.", StringComparison.Ordinal))}",
            $"--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}");
    }

    private sealed record Run(string Label, int Exit, string Stdout, string Stderr, int LifecyclePosts, int TokenPosts, string Pending, string KeyFiles, string SigningKid);

    private static string KeyFilesOf(UnheardScene scene) =>
        Directory.Exists(Path.Combine(scene.Rig.Directory, "keys"))
            ? string.Join(",", Directory.GetFiles(Path.Combine(scene.Rig.Directory, "keys")).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal))
            : "none";

    private static async Task<Run> Observe(UnheardScene scene, string act, string label)
    {
        scene.Rig.Double.Requests.Clear();
        var (exit, stdout, stderr) = await RunAsync(scene, act);
        return new Run(label, exit, stdout, stderr, scene.Rig.Double.LifecyclePosts, scene.Rig.Double.TokenPosts, scene.Pending(), KeyFilesOf(scene), scene.Rig.Profile().Kid);
    }

    private static bool OnALine(string text, string word) =>
        text.Split('\n').Any(l => l.StartsWith(word, StringComparison.Ordinal));

    /// <summary>
    /// WRD4 (Amendment 1 ruling 1, Task 0's K3 measured): what a run that ends with exit 8 does, by the double's request log and the profile's files, never by a verb's words. The scenes, a rotation and a retirement alike:
    /// (i) the first send answered <c>503</c>; (i') the first send unheard; (ii) a retry inside the window; (iii-a) inside it with the key set unreadable; (iii-b) past it with the key set unreadable; (iv) past it with the
    /// key set readable and showing nothing, the new send unheard; (iv-b) once more, answered <c>503</c>. In every scene: the paragraph's first sentence is on stderr if and only if the run made at least one lifecycle
    /// POST. In (ii) to (iii-b): exit 8, no lifecycle or token POST, the pending act and the key files and the signing key as they were, no <c>dropped</c> line on stdout; (ii) has a <c>refused</c> line on stderr and
    /// (iii-a, iii-b) none. In (iv) and (iv-b): exit 8, exactly one lifecycle POST and one token POST, a <c>dropped</c> line first on stdout, one pending act of the kind sent later than the earlier one (a rotation: a
    /// different kid, the old key's file gone and the new one's present). Green on the tree: it is the truth of the sentences, held against the code's behaviour; it carries information only through FWRD3f.
    /// </summary>
    [Theory]
    [InlineData("rotate")]
    [InlineData("retire")]
    public async Task WRD4_WhatAnExitEightRunDidIsWhatItPrints_Control(string act)
    {
        var rotation = act == "rotate";
        var refused = KeyPagesWords.LineWord(rotation ? "Refused inside the window, a rotation" : "Refused inside the window, a retirement");
        var dropped = KeyPagesWords.LineWord(rotation ? "Dropped, a rotation" : "Dropped, a retirement");
        var template = KeyPagesWords.AfterNotHeard(rotation);
        var stem = template[..template.IndexOf("<sent>", StringComparison.Ordinal)].Replace("<kid>", NotHonouredKit.Kid(NotHonouredKit.OwnKid), StringComparison.Ordinal);   // "... it was sent at ": the paragraph's first sentence up to the instant
        var problems = new List<string>();

        void Check(bool holds, string what) { if (!holds) problems.Add(what); }

        void Biconditional(Run run) =>
            Check(PublishedClientWords.Says(run.Stderr, stem) == (run.LifecyclePosts >= 1), $"[{run.Label}] the first sentence on stderr is {PublishedClientWords.Says(run.Stderr, stem)} and the lifecycle POSTs are {run.LifecyclePosts}");

        void NothingSent(Run run, string before)
        {
            Check(run.Exit == 8, $"[{run.Label}] exit {run.Exit}");
            Check(run.LifecyclePosts == 0 && run.TokenPosts == 0, $"[{run.Label}] lifecycle POSTs {run.LifecyclePosts}, token POSTs {run.TokenPosts}");
            Check(run.Pending == before, $"[{run.Label}] pending {run.Pending}, was {before}");
            Check(!OnALine(run.Stdout, dropped), $"[{run.Label}] a {dropped} line on stdout");
        }

        using var scene = await SceneAsync(act);

        // (i) the first send answered 503
        Fail(scene, "503");
        var first = await Observe(scene, act, "i");
        Check(first.Exit == 8 && first.LifecyclePosts == 1, $"[i] exit {first.Exit}, lifecycle POSTs {first.LifecyclePosts}");
        Biconditional(first);
        var keptPending = first.Pending;
        var keptFiles = first.KeyFiles;
        var signing = first.SigningKid;
        var firstSent = scene.Rig.Profile().Pending.Single().SentAt;
        var firstKid = scene.Rig.Profile().Pending.Single().Kid;

        // (ii) a retry inside the window
        scene.Clock.Set(Sent.AddMinutes(1));
        var inside = await Observe(scene, act, "ii");
        NothingSent(inside, keptPending);
        Check(OnALine(inside.Stderr, refused), $"[ii] no {refused} line on stderr");
        Check(inside.KeyFiles == keptFiles && inside.SigningKid == signing, $"[ii] key files {inside.KeyFiles} (were {keptFiles}), signing kid {inside.SigningKid} (was {signing})");
        Biconditional(inside);

        // (iii-a) inside the window with the key set unreadable
        scene.Rig.Double.Routes["GET /v1/jwks"] = (HttpStatusCode.ServiceUnavailable, KeyActDouble.Problem("curia/server/unavailable", "The Forum is unavailable"));
        var unreadableInside = await Observe(scene, act, "iii-a");
        NothingSent(unreadableInside, keptPending);
        Check(!OnALine(unreadableInside.Stderr, refused), $"[iii-a] a {refused} line on stderr");
        Check(unreadableInside.KeyFiles == keptFiles && unreadableInside.SigningKid == signing, $"[iii-a] key files {unreadableInside.KeyFiles}, signing kid {unreadableInside.SigningKid}");
        Biconditional(unreadableInside);

        // (iii-b) past the window with the key set unreadable
        scene.Clock.Set(Sent.AddMinutes(6));
        var unreadablePast = await Observe(scene, act, "iii-b");
        NothingSent(unreadablePast, keptPending);
        Check(!OnALine(unreadablePast.Stderr, refused), $"[iii-b] a {refused} line on stderr");
        Check(unreadablePast.KeyFiles == keptFiles && unreadablePast.SigningKid == signing, $"[iii-b] key files {unreadablePast.KeyFiles}, signing kid {unreadablePast.SigningKid}");
        Biconditional(unreadablePast);
        scene.Rig.Double.Routes.Remove("GET /v1/jwks");

        // (iv) past the window, the key set readable and showing nothing, the new send unheard
        scene.Rig.Double.LifecycleFaults.Enqueue(Fault.AnswerLost);
        var second = await Observe(scene, act, "iv");
        var secondPending = scene.Rig.Profile().Pending;
        Check(second.Exit == 8 && second.LifecyclePosts == 1 && second.TokenPosts == 1, $"[iv] exit {second.Exit}, lifecycle POSTs {second.LifecyclePosts}, token POSTs {second.TokenPosts}");
        Check(second.Stdout.StartsWith(dropped, StringComparison.Ordinal), $"[iv] stdout does not begin with {dropped}: {second.Stdout.Trim()}");
        Check(secondPending.Length == 1 && secondPending[0].Kind == (rotation ? "rotation" : "retirement") && secondPending[0].SentAt > firstSent, $"[iv] pending {second.Pending}, the earlier act was sent {firstSent:o}");
        Check(secondPending.Length == 1 && PublishedClientWords.Says(second.Stderr, $"sent at {UnheardWords.Instant(secondPending[0].SentAt)}"), $"[iv] stderr does not say the new act's instant");
        if (rotation && secondPending.Length == 1)
        {
            var newKid = secondPending[0].Kid;
            Check(newKid != firstKid, $"[iv] the pending rotation's kid {newKid} is the earlier one");
            Check(!scene.KeyFileHeld(firstKid) && scene.KeyFileHeld(newKid), $"[iv] key files {second.KeyFiles}: the earlier key's file should be gone and the new one's present");
        }

        if (!rotation) Check(second.KeyFiles == keptFiles && second.SigningKid == signing, $"[iv] key files {second.KeyFiles} (were {keptFiles}), signing kid {second.SigningKid} (was {signing})");
        if (rotation) Check(second.SigningKid == signing, $"[iv] signing kid {second.SigningKid} (was {signing})");
        Biconditional(second);

        // (iv-b) once more, past the new window; the send answered 503
        scene.Clock.Set(Sent.AddMinutes(20));
        scene.Rig.Double.LifecycleAnswers.Enqueue((HttpStatusCode.ServiceUnavailable, KeyActDouble.Problem("curia/store/unavailable", "store unavailable")));
        var third = await Observe(scene, act, "iv-b");
        var thirdPending = scene.Rig.Profile().Pending;
        Check(third.Exit == 8 && third.LifecyclePosts == 1 && third.TokenPosts == 1, $"[iv-b] exit {third.Exit}, lifecycle POSTs {third.LifecyclePosts}, token POSTs {third.TokenPosts}");
        Check(third.Stdout.StartsWith(dropped, StringComparison.Ordinal), $"[iv-b] stdout does not begin with {dropped}: {third.Stdout.Trim()}");
        Check(thirdPending.Length == 1 && secondPending.Length == 1 && thirdPending[0].SentAt > secondPending[0].SentAt, $"[iv-b] pending {third.Pending}, the earlier act was {second.Pending}");
        Biconditional(third);

        // (i') the first send unheard
        using var unheard = await SceneAsync(act);
        Fail(unheard, "unheard");
        var lost = await Observe(unheard, act, "i'");
        Check(lost.Exit == 8 && lost.LifecyclePosts == 1 && lost.Pending != "[]", $"[i'] exit {lost.Exit}, lifecycle POSTs {lost.LifecyclePosts}, pending {lost.Pending}");
        Biconditional(lost);

        WholeText.Same("every scene says what it did: no problems", "every scene says what it did: " + (problems.Count == 0 ? "no problems" : string.Join("; ", problems)));
    }
}

/// <summary>
/// WRD5, WRD6 and WRD9 (errata G77, R11.105, R11.99 words (rev.), R11.101 words (rev.), register items 329 (b), 330 (c) and (d), 330 (e)): the helps. Each help is held to the errata's span, read at test time. Written against
/// the unmodified tree.
/// </summary>
[Collection("console")]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class KeyPagesHelpTests
{
    private static readonly int[] OtherRows = [6, 7];

    private static string Said(bool says) => says ? "said" : "not said";

    private static bool Has(string text, string published) => PublishedClientWords.Fold(text).Contains(PublishedClientWords.Fold(published), StringComparison.Ordinal);

    /// <summary>WRD5: the help of <c>curia rotate</c> and of <c>curia retire</c> says what the profile signs with, or lists as live, until the act is settled: the errata's added sentence. Red on the tree: <c>Expected: ... said  Actual: ... not said</c>.</summary>
    [Theory]
    [InlineData("rotate")]
    [InlineData("retire")]
    public void WRD5_TheHelpOfRotateAndRetireSaysWhatSignsMeanwhile(string verb)
    {
        var sentence = KeyPagesWords.HelpAdded(verb == "rotate");
        var help = Help.For(verb) ?? "<no help>";

        WholeText.Same($"curia {verb} --help carries \"{sentence}\": said", $"curia {verb} --help carries \"{sentence}\": {Said(Has(help, sentence))}");
    }

    /// <summary>WRD6a: <c>curia keys --help</c> gives <c>use-key</c>'s form: the errata's revised clause, with <c>curia use-key --agent &lt;slug&gt; --kid &lt;kid&gt;</c>. Red on the tree: the clause names <c>curia use-key</c> and no flags.</summary>
    [Fact]
    public void WRD6a_TheKeysHelpGivesUseKeysForm()
    {
        var clause = SigningKeyNotLiveWords.KeysHelpClause();
        var help = Help.For("keys") ?? "<no help>";

        WholeText.Same($"curia keys --help carries \"{clause}\": said", $"curia keys --help carries \"{clause}\": {Said(Has(help, clause))}");
    }

    /// <summary>WRD6b: <c>curia use-key --help</c> says how an owner learns the signing key is not live: the errata's revised paragraph, which names <c>curia keys --agent &lt;slug&gt;</c>. Red on the tree: the paragraph begins <c>Only when the signing key is not live, and only for ...</c>.</summary>
    [Fact]
    public void WRD6b_TheUseKeyHelpSaysHowToLearnTheSigningKeyIsNotLive()
    {
        var paragraph = SigningKeyNotLiveWords.UseKeyParagraph();
        var help = Help.For("use-key") ?? "<no help>";

        WholeText.Same($"curia use-key --help carries \"{paragraph}\": said", $"curia use-key --help carries \"{paragraph}\": {Said(Has(help, paragraph))}");
    }

    /// <summary>
    /// WRD6c (control): every place <c>curia use-key --help</c> names <c>curia keys</c> it gives the form <c>curia keys --agent &lt;slug&gt;</c> and no other. Vacuously green on the tree, which names <c>curia keys</c> nowhere; it
    /// carries information only through FWRD6 (the paragraph with a bare <c>curia keys</c> or the form <c>--agent &lt;name&gt;</c>).
    /// </summary>
    [Fact]
    public void WRD6c_UseKeyHelpNamesCuriaKeysOnlyWithItsForm_Control()
    {
        var help = PublishedClientWords.Fold(Help.For("use-key") ?? "<no help>");
        var others = Regex.Matches(help, @"curia keys(?! --agent <slug>)", RegexOptions.CultureInvariant).Select(m => help[m.Index..Math.Min(help.Length, m.Index + 40)]).ToArray();

        WholeText.Same("places that name curia keys in another form: none", "places that name curia keys in another form: " + (others.Length == 0 ? "none" : string.Join(" | ", others)));
    }

    /// <summary>WRD9a: row 8 of the exit table carries the errata's added clause, after "(R9.34).". Red on the tree: <c>... said  Actual: ... not said</c>.</summary>
    [Fact]
    public void WRD9a_RowEightOfTheExitTableCarriesTheClause()
    {
        var clause = KeyPagesWords.ExitEightClause();
        var row = HelpTextTests.ExitCodeEntry(8);

        WholeText.Same($"row 8 carries \"{clause}\": said", $"row 8 carries \"{clause}\": {Said(Has(row, clause))}");
    }

    /// <summary>
    /// WRD9b (control): rows 6 and 7 do not carry the clause: a clause added to the wrong row is red. Green on the tree; it carries information only through FWRD9 (the clause moved to row 7). The clause is recognised by its
    /// own opening words, which no other row of the table has (<c>Also from rotate and retire, when a rotation or retirement sent and not heard from</c>).
    /// </summary>
    [Fact]
    public void WRD9b_RowsSixAndSevenDoNotCarryTheClause_Control()
    {
        var clause = KeyPagesWords.ExitEightClause();
        var opening = clause[..clause.IndexOf(':', StringComparison.Ordinal)];
        var found = OtherRows.Where(code => Has(HelpTextTests.ExitCodeEntry(code), opening)).ToArray();

        WholeText.Same($"rows that carry \"{opening}\" besides row 8: none", $"rows that carry \"{opening}\" besides row 8: {(found.Length == 0 ? "none" : string.Join(", ", found))}");
    }

    /// <summary>
    /// WRD11 (item 330 (e), Amendment 1 ruling 2; re-pointed by errata G78, <c>R11.105 (rev.)</c>): <c>curia retire --help</c> says a retirement is permanent for the key itself: the span of <c>R11.106 words</c>, which replaces
    /// the sentence of <c>R11.105 words</c> that says it of the key's kid. Red on the tree until the code's sentence is the revised one: <c>... said  Actual: ... not said</c>.
    /// </summary>
    [Fact]
    public void WRD11_TheRetireHelpSaysARetirementIsPermanentForTheKid()
    {
        var sentence = KeyPagesWords.RetirePermanence();
        var help = Help.For("retire") ?? "<no help>";

        WholeText.Same($"curia retire --help carries \"{sentence}\": said", $"curia retire --help carries \"{sentence}\": {Said(Has(help, sentence))}");
    }
}

/// <summary>
/// WRD7 (errata G77, R11.105, R11.101 words (rev.), register item 330 (d)): <c>use-key</c>'s result says why <c>curia keys</c> still exits 6 while the key set it read lists a skipped row, says nothing of it when the key set
/// lists none and nothing when it does not say, and what it says is true of the next run. Scenes from <see cref="SigningKeyNotLiveKit"/>. Written against the unmodified tree.
/// </summary>
[Collection("console")]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Each rig is a using declaration; the analyzer loses the ownership across the awaits.")]
public sealed class KeyPagesUseKeyTests
{
    private static async Task<(string Stdout, int Exit, string Stderr, HeldKey K0, HeldKey K1)> UseKeyOfK1Async(KeyActRig rig, string? skippedMember)
    {
        var (k0, k1, _) = SigningKeyNotLiveKit.Lockout(rig);
        if (skippedMember == "none") rig.Double.KeySet = KeyActDouble.KeySetJson([k0.Served(1), k1.Served(2)], [0, 1, 2], skipped: []);
        else if (skippedMember == "absent") rig.Double.KeySet = KeyActDouble.KeySetJson([k0.Served(1), k1.Served(2)], [0, 1, 2]);

        rig.Store.WriteToken(rig.Slug, new CachedToken("cached", DateTimeOffset.UtcNow.AddHours(1), null));
        var (exit, stdout, stderr) = await SigningKeyNotLiveKit.CliAsync(rig, "use-key", "--agent", SigningKeyNotLiveKit.Slug, "--kid", k1.Kid);
        return (stdout, exit, stderr, k0, k1);
    }

    /// <summary>
    /// WRD7a: in the lockout scene, whose key set lists the skipped row (log index 3, <c>broken-link</c>), <c>use-key --kid K1</c> exits 0 and its result is the plain result, the others sentence and then the errata's
    /// skipped sentence. Red on the tree: <c>the result with the skipped sentence: not said</c>.
    /// </summary>
    [Fact]
    public async Task WRD7a_TheResultSaysWhyExitSixStaysWhileTheKeySetListsASkippedRow()
    {
        using var rig = KeyActRig.Create();

        var (stdout, exit, stderr, k0, k1) = await UseKeyOfK1Async(rig, skippedMember: null);

        var expected = SigningKeyNotLiveKit.ResultWithOthers(k1.Kid, k0.Kid) + " " + KeyPagesWords.UseKeySkippedSentence();
        SigningKeyNotLiveKit.Red(
            "exit 0; the result with the skipped sentence: said",
            $"exit {exit}; the result with the skipped sentence: {(PublishedClientWords.Says(stdout, expected) ? "said" : "not said")}",
            $"--- sought ---\n{expected}\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}");
    }

    /// <summary>
    /// WRD7b (the sentence's truth, read from the next run and not from the key set): after <c>use-key</c> in the same scene, <c>curia keys</c> exits 6 for the SKIPPED line and for nothing else: exactly one SKIPPED line, at
    /// log index 3, and no MISSING, FOREIGN, NOW IN FORCE or SIGNING KEY NOT LIVE line. Green on the tree (K4 measured it); it is what the sentence says, held against the verb the sentence is about.
    /// </summary>
    [Fact]
    public async Task WRD7b_TheNextCuriaKeysExitsSixForTheSkippedLineAndNothingElse_Control()
    {
        using var rig = KeyActRig.Create();
        await UseKeyOfK1Async(rig, skippedMember: null);

        var (exit, keys, error) = await SigningKeyNotLiveKit.CliAsync(rig, "keys", "--agent", SigningKeyNotLiveKit.Slug);

        var skipped = Regex.Matches(keys, @"SKIPPED:[^\n]*", RegexOptions.CultureInvariant).Select(m => m.Value).ToArray();
        string Has(string text) => keys.Contains(text, StringComparison.Ordinal) ? "present" : "absent";
        SigningKeyNotLiveKit.Red(
            "exit 6; SKIPPED lines 1, at log index 3: yes; MISSING absent; FOREIGN absent; NOW IN FORCE absent; SIGNING KEY NOT LIVE written 0 time(s)",
            $"exit {exit}; SKIPPED lines {skipped.Length}, at log index 3: {(skipped.Length == 1 && skipped[0].Contains("at log index 3", StringComparison.Ordinal) ? "yes" : "no")}; MISSING {Has("MISSING:")}; FOREIGN {Has("FOREIGN:")}; NOW IN FORCE {Has("NOW IN FORCE:")}; SIGNING KEY NOT LIVE written {SigningKeyNotLiveKit.Mentions(keys + error)} time(s)",
            keys + error);
    }

    /// <summary>
    /// WRD7c and WRD7d (controls): the sentence is not a constant. When the key set lists no skipped row (<c>curia_skipped</c> is an empty list) and when it does not say (<c>curia_skipped</c> absent), the result is the plain
    /// result and the others sentence and does not carry the skipped sentence. Green on the tree, where no result carries it; WRD7c carries information only through FWRD7 and WRD7d only through FWRD7c.
    /// </summary>
    [Theory]
    [InlineData("none")]
    [InlineData("absent")]
    public async Task WRD7c_NoSkippedSentenceWhenTheKeySetListsNoneOrDoesNotSay_Control(string skippedMember)
    {
        using var rig = KeyActRig.Create();

        var (stdout, exit, stderr, k0, k1) = await UseKeyOfK1Async(rig, skippedMember);

        var sentence = KeyPagesWords.UseKeySkippedSentence();
        var said = PublishedClientWords.Says(stdout, sentence);
        var plain = PublishedClientWords.Says(stdout, SigningKeyNotLiveKit.ResultWithOthers(k1.Kid, k0.Kid));
        SigningKeyNotLiveKit.Red(
            $"key set {(skippedMember == "none" ? "lists no skipped row" : "does not say which rows it skips")}: exit 0; the result with the others sentence: said; the skipped sentence: not said",
            $"key set {(skippedMember == "none" ? "lists no skipped row" : "does not say which rows it skips")}: exit {exit}; the result with the others sentence: {(plain ? "said" : "not said")}; the skipped sentence: {(said ? "said" : "not said")}",
            stdout + "\n" + stderr);
    }
}
