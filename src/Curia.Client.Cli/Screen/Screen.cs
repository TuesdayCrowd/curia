using System.Collections.Immutable;
using System.Globalization;
using Curia.Client;
using Curia.Domain.Serving;

namespace Curia.Client.Cli;

/// <summary>The terminal's size in columns and rows, as the screen was laid out for it.</summary>
internal readonly record struct ScreenSize(int Width, int Height);

/// <summary>
/// R10.72 to R10.75: what a screen shows, as rows, with no terminal in it. Pure: the same passages, size and offset give
/// the same rows, so every property about a screen is a fact about this type.
///
/// <para><b>The document.</b> A view is a sequence of blocks, one per passage: its header, its body (the canonical form's
/// content rows, or one frame row saying why there are none) and a row saying where it ends. The window shows a run of
/// that sequence. When the window's first row is inside a body, the passage's header is pinned above it and marked
/// continued, so a scroll cannot leave content without its header or the warning (R10.18, R10.72). When it is inside a
/// header, it snaps to the header's first row.</para>
///
/// <para><b>Content is drawn only for a signature that held.</b> Verified or disputed (R6.57: served and marked, so a
/// person can read what not to cite), laid out from the signed bytes. A signature that failed or could not be checked
/// draws none (R10.71), and says so on a frame row that names the line form. The exit is <see cref="Passage.Outcome"/>'s,
/// which is also failed for a span that does not render the canonical form: a passage whose signature held but whose span
/// did not draws its canonical form under a header saying so (H4b).</para>
///
/// <para><b>The minimum is computed.</b> Content is drawn only when the width is at least <see cref="MinimumWidth"/> and the
/// window has the screen's chrome, the tallest header actually present (a 300-character author wraps and raises it) and
/// <see cref="MinimumContentRows"/> rows besides (the tallest wrapped no-content row, where no passage draws content). Otherwise only this client's
/// rows are drawn, saying how many rows and columns the window needs, with the line form's command.</para>
///
/// <para><b>Two header forms, one per document (spec 14.1).</b> The full form is used where the window holds the chrome, the
/// tallest full header and the content rows; otherwise the compact form (<see cref="PassageHeader"/>), which leaves out only
/// what R10.74 does not list. The choice is made from the size alone, so scrolling never changes a header's form.
/// <see cref="MinimumHeight"/> is the compact form's.</para>
/// </summary>
internal sealed class Screen
{
    /// <summary>Narrower than this, the status row and the header's rows cannot be laid out honestly.</summary>
    internal const int MinimumWidth = 72;

    /// <summary>R10.72's eight rows of content a window must leave after the chrome and the tallest header.</summary>
    internal const int MinimumContentRows = 8;

    internal const string Title = "curia browse: a screen for a person. A model or script: a curia verb or --plain";

    internal const string MarkingRow = "content is the signed bytes laid out, not datamarked: a model uses curia read";

    internal const string WideOnScreen = "wide characters on screen: a row may be cut";

    private const string WideShort = "wide characters on screen";

    private Screen(ImmutableArray<ScreenRow> rows, bool drawsContent, int offset, int documentRows, int minimumHeight, ImmutableArray<int> passageStarts, int maxOffset, ImmutableArray<(int From, int To)> gaps)
    {
        _gaps = gaps;
        Rows = rows;
        DrawsContent = drawsContent;
        Offset = offset;
        DocumentRows = documentRows;
        MinimumHeight = minimumHeight;
        PassageStarts = passageStarts;
        MaxOffset = maxOffset;
    }

    /// <summary>Every row, top to bottom: the title, the warning, the marking row, the window, the status row last.</summary>
    internal ImmutableArray<ScreenRow> Rows { get; }

    /// <summary>Whether the window has room for content under the headers it shows.</summary>
    internal bool DrawsContent { get; }

    /// <summary>The first row of the document the window shows, after snapping.</summary>
    internal int Offset { get; }

    /// <summary>How many rows the whole document has.</summary>
    internal int DocumentRows { get; }

    private readonly ImmutableArray<(int From, int To)> _gaps;

    /// <summary>The fewest rows at which this document, at this width, is drawn with content: the compact form's (spec 14.1).</summary>
    internal int MinimumHeight { get; }

    /// <summary>
    /// The last page's first row (spec 14.3): the first offset at which the document's last row is in the window, so that a
    /// scroll stops with the last row at the bottom and not with one row on show.
    /// </summary>
    internal int MaxOffset { get; }

    /// <summary>The document row each passage (or index row) begins at, in order: where Tab goes, and what Enter opens.</summary>
    internal ImmutableArray<int> PassageStarts { get; }

