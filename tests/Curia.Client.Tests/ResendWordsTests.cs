using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Curia.Client.Cli;
using Curia.Domain.Content;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// ASA10, ASA11, ASA20, ASA21, ASA23, ASA24, ASA28, ASA29, ASA41 (the session's half), ASA42 and ASA43 (errata G58, R10.95, R11.79 to R11.81): what a client prints after a fault,
/// composed from the act refused. Every sentence, clause, figure and cause is read from the errata (<see cref="PublishedResendWords"/>, <see cref="PublishedLateCommitWords"/>),
/// folded for whitespace, never copied from the code; the digest an act line carries is the digest the stub recomputes from the bytes it received (<see cref="ReceivedAct"/>).
/// Each probe is in its failing form; a probe green on the seam says so and is a control.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Each kit is a using declaration; the analyzer loses the ownership across the awaits.")]
[Collection("console")]
public sealed partial class ResendWordsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Fold(string text) => CliRun.Fold(text);

    [GeneratedRegex(@"\d+\s+seconds?", RegexOptions.CultureInvariant)]
    private static partial Regex Seconds();

    private static HttpResponseMessage StoreUnavailable() =>
        ScriptedForum.Problem(503, "curia/store/unavailable", "A store fault");

    /// <summary>The command line of each signing verb that needs no read before it signs (a revision and an answer read their parent first: their half is the tool's).</summary>
    private static readonly (string Verb, string[] Args, bool KindWords)[] SigningVerbs =
    [
        ("ask", ["ask", "--board", "b", "--title", "A title?", "--body", "A body."], false),
        ("finding", ["finding", "--board", "b", "--title", "A finding", "--body", "A body."], false),
        ("endorse", ["endorse", "sha256:" + "b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0", "--board", "b"], true),
        ("reproduce", ["reproduce", "sha256:" + "b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0", "--board", "b", "--method", "ran it", "--body", "A body.", "--refs", "https://example.org/e"], false),
        ("contradict", ["contradict", "sha256:" + "b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0", "--board", "b", "--method", "ran it", "--body", "A body.", "--refs", "https://example.org/e"], false),
    ];

    /// <summary>A regular expression for <paramref name="folded"/> text with each placeholder read as the pattern given for it and everything else literal.</summary>
    private static string Pattern(string folded, params (string Placeholder, string Regex)[] placeholders)
    {
        var pattern = Regex.Escape(folded);
        foreach (var (placeholder, regex) in placeholders)
            pattern = pattern.Replace(Regex.Escape(placeholder), regex, StringComparison.Ordinal);

        return pattern;
    }

    private static string ActLineFor(string digest, bool kindWords) =>
        Fold(PublishedResendWords.ActWords.Replace("<digest>", digest, StringComparison.Ordinal) + (kindWords ? PublishedResendWords.KindWords : string.Empty));

    /// <summary>
    /// ASA10. After <c>503 store/unavailable</c> to a signed act the CLI prints the post clause and then the act line exactly as G58 publishes it, with the digest the stub recomputed
    /// from the bytes it received; the kind words follow for an <c>endorse</c> and for none of the others; and no figure of seconds appears anywhere. (<c>revision</c>'s kind words are the
    /// tool's probe: a revision reads its parent first.) Predicted red: <c>expected 'curia resend', found 'the Forum's recheck'</c>.
    /// </summary>
    [Fact]
    public async Task R11_80_ASA10_TheActLineNamesTheResendAndNoWait()
    {
        var problems = new List<string>();
        var clause = Fold(PublishedResendWords.Clauses().Post);

        foreach (var (verb, args, kindWords) in SigningVerbs)
        {
            using var kit = new SessionKit();
            kit.Forum.OnPost = (_, _, _) => Task.FromResult(StoreUnavailable());
            var run = await CliKit.RunAsync(kit, args);
            if (kit.Forum.Posts.Count != 1) { problems.Add($"{verb}: {kit.Forum.Posts.Count} submissions reached the stub ({run.Text})"); continue; }

            var digest = ReceivedAct.DigestOf(System.Text.Encoding.UTF8.GetString(kit.Forum.Posts[0].Body));
            var expected = ActLineFor(digest, kindWords);
            if (!run.Text.Contains(clause, StringComparison.Ordinal)) problems.Add($"{verb}: lacks the post clause");
            if (!run.Text.Contains(expected, StringComparison.Ordinal)) problems.Add($"{verb}: expected 'curia resend', found {(run.Text.Contains("recheck", StringComparison.Ordinal) ? "the Forum's recheck" : "no act line")}");
            if (!kindWords && run.Text.Contains(Fold(PublishedResendWords.KindWords), StringComparison.Ordinal)) problems.Add($"{verb}: carries the revision and endorse kind words");
            if (Seconds().IsMatch(run.Text)) problems.Add($"{verb}: a figure of seconds appears ({Seconds().Match(run.Text).Value})");
        }

        Red.None(problems, "the post clause and the published act line with the digest of the bytes sent, kind words for endorse alone, no wait in seconds");
    }

    /// <summary>
    /// ASA11. Unheard: the title of R11.77, then the unheard sentence of G58, then the same act line, in that order, with no figure of seconds. A request the stub reads and never
    /// answers, with the client's timeout at 300 ms. Predicted red: the same as ASA10.
    /// </summary>
    [Fact]
    public async Task R11_80_ASA11_AnUnheardActIsFollowedByTheSameActLine()
    {
        using var kit = new SessionKit();
        kit.Forum.OnPost = (_, _, _) => throw new HttpRequestException(HttpRequestError.Unknown, "reset by the stub");
        var run = await CliKit.RunAsync(kit, "ask", "--board", "b", "--title", "A title?", "--body", "A body.");

        var problems = new List<string>();
        if (kit.Forum.Posts.Count != 1) problems.Add($"{kit.Forum.Posts.Count} submissions reached the stub");
        else
        {
            var digest = ReceivedAct.DigestOf(System.Text.Encoding.UTF8.GetString(kit.Forum.Posts[0].Body));
            var title = Fold(PublishedConfirmWords.UnheardRefusal().Title);
            var sentence = Fold(PublishedResendWords.UnheardWords);
            var act = ActLineFor(digest, false);
            var at = (run.Text.IndexOf(title, StringComparison.Ordinal), run.Text.IndexOf(sentence, StringComparison.Ordinal), run.Text.IndexOf(act, StringComparison.Ordinal));
            if (at.Item1 < 0) problems.Add("no unheard title");
            if (at.Item2 < 0) problems.Add("expected 'curia resend', found the old unheard sentence");
            if (at.Item3 < 0) problems.Add("expected 'curia resend', found no published act line");
            if (at.Item1 >= 0 && at.Item2 >= 0 && at.Item3 >= 0 && !(at.Item1 < at.Item2 && at.Item2 < at.Item3)) problems.Add($"out of order (at {at.Item1}, {at.Item2}, {at.Item3})");
            if (Seconds().IsMatch(run.Text)) problems.Add($"a figure of seconds appears ({Seconds().Match(run.Text).Value})");
        }

        Red.None(problems, "the unheard title, the published unheard sentence, the published act line with the digest, in that order, and no seconds");
    }

    /// <summary>One refusal for each act the session knows, as the stub gives it.</summary>
    private static async Task<Refusal> RefusedAsync(SessionKit kit, ActContext act, string type = "curia/store/unavailable")
    {
        kit.Forum.OnPost = (_, _, _) => Task.FromResult(ScriptedForum.Problem(503, type, "A store fault"));
        kit.Forum.OnOther = (_, _) => Task.FromResult(ScriptedForum.Problem(503, type, "A store fault"));
        var refusal = act switch
        {
            ActContext.Post => (await kit.Session.SubmitAsync(kit.Build(), MarkingMode.None, Ct)).Refusal,
            ActContext.Resolve => (await kit.Session.AcceptAsync("01ANSWER0000000000000000AA", Ct)).Refusal,
            ActContext.Flag => (await kit.Session.FlagAsync("01POST000000000000000000AA", "spam", "this reads as spam", Ct)).Refusal,
            ActContext.Other => (await kit.Session.BudgetAsync(Ct)).Refusal,
            _ => throw new ArgumentOutOfRangeException(nameof(act), act, null),
        };
        return refusal ?? throw new InvalidOperationException("the stub's refusal was read as a success");
    }

    /// <summary>
    /// ASA20. The clause by act: a resolve's <c>503</c> and a flag's hold no digest, no <c>act sent</c> and no <c>recheck</c> and each its own reading pair; a post's holds the post
    /// clause; another request's holds the generic clause; each chosen by <c>Refusal.Act</c> alone. Predicted red: <c>resolve text names the digest of the act sent</c>.
    /// </summary>
    [Fact]
    public async Task R11_80_ASA20_TheStoreSentenceIsTheClauseOfTheActRefused()
    {
        var (post, resolve, flag, other) = PublishedResendWords.Clauses();
        var problems = new List<string>();
        foreach (var (act, clause) in new[] { (ActContext.Post, post), (ActContext.Resolve, resolve), (ActContext.Flag, flag), (ActContext.Other, other) })
        {
            using var kit = new SessionKit();
            var refusal = await RefusedAsync(kit, act);
            var text = Fold(refusal.Summary);
            if (refusal.Act != act) problems.Add($"{act}: Refusal.Act is {refusal.Act}");
            if (!text.Contains(Fold(clause), StringComparison.Ordinal)) problems.Add($"{act}: the summary lacks the published {act} clause");
            if (act is ActContext.Resolve or ActContext.Flag)
                foreach (var forbidden in new[] { "digest", "act sent", "recheck" })
                    if (text.Contains(forbidden, StringComparison.Ordinal)) problems.Add($"{act} text names the {forbidden}");
        }

        Red.None(problems, "each act's published clause; a resolve's and a flag's name no digest, no act sent and no recheck");
    }

    /// <summary>
    /// ASA21. The session sets the act: every public method of <c>ForumSession</c> that returns a Forum result is driven to a refusal and the refusal's <c>Act</c> is the one the
    /// call is (submit and resend: a post; accept: a resolve; flag: a flag; every other: another). The list is derived by reflection and its count asserted after (trap 81). A
    /// control on the seam, which sets the act and reads it nowhere; red under FASA23.
    /// </summary>
    [Fact]
    public async Task R11_80_ASA21_EveryWriteTheSessionMakesSetsTheActItRefused()
    {
        var methods = typeof(ForumSession).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.ReturnType.IsGenericType && m.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)
                && m.ReturnType.GetGenericArguments()[0].IsGenericType && m.ReturnType.GetGenericArguments()[0].GetGenericTypeDefinition() == typeof(ForumResult<>))
            .Select(m => m.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        var expected = new Dictionary<string, ActContext>(StringComparer.Ordinal)
        {
            ["SubmitAsync"] = ActContext.Post, ["ResendAsync"] = ActContext.Post, ["FlagAsync"] = ActContext.Flag, ["AcceptAsync"] = ActContext.Resolve,
            ["AccessTokenAsync"] = ActContext.Other, ["RotateKeyAsync"] = ActContext.Other, ["RetireKeyAsync"] = ActContext.Other, ["InboxAsync"] = ActContext.Other, ["FlagsAsync"] = ActContext.Other, ["BudgetAsync"] = ActContext.Other,
        };

        var problems = new List<string>();
        if (!methods.SequenceEqual(expected.Keys.Order(StringComparer.Ordinal)))
            problems.Add($"the session's Forum-result methods are [{string.Join(", ", methods)}], this probe covers [{string.Join(", ", expected.Keys.Order(StringComparer.Ordinal))}]");

        foreach (var (name, act) in expected)
        {
            using var kit = new SessionKit();
            kit.Forum.OnPost = (_, _, _) => Task.FromResult(ScriptedForum.Problem(503, "curia/store/unavailable"));
            kit.Forum.OnOther = (_, _) => Task.FromResult(ScriptedForum.Problem(503, "curia/store/unavailable"));
            kit.Forum.OnToken = () => ScriptedForum.Problem(503, "curia/store/unavailable");
            var submission = kit.Build();
            var refusal = name switch
            {
                "SubmitAsync" => (await kit.Session.SubmitAsync(submission, MarkingMode.None, Ct)).Refusal,
                "ResendAsync" => (await kit.Session.ResendAsync(new KeptAct(new KeptActHeader(submission.PrefixedDigest, "question", "b", "http://forum.test", "a", DateTimeOffset.UtcNow, 0, null, null), submission.Wire), MarkingMode.None, Ct)).Refusal,
                "FlagAsync" => (await kit.Session.FlagAsync("01POST000000000000000000AA", "spam", "this reads as spam", Ct)).Refusal,
                "AcceptAsync" => (await kit.Session.AcceptAsync("01ANSWER0000000000000000AA", Ct)).Refusal,
                "AccessTokenAsync" => (await kit.Session.AccessTokenAsync(Ct)).Refusal,
                "RotateKeyAsync" => (await kit.Session.RotateKeyAsync("urn:curia:agent:alice", new RotationRequest("a statement", "an authorization", "a possession"), Ct)).Refusal,
                "RetireKeyAsync" => (await kit.Session.RetireKeyAsync("urn:curia:agent:alice", "alice-1", new RetirementRequest("a statement", "an authorization"), Ct)).Refusal,
                "InboxAsync" => (await kit.Session.InboxAsync(new InboxRequest(), MarkingMode.None, Ct)).Refusal,
                "FlagsAsync" => (await kit.Session.FlagsAsync(null, Ct)).Refusal,
                "BudgetAsync" => (await kit.Session.BudgetAsync(Ct)).Refusal,
                _ => null,
            };
            if (refusal is null) problems.Add($"{name}: was not refused");
            else if (refusal.Act != act) problems.Add($"{name}: Act is {refusal.Act} for {name.Replace("Async", string.Empty, StringComparison.Ordinal).ToUpperInvariant()}, expected {act}");
        }

        Red.None(problems, "every method sets the act it is");
    }

    /// <summary>
    /// ASA23. Exit 5: the <c>unknown</c> line carries <c>curia resend</c> and both readings (the published line, with the reader's own instant and the digest); a batch exiting 5 prints
    /// the closing line on standard error, naming <c>withheld</c> and <c>unknown</c>; stdout's other lines are unchanged; a batch exiting 0 prints no closing line (the control).
    /// Predicted red: <c>no closing line</c>.
    /// </summary>
    [Fact]
    public async Task R10_95_ASA23_ExitFiveIsExplainedWhereItIsPrinted()
    {
        using var log = new StubLog();
        var unknown = "sha256:" + new string('d', 64);
        var problems = new List<string>();

        log.BatchAnswer = _ => [StubLog.BatchItem(unknown, "unknown")];
        var served = await log.Client().BatchAsync([unknown], MarkingMode.None, Ct);
        Assert.True(served.TryGetValue(out var items, out var refusal), refusal?.Summary);
        var run = await ConsoleCapture.CaptureAsync(() => Task.FromResult(Program.ReportRecheck([unknown], items)));

        var line = Fold(run.Stdout);
        var expectedLine = Pattern(Fold(PublishedResendWords.UnknownLine), ("<instant>", @"\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ"), ("<digest>", @"(?:<digest>|""?sha256:[0-9a-f]{64}""?)"));
        if (run.Exit != 5) problems.Add($"exit {run.Exit}, expected 5");
        if (!Regex.IsMatch(line, expectedLine, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2))) problems.Add("the unknown line is not the published line (curia resend, both readings)");
        if (!Fold(run.Stderr).Contains(Fold(PublishedResendWords.ClosingLine), StringComparison.Ordinal)) problems.Add("no closing line");

        var current = StubLog.BatchItem(StubLog.PostId, "current");
        log.BatchAnswer = _ => [current];
        var ok = await log.Client().BatchAsync([StubLog.PostId], MarkingMode.None, Ct);
        if (ok.TryGetValue(out var okItems, out _))
        {
            var okRun = await ConsoleCapture.CaptureAsync(() => Task.FromResult(Program.ReportRecheck([StubLog.PostId], okItems)));
            if (okRun.Stderr.Contains("exit 5", StringComparison.Ordinal)) problems.Add("a batch exiting other than 5 printed the closing line");
        }

        Red.None(problems, "exit 5, the published unknown line, the published closing line on stderr; no closing line otherwise");
    }

    /// <summary>
    /// ASA24. The L8 leads, by their published words: <c>already-revised</c> ends <c>Nothing was stored or spent.</c> (capitalised); <c>NOT SENT ... superseded</c> says the successor
    /// may be the text just sent and points at <c>curia read</c>; <c>curia/flag/contended</c> says nothing was recorded and the flag may be sent again; item 94's refusal (a
    /// rationale too long) says nothing was recorded. Predicted red: <c>nothing was stored</c> in lower case.
    /// </summary>
    [Fact]
    public async Task R11_81_ASA24_TheLeadsOfTheLastExerciseSayWhatIsTrueOfThem()
    {
        var problems = new List<string>();
        var (last, superseded) = PublishedResendWords.RevisionWords;

        var revised = new Refusal(RefusalKind.Conflict, 409, new Curia.Domain.Primitives.Error("curia/ingest/already-revised", "x", "successor=01REVISIONID00000000000001"));
        if (!Fold(revised.Summary).EndsWith(last, StringComparison.Ordinal)) problems.Add($"already-revised does not end \"{last}\" (it ends \"{Fold(revised.Summary)[^40..]}\")");

        using var log = new StubLog();
        log.SupersededBy = [new Successor("sha256:" + new string('7', 64), "01REVISIONID00000000000001")];
        var read = await log.Client().GetPostAsync(StubLog.PostId, MarkingMode.None, Ct);
        Assert.True(read.TryGetValue(out var post, out var readRefusal), readRefusal?.Summary);
        var (refusalText, _) = Program.ReviseRefusal(post! with { Provenance = post!.Provenance with { Author = "https://x/b" } }, "https://x/b");
        var supersededPattern = Pattern(Fold(superseded), ("<post id>", ".+?"));
        if (refusalText is null || !Regex.IsMatch(Fold(refusalText), supersededPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2))) problems.Add("NOT SENT ... superseded lacks the published clause (the successor may be the text just sent; curia read)");

        var (cType, cStatus, cTitle) = PublishedLateCommitWords.Contended();
        var contended = new Refusal(RefusalKind.ServerFault, cStatus, new Curia.Domain.Primitives.Error(cType, cTitle));
        var expectedContended = Fold(PublishedResendWords.ContendedWords.Replace("<title>", cTitle, StringComparison.Ordinal));
        if (!Fold(contended.Summary).Contains(expectedContended, StringComparison.Ordinal)) problems.Add($"curia/flag/contended is not worded \"{expectedContended}\" (it reads \"{Fold(contended.Summary)}\")");

        var tooLong = new Refusal(RefusalKind.Content, 400, new Curia.Domain.Primitives.Error("curia/flag/rationale-too-long", "The rationale is too long", "4097 bytes"));
        if (!Fold(tooLong.Summary).Contains(PublishedResendWords.NothingRecordedClause, StringComparison.Ordinal)) problems.Add("item 94's refusal does not say nothing was recorded by this call");
        Red.None(problems, "the capitalised last sentence, the superseded clause, the contended words and the clause on a rationale too long");
    }

    /// <summary>
    /// ASA28. A control (green on the seam, because G58 is applied): every span the probes read is parsed from the errata with its count asserted, the figures are the published
    /// ones (30 seconds, 64 acts, 5 minutes) and the cause list of R11.81 (c) reads as ruling 23 states it. Red only under a removed or duplicated span (FASA32).
    /// </summary>
    [Fact]
    public void R11_80_ASA28_EverySpanTheProbesReadIsInG58WithItsCountAsserted()
    {
        var (post, resolve, flag, other) = PublishedResendWords.Clauses();
        string[] spans =
        [
            post, resolve, flag, other, PublishedResendWords.ActWords, PublishedResendWords.KindWords, PublishedResendWords.UnheardWords, PublishedResendWords.StopWords,
            PublishedResendWords.ResendRefusalWords, PublishedResendWords.AlreadyRecordedWords.FirstLine, PublishedResendWords.AlreadyRecordedWords.After, PublishedResendWords.NothingRecordedClause,
            PublishedResendWords.RevisionWords.Last, PublishedResendWords.RevisionWords.Superseded, PublishedResendWords.ContendedWords, PublishedResendWords.UnknownLine, PublishedResendWords.ClosingLine,
        ];
        Assert.All(spans, span => Assert.True(span.Trim().Length > 8, $"a span was parsed as a bare token: \"{span}\""));
        Assert.Equal(30, PublishedResendWords.WriteBoundSeconds);
        Assert.Equal(64, PublishedResendWords.KeptActBound);
        Assert.Equal((5, "minutes"), PublishedResendWords.StopWait);

        var (says, notTwice, never, resend) = PublishedResendWords.ClauseCauses();
        Assert.Contains("agent-in-flight", says);
        Assert.Contains("revision-in-flight", says);
        Assert.Equal(["already-revised", "curia/flag/contended", "NOT SENT"], notTwice);
        Assert.Equal(["already-raised", "already-recorded"], never);
        Assert.True(resend);

        var missing = Assert.Throws<InvalidOperationException>(() => PublishedResendWords.Paragraph("R99.99 words"));
        Assert.Contains("R99.99 words", missing.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// ASA29. No sentence names what is not printed: over every act (post, resolve, flag, other) and every fault type a client can print (the three store types and the token's code, and
    /// the unheard refusal), a text that says <c>printed with this refusal</c> is followed by a digest on the same output, and a digest named is one printed. The generator is the
    /// closed product and asserts every act and type was reached. Predicted red: <c>resolve: digest not printed</c>.
    /// </summary>
    [Fact]
    public void R11_80_ASA29_NoSentenceNamesADigestThatIsNotPrinted()
    {
        var digest = "sha256:" + new string('a', 64);
        var acts = new[] { ActContext.Post, ActContext.Resolve, ActContext.Flag, ActContext.Other };
        var types = new[] { StoreWording.StoreUnavailableType, StoreWording.LogUnreadableType, StoreWording.TemporarilyUnavailableCode, ClientErrors.UnheardType };
        var reached = new HashSet<(ActContext, string)>();
        var problems = new List<string>();

        foreach (var act in acts)
        {
            foreach (var type in types)
            {
                var refusal = type == ClientErrors.UnheardType
                    ? new Refusal(RefusalKind.Transport, 0, ClientErrors.Unheard("http://forum.test: reset"), Unheard: true) { Act = act }
                    : new Refusal(RefusalKind.ServerFault, 503, new Curia.Domain.Primitives.Error(type, "A store fault")) { Act = act };
                var printed = Fold(refusal.Summary + " " + string.Join(" ", act == ActContext.Post ? StoreWording.AfterRefusal(refusal, digest) : []));
                reached.Add((act, type));

                var namesDigest = Regex.IsMatch(printed, @"\bthe digest\b|printed with this refusal", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
                var printsDigest = printed.Contains("sha256:", StringComparison.Ordinal);
                if (namesDigest && !printsDigest) problems.Add($"{act}: digest not printed ({type})");
            }
        }

        if (reached.Count != acts.Length * types.Length) problems.Add("NOT REACHED: " + (acts.Length * types.Length - reached.Count) + " act and type pairs");
        Red.None(problems, "a digest named only where one is printed, for every act and fault type");
    }

    /// <summary>
    /// ASA41, the session's half (plan ruling 24, R11.79). With a write bound injected small, a signed post sent through a Forum that never answers ends with the unheard
    /// refusal, the file kept and one request, within three seconds; the control: a read through the same never-answering Forum is not ended by the bound; and the caller's own
    /// cancellation mid-write is kept and rethrown and is not worded as the bound. Predicted red: the call does not return (here: <c>did not return within 3 s</c>).
    /// </summary>
    [Fact]
    public async Task R11_79_ASA41_AWriteThatIsNotAnsweredEndsAtTheBoundAndAReadDoesNot()
    {
        var problems = new List<string>();
        var bound = TimeSpan.FromMilliseconds(300);

        using (var kit = new SessionKit(writeBound: bound))
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            kit.Forum.OnPost = async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return StoreUnavailable(); };
            var act = kit.Build();
            var send = kit.Session.SubmitAsync(act, MarkingMode.None, stop.Token);
            var finished = await Task.WhenAny(send, Task.Delay(TimeSpan.FromSeconds(3), Ct)) == send;
            if (!finished)
            {
                problems.Add("the write did not return within 3 s");
                await stop.CancelAsync();
                try { await send; } catch (OperationCanceledException) { /* the probe's own clean-up */ }
            }
            else
            {
                var refusal = (await send).Refusal;
                if (refusal?.Unheard != true) problems.Add($"the write ended as {refusal?.Error.Type ?? "a success"}, expected the unheard refusal");
                if (kit.Forum.Posts.Count != 1) problems.Add($"{kit.Forum.Posts.Count} requests, expected 1");
                if (kit.KeptFiles().Length != 1) problems.Add("the act was not kept");
            }
        }

        using (var kit = new SessionKit(writeBound: bound))
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            kit.Forum.OnOther = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return StoreUnavailable(); };
            var read = kit.Session.BudgetAsync(stop.Token);
            var ended = await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(1), Ct)) == read;
            if (ended) problems.Add("a read was ended by the write bound");
            await stop.CancelAsync();
            try { await read; } catch (OperationCanceledException) { /* clean-up */ }
        }

        using (var kit = new SessionKit(writeBound: TimeSpan.FromSeconds(30)))
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            kit.Forum.OnPost = async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return StoreUnavailable(); };
            var act = kit.Build();
            cancel.CancelAfter(TimeSpan.FromMilliseconds(300));
            var threw = false;
            try { await kit.Session.SubmitAsync(act, MarkingMode.None, cancel.Token); }
            catch (OperationCanceledException) { threw = true; }
            if (!threw) problems.Add("the caller's own cancellation was not rethrown");
            if (kit.KeptFiles().Length != 1) problems.Add("the cancelled write's act was not kept");
        }

        Red.None(problems, "an unheard refusal at the bound with the act kept; a read not ended; the caller's cancellation rethrown and kept");
    }

    /// <summary>
    /// ASA42. The figure comes from the errata: the bound is read from R11.79's text, the client's one constant equals it, and the CLI's own <c>HttpClient</c> timeout is that constant.
    /// Green on the seam (the constant exists and the CLI uses it); <c>curia-mcp</c>'s leg (its composition's bound is the published one) is the tool suite's and is red on the seam.
    /// </summary>
    [Fact]
    public void R11_79_ASA42_TheWriteBoundIsOneFigureDerivedFromTheErrata()
    {
        var published = PublishedResendWords.WriteBoundSeconds;
        Assert.Equal(ForumSession.WriteBoundSeconds, published);
        Assert.Equal(TimeSpan.FromSeconds(published), Program.HttpClientFactory(new Uri("http://forum.test")).Timeout);
    }

    /// <summary>One refusal of a route: its class and how ruling 23 words it.</summary>
    private sealed record Case(string Name, ActContext Act, Refusal Refusal, int Says);

    private static Case Of(string name, ActContext act, RefusalKind kind, int status, string type, string? detail, int says) =>
        new(name, act, new Refusal(kind, status, new Curia.Domain.Primitives.Error(type, "A refusal", detail)) { Act = act }, says);

    /// <summary>
    /// ASA43. The clause by cause (item 94's successor, plan ruling 23): over every refusal type of the flag, ask, answer and resolve routes, <c>Nothing was recorded by this call.</c>
    /// appears exactly once where R11.81 (c) says (a content, not-found, authorization or budget refusal; <c>agent-in-flight</c> and <c>revision-in-flight</c>) and zero times for
    /// <c>already-raised</c>, <c>already-recorded</c>, <c>already-revised</c>, <c>curia/flag/contended</c>, a <c>NOT SENT</c> and a Forum fault. The classes are those the errata's list
    /// names, and the generator asserts each was reached (<c>NOT REACHED</c> otherwise). The refusal of a resend (which says (a) in its place) is <c>ResendVerbTests</c>'s.
    /// Predicted red: <c>expected 1 occurrence, found 0</c> for a flag's <c>rationale-too-long</c>.
    /// </summary>
    [Fact]
    public void R11_81_ASA43_TheClauseIsSaidOnceWhereTheErrataSaysAndNowhereElse()
    {
        var clause = PublishedResendWords.NothingRecordedClause;
        var (_, notTwice, never, _) = PublishedResendWords.ClauseCauses();
        const string budget = "table-11/rate-budget-exhausted next_slot_at=2026-01-01T00:00:00Z";
        var cases = new[]
        {
            Of("flag: rationale too long", ActContext.Flag, RefusalKind.Content, 400, "curia/flag/rationale-too-long", "4097 bytes", 1),
            Of("flag: detail unstorable", ActContext.Flag, RefusalKind.Content, 400, "curia/flag/detail-unstorable", "a character", 1),
            Of("flag: no such post", ActContext.Flag, RefusalKind.NotFound, 404, "curia/flag/no-such-post", "x", 1),
            Of("flag: tier denial", ActContext.Flag, RefusalKind.Authorization, 403, "curia/authz/denied", "table-10/denied", 1),
            Of("flag: budget", ActContext.Flag, RefusalKind.RateBudget, 403, "curia/authz/denied", "table-11/flag-budget-exhausted next_slot_at=2026-01-01T00:00:00Z", 1),
            Of("flag: already raised", ActContext.Flag, RefusalKind.Conflict, 409, "curia/flag/already-raised", "kind=spam raised_at=2026-01-01T00:00:00Z", 0),
            Of("flag: raise in flight", ActContext.Flag, RefusalKind.Conflict, 409, "curia/flag/raise-in-flight", null, 0),
            Of("flag: contended", ActContext.Flag, RefusalKind.ServerFault, 503, "curia/flag/contended", null, 0),
            Of("ask: content", ActContext.Post, RefusalKind.Content, 400, "curia/admit/invalid-envelope", "x", 1),
            Of("ask: 422", ActContext.Post, RefusalKind.Content, 422, "curia/canon/not-canonicalizable", "x", 1),
            Of("answer: no such parent", ActContext.Post, RefusalKind.NotFound, 404, "curia/posts/not-found", "x", 1),
            Of("answer: tier denial", ActContext.Post, RefusalKind.Authorization, 403, "curia/authz/denied", "table-10/denied", 1),
            Of("ask: posting budget", ActContext.Post, RefusalKind.RateBudget, 403, "curia/authz/denied", budget, 1),
            Of("ask: agent in flight", ActContext.Post, RefusalKind.Conflict, 409, "curia/budget/agent-in-flight", "x", 1),
            Of("revision: revision in flight", ActContext.Post, RefusalKind.Conflict, 409, "curia/ingest/revision-in-flight", "x", 1),
            Of("revision: already revised", ActContext.Post, RefusalKind.Conflict, 409, "curia/ingest/already-revised", "successor=01REVISIONID00000000000001", 0),
            Of("ask: already recorded", ActContext.Post, RefusalKind.Conflict, 409, "curia/ingest/already-recorded", "post=01TESTRECEIPTPOST000000000A", 0),
            Of("ask: a Forum fault", ActContext.Post, RefusalKind.ServerFault, 503, "curia/store/unavailable", null, 0),
            Of("ask: a server fault", ActContext.Post, RefusalKind.ServerFault, 500, "curia/server/fault", null, 0),
            Of("resolve: not an answer", ActContext.Resolve, RefusalKind.Content, 400, "curia/accept/root-not-a-question", "x", 1),
            Of("resolve: not found", ActContext.Resolve, RefusalKind.NotFound, 404, "curia/posts/not-found", "x", 1),
            Of("resolve: own thread only", ActContext.Resolve, RefusalKind.Authorization, 403, "curia/authz/denied", "table-10/own-thread-only", 1),
            Of("resolve: acceptance budget", ActContext.Resolve, RefusalKind.RateBudget, 403, "curia/authz/denied", "table-11/accept-budget-exhausted next_slot_at=2026-01-01T00:00:00Z", 1),
            new("ask: a NOT SENT", ActContext.Post, Refusal.Local(new Curia.Domain.Primitives.Error("curia/client/envelope-invalid", "NOT SENT: the envelope is invalid", "x")) with { Act = ActContext.Post }, 0),
        };

        var problems = new List<string>();
        var reachedSays = cases.Count(c => c.Says == 1);
        var reachedNever = cases.Count(c => c.Says == 0);
        foreach (var c in cases)
        {
            var found = Regex.Count(c.Refusal.Summary, Regex.Escape(clause), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
            if (found != c.Says) problems.Add($"{c.Name}: expected {c.Says} occurrence{(c.Says == 1 ? string.Empty : "s")}, found {found}");
        }

        foreach (var name in new[] { "already raised", "already recorded", "already revised", "contended", "NOT SENT", "a Forum fault" })
            if (!cases.Any(c => c.Says == 0 && c.Name.Contains(name, StringComparison.Ordinal))) problems.Add($"NOT REACHED: {name}");
        if (reachedSays < 6 || reachedNever < 6) problems.Add($"NOT REACHED: classes ({reachedSays} saying it, {reachedNever} not)");
        if (notTwice.Length != 3 || never.Length != 2) problems.Add("the published cause list changed shape");
        Red.None(problems, "the clause exactly once for content, not-found, authorization, budget and the two in-flight 409s, and zero times for the rest");
    }
}
