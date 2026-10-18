using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Curia.Client;
using Curia.Client.Cli;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Curia.Domain.Serving;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R10.74 (errata G22) and the spec's section 8: the screen's exit is the worst outcome of every passage it displayed, and
/// every surface that loads a post counts that post. The surface table is a fact over the closed hierarchy of transitions,
/// so a transition added without a row breaks the build, and the state is driven through them with a source that counts
/// its loads.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class BrowseStateTests
{
    private static readonly ScreenSize Size = new(100, 40);

    private static readonly Dictionary<string, OutcomeRule> SpecTable = new(StringComparer.Ordinal)
    {
        // Spec 8.2, row by row.
        ["Open"] = OutcomeRule.Counted,
        ["OpenFromIndex"] = OutcomeRule.AlreadyCounted,
        ["Back"] = OutcomeRule.AlreadyCounted,
        ["Reload"] = OutcomeRule.Counted,
        ["Scroll"] = OutcomeRule.NoLoad,
        ["NextPassage"] = OutcomeRule.NoLoad,
        ["ShowHelp"] = OutcomeRule.NoLoad,
        ["ShowCommand"] = OutcomeRule.NoLoad,
    };

    [Fact]
    public void R10_74_EveryTransitionOfTheViewHierarchyHasARowInSpecSection82AndOnlyThoseDo()
    {
        var kinds = typeof(BrowseTransition).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)
            .Where(t => t.IsSealed && t.IsSubclassOf(typeof(BrowseTransition)))
            .Select(t => t.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(kinds.SetEquals(SpecTable.Keys), "transitions: " + string.Join(", ", kinds) + "; the table: " + string.Join(", ", SpecTable.Keys));

        var sampled = BrowseTransition.All.Select(t => t.GetType().Name).ToList();
        Assert.True(sampled.Count == kinds.Count && kinds.SetEquals(sampled), "BrowseTransition.All holds one of each kind: " + string.Join(", ", sampled));

        foreach (var transition in BrowseTransition.All)
            Assert.Equal(SpecTable[transition.GetType().Name], transition.Rule);

        Assert.Contains(OutcomeRule.Counted, SpecTable.Values);
        Assert.Contains(OutcomeRule.AlreadyCounted, SpecTable.Values);
        Assert.Contains(OutcomeRule.NoLoad, SpecTable.Values);
    }

    [Fact]
    public void R10_74_AThirdKindOfTransitionCannotBeMadeOutsideTheHierarchy()
    {
        var constructors = typeof(BrowseTransition).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        // Nothing outside this assembly can derive a kind, and the base cannot be made on its own.
        Assert.NotEmpty(constructors);
        Assert.All(constructors, c => Assert.False(c.IsPublic, "a public constructor: " + c));
        Assert.True(typeof(BrowseTransition).IsAbstract);
    }

    [Fact]
    public async Task R10_74_TheExitIsTheWorstOutcomeDisplayedAndAReloadNeverRemovesOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = new CountingSource(Passages(Outcome.Verified, Outcome.Disputed));
        var state = new BrowseState(source);

        Assert.Null(await state.ApplyAsync(new BrowseTransition.Open(new ViewSource(ViewKind.Board, "general")), Size, ct));
        Assert.Equal(ExitCode.Disputed, state.Ledger.Exit);

        // Enter, Tab, back and scroll load nothing and change nothing.
        Assert.Null(await state.ApplyAsync(new BrowseTransition.OpenFromIndex(), Size, ct));
        Assert.Null(await state.ApplyAsync(new BrowseTransition.Back(), Size, ct));
        Assert.Null(await state.ApplyAsync(new BrowseTransition.Scroll(5, Relative: true), Size, ct));
        Assert.Equal(1, source.Loads);
        Assert.Equal(ExitCode.Disputed, state.Ledger.Exit);

        // A reload that now shows only a verified post adds it and removes nothing.
        source.Next = Passages(Outcome.Verified);
        Assert.Null(await state.ApplyAsync(new BrowseTransition.Reload(), Size, ct));
        Assert.Equal(2, source.Loads);
        Assert.Equal(ExitCode.Disputed, state.Ledger.Exit);

        // A reload that shows a failed one raises it, and scrolling away does not lower it.
        source.Next = Passages(Outcome.Failed);
        Assert.Null(await state.ApplyAsync(new BrowseTransition.Reload(), Size, ct));
        Assert.Equal(ExitCode.Unverified, state.Ledger.Exit);
        source.Next = Passages(Outcome.Verified);
        Assert.Null(await state.ApplyAsync(new BrowseTransition.Reload(), Size, ct));
        Assert.Equal(ExitCode.Unverified, state.Ledger.Exit);
    }

    [Fact]
    public async Task R10_74_AnIndexRowOpenedWithEnterIsThePassageTheIndexAlreadyVerifiedAndIsNotLoadedAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = new CountingSource(Passages(Outcome.Verified, Outcome.Failed, Outcome.Disputed));
        var state = new BrowseState(source);
        await state.ApplyAsync(new BrowseTransition.Open(new ViewSource(ViewKind.Board, "general")), Size, ct);
        Assert.Equal(ExitCode.Unverified, state.Ledger.Exit);

        await state.ApplyAsync(new BrowseTransition.NextPassage(), Size, ct);
        await state.ApplyAsync(new BrowseTransition.OpenFromIndex(), Size, ct);

        var read = Assert.IsType<BrowseView.Read>(state.Current);
        Assert.Equal(source.Next[1].Post.PostId, read.Passages.Single().Post.PostId);
        Assert.Equal(1, source.Loads);

        // And back to the board.
        await state.ApplyAsync(new BrowseTransition.Back(), Size, ct);
        Assert.IsType<BrowseView.Board>(state.Current);

        // Enter does nothing on a thread, which has no index.
        await state.ApplyAsync(new BrowseTransition.Open(new ViewSource(ViewKind.Thread, "root")), Size, ct);
        await state.ApplyAsync(new BrowseTransition.OpenFromIndex(), Size, ct);
        Assert.IsType<BrowseView.Thread>(state.Current);
    }

    [Fact]
    public async Task R10_74_ALoadThatIsRefusedAfterTheScreenIsUpJoinsTheExitByTheRank68945AndLeavesTheViewStanding()
    {
        var ct = TestContext.Current.CancellationToken;

        (Outcome First, RefusalKind Refused, int Expected)[] cases =
        [
            (Outcome.Verified, RefusalKind.NotFound, ExitCode.NotFound),
            (Outcome.Disputed, RefusalKind.NotFound, ExitCode.Disputed),
            (Outcome.Disputed, RefusalKind.Authorization, ExitCode.Disputed),
            (Outcome.Failed, RefusalKind.NotFound, ExitCode.Unverified),
            (Outcome.CouldNotCheck, RefusalKind.Authorization, ExitCode.ForumFault),
            (Outcome.Verified, RefusalKind.Authorization, ExitCode.Denied),
            (Outcome.Verified, RefusalKind.Transport, ExitCode.ForumFault),
        ];

        foreach (var (first, refused, expected) in cases)
        {
            var source = new CountingSource(Passages(first));
            var state = new BrowseState(source);
            await state.ApplyAsync(new BrowseTransition.Open(new ViewSource(ViewKind.Board, "general")), Size, ct);

            source.Refusal = new Refusal(refused, refused == RefusalKind.NotFound ? 404 : 403, new Error("t", "t", "t"));
            var refusal = await state.ApplyAsync(new BrowseTransition.Reload(), Size, ct);

            Assert.NotNull(refusal);
            Assert.Equal(expected, state.Ledger.Exit);
            Assert.IsType<BrowseView.Board>(state.Current);
        }
    }

    [Fact]
    public void R10_74_TheRankOfTheCodesIs6Then8Then9Then4Then5ThenAnyOtherThen0()
    {
        int Join(params int[] codes)
        {
            var ledger = new OutcomeLedger();
            foreach (var code in codes) ledger.AddRefusal(code);
            return ledger.Exit;
        }

        Assert.Equal(6, Join(9, 5, 4, 8, 6));
        Assert.Equal(8, Join(9, 5, 4, 8));
        Assert.Equal(9, Join(9, 5, 4));
        Assert.Equal(9, Join(4, 9));
        Assert.Equal(9, Join(5, 9));
        Assert.Equal(9, Join(9, 3));
        Assert.Equal(4, Join(5, 4));
        Assert.Equal(5, Join(3, 5));
        Assert.Equal(3, Join(3, 0));
        Assert.Equal(9, Join(9, 0));
        Assert.Equal(0, Join());
        Assert.Equal(0, Join(0));
    }

    [Fact]
    public async Task R10_74_ALaterRefusalNeverHidesADisputedOutcome()
    {
        var ct = TestContext.Current.CancellationToken;

        async Task<int> Exit(Outcome first, RefusalKind refused, int status)
        {
            var source = new CountingSource(Passages(first));
            var state = new BrowseState(source);
            await state.ApplyAsync(new BrowseTransition.Open(new ViewSource(ViewKind.Read, "p")), Size, ct);
            source.Refusal = new Refusal(refused, status, new Error("t", "t", "t"));
            Assert.NotNull(await state.ApplyAsync(new BrowseTransition.Reload(), Size, ct));
            return state.Ledger.Exit;
        }

        Assert.Equal(ExitCode.Disputed, await Exit(Outcome.Disputed, RefusalKind.NotFound, 404));
        Assert.Equal(ExitCode.Disputed, await Exit(Outcome.Disputed, RefusalKind.Authorization, 403));
        Assert.Equal(ExitCode.Unverified, await Exit(Outcome.Failed, RefusalKind.NotFound, 404));
        Assert.Equal(ExitCode.NotFound, await Exit(Outcome.Verified, RefusalKind.NotFound, 404));
        Assert.Equal(ExitCode.Denied, await Exit(Outcome.Verified, RefusalKind.Authorization, 403));
        Assert.Equal(ExitCode.ForumFault, await Exit(Outcome.Disputed, RefusalKind.Transport, 0));
    }

    [Fact]
    public async Task R10_74_AnOutcomeIsTakenFromThePassagesOutcomeAndNoOtherWord()
    {
        var ct = TestContext.Current.CancellationToken;
        foreach (var (outcome, exit) in new[]
        {
            (Outcome.Verified, ExitCode.Ok), (Outcome.Disputed, ExitCode.Disputed), (Outcome.Failed, ExitCode.Unverified),
            (Outcome.CouldNotCheck, ExitCode.ForumFault), (Outcome.SpanMismatch, ExitCode.Unverified),
        })
        {
            var passages = Passages(outcome);
            var state = new BrowseState(new CountingSource(passages));
            await state.ApplyAsync(new BrowseTransition.Open(new ViewSource(ViewKind.Read, "p")), Size, ct);

            Assert.Equal(exit, state.Ledger.Exit);
            Assert.Equal(ExitCode.ForOutcomes(passages[0].Outcome), state.Ledger.Exit);
        }
    }

    [Fact]
    public async Task R10_72_HelpAndTheCommandNoteLoadNothingAndTheNextKeyClearsTheNote()
    {
        var ct = TestContext.Current.CancellationToken;
        var source = new CountingSource(Passages(Outcome.Verified, Outcome.Verified));
        var state = new BrowseState(source);
        await state.ApplyAsync(new BrowseTransition.Open(new ViewSource(ViewKind.Thread, "root")), Size, ct);

        await state.ApplyAsync(new BrowseTransition.ShowCommand(), Size, ct);
        Assert.Contains("curia verify", state.Render(Size).Status.Text, StringComparison.Ordinal);
        await state.ApplyAsync(new BrowseTransition.Scroll(1, Relative: true), Size, ct);
        Assert.DoesNotContain("curia verify", state.Render(Size).Status.Text, StringComparison.Ordinal);

        await state.ApplyAsync(new BrowseTransition.ShowHelp(), Size, ct);
        Assert.IsType<BrowseView.Help>(state.Current);
        Assert.Contains(state.Render(Size).Rows, r => r.Text.Contains("quit", StringComparison.Ordinal));
        await state.ApplyAsync(new BrowseTransition.Back(), Size, ct);
        Assert.IsType<BrowseView.Thread>(state.Current);
        Assert.Equal(1, source.Loads);
    }

    [Fact]
    public async Task R10_74_TheExitCodeHelpAndTheScreensHelpRankTheCodesInTheSameWords()
    {
        var ct = TestContext.Current.CancellationToken;
        var state = new BrowseState(new CountingSource(Passages(Outcome.Verified)));
        await state.ApplyAsync(new BrowseTransition.Open(new ViewSource(ViewKind.Thread, "root")), Size, ct);
        await state.ApplyAsync(new BrowseTransition.ShowHelp(), Size, ct);

        const string rank = "6 a check did not hold, 8 it could not be run, 9 disputed, then 4 or 5 a load was refused, 0 otherwise";
        var screen = string.Join(' ', state.Render(new ScreenSize(400, 60)).Rows.Select(r => r.Text));
        var entry = string.Join(' ', HelpTextTests.ExitCodeEntry(ExitCode.Disputed).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var usage = string.Join(' ', Help.For("browse")!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains(rank, screen, StringComparison.Ordinal);
        Assert.Contains(rank, entry, StringComparison.Ordinal);
        Assert.Contains(rank, usage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R10_72_TabMovesToTheNextPassageAndWrapsToTheFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        var state = new BrowseState(new CountingSource(Passages(Outcome.Verified, Outcome.Verified, Outcome.Verified)));
        await state.ApplyAsync(new BrowseTransition.Open(new ViewSource(ViewKind.Thread, "root")), Size, ct);
        var starts = state.Render(Size).PassageStarts;
        Assert.Equal(3, starts.Length);

        await state.ApplyAsync(new BrowseTransition.NextPassage(), Size, ct);
        Assert.Equal(starts[1], state.Render(Size).Offset);
        await state.ApplyAsync(new BrowseTransition.NextPassage(), Size, ct);
        Assert.Equal(starts[2], state.Render(Size).Offset);
        await state.ApplyAsync(new BrowseTransition.NextPassage(), Size, ct);
        Assert.Equal(starts[0], state.Render(Size).Offset);
    }

    [Fact]
    public async Task R10_72_EndShowsTheLastPageAndAHeldJStopsThere()
    {
        var ct = TestContext.Current.CancellationToken;
        var sources = new[] { 60, 5, 25 };
        var passages = sources.Select((n, i) => Long(n, i)).ToImmutableArray();
        var minimum = Screen.MinimumHeightFor(passages, 80);
        var shown = 0;
        var eight = 0;

        for (var height = minimum; height < minimum + 30; height++)
        {
            var size = new ScreenSize(80, height);
            var end = new BrowseState(new CountingSource(passages));
            await end.ApplyAsync(new BrowseTransition.Open(new ViewSource(ViewKind.Thread, "root")), size, ct);
            await end.ApplyAsync(new BrowseTransition.Scroll(int.MaxValue / 2, Relative: false), size, ct);
            var screen = end.Render(size);

            // The document's last row is the last row of the window, and so is the status row's range.
            Assert.Equal("end of #3", screen.Rows[^2].Text);
            Assert.Matches($@"^rows [0-9]+-{screen.DocumentRows} of {screen.DocumentRows}", screen.Status.Text);

            // Spec 14.8 item 2: the content rows plus the `end of #3` row reach min(8, the last passage's content rows + 1). The
            // eighth row at the very smallest window is the passage's own end row, which is the reader's row and is meant to be seen.
            var content = screen.Rows.Count(r => r is ScreenRow.Content);
            Assert.True(content + 1 >= Math.Min(8, sources[^1] + 1), $"height {height}: {content} content rows and the end row at End, {sources[^1]} in the last passage");
            if (content >= 8) eight++;
            if (screen.Rows.Length == height) shown++;

            // A held j stops where End stops, and End is where it was after more of them.
            var held = new BrowseState(new CountingSource(passages));
            await held.ApplyAsync(new BrowseTransition.Open(new ViewSource(ViewKind.Thread, "root")), size, ct);
            for (var i = 0; i < screen.DocumentRows + 20; i++)
                await held.ApplyAsync(new BrowseTransition.Scroll(1, Relative: true), size, ct);
            Assert.Equal(screen.Offset, held.Render(size).Offset);
            Assert.Equal("end of #3", held.Render(size).Rows[^2].Text);
        }

        Assert.True(shown > 10 && eight > 10, $"only {shown} sizes drew a full last page, {eight} with eight content rows: the property is vacuous");
    }

    [Fact]
    public async Task R10_72_AHeldJAndAHeldKCrossEveryHeaderInTheDocument()
    {
        var ct = TestContext.Current.CancellationToken;
        ImmutableArray<Passage> passages = [Long(30, 0), Passage(Outcome.Failed, 1), Long(12, 2)];
        var size = new ScreenSize(80, 30);
        var state = new BrowseState(new CountingSource(passages));
        await state.ApplyAsync(new BrowseTransition.Open(new ViewSource(ViewKind.Thread, "root")), size, ct);
        var last = state.Render(size).MaxOffset;
        Assert.True(last > 30, $"the last page starts at {last}: the document is too short for the property");

        var seen = new List<int> { state.Render(size).Offset };
        for (var i = 0; i < 400 && seen[^1] < last; i++)
        {
            await state.ApplyAsync(new BrowseTransition.Scroll(1, Relative: true), size, ct);
            seen.Add(state.Render(size).Offset);
        }

        Assert.Equal(last, seen[^1]);
        Assert.Equal(seen.Order().ToList(), seen);
        Assert.Equal(seen.Count, seen.Distinct().Count());

        // And back: a held k leaves every header's rows behind and reaches the top.
        var back = new List<int> { seen[^1] };
        for (var i = 0; i < 400 && back[^1] > 0; i++)
        {
            await state.ApplyAsync(new BrowseTransition.Scroll(-1, Relative: true), size, ct);
            back.Add(state.Render(size).Offset);
        }

        Assert.Equal(0, back[^1]);
        Assert.Equal(back.Count, back.Distinct().Count());
    }

    [Fact]
    public async Task R10_72_AJAloneFromTheTopSeesEveryContentRowOnTheWayToTheLastPageAndAKAloneReturns()
    {
        var ct = TestContext.Current.CancellationToken;
        ImmutableArray<Passage> passages = [Tagged(30, 0), Passage(Outcome.Failed, 1), Tagged(12, 2), Tagged(1, 3)];
        var multiRowHeaders = 0;
        foreach (var size in new[] { new ScreenSize(80, 28), new ScreenSize(80, 30), new ScreenSize(100, 40) })
        {
            // Every content row of the document, by the passage it belongs to, from a window tall enough to show all of it.
            var everything = Screen.ForPassages(passages, new ScreenSize(size.Width, 1000), 0);
            var expected = Keyed(everything).ToHashSet(StringComparer.Ordinal);
            Assert.True(expected.Count > 40, $"{expected.Count} content rows: the document is too short for the property");
            multiRowHeaders += PassageHeaderRows(everything) > 1 ? 1 : 0;

            var state = new BrowseState(new CountingSource(passages));
            await state.ApplyAsync(new BrowseTransition.Open(new ViewSource(ViewKind.Thread, "root")), size, ct);
            var last = state.Render(size).MaxOffset;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var offsets = new List<int> { state.Render(size).Offset };
            var hops = 0;
            Collect(state.Render(size), seen);
            for (var i = 0; i < 1000 && offsets[^1] < last; i++)
            {
                await state.ApplyAsync(new BrowseTransition.Scroll(1, Relative: true), size, ct);
                var screen = state.Render(size);
                if (screen.Offset - offsets[^1] > 1) hops++;
                offsets.Add(screen.Offset);
                Collect(screen, seen);
            }

            Assert.Equal(last, offsets[^1]);
            Assert.True(hops > 0, "no j hopped a gap: the property is vacuous");
            Assert.Empty(expected.Except(seen));

            var back = new List<int> { offsets[^1] };
            for (var i = 0; i < 1000 && back[^1] > 0; i++)
            {
                await state.ApplyAsync(new BrowseTransition.Scroll(-1, Relative: true), size, ct);
                back.Add(state.Render(size).Offset);
            }

            Assert.Equal(0, back[^1]);
        }

        Assert.True(multiRowHeaders == 3, "a header was one row: the property is vacuous");

        static void Collect(Screen screen, HashSet<string> seen)
        {
            foreach (var key in Keyed(screen)) seen.Add(key);
        }

        // A content row told apart by the passage it belongs to: the number in the last header row (pinned or not) above it.
        static IEnumerable<string> Keyed(Screen screen)
        {
            var passage = "?";
            foreach (var row in screen.Rows)
            {
                if (row is ScreenRow.Content c) yield return passage + ":" + c.Text;
                else if (System.Text.RegularExpressions.Regex.Match(row.Text, @"^#([0-9]+) post ") is { Success: true } m) passage = m.Groups[1].Value;
            }
        }

        static int PassageHeaderRows(Screen screen) =>
            screen.Rows.ToList().FindIndex(r => r is ScreenRow.Content) - screen.Rows.ToList().FindIndex(r => r.Text.StartsWith("#1 post ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task R10_72_HomeAndTabAreAsTheyWereAfterTheClampChanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var passages = Enumerable.Range(0, 3).Select(i => Long(40, i)).ToImmutableArray();
        var size = new ScreenSize(80, 30);
        var state = new BrowseState(new CountingSource(passages));
        await state.ApplyAsync(new BrowseTransition.Open(new ViewSource(ViewKind.Thread, "root")), size, ct);
        var starts = state.Render(size).PassageStarts;

        await state.ApplyAsync(new BrowseTransition.Scroll(int.MaxValue / 2, Relative: false), size, ct);
        await state.ApplyAsync(new BrowseTransition.Scroll(0, Relative: false), size, ct);
        Assert.Equal(0, state.Render(size).Offset);
        await state.ApplyAsync(new BrowseTransition.NextPassage(), size, ct);
        Assert.Equal(starts[1], state.Render(size).Offset);
        await state.ApplyAsync(new BrowseTransition.NextPassage(), size, ct);
        await state.ApplyAsync(new BrowseTransition.NextPassage(), size, ct);
        Assert.Equal(starts[0], state.Render(size).Offset);
    }

    [Fact]
    public async Task R10_74_TheHelpStatesTheRankAndTheCompactHeader()
    {
        var ct = TestContext.Current.CancellationToken;
        var state = new BrowseState(new CountingSource(Passages(Outcome.Verified)));
        await state.ApplyAsync(new BrowseTransition.Open(new ViewSource(ViewKind.Read, "p")), Size, ct);
        await state.ApplyAsync(new BrowseTransition.ShowHelp(), Size, ct);

        var text = string.Join(" ", state.Render(new ScreenSize(200, 60)).Rows.Select(r => r.Text));
        Assert.Contains("6 a check did not hold, 8 it could not be run, 9 disputed, then 4 or 5 a load was refused", text, StringComparison.Ordinal);
        Assert.Contains("compact", text, StringComparison.Ordinal);
        Assert.Contains("digest", text, StringComparison.Ordinal);
        Assert.Contains("a combining mark on a layout space draws as it would on any base", text, StringComparison.OrdinalIgnoreCase);
    }

    // ---- helpers ---------------------------------------------------------------------------

    internal enum Outcome
    {
        Verified,
        Disputed,
        Failed,
        CouldNotCheck,
        SpanMismatch,
    }

    /// <summary>A verified passage of <paramref name="lines"/> lines of content, with a parent.</summary>
    private static Passage Long(int lines, int index)
    {
        var canonical = SpanCheckTests.Envelope(string.Join("\n", Enumerable.Range(0, lines).Select(i => "line " + i.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        var post = SpanCheckTests.Post(canonical, Datamarking.Render(canonical, MarkingMode.None), MarkingMode.None);
        post = post with
        {
            PostId = "01LONG" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Parent = "01PARENT",
            Provenance = post.Provenance with { Author = "https://agents.example/alice" },
            LogIndex = 7,
        };
        return new Passage(post, new SignatureVerdict(true, "alice-1", "recanonicalized bytes are byte-identical to the served canonical form", "ab", "sha256:ab", KeyStatus: "active"));
    }

    /// <summary>A parented, verified passage whose every content row begins with a tag no other passage's does.</summary>
    private static Passage Tagged(int lines, int index)
    {
        var canonical = SpanCheckTests.Envelope(string.Join("\n", Enumerable.Range(0, lines).Select(i => "p" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "L" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "x")));
        var post = SpanCheckTests.Post(canonical, Datamarking.Render(canonical, MarkingMode.None), MarkingMode.None);
        post = post with
        {
            PostId = "01TAGGED" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Parent = "01PARENT",
            Provenance = post.Provenance with { Author = "https://agents.example/alice" },
            LogIndex = 7,
        };
        return new Passage(post, new SignatureVerdict(true, "alice-1", "recanonicalized bytes are byte-identical to the served canonical form", "ab", "sha256:ab", KeyStatus: "active"));
    }

    private sealed class CountingSource(ImmutableArray<Passage> first) : IBrowseSource
    {
        internal ImmutableArray<Passage> Next { get; set; } = first;

        internal Refusal? Refusal { get; set; }

        internal int Loads { get; private set; }

        public Task<LoadResult> LoadAsync(ViewSource source, CancellationToken ct)
        {
            Loads++;
            return Task.FromResult(Refusal is { } refusal ? LoadResult.Refused(refusal) : LoadResult.Loaded(Next));
        }
    }

    private static ImmutableArray<Passage> Passages(params Outcome[] outcomes) =>
        [.. outcomes.Select((outcome, i) => Passage(outcome, i))];

    private static Passage Passage(Outcome outcome, int index)
    {
        var canonical = SpanCheckTests.Envelope("body of passage " + index);
        var rendered = Datamarking.Render(canonical, MarkingMode.None);
        var post = SpanCheckTests.Post(canonical, outcome == Outcome.SpanMismatch ? Datamarking.Render(SpanCheckTests.Envelope("another"), MarkingMode.None) : rendered, MarkingMode.None);
        post = post with { PostId = "01POST" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), Provenance = post.Provenance with { Author = "https://agents.example/alice" }, LogIndex = 7 };

        switch (outcome)
        {
            case Outcome.Disputed:
                post = post with { Provenance = post.Provenance with { DisputedByLogIndex = 41 } };
                break;
            case Outcome.CouldNotCheck:
                return new Passage(post, SignatureCheck.Unreachable(post, new Refusal(RefusalKind.Transport, 0, new Error("t", "t", "t"))));
            case Outcome.Failed:
                return new Passage(post, new SignatureVerdict(false, "alice-1", "the signature did not verify", "ab", "sha256:ab"));
        }

        return new Passage(post, new SignatureVerdict(true, "alice-1", "recanonicalized bytes are byte-identical to the served canonical form", "ab", "sha256:ab"));
    }
}