    /// <summary>
    /// The nearest row a window can start at to <paramref name="target"/>: itself, or the end of the gap it falls in when
    /// scrolling <paramref name="towardsEnd"/>, or the start of the passage the gap belongs to when scrolling back.
    /// </summary>
    internal int Settle(int target, bool towardsEnd)
    {
        foreach (var (from, to) in _gaps)
        {
            if (target >= from && target < to) return towardsEnd ? to : from - 1;
        }

        return target;
    }

    /// <summary>The row drawn last.</summary>
    internal ScreenRow Status => Rows[^1];

    /// <summary>A thread or a single post: each passage under its header.</summary>
    internal static Screen ForPassages(IReadOnlyList<Passage> passages, ScreenSize size, int offset) =>
        LayoutPassages(passages, size).At(offset);

    /// <summary>
    /// The document of a thread or a single post at <paramref name="size"/>, laid out once: every offset is then a window onto
    /// it (<see cref="Document.At"/>), so a scroll does not lay the document out again.
    /// </summary>
    internal static Document LayoutPassages(IReadOnlyList<Passage> passages, ScreenSize size)
    {
        ArgumentNullException.ThrowIfNull(passages);

        var draws = new bool[passages.Count];
        var bodies = new ImmutableArray<ScreenRow>[passages.Count];
        for (var i = 0; i < passages.Count; i++)
        {
            var passage = passages[i];
            draws[i] = passage.SignatureOutcome is CheckOutcome.Verified or CheckOutcome.Disputed
                && SpanCheck.CoveredForm(passage.Post) is not null;

            bodies[i] = draws[i]
                ? ContentLayout.Rows(SpanCheck.CoveredForm(passage.Post)!, size.Width)
                : [.. NoContentRows(passage, size.Width).Select(row => (ScreenRow)new ScreenRow.Frame(row))];
        }

        List<Block> Blocks(bool compact)
        {
            var blocks = new List<Block>(passages.Count);
            for (var i = 0; i < passages.Count; i++)
            {
                var passage = passages[i];
                var number = i + 1;
                blocks.Add(new Block(
                    PassageHeader.Rows(passage, number, size.Width, null, compact),
                    bodies[i],
                    draws[i],
                    new ScreenRow.Frame($"end of #{number.ToString(CultureInfo.InvariantCulture)}"),
                    draws[i] ? range => PassageHeader.Rows(passage, number, size.Width, range, compact) : null));
            }

            return blocks;
        }

        // Eight rows of content where any passage draws any; where none does, the tallest wrapped row that says why (spec 14.8 item 3).
        var contentRows = draws.Any(d => d) ? MinimumContentRows : Math.Max(1, bodies.Select(body => body.Length).DefaultIfEmpty(1).Max());
        return Prepare(Blocks(compact: false), Blocks(compact: true), size, contentRows, firstPostId: passages.Count > 0 ? passages[0].Post.PostId : null);
    }

    /// <summary>A board: one index row per post, then its title as content, or why there is none (spec 4.4).</summary>
    internal static Screen ForBoard(IReadOnlyList<Passage> passages, ScreenSize size, int offset) =>
        LayoutBoard(passages, size).At(offset);

    /// <summary>The document of a board at <paramref name="size"/>, laid out once.</summary>
    internal static Document LayoutBoard(IReadOnlyList<Passage> passages, ScreenSize size)
    {
        ArgumentNullException.ThrowIfNull(passages);

        var digits = passages.Count.ToString(CultureInfo.InvariantCulture).Length;
        var blocks = new List<Block>(passages.Count);
        for (var i = 0; i < passages.Count; i++)
        {
            var passage = passages[i];
            var number = i + 1;
            var header = ImmutableArray.CreateBuilder<ScreenRow>();
            foreach (var row in RowWrap.Frame(IndexRow.Text(passage, number), size.Width)) header.Add(new ScreenRow.Frame(row));
            if (TitledIndex.ContentFailed(passage, TitledIndex.Title(passage) is not null) is { } failed)
            {
                foreach (var row in RowWrap.Frame(failed.Text, size.Width)) header.Add(new ScreenRow.Frame(row));
            }

            ImmutableArray<ScreenRow> body;
            var title = TitledIndex.Title(passage);
            if (title is not null)
            {
                var bodyWidth = Math.Max(1, size.Width - ScreenRow.Content.GutterWidth(digits));
                var wrapped = ContentLayout.Wrap(ContentLayout.Escape(title), bodyWidth);
                body = [.. wrapped.Select((text, r) => (ScreenRow)new ScreenRow.Content(number, r > 0, digits, text))];
            }
            else
            {
                body = [.. RowWrap.Frame(TitledIndex.NoTitle(passage).Text, size.Width).Select(row => (ScreenRow)new ScreenRow.Frame(row))];
            }

            blocks.Add(new Block(header.ToImmutable(), body, title is not null, null, null));
        }

        return Prepare(blocks, blocks, size, 1, firstPostId: passages.Count > 0 ? passages[0].Post.PostId : null);
    }

