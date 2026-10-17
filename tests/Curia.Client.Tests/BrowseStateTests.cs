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
    public async Task R10_74_ALoadThatIsRefusedAfterTheScreenIsUpJoinsTheExitByTheRank6845AndLeavesTheViewStanding()
    {
        var ct = TestContext.Current.CancellationToken;

        (Outcome First, RefusalKind Refused, int Expected)[] cases =
        [
            (Outcome.Verified, RefusalKind.NotFound, ExitCode.NotFound),
            (Outcome.Disputed, RefusalKind.NotFound, ExitCode.NotFound),
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
    public void R10_74_TheRankOfTheCodesIs6Then8Then4Then5ThenAnyOtherThen9Then0()
    {
        int Join(params int[] codes)
        {
            var ledger = new OutcomeLedger();
            foreach (var code in codes) ledger.AddRefusal(code);
            return ledger.Exit;
        }

        Assert.Equal(6, Join(9, 5, 4, 8, 6));
        Assert.Equal(8, Join(9, 5, 4, 8));
        Assert.Equal(4, Join(9, 5, 4));
        Assert.Equal(5, Join(9, 5));
        Assert.Equal(3, Join(9, 3));
        Assert.Equal(5, Join(3, 5));
        Assert.Equal(9, Join(9, 0));
        Assert.Equal(0, Join());
        Assert.Equal(0, Join(0));
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

    // ---- helpers ---------------------------------------------------------------------------

    internal enum Outcome
    {
        Verified,
        Disputed,
        Failed,
        CouldNotCheck,
        SpanMismatch,
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
