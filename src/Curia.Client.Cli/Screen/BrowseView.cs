using System.Collections.Immutable;
using Curia.Client;

namespace Curia.Client.Cli;

/// <summary>What a view shows: a board's index, a thread under its root, or one post.</summary>
internal enum ViewKind
{
    Board,
    Thread,
    Read,
}

/// <summary>Where a view's posts come from, so a reload can fetch them again.</summary>
internal readonly record struct ViewSource(ViewKind Kind, string Id);

/// <summary>
/// The screen's views as a closed hierarchy with an explicit <see cref="Match{T}"/> (CS-11): a view added later breaks every
/// call site, including <see cref="BrowseState"/>'s rendering. Each holds the passages it displays and the document row its
/// window starts at; a board is an index and lays out differently from a thread or a post.
/// </summary>
internal abstract record BrowseView
{
    private protected BrowseView()
    {
    }

    /// <summary>The first document row of the window.</summary>
    internal abstract int Offset { get; }

    /// <summary>Where a reload fetches this view's posts from; none for a view that shows no post.</summary>
    internal abstract ViewSource? Source { get; }

    internal abstract BrowseView WithOffset(int offset);

    internal T Match<T>(Func<Board, T> board, Func<Thread, T> thread, Func<Read, T> read, Func<Help, T> help)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(help);

        return this switch
        {
            Board b => board(b),
            Thread t => thread(t),
            Read r => read(r),
            Help h => help(h),
            _ => throw new InvalidOperationException("a view is a board, a thread, a post or the help"),
        };
    }

    /// <summary>The passages the view displays; none for the help.</summary>
    internal ImmutableArray<Passage> Passages => Match(b => b.Posts, t => t.Posts, r => r.Posts, _ => []);

    internal sealed record Board(string Name, ImmutableArray<Passage> Posts, int Top = 0) : BrowseView
    {
        internal override int Offset => Top;

        internal override ViewSource? Source => new(ViewKind.Board, Name);

        internal override BrowseView WithOffset(int offset) => this with { Top = offset };
    }

    internal sealed record Thread(string Root, ImmutableArray<Passage> Posts, int Top = 0) : BrowseView
    {
        internal override int Offset => Top;

        internal override ViewSource? Source => new(ViewKind.Thread, Root);

        internal override BrowseView WithOffset(int offset) => this with { Top = offset };
    }

    internal sealed record Read(string Id, ImmutableArray<Passage> Posts, int Top = 0) : BrowseView
    {
        internal override int Offset => Top;

        internal override ViewSource? Source => new(ViewKind.Read, Id);

        internal override BrowseView WithOffset(int offset) => this with { Top = offset };
    }

    /// <summary>The keys. It loads nothing and shows no post, so it counts no outcome.</summary>
    internal sealed record Help : BrowseView
    {
        internal override int Offset => 0;

        internal override ViewSource? Source => null;

        internal override BrowseView WithOffset(int offset) => this;
    }
}
