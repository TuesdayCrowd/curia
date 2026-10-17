using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Curia.Canon.Json;
using Curia.Client;
using Curia.Client.Cli;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Curia.Domain.Serving;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R10.72 and R10.73 (errata G22), the terminal layer: what the screen writes, byte for byte, and what a terminal does with
/// it. The model's rows are <see cref="ScreenModelTests"/>'s; these facts are about the vocabulary, the order, and the
/// cells that end up on a display, against <see cref="TestTerminal"/>, which throws on any sequence outside the vocabulary.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class BrowseTerminalTests
{
    private static readonly string[] HostileLines =
    [
        "plain prose that wraps if it is long enough, and it is, to need more than one row at the narrowest width",
        "tab\there\tand\tthere",
        "\t\t\tx",
        "csi \u009b31m inside a line",
        " 3| a row that looks like the reader's gutter",
        "esc \u001b[2J inside a line",
        "e" + new string('\u0301', 40),
        "private use \uE000 and unassigned \u0378 and joiner \u200D and bidi \u202E and separator \u2028",
        "astral \U000E0041 tag and \U0001F600 emoji and wide \u4E2D\u6587",
        "",
    ];

    [GeneratedRegex("\u001b\\[[0-9]+;[0-9]+H")]
    private static partial Regex AddressPattern();

    [GeneratedRegex("\u001b\\[([0-9]+);[0-9]+H(?<rest>.*?)\u001b\\[K", RegexOptions.Singleline)]
    private static partial Regex RowWritePattern();

    // ---- the vocabulary --------------------------------------------------------------------

    [Fact]
    public void R10_73_EveryEscapeTheScreenWritesBeginsAVocabularyConstantOrAnAddress()
    {
        var vocabulary = Vocabulary();
        var stream = new StringBuilder(ScreenWriter.Enter().Text);
        foreach (var (screen, size) in Screens())
            stream.Append(ScreenWriter.Serialize(screen, size).Text);
        stream.Append(ScreenWriter.Leave().Text);
        var bytes = stream.ToString();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var addresses = 0;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] != '\u001b') continue;

            var tail = bytes[i..];
            var address = AddressPattern().Match(tail);
            if (address.Success && address.Index == 0)
            {
                addresses++;
                continue;
            }

            var match = vocabulary.Where(v => tail.StartsWith(v, StringComparison.Ordinal)).OrderByDescending(v => v.Length).FirstOrDefault();
            Assert.True(match is not null, $"an escape that begins no vocabulary constant and is no address: {DisplayLiteral.Of(tail[..Math.Min(12, tail.Length)])}");
            seen.Add(match);
            i += match.Length - 1;
        }

        Assert.True(addresses > 100, $"only {addresses} addresses were written");
        foreach (var expected in new[] { Vt.Plain, Vt.EraseToEndOfRow, Vt.Bold, Vt.Reverse, Vt.AlternateScreenOn, Vt.AlternateScreenOff, Vt.WrapOff, Vt.WrapOn, Vt.Clear, Vt.Home, Vt.CursorHidden, Vt.CursorShown, Vt.KeypadOff })
            Assert.Contains(expected, seen);
    }

    [Fact]
    public void R10_73_TheDecodedStreamHoldsNoC1AndNoControlCharacterButEscape()
    {
        var stream = new StringBuilder(ScreenWriter.Enter().Text);
        foreach (var (screen, size) in Screens()) stream.Append(ScreenWriter.Serialize(screen, size).Text);
        stream.Append(ScreenWriter.Leave().Text);

        // The bytes a terminal reads: UTF-8, decoded back. A C1 character is two bytes (C2 80 to C2 9F); under an 8-bit
        // charset those bytes are a CSI or an OSC, and under UTF-8 they are this.
        var decoded = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(stream.ToString()));
        Assert.Equal(stream.ToString(), decoded);

        var controls = 0;
        foreach (var rune in decoded.EnumerateRunes())
        {
            Assert.False(rune.Value is >= 0x80 and <= 0x9F, $"a C1 control in the stream: U+{rune.Value:X4}");
            if (rune.Value < 0x20 || rune.Value == 0x7F)
            {
                Assert.Equal(0x1B, rune.Value);
                controls++;
            }
        }

        Assert.True(controls > 0, "the stream held no escape: the fact is vacuous");
    }

    [Fact]
    public void R10_73_EnterAndLeaveAreTheClosedSequencesTheDesignNames()
    {
        Assert.Equal(Vt.AlternateScreenOn + Vt.WrapOff + Vt.CursorHidden + Vt.Clear + Vt.Home, ScreenWriter.Enter().Text);
        Assert.Equal(
            Vt.Plain + Vt.Home + Vt.Clear + Vt.WrapOn + Vt.CursorShown + Vt.KeypadOff + Vt.AlternateScreenOff,
            ScreenWriter.Leave().Text);
        Assert.True(ScreenWriter.Leave().Leaves);
        Assert.False(ScreenWriter.Enter().Leaves);
    }

    // ---- the order -------------------------------------------------------------------------

    [Fact]
    public void R10_72_TheStreamIsTopToBottomThenEveryFrameRowAgainThenTheStatusRowLast()
    {
        var passages = new[] { Hand(["first line", "second line", "third line"]), Hand(["another passage", "with two lines"]) };
        var size = new ScreenSize(100, Screen.MinimumHeightFor(passages, 100) + 4);
        var screen = Screen.ForPassages(passages, size, 0);
        Assert.Contains(screen.Rows, r => r is ScreenRow.Content);

        var writes = RowWritePattern().Matches(ScreenWriter.Serialize(screen, size).Text)
            .Select(m => (Row: int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), Text: Strip(m.Groups["rest"].Value)))
            .ToArray();

        // Title first.
        Assert.StartsWith("curia browse:", writes[0].Text, StringComparison.Ordinal);
        Assert.Equal(1, writes[0].Row);

        // The standing warning is written before the first content row's address.
        var firstContent = Array.FindIndex(writes, w => ScreenRow.BeginsWithGutter(w.Text));
        var warning = Array.FindIndex(writes, w => w.Text.StartsWith(Provenance.StandardWarning[..30], StringComparison.Ordinal));
        Assert.True(firstContent > 0 && warning >= 0 && warning < firstContent, $"the warning at {warning}, the first content row at {firstContent}");

        // Every frame row in view is addressed again after the last content row, in order, and the status row is last.
        var lastContent = Array.FindLastIndex(writes, w => ScreenRow.BeginsWithGutter(w.Text));
        var frameRows = screen.Rows.Take(screen.Rows.Length - 1).Where(r => r is ScreenRow.Frame).Select(r => r.Text).ToArray();
        Assert.True(lastContent >= 0 && writes.Length >= frameRows.Length + 1);
        var again = writes[^(frameRows.Length + 1)..].Select(w => w.Text).ToArray();
        Assert.Equal(frameRows, again[..^1]);
        Assert.Equal(screen.Status.Text, again[^1]);
        Assert.Equal(size.Height, writes[^1].Row);
    }

    // ---- what a terminal does with it ------------------------------------------------------

    [Theory]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    public void R10_72_NothingScrollsAndEveryRowBeginsAsDrawn(int nonAsciiCells, bool ignoresAlternateScreen)
    {
        var shown = 0;
        foreach (var width in new[] { 72, 80, 100 })
        {
            foreach (var (screen, size) in Screens(width))
            {
                var terminal = new TestTerminal(width, size.Height, nonAsciiCells, ignoresAlternateScreen);
                terminal.Feed(ScreenWriter.Enter().Text);
                terminal.Feed(ScreenWriter.Serialize(screen, size).Text);

                Assert.Equal(0, terminal.Scrolls);
                Assert.False(terminal.WrapOn);
                for (var i = 0; i < screen.Rows.Length; i++)
                {
                    var at = i == screen.Rows.Length - 1 ? size.Height - 1 : i;
                    var drawn = screen.Rows[i].Text;
                    var ascii = new string(drawn.TakeWhile(c => c is >= ' ' and <= '~').ToArray());
                    Assert.True(
                        terminal.RowText(at).StartsWith(ascii.TrimEnd(' '), StringComparison.Ordinal),
                        $"row {i} drawn as {DisplayLiteral.Of(drawn)} stands as {DisplayLiteral.Of(terminal.RowText(at))}");
                }

                shown++;
            }
        }

        Assert.True(shown > 10, $"only {shown} screens were drawn");
    }

    [Fact]
    public void R10_72_ATabCannotLeaveAnEarlierFramesTextStandingInARow()
    {
        var width = 100;
        var size = new ScreenSize(width, 30);
        var passage = Hand(["\t\t\tx"]);
        var content = Screen.ForPassages([passage], size, 0);
        var contentRow = Array.FindIndex([.. content.Rows], r => r is ScreenRow.Content);
        Assert.True(contentRow > 0, "the screen has no content row");

        // Frame one is a row of this client's at that very row, as the board's index writes one.
        var earlier = Enumerable.Range(0, content.Rows.Length)
            .Select(i => (ScreenRow)new ScreenRow.Frame(i == contentRow ? "#9 post SIGNATURE-VERIFIED" : string.Empty))
            .ToImmutableArray();

        var terminal = new TestTerminal(width, size.Height);
        terminal.Feed(ScreenWriter.Enter().Text);
        terminal.Feed(ScreenWriter.Serialize(ScreenFrame.Of(earlier, size.Height)).Text);
        Assert.Contains("SIGNATURE-VERIFIED", terminal.RowText(contentRow), StringComparison.Ordinal);

        terminal.Feed(ScreenWriter.Serialize(content, size).Text);

        Assert.DoesNotContain("SIGNATURE", terminal.RowText(contentRow), StringComparison.Ordinal);
        Assert.EndsWith("x", terminal.RowText(contentRow), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void R10_73_NothingIsLeftOnTheTerminalAfterLeavingEvenWhereTheAlternateScreenIsIgnored(bool ignoresAlternateScreen)
    {
        var (screen, size) = Screens(100).First();
        var terminal = new TestTerminal(size.Width, size.Height, ignoresAlternateScreen: ignoresAlternateScreen);

        terminal.Feed(ScreenWriter.Enter().Text);
        terminal.Feed(ScreenWriter.Serialize(screen, size).Text);
        Assert.False(terminal.IsBlank, "nothing was drawn: the fact is vacuous");
        Assert.False(terminal.CursorVisible);
        Assert.False(terminal.WrapOn);

        terminal.Feed(ScreenWriter.Leave().Text);

        Assert.True(terminal.IsBlank);
        Assert.True(terminal.CursorVisible);
        Assert.True(terminal.WrapOn);
        Assert.False(terminal.AlternateScreenActive);
    }

    [Fact]
    public void R10_72_NoAttributeIsCarriedIntoAContentRow()
    {
        var content = 0;
        var attributed = 0;

        foreach (var (screen, size) in Screens(100))
        {
            var terminal = new TestTerminal(size.Width, size.Height);
            terminal.Feed(ScreenWriter.Enter().Text);
            terminal.Feed(ScreenWriter.Serialize(screen, size).Text);

            for (var i = 0; i < screen.Rows.Length - 1; i++)
            {
                if (screen.Rows[i] is ScreenRow.Content)
                {
                    content++;
                    Assert.False(terminal.RowTextCarriesAttributes(i), $"content row {i} carries an attribute: {DisplayLiteral.Of(terminal.RowText(i))}");
                }
                else if (terminal.RowTextCarriesAttributes(i))
                {
                    attributed++;
                }
            }
        }

        Assert.True(content > 20, $"only {content} content rows were checked");
        Assert.True(attributed > 0, "no frame row carried an attribute: the fact cannot tell a carried one from none");
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static string[] Vocabulary() =>
    [
        .. typeof(Vt).GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!),
    ];

    private static string Strip(string text)
    {
        foreach (var constant in new[] { Vt.Plain, Vt.Bold, Vt.Dim, Vt.Reverse }) text = text.Replace(constant, string.Empty, StringComparison.Ordinal);
        return text;
    }

    private static IEnumerable<(Screen Screen, ScreenSize Size)> Screens(int width = 100)
    {
        var lines = Enumerable.Range(0, 80).Select(i => HostileLines[i % HostileLines.Length] + " " + i.ToString(CultureInfo.InvariantCulture)).ToArray();
        var passages = new[] { Hand(lines), Hand(lines.Take(6).ToArray(), "https://agents.example/" + new string('b', 120)) };
        var minimum = Screen.MinimumHeightFor(passages, width);

        foreach (var offset in new[] { 0, 3, 17, 60, 120, 400 })
        {
            foreach (var height in new[] { minimum, minimum + 3, 40 })
            {
                var size = new ScreenSize(width, Math.Max(height, minimum));
                yield return (Screen.ForPassages(passages, size, offset), size);
            }
        }

        var board = new ScreenSize(width, 30);
        yield return (Screen.ForBoard(passages, board, 0), board);
        var small = new ScreenSize(width, 12);
        yield return (Screen.ForPassages(passages, small, 0), small);
    }

    private static Passage Hand(string[] lines, string author = "https://agents.example/alice")
    {
        var canonical = SpanCheckTests.Envelope(string.Join("\n", lines));
        var post = SpanCheckTests.Post(canonical, Datamarking.Render(canonical, MarkingMode.None), MarkingMode.None);
        post = post with { Provenance = post.Provenance with { Author = author }, LogIndex = 7 };
        return new Passage(post, new SignatureVerdict(true, "alice-1", "recanonicalized bytes are byte-identical to the served canonical form", "ab", "sha256:ab"));
    }
}
