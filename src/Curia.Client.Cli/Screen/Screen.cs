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
/// <see cref="MinimumContentRows"/> rows besides. Otherwise only this client's rows are drawn, saying so, with the line
/// form's command.</para>
/// </summary>
internal sealed class Screen
{
    /// <summary>Narrower than this, the status row and the header's rows cannot be laid out honestly.</summary>
    internal const int MinimumWidth = 72;

    /// <summary>R10.72's eight rows of content a window must leave after the chrome and the tallest header.</summary>
    internal const int MinimumContentRows = 8;

    internal const string Title = "curia browse: a screen for a person at a terminal. The line form, for a script or a model, is the matching curia verb or --plain";

    internal const string MarkingRow = "content shown here is laid out from the signed bytes and is not datamarked: for a model, use curia read";

    internal const string WideOnScreen = "wide characters on screen: a row may be cut at the right edge";

    private Screen(ImmutableArray<ScreenRow> rows, bool drawsContent, int offset, int documentRows, int minimumHeight)
    {
        Rows = rows;
        DrawsContent = drawsContent;
        Offset = offset;
        DocumentRows = documentRows;
        MinimumHeight = minimumHeight;
    }

    /// <summary>Every row, top to bottom: the title, the warning, the marking row, the window, the status row last.</summary>
    internal ImmutableArray<ScreenRow> Rows { get; }

    /// <summary>Whether the window has room for content under the headers it shows.</summary>
    internal bool DrawsContent { get; }

    /// <summary>The first row of the document the window shows, after snapping.</summary>
    internal int Offset { get; }

    /// <summary>How many rows the whole document has.</summary>
    internal int DocumentRows { get; }

    /// <summary>The fewest rows at which this document, at this width, is drawn with content.</summary>
    internal int MinimumHeight { get; }

    /// <summary>The row drawn last.</summary>
    internal ScreenRow Status => Rows[^1];

    /// <summary>A thread or a single post: each passage under its header.</summary>
    internal static Screen ForPassages(IReadOnlyList<Passage> passages, ScreenSize size, int offset)
    {
        ArgumentNullException.ThrowIfNull(passages);

        var blocks = new List<Block>(passages.Count);
        for (var i = 0; i < passages.Count; i++)
        {
            var passage = passages[i];
            var number = i + 1;
            var draws = passage.SignatureOutcome is CheckOutcome.Verified or CheckOutcome.Disputed
                && SpanCheck.CoveredForm(passage.Post) is not null;

            ImmutableArray<ScreenRow> body = draws
                ? ContentLayout.Rows(SpanCheck.CoveredForm(passage.Post)!, size.Width)
                : [new ScreenRow.Frame(NoContent(passage, size.Width))];

            blocks.Add(new Block(
                PassageHeader.Rows(passage, number, size.Width),
                body,
                draws,
                new ScreenRow.Frame($"end of #{number.ToString(CultureInfo.InvariantCulture)}"),
                draws ? range => PassageHeader.Rows(passage, number, size.Width, range) : null));
        }

        return Compose(blocks, size, offset, MinimumContentRows, firstPostId: passages.Count > 0 ? passages[0].Post.PostId : null);
    }

    /// <summary>A board: one index row per post, then its title as content, or why there is none (spec 4.4).</summary>
    internal static Screen ForBoard(IReadOnlyList<Passage> passages, ScreenSize size, int offset)
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

