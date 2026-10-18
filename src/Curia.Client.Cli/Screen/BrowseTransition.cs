namespace Curia.Client.Cli;

/// <summary>
/// Spec 8.2: what a transition does about exit 9's coverage (D49's item 4). A surface that omits this rule fails silently,
/// so it is a value every transition has and <see cref="BrowseState"/> enforces: a transition that loads a post counts it,
/// and one that is not <see cref="Counted"/> and loads is a defect that throws.
/// </summary>
internal enum OutcomeRule
{
    /// <summary>A fresh load: every passage it brings is counted, and a reload adds to the session and never removes.</summary>
    Counted,

    /// <summary>A post already counted when it was loaded: Enter on an index row, and back to a view held in the history.</summary>
    AlreadyCounted,

    /// <summary>No post is loaded: scrolling, Tab, the help, the command.</summary>
    NoLoad,
}

/// <summary>
/// Everything a key can ask the screen to do to its state, as a closed hierarchy with an explicit
/// <see cref="Match{T}"/> (CS-11): a transition added later has no <see cref="Rule"/> until someone writes its row of spec
/// 8.2, and breaks the build until then.
/// </summary>
internal abstract record BrowseTransition
{
    private protected BrowseTransition()
    {
    }

    /// <summary>One of each kind, for the fact that holds the table to spec 8.2.</summary>
    internal static IReadOnlyList<BrowseTransition> All { get; } =
    [
        new Open(new ViewSource(ViewKind.Read, string.Empty)), new OpenFromIndex(), new Back(), new Reload(),
        new Scroll(0, Relative: true), new NextPassage(), new ShowHelp(), new ShowCommand(),
    ];

    /// <summary>The row of spec 8.2 for this transition.</summary>
    internal OutcomeRule Rule => Match(
        _ => OutcomeRule.Counted,
        _ => OutcomeRule.AlreadyCounted,
        _ => OutcomeRule.AlreadyCounted,
        _ => OutcomeRule.Counted,
        _ => OutcomeRule.NoLoad,
        _ => OutcomeRule.NoLoad,
        _ => OutcomeRule.NoLoad,
        _ => OutcomeRule.NoLoad);

    internal T Match<T>(
        Func<Open, T> open,
        Func<OpenFromIndex, T> openFromIndex,
        Func<Back, T> back,
        Func<Reload, T> reload,
        Func<Scroll, T> scroll,
        Func<NextPassage, T> nextPassage,
        Func<ShowHelp, T> showHelp,
        Func<ShowCommand, T> showCommand)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(openFromIndex);
        ArgumentNullException.ThrowIfNull(back);
        ArgumentNullException.ThrowIfNull(reload);
        ArgumentNullException.ThrowIfNull(scroll);
        ArgumentNullException.ThrowIfNull(nextPassage);
        ArgumentNullException.ThrowIfNull(showHelp);
        ArgumentNullException.ThrowIfNull(showCommand);

        return this switch
        {
            Open o => open(o),
            OpenFromIndex i => openFromIndex(i),
            Back b => back(b),
            Reload r => reload(r),
            Scroll s => scroll(s),
            NextPassage n => nextPassage(n),
            ShowHelp h => showHelp(h),
            ShowCommand c => showCommand(c),
            _ => throw new InvalidOperationException("a transition is one of the eight kinds spec 8.2 has a row for"),
        };
    }

    /// <summary>The initial view, from the command line.</summary>
    internal sealed record Open(ViewSource Source) : BrowseTransition;

    /// <summary>Enter on an index row: the post the index already verified, as a view of its own, with no second fetch.</summary>
    internal sealed record OpenFromIndex : BrowseTransition;

    internal sealed record Back : BrowseTransition;

    /// <summary>A fresh load of the current view's posts.</summary>
    internal sealed record Reload : BrowseTransition;

    /// <summary>Scroll by <paramref name="Amount"/> rows, or to row <paramref name="Amount"/> when not relative.</summary>
    internal sealed record Scroll(int Amount, bool Relative) : BrowseTransition;

    internal sealed record NextPassage : BrowseTransition;

    internal sealed record ShowHelp : BrowseTransition;

    /// <summary>The equivalent <c>curia</c> command, on the status row.</summary>
    internal sealed record ShowCommand : BrowseTransition;
}