    /// <summary>The fewest rows a screen of <paramref name="passages"/> is drawn with content at, at <paramref name="width"/> columns.</summary>
    internal static int MinimumHeightFor(IReadOnlyList<Passage> passages, int width) =>
        ForPassages(passages, new ScreenSize(width, 1000), 0).MinimumHeight;

    /// <summary>The keys, as this client's own rows under the screen's chrome. It loads nothing and shows no post.</summary>
    internal static Screen ForHelp(IReadOnlyList<string> lines, ScreenSize size) =>
        LayoutHelp(lines, size).At(0);

    /// <summary>The document of the help at <paramref name="size"/>, laid out once.</summary>
    internal static Document LayoutHelp(IReadOnlyList<string> lines, ScreenSize size)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var header = ImmutableArray.CreateBuilder<ScreenRow>();
        foreach (var line in lines)
        {
            foreach (var row in RowWrap.Frame(line, size.Width)) header.Add(new ScreenRow.Frame(row));
        }

        List<Block> blocks = [new Block(header.ToImmutable(), [], false, null, null)];
        return Prepare(blocks, blocks, size, 0, firstPostId: null);
    }

    /// <summary>
    /// This screen with its status row replaced by <paramref name="note"/>, a sentence of this client's own: what a key
    /// answered (the equivalent command; suspending is not supported). The position is back on the next draw.
    /// </summary>
    internal Screen WithNote(OwnText note, int width)
    {
        var status = new ScreenRow.Frame(Truncate(note.Text, width - 1));
        return new Screen(Rows.SetItem(Rows.Length - 1, status), DrawsContent, Offset, DocumentRows, MinimumHeight, PassageStarts, MaxOffset, _gaps);
    }

    private static ScreenRow.Frame[] Chrome(string text, int width) =>
        [.. RowWrap.Frame(text, width).Select(row => new ScreenRow.Frame(row))];

    /// <summary>
    /// The frame row that says why a passage has no content (spec 14.8 item 3), as the reader's own rows: wrapped between
    /// tokens with <see cref="RowWrap.Frame"/>, and its last words, the command, kept whole on one row whenever one row can
    /// hold it. A command wider than a whole row is the only thing that is cut, across rows with all of it kept, in order.
    /// </summary>
    private static ImmutableArray<string> NoContentRows(Passage passage, int width)
    {
        var outcome = passage.SignatureOutcome is CheckOutcome.CouldNotCheck
            ? "the signature could not be checked"
            : "the signature did not verify";
        var sentence = new FrameBuilder()
            .Append($"no content is shown: {new OwnText(outcome)}, so nothing it would cover is shown (R10.71). The Forum's span, under its own failing line, is in:")
            .ToString();
        var command = Hints.Read(passage.Post.PostId).Text;

        var rows = RowWrap.Frame(sentence, width).ToBuilder();
        if (CellWidth.Of(rows[^1]) + 1 + CellWidth.Of(command) <= width)
        {
            rows[^1] = rows[^1] + " " + command;
        }
        else if (CellWidth.Of(RowWrap.ContinuationPrefix) + CellWidth.Of(command) <= width)
        {
            rows.Add(RowWrap.ContinuationPrefix + command);
        }
        else
        {
            rows.AddRange(RowWrap.Frame(RowWrap.ContinuationPrefix + command, width));
        }

        return rows.ToImmutable();
    }

    /// <summary>
    /// Everything about a document that depends on the size and not on the offset: the header form, the document's rows, where
    /// each passage starts, the gaps, the last page's first row. Built once by <see cref="Prepare"/>; <see cref="At"/> then
    /// draws a window onto it, which costs a window and one pinned header, not the document.
    /// </summary>
    internal sealed class Document
    {
        private readonly List<Block> _blocks;
        private readonly List<ScreenRow> _rows;
        private readonly List<int> _starts;
        private readonly ImmutableArray<int> _passageStarts;
        private readonly ImmutableArray<(int From, int To)> _gaps;
        private readonly ScreenRow.Frame[] _title;
        private readonly ScreenRow.Frame[] _warning;
        private readonly ScreenRow.Frame[] _marking;
        private readonly ImmutableArray<ScreenRow>? _tooSmall;
        private readonly int?[] _shell;
        private readonly int _minimum;
        private readonly int _window;
        private readonly int _width;
        private readonly int _maxOffset;

        internal Document(
            List<Block> blocks, List<ScreenRow> rows, List<int> starts, ImmutableArray<(int From, int To)> gaps, (ScreenRow.Frame[] Title, ScreenRow.Frame[] Warning, ScreenRow.Frame[] Marking) chrome,
            ImmutableArray<ScreenRow>? tooSmall, int minimum, int window, int width)
        {
            _blocks = blocks;
            _rows = rows;
            _starts = starts;
            _passageStarts = [.. starts];
            _gaps = gaps;
            (_title, _warning, _marking) = chrome;
            _tooSmall = tooSmall;
            _minimum = minimum;
            _window = window;
            _shell = new int?[blocks.Count];
            _width = width;
            _maxOffset = _tooSmall is null ? MaxOffset() : 0;
        }

        /// <summary>Where a window that starts at row <paramref name="requested"/> really starts, and the header it pins above it.</summary>
        internal (int Top, ImmutableArray<ScreenRow> Pinned) Resolve(int requested)
        {
            var at = _starts.FindLastIndex(start => start <= requested);
            var owner = _blocks[at];
            var bodyStart = _starts[at] + owner.Header.Length;
            var bodyEnd = bodyStart + owner.Body.Length;
            if (requested < bodyStart || (requested < bodyEnd && !owner.BodyIsContent)) return (_starts[at], []);

            if (requested < bodyEnd && owner.Pinned is not null)
            {
                // The shell's height is the same for every range of a block (the numbers are padded to one width), so it is asked once.
                _shell[at] ??= owner.Pinned(Widest(owner)).Length;
                var shown = Math.Min(_window - _shell[at]!.Value, bodyEnd - requested);
                return (requested, owner.Pinned(new ContinuedRange(requested - bodyStart + 1, requested - bodyStart + shown, owner.Body.Length)));
            }

            return (requested, []);
        }

        /// <summary>The first window that shows the document's last row (spec 14.3), not the last row alone.</summary>
        private int MaxOffset()
        {
            for (var candidate = Math.Max(0, _rows.Count - _window); candidate < _rows.Count; candidate++)
            {
                var (resolved, pin) = Resolve(candidate);
                if (resolved != candidate || candidate + _window - pin.Length < _rows.Count) continue;

                return candidate;
            }

            return 0;
        }

        /// <summary>The screen whose window starts at <paramref name="offset"/> (snapped as <see cref="Resolve"/> says).</summary>
        internal Screen At(int offset)
        {
            if (_tooSmall is { } small) return new Screen(small, drawsContent: false, 0, _rows.Count, _minimum, _passageStarts, 0, []);

            var (top, pinned) = Resolve(Math.Clamp(offset, 0, Math.Max(0, _rows.Count - 1)));

            var available = _window - pinned.Length;
            var last = Math.Min(_rows.Count, top + available);
            var rows = ImmutableArray.CreateBuilder<ScreenRow>();
            rows.AddRange(_title);
            rows.AddRange(_warning);
            rows.AddRange(_marking);
            rows.AddRange(pinned);
            for (var i = top; i < last; i++) rows.Add(_rows[i]);
            rows.Add(StatusRow(_rows, top, last, _width));

            return new Screen(rows.ToImmutable(), drawsContent: true, top, _rows.Count, _minimum, _passageStarts, _maxOffset, _gaps);
        }
    }

    private static Document Prepare(List<Block> full, List<Block> compact, ScreenSize size, int contentRows, string? firstPostId)
    {
        var title = Chrome(Title, size.Width);
        var warning = Chrome(Provenance.StandardWarning, size.Width);
        var marking = Chrome(MarkingRow, size.Width);
        var chrome = title.Length + warning.Length + marking.Length + 1;

        // The minimum is the compact form's; the full form is used where the window holds it with the content rows (spec 14.1).
        var minimum = chrome + Tallest(compact) + contentRows;
        var blocks = size.Height >= chrome + Tallest(full) + contentRows ? full : compact;

        var document = new List<ScreenRow>();
        var starts = new List<int>();
        foreach (var block in blocks)
        {
            starts.Add(document.Count);
            document.AddRange(block.Header);
            document.AddRange(block.Body);
            if (block.End is not null) document.Add(block.End);
        }

        var fits = size.Width >= MinimumWidth && size.Height >= minimum && blocks.Count > 0;
        if (!fits)
            return new Document(blocks, document, starts, [], (title, warning, marking), TooSmall(size, title, firstPostId, blocks.Count > 0, minimum), minimum, 0, size.Width);

        var window = size.Height - chrome;

        // The offsets no window starts at: inside a header, or inside a body that is not content. A window asked to start
        // there starts at the passage's first row, so a scroll by rows must hop over the gap or it would never leave it.
        var gaps = ImmutableArray.CreateBuilder<(int From, int To)>();
        for (var i = 0; i < blocks.Count; i++)
        {
            var bodyStart = starts[i] + blocks[i].Header.Length;
            var to = blocks[i].BodyIsContent ? bodyStart : bodyStart + blocks[i].Body.Length;
            if (to > starts[i] + 1) gaps.Add((starts[i] + 1, to));
        }

        return new Document(blocks, document, starts, gaps.ToImmutable(), (title, warning, marking), null, minimum, window, size.Width);
    }

    /// <summary>The tallest header this document can put above a window: a passage's own, or the pinned form of it.</summary>
    private static int Tallest(List<Block> blocks)
    {
        var tallest = 0;
        foreach (var block in blocks)
        {
            tallest = Math.Max(tallest, block.Header.Length);
            if (block.Pinned is not null) tallest = Math.Max(tallest, block.Pinned(Widest(block)).Length);
        }

        return tallest;
    }

    /// <summary>A range whose numbers are as wide as any this block can write, so its header is as tall as any it can have.</summary>
    private static ContinuedRange Widest(Block block) => new(block.Body.Length, block.Body.Length, block.Body.Length);

    private static ImmutableArray<ScreenRow> TooSmall(ScreenSize size, ScreenRow.Frame[] title, string? firstPostId, bool any, int minimum)
    {
        var rows = ImmutableArray.CreateBuilder<ScreenRow>();
        rows.AddRange(title);
        var command = firstPostId is null ? new OwnText("curia read") : Hints.Read(firstPostId);
        var sentence = any
            ? new FrameBuilder().Append($"this window is too small to draw content under its header: it needs at least {minimum} rows and {MinimumWidth} columns at this width. Make it larger, or use the line form: {command}").ToString()
            : "there is nothing to show";
        foreach (var row in RowWrap.Frame(sentence, Math.Max(1, size.Width))) rows.Add(new ScreenRow.Frame(row));
        rows.Add(new ScreenRow.Frame(Truncate("q quits", size.Width - 1)));
        return rows.ToImmutable();
    }

    private static ScreenRow.Frame StatusRow(List<ScreenRow> document, int top, int last, int width)
    {
        var wide = false;
        for (var i = top; i < last; i++) wide |= CellWidth.HasWide(document[i].Text);

        var position = last > top
            ? string.Create(CultureInfo.InvariantCulture, $"rows {top + 1}-{last} of {document.Count}")
            : string.Create(CultureInfo.InvariantCulture, $"row {Math.Min(top + 1, Math.Max(1, document.Count))} of {document.Count}");
        return new ScreenRow.Frame(StatusText(position, wide, width));
    }

    /// <summary>
    /// The status row's text at <paramref name="width"/> columns (spec 14.2): the position, <c>q quits</c>, the notice that wide
    /// characters are in view, and <c>? help</c>. Parts are given up whole, the notice shortening first and then
    /// <c>? help</c> and the notice going; <c>q quits</c> is the one part that is never given up.
    /// </summary>
    internal static string StatusText(string position, bool wide, int width)
    {
        string[] candidates = wide
            ? [
                $"{position}  q quits  {WideOnScreen}  ? help", $"{position}  q quits  {WideShort}  ? help", $"{position}  q quits  {WideShort}",
                $"{position}  q quits", "q quits",
            ]
            : [$"{position}  q quits  ? help", $"{position}  q quits", "q quits"];

        foreach (var candidate in candidates)
        {
            if (CellWidth.Of(candidate) <= width - 1) return candidate;
        }

        return Truncate("q quits", width - 1);
    }

    private static string Truncate(string text, int width)
    {
        var limit = Math.Max(1, width);
        if (CellWidth.Of(text) <= limit) return text;

        var cut = new System.Text.StringBuilder();
        var used = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            used += CellWidth.Of(rune);
            if (used > limit) break;
            cut.Append(rune.ToString());
        }

        return cut.ToString();
    }

    internal sealed record Block(
        ImmutableArray<ScreenRow> Header,
        ImmutableArray<ScreenRow> Body,
        bool BodyIsContent,
        ScreenRow? End,
        Func<ContinuedRange, ImmutableArray<ScreenRow>>? Pinned);
}