        return Compose(blocks, size, offset, 1, firstPostId: passages.Count > 0 ? passages[0].Post.PostId : null);
    }

    /// <summary>The fewest rows a screen of <paramref name="passages"/> is drawn with content at, at <paramref name="width"/> columns.</summary>
    internal static int MinimumHeightFor(IReadOnlyList<Passage> passages, int width) =>
        ForPassages(passages, new ScreenSize(width, 1000), 0).MinimumHeight;

    private static ScreenRow.Frame[] Chrome(string text, int width) =>
        [.. RowWrap.Frame(text, width).Select(row => new ScreenRow.Frame(row))];

    private static string NoContent(Passage passage, int width)
    {
        _ = width;
        var outcome = passage.SignatureOutcome is CheckOutcome.CouldNotCheck
            ? "the signature could not be checked"
            : "the signature did not verify";
        return new FrameBuilder()
            .Append($"no content is shown: {new OwnText(outcome)}, so nothing it would cover is shown (R10.71). The Forum's span, under its own failing line, is in: {Hints.Read(passage.Post.PostId)}")
            .ToString();
    }

    private static Screen Compose(List<Block> blocks, ScreenSize size, int offset, int contentRows, string? firstPostId)
    {
        var title = Chrome(Title, size.Width);
        var warning = Chrome(Provenance.StandardWarning, size.Width);
        var marking = Chrome(MarkingRow, size.Width);
        var chrome = title.Length + warning.Length + marking.Length + 1;

        // The tallest header this document can put above a window: its own, or the pinned form of it.
        var tallest = 0;
        foreach (var block in blocks)
        {
            tallest = Math.Max(tallest, block.Header.Length);
            if (block.Pinned is not null) tallest = Math.Max(tallest, block.Pinned(Widest(block)).Length);
        }

        var minimum = chrome + tallest + contentRows;
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
            return new Screen(TooSmall(size, title, firstPostId, blocks.Count > 0), drawsContent: false, 0, document.Count, minimum);

        var top = Math.Clamp(offset, 0, Math.Max(0, document.Count - 1));
        var at = starts.FindLastIndex(start => start <= top);
        var owner = blocks[at];
        var bodyStart = starts[at] + owner.Header.Length;
        var bodyEnd = bodyStart + owner.Body.Length;

        ImmutableArray<ScreenRow> pinned = [];
        if (top < bodyStart || (top < bodyEnd && !owner.BodyIsContent))
        {
            top = starts[at];
        }
        else if (top < bodyEnd && owner.Pinned is not null)
        {
            var window = size.Height - chrome;
            var shell = owner.Pinned(Widest(owner));
            var shown = Math.Min(window - shell.Length, bodyEnd - top);
            pinned = owner.Pinned(new ContinuedRange(top - bodyStart + 1, top - bodyStart + shown, owner.Body.Length));
        }

        var available = size.Height - chrome - pinned.Length;
        var last = Math.Min(document.Count, top + available);
        var rows = ImmutableArray.CreateBuilder<ScreenRow>();
        rows.AddRange(title);
        rows.AddRange(warning);
        rows.AddRange(marking);
        rows.AddRange(pinned);
        for (var i = top; i < last; i++) rows.Add(document[i]);
        rows.Add(StatusRow(document, top, last, size.Width));

        return new Screen(rows.ToImmutable(), drawsContent: true, top, document.Count, minimum);
    }

    /// <summary>A range whose numbers are as wide as any this block can write, so its header is as tall as any it can have.</summary>
    private static ContinuedRange Widest(Block block) => new(block.Body.Length, block.Body.Length, block.Body.Length);

    private static ImmutableArray<ScreenRow> TooSmall(ScreenSize size, ScreenRow.Frame[] title, string? firstPostId, bool any)
    {
        var rows = ImmutableArray.CreateBuilder<ScreenRow>();
        rows.AddRange(title);
        var command = firstPostId is null ? new OwnText("curia read") : Hints.Read(firstPostId);
        var sentence = any
            ? new FrameBuilder().Append($"this window is too small to draw content under its header: make it larger, or use the line form: {command}").ToString()
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
        var text = wide ? position + "  " + WideOnScreen : position + "  q quits  ? help";
        if (CellWidth.Of(text) > width - 1) text = wide ? position + "  wide characters: a row may be cut" : position;
        return new ScreenRow.Frame(Truncate(text, width - 1));
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

    private sealed record Block(
        ImmutableArray<ScreenRow> Header,
        ImmutableArray<ScreenRow> Body,
        bool BodyIsContent,
        ScreenRow? End,
        Func<ContinuedRange, ImmutableArray<ScreenRow>>? Pinned);
}
