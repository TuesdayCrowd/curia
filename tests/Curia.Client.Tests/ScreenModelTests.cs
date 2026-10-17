using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CsCheck;
using Curia.Canon.Json;
using Curia.Client;
using Curia.Client.Cli;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Curia.Domain.Serving;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R10.72 to R10.75 (errata G22), the screen model: pure rows from passages, a size and an offset, no terminal. Every fact
/// is about <see cref="Screen"/>, <see cref="ContentLayout"/>, <see cref="PassageHeader"/> or <see cref="CellWidth"/>.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ScreenModelTests
{
    private static readonly Regex GutterPattern = new(@"^ *[0-9]+[|+] ", RegexOptions.CultureInvariant);

    /// <summary>Lines a hostile post would hold: every one a terminal, a wrap or the gutter's own form could trip on.</summary>
    private static readonly string[] HostileLines =
    [
        "plain prose that wraps if it is long enough, and it is, to need more than one row at the narrowest width",
        "tab\there\tand\tthere",
        "\tleading tab",
        "csi \u009b31m inside a line",
        " 3| a row that looks like the reader's gutter",
        " 3+ a continuation that looks like the reader's gutter",
        "e" + new string('\u0301', 40),
        "\u0301 a mark with no base at the start of a line",
        "private use \uE000 and unassigned \u0378 and joiner \u200D and bidi \u202E and separator \u2028",
        "astral \U000E0041 tag and \U0001F600 emoji and wide \u4E2D\u6587",
        "\U0001D400" + new string('\u0301', 6) + " astral base",
        "",
        "   indented with spaces",
    ];

    // ---- the rows --------------------------------------------------------------------------

    [Fact]
    public void R10_72_EveryRowBeginsWithTheReadersTextAndEveryContentRowWithItsGutter()
    {
        var content = 0;
        var continuation = 0;
        var screens = 0;

        Gen.Int[0, HostileLines.Length - 1].List[1, 40].Select(Gen.Int[72, 130], Gen.Int[0, 3]).Sample(
            (picks, width, authorPad) =>
            {
                var lines = picks.Select(i => HostileLines[i]).ToArray();
                var passages = new[] { Hand(lines, author: new string('a', 1 + (authorPad * 100))), Hand(lines.Reverse().ToArray()) };

                foreach (var screen in Sweep(passages, width, extraHeights: 6))
                {
                    screens++;
                    foreach (var row in screen.Rows)
                    {
                        AssertRowHasASide(row, width);
                        if (row is ScreenRow.Content c)
                        {
                            content++;
                            if (c.Continuation) continuation++;
                        }
                    }
                }
            },
            iter: 40);

        Assert.True(screens > 0 && content > 0, "no content row was drawn: the property is vacuous");
        Assert.True(continuation > 0, "no wrapped continuation row was drawn: the property is vacuous");
    }

    [Fact]
    public void R10_72_ThreeThousandHostileLinesKeepEveryRowOnItsSide()
    {
        var lines = Enumerable.Range(0, 3000).Select(i => HostileLines[i % HostileLines.Length] + " " + i.ToString(CultureInfo.InvariantCulture)).ToArray();
        var passage = Hand(lines, author: new string('a', 300));

        var count = 0;
        foreach (var screen in Sweep([passage], 80, extraHeights: 2, offsets: [0, 1, 17, 500, 4000, 9000]))
        {
            foreach (var row in screen.Rows)
            {
                AssertRowHasASide(row, 80);
                if (row is ScreenRow.Content) count++;
            }
        }

        Assert.True(count > 100, $"only {count} content rows were drawn");
    }

    [Fact]
    public void R10_72_EveryScreenHoldingAPassagesContentHoldsItsHeaderAndTheWarning()
    {
        var lines = Enumerable.Range(0, 60).Select(i => "line " + i.ToString(CultureInfo.InvariantCulture)).ToArray();
        var passages = new[] { Hand(lines), Hand(lines.Take(5).ToArray()) };
        var pinned = 0;
        var ends = 0;
        var withContent = 0;

        foreach (var screen in Sweep(passages, 80, extraHeights: 5))
        {
            if (!screen.Rows.Any(r => r is ScreenRow.Content)) continue;
            withContent++;

            var text = string.Concat(screen.Rows.Where(r => r is ScreenRow.Frame).Select(r => r.Text + "\n"));
            Assert.Contains(Provenance.StandardWarning[..40], text, StringComparison.Ordinal);

            for (var i = 0; i < screen.Rows.Length; i++)
            {
                if (screen.Rows[i] is not ScreenRow.Content) continue;

                // The nearest frame row above the content, with no end row between, is a header's first row.
                var j = i;
                while (j >= 0 && screen.Rows[j] is ScreenRow.Content) j--;
                Assert.True(j >= 0 && Regex.IsMatch(screen.Rows[j].Text, @"^#\d+ post |^  > |^[a-z]", RegexOptions.CultureInvariant), $"content with no frame row above it at {i}");
                var h1 = Enumerable.Range(0, j + 1).Reverse().First(k => screen.Rows[k] is ScreenRow.Frame f && Regex.IsMatch(f.Row, @"^#\d+ post ", RegexOptions.CultureInvariant));
                Assert.DoesNotContain(Enumerable.Range(h1, j - h1 + 1), k => screen.Rows[k].Text.StartsWith("end of #", StringComparison.Ordinal));
            }

            // The first row of the window is never content: a header (pinned and continued, or the passage's own) precedes it.
            var first = screen.Rows.Select((r, i) => (r, i)).First(x => x.r is ScreenRow.Content).i;
            Assert.Contains(screen.Rows.Take(first), r => Regex.IsMatch(r.Text, @"^#\d+ post ", RegexOptions.CultureInvariant));
            if (screen.Offset > 0 && screen.Rows.Take(first).Any(r => r.Text.Contains("(continued:", StringComparison.Ordinal))) pinned++;

            for (var i = 0; i + 1 < screen.Rows.Length; i++)
            {
                if (screen.Rows[i] is ScreenRow.Content && screen.Rows[i + 1] is ScreenRow.Frame next && next != screen.Status)
                {
                    Assert.StartsWith("end of #", next.Text, StringComparison.Ordinal);
                    ends++;
                }
            }
        }

        Assert.True(withContent > 0 && pinned > 0, "no pinned header was drawn: the property is vacuous");
        Assert.True(ends > 0, "no passage's end was drawn in view");
    }

    [Fact]
    public void R10_72_AtEverySizeAboveTheMinimumEightContentRowsFit()
    {
        var lines = Enumerable.Range(0, 120).Select(i => "line " + i.ToString(CultureInfo.InvariantCulture)).ToArray();
        var passage = Hand(lines, author: new string('b', 150));
        const int width = 80;

        var minimum = Screen.MinimumHeightFor([passage], width);
        var total = ContentLayout.Rows(SpanCheck.CoveredForm(passage.Post)!, width).Length;
        var header = PassageHeader.Rows(passage, 1, width).Length;

        for (var height = minimum; height < minimum + 12; height++)
        {
            for (var offset = 0; offset < header + total; offset += 7)
            {
                var screen = Screen.ForPassages([passage], new ScreenSize(width, height), offset);
                var remaining = total - Math.Max(0, screen.Offset - header);
                var drawn = screen.Rows.Count(r => r is ScreenRow.Content);
                Assert.True(drawn >= Math.Min(8, remaining), $"{drawn} content rows at height {height} offset {offset}, {remaining} left");
                Assert.True(screen.Rows.Length <= height, $"{screen.Rows.Length} rows at height {height}");
            }
        }

        for (var height = 4; height < minimum; height++)
        {
            var small = Screen.ForPassages([passage], new ScreenSize(width, height), 0);
            Assert.DoesNotContain(small.Rows, r => r is ScreenRow.Content);
            Assert.Contains(small.Rows, r => r.Text.Contains("too small", StringComparison.Ordinal));
        }

        var narrow = Screen.ForPassages([passage], new ScreenSize(Screen.MinimumWidth - 1, 60), 0);
        Assert.DoesNotContain(narrow.Rows, r => r is ScreenRow.Content);
    }

    [Fact]
    public void R10_72_ALiteralIsNeverCut()
    {
        var shared = new string('x', 70);
        var first = shared + new string('p', 40);
        var second = shared + new string('q', 40);
        const int width = Screen.MinimumWidth;

        string Joined(string author)
        {
            var rows = PassageHeader.Rows(Hand(["a line"], author: author), 1, width);
            Assert.All(rows, r => Assert.True(CellWidth.Of(r.Text) <= width, r.Text));
            return string.Concat(rows.Select(r => r.Text.StartsWith(RowWrap.ContinuationPrefix, StringComparison.Ordinal) ? r.Text[RowWrap.ContinuationPrefix.Length..] : "\n" + r.Text));
        }

        var one = Joined(first);
        var two = Joined(second);
        Assert.NotEqual(one, two);
        Assert.Contains(DisplayLiteral.Of(first), one, StringComparison.Ordinal);
        Assert.Contains(DisplayLiteral.Of(second), two, StringComparison.Ordinal);

        // A literal that fits a row is on one row, whole.
        var short40 = new string('s', 40);
        var rows40 = PassageHeader.Rows(Hand(["a line"], author: short40), 1, width);
        Assert.Contains(rows40, r => r.Text.Contains(DisplayLiteral.Of(short40), StringComparison.Ordinal));
    }

    // ---- escapes, marks, width -------------------------------------------------------------

    [Fact]
    public void R10_75_TheFifthMarkOnABaseIsAnEscape()
    {
        const string mark = "\u0301";
        const string enclosing = "\u20E3";

        Assert.Equal("a" + string.Concat(Enumerable.Repeat(mark, 4)) + "\\u0301", ContentLayout.Escape("a" + string.Concat(Enumerable.Repeat(mark, 5))));
        Assert.Equal("\U0001D400" + string.Concat(Enumerable.Repeat(mark, 4)) + "\\u0301", ContentLayout.Escape("\U0001D400" + string.Concat(Enumerable.Repeat(mark, 5))));
        Assert.Equal("a" + enclosing + mark + mark + mark, ContentLayout.Escape("a" + enclosing + mark + mark + mark));
        Assert.Equal("a" + enclosing + mark + mark + mark + "\\u20e3", ContentLayout.Escape("a" + enclosing + mark + mark + mark + enclosing));

        // The cap resets on the next base.
        Assert.Equal("a" + string.Concat(Enumerable.Repeat(mark, 4)) + "b" + string.Concat(Enumerable.Repeat(mark, 4)),
            ContentLayout.Escape("a" + string.Concat(Enumerable.Repeat(mark, 4)) + "b" + string.Concat(Enumerable.Repeat(mark, 4))));

        // A base and its marks stay on one row at the wrap width, however the width falls.
        var text = "xx" + "a" + string.Concat(Enumerable.Repeat(mark, 4)) + "yy";
        for (var width = 1; width < 20; width++)
        {
            var rows = ContentLayout.Wrap(text, width);
            Assert.Equal(text, string.Concat(rows));
            Assert.All(rows, r => Assert.False(r.Length > 0 && IsMark(Rune.GetRuneAt(r, 0)), $"a row begins with a mark at width {width}"));
            Assert.Contains(rows, r => r.Contains("a" + string.Concat(Enumerable.Repeat(mark, 4)), StringComparison.Ordinal));
        }
    }

    [Fact]
    public void R10_75_WhatATerminalActsOnIsAnEscapeAndATabIsExpanded()
    {
        Assert.Equal("a       b", ContentLayout.Escape("a\tb"));
        Assert.Equal("        x", ContentLayout.Escape("\tx"));
        Assert.Equal("\\u009bx", ContentLayout.Escape("\u009Bx"));
        Assert.Equal("\\ue000", ContentLayout.Escape("\uE000"));
        Assert.Equal("\\u0378", ContentLayout.Escape("\u0378"));
        Assert.Equal("\\u2028", ContentLayout.Escape("\u2028"));
        Assert.Equal("\\ud800", ContentLayout.Escape("\uD800"));
        Assert.Equal("\\udb40\\udc41", ContentLayout.Escape("\U000E0041"));
        Assert.Equal("\\u0301a", ContentLayout.Escape("\u0301a"));
        Assert.Equal("\\u0301\\u0301", ContentLayout.Escape("\u0301\u0301"));
    }

    [Fact]
    public void R10_75_TheWidthEstimateNeverUndercounts()
    {
        var scalars = 0;
        for (var value = 0; value <= 0x10FFFF; value++)
        {
            if (!Rune.IsValid(value)) continue;
            scalars++;
            var rune = new Rune(value);
            Assert.Equal(value is >= 0x20 and <= 0x7E ? 1 : 2, CellWidth.Of(rune));
        }

        Assert.True(scalars > 1_000_000);
        Assert.Equal(2 + 2, CellWidth.Of("\u4E2D\u6587"));
        Assert.Equal(2, CellWidth.Of("\U0001F600"));
        Assert.Equal(3, CellWidth.Of("abc"));
    }

    [Fact]
    public void R10_75_TheScreenLaysOutTheCanonicalFormNotTheSpan()
    {
        var canonical = SpanCheckTests.Envelope("What the author signed.");
        var other = SpanCheckTests.Envelope("zyzzyva, what the Forum would have a reader see.");
        var post = SpanCheckTests.Post(canonical, Datamarking.Render(other, MarkingMode.None), MarkingMode.None);
        var passage = new Passage(post, new SignatureVerdict(true, "k", "d", "ab", "sha256:ab"));

        Assert.False(SpanCheck.RendersCanonical(post));
        var screen = Screen.ForPassages([passage], new ScreenSize(100, 60), 0);
        var text = string.Join("\n", screen.Rows.Select(r => r.Text));

        Assert.DoesNotContain("zyzzyva", text, StringComparison.Ordinal);
        Assert.Contains("body: What the author signed.", text, StringComparison.Ordinal);
        Assert.Contains(screen.Rows, r => r.Text.StartsWith("content   FAILED:", StringComparison.Ordinal));
        Assert.Contains(screen.Rows, r => r.Text.Contains("[CONTENT FAILED]", StringComparison.Ordinal));
    }

    [Fact]
    public void R10_72_TheBoardIndexHoldsWhatPlainOutputHolds()
    {
        using var signer = new ScreenSigner();
        var titles = new[] { "Which fields of a served post are the signed bytes", "How is a key retired", "What does disputed mean" };
        var passages = titles.Select((t, i) => ScreenSigner.Passage(signer.Question(t, 10 + i, i), signer.Jwks("active"))).ToArray();

        var plain = new FrameBuilder();
        for (var i = 0; i < passages.Length; i++) TitledIndex.Write(plain, i + 1, passages[i], MarkingMode.None, null);
        var plainText = plain.ToString();

        var screen = Screen.ForBoard(passages, new ScreenSize(100, 40), 0);
        var screenText = string.Join("\n", screen.Rows.Select(r => r.Text));

        foreach (var (passage, title) in passages.Zip(titles, (p, t) => (p, t)))
        {
            var id = DisplayLiteral.Of(passage.Post.PostId);
            Assert.Contains(id, plainText, StringComparison.Ordinal);
            Assert.Contains(id, screenText, StringComparison.Ordinal);
            Assert.Contains(title, plainText, StringComparison.Ordinal);
            Assert.Contains(title, screenText, StringComparison.Ordinal);
        }

        Assert.Contains(screen.Rows, r => r is ScreenRow.Content);
    }

    // ---- the header ------------------------------------------------------------------------

    [Fact]
    public void R10_74_TheKeyRowIsAFactAndTheOutcomeWordIsThePassagesOutcome()
    {
        using var signer = new ScreenSigner();
        var jwks = signer.Jwks("compromised", compromisedAt: 40, disputedFrom: 30);
        var stands = ScreenSigner.Passage(signer.Question("t", 12, 0), jwks);
        var disputed = ScreenSigner.Passage(signer.Question("t", 35, 1), jwks);
        var failed = ScreenSigner.Passage(signer.Question("t", 41, 2), jwks);

        Assert.Equal(CheckOutcome.Verified, stands.Outcome);
        Assert.Equal(CheckOutcome.Disputed, disputed.Outcome);
        Assert.Equal(CheckOutcome.Failed, failed.Outcome);

        string Tail(string row) => row[..row.IndexOf("; this post: log index", StringComparison.Ordinal)];
        var keys = new[] { stands, disputed, failed }.Select(PassageHeader.KeyRow).ToArray();
        Assert.Single(keys.Select(Tail).Distinct());
        Assert.Equal(3, keys.Distinct().Count());
        Assert.EndsWith("log index 12", keys[0], StringComparison.Ordinal);
        Assert.EndsWith("log index 35", keys[1], StringComparison.Ordinal);
        Assert.EndsWith("log index 41", keys[2], StringComparison.Ordinal);

        AssertWordAndTag(stands, "signature verified", null);
        AssertWordAndTag(disputed, "signature DISPUTED", "[DISPUTED]");
        AssertWordAndTag(failed, "signature NOT VERIFIED", "[NOT VERIFIED]");

        var retired = signer.Jwks("retired", retiredAt: 20);
        var before = ScreenSigner.Passage(signer.Question("t", 10, 3), retired);
        var after = ScreenSigner.Passage(signer.Question("t", 25, 4), retired);
        Assert.Equal(CheckOutcome.Verified, before.Outcome);
        Assert.Equal(CheckOutcome.Failed, after.Outcome);
        AssertWordAndTag(before, "signature verified", null);
        AssertWordAndTag(after, "signature NOT VERIFIED", "[NOT VERIFIED]");
        Assert.Equal(Tail(PassageHeader.KeyRow(before)), Tail(PassageHeader.KeyRow(after)));
    }

    [Fact]
    public void R10_74_AKeySetThatStatesNoStatusIsNotShownAsActive()
    {
        using var signer = new ScreenSigner();
        var post = signer.Question("t", 5);
        var stated = PassageHeader.KeyRow(ScreenSigner.Passage(post, signer.Jwks("active")));
        var unstated = PassageHeader.KeyRow(ScreenSigner.Passage(post, signer.Jwks()));

        Assert.Contains("active (as the Forum's key set says)", stated, StringComparison.Ordinal);
        Assert.Contains(PassageHeader.NoStatusSentence, unstated, StringComparison.Ordinal);
        Assert.DoesNotContain("active", unstated, StringComparison.Ordinal);
        Assert.NotEqual(stated, unstated);
    }

    [Fact]
    public void R10_74_TheStatusTemplatesAndTheUnknownWord()
    {
        using var signer = new ScreenSigner();
        var post = signer.Question("t", 5);

        string Row(ImmutableArray<ForumJwk> jwks) => PassageHeader.KeyRow(ScreenSigner.Passage(post, jwks));

        Assert.Equal(
            "key \"alice-1\": retired at log index 50 (as the Forum's key set says); this post: log index 5",
            Row(signer.Jwks("retired", retiredAt: 50)));
        Assert.Equal(
            "key \"alice-1\": compromised, declaration at log index 40, content disputed from log index 30 (as the Forum's key set says); this post: log index 5",
            Row(signer.Jwks("compromised", compromisedAt: 40, disputedFrom: 30)));
        Assert.Equal(
            "key \"alice-1\": marked \"compromised\" (as the Forum's key set says); this post: log index 5",
            Row(signer.Jwks("compromised", compromisedAt: 40)));
        Assert.Equal(
            "key \"alice-1\": marked \"revoked\" (as the Forum's key set says); this post: log index 5",
            Row(signer.Jwks("revoked")));
        Assert.Equal(
            "key \"alice-1\": marked \"rev\\u000aoked\" (as the Forum's key set says); this post: log index 5",
            Row(signer.Jwks("rev\noked")));
        Assert.EndsWith("log index not stated", PassageHeader.KeyRow(ScreenSigner.Passage(signer.Question("t", null), signer.Jwks("active"))), StringComparison.Ordinal);
    }

    [Fact]
    public void R10_74_AFailedOrUnverifiableSignatureDrawsNoContent()
    {
        using var signer = new ScreenSigner();
        var size = new ScreenSize(100, 60);

        var donor = signer.Question("another", 5, 1);
        var forged = ScreenSigner.Passage(signer.Question("t", 5, 0) with { Signature = donor.Signature }, signer.Jwks("active"));
        var unreachable = new Passage(
            signer.Question("t", 5, 2),
            SignatureCheck.Unreachable(signer.Question("t", 5, 2), new Refusal(RefusalKind.Transport, 0, new Error("t", "t", "t"))));
        Assert.Equal(CheckOutcome.Failed, forged.Outcome);
        Assert.Equal(CheckOutcome.CouldNotCheck, unreachable.Outcome);

        foreach (var passage in new[] { forged, unreachable })
        {
            var screen = Screen.ForPassages([passage], size, 0);
            Assert.DoesNotContain(screen.Rows, r => r is ScreenRow.Content);
            Assert.Contains(screen.Rows, r => r.Text.Contains("curia read", StringComparison.Ordinal) && r.Text.Contains("no content", StringComparison.Ordinal));
        }

        var disputed = ScreenSigner.Passage(signer.Question("t", 35, 3), signer.Jwks("compromised", compromisedAt: 40, disputedFrom: 30));
        var drawn = Screen.ForPassages([disputed], size, 0);
        Assert.Contains(drawn.Rows, r => r is ScreenRow.Content);
        Assert.Contains(drawn.Rows, r => r.Text.Contains("[DISPUTED]", StringComparison.Ordinal));

        var canonical = SpanCheckTests.Envelope("The signed body.");
        var mismatched = new Passage(
            SpanCheckTests.Post(canonical, Datamarking.Render(SpanCheckTests.Envelope("Another body."), MarkingMode.None), MarkingMode.None),
            new SignatureVerdict(true, "k", "d", "ab", "sha256:ab"));
        var shown = Screen.ForPassages([mismatched], size, 0);
        Assert.Contains(shown.Rows, r => r.Text.StartsWith("content   FAILED:", StringComparison.Ordinal));
        Assert.Contains(shown.Rows, r => r is ScreenRow.Content c && c.Body.Contains("The signed body.", StringComparison.Ordinal));
    }

    [Fact]
    public void R10_74_AcceptedIsTheForumsWordAndOnlyInH6()
    {
        var accepted = Hand(["a line"]) with { Post = Hand(["a line"]).Post with { Accepted = true } };
        var plain = Hand(["a line"]);

        var withWord = PassageHeader.Rows(accepted, 1, 200).Select(r => r.Text).ToArray();
        var without = PassageHeader.Rows(plain, 1, 200).Select(r => r.Text).ToArray();

        var hits = withWord.Where(r => r.Contains("accepted", StringComparison.Ordinal)).ToArray();
        Assert.Single(hits);
        Assert.StartsWith("forum says ", hits[0], StringComparison.Ordinal);
        Assert.Contains("accepted (the Forum's word: no signature covers an acceptance)", hits[0], StringComparison.Ordinal);
        Assert.DoesNotContain(without, r => r.Contains("accepted", StringComparison.Ordinal));
    }

    [Fact]
    public void R6_57_APostOnlyTheForumMarksDisputedReadsDisputedOnTheScreen()
    {
        var passage = Hand(["a line"]);
        passage = passage with { Post = passage.Post with { Provenance = passage.Post.Provenance with { DisputedByLogIndex = 41 } } };

        var rows = PassageHeader.Rows(passage, 1, 200).Select(r => r.Text).ToArray();
        Assert.EndsWith("[DISPUTED]", rows[0], StringComparison.Ordinal);
        Assert.Contains(rows, r => r.StartsWith("signature DISPUTED:", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.StartsWith("forum says", StringComparison.Ordinal) && r.Contains("; marked disputed by log index 41", StringComparison.Ordinal));
        Assert.Contains(Screen.ForPassages([passage], new ScreenSize(100, 60), 0).Rows, r => r is ScreenRow.Content);
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static void AssertWordAndTag(Passage passage, string word, string? tag)
    {
        var rows = PassageHeader.Rows(passage, 1, 300).Select(r => r.Text).ToArray();
        Assert.Contains(rows, r => r.StartsWith(word, StringComparison.Ordinal));
        if (tag is null) Assert.DoesNotContain(rows[0], "[", StringComparison.Ordinal);
        else Assert.EndsWith(tag, rows[0], StringComparison.Ordinal);
    }

    private static bool IsMark(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark;

    /// <summary>A passage whose content is <paramref name="lines"/>, signature verdict held verified by hand.</summary>
    private static Passage Hand(string[] lines, string author = "https://agents.example/alice")
    {
        var canonical = SpanCheckTests.Envelope(string.Join("\n", lines));
        var post = SpanCheckTests.Post(canonical, Datamarking.Render(canonical, MarkingMode.None), MarkingMode.None);
        post = post with { Provenance = post.Provenance with { Author = author }, LogIndex = 7 };
        return new Passage(post, new SignatureVerdict(true, "alice-1", "recanonicalized bytes are byte-identical to the served canonical form", "ab", "sha256:ab"));
    }

    /// <summary>Every screen of <paramref name="passages"/> at sizes from the computed minimum up and a spread of offsets.</summary>
    private static IEnumerable<Screen> Sweep(Passage[] passages, int width, int extraHeights, int[]? offsets = null)
    {
        var minimum = Screen.MinimumHeightFor(passages, width);
        var total = Screen.ForPassages(passages, new ScreenSize(width, minimum), 0).DocumentRows;
        var at = offsets ?? [.. Enumerable.Range(0, 12).Select(i => i * Math.Max(1, total / 12)).Append(total - 1)];

        for (var height = minimum; height <= minimum + extraHeights; height++)
        {
            foreach (var offset in at)
            {
                var screen = Screen.ForPassages(passages, new ScreenSize(width, height), offset);
                Assert.True(screen.DrawsContent, $"minimum {minimum} did not draw content at height {height}");
                yield return screen;
            }
        }
    }

    private static void AssertRowHasASide(ScreenRow row, int width)
    {
        Assert.True(CellWidth.Of(row.Text) <= width, $"a row of {CellWidth.Of(row.Text)} cells at width {width}: {DisplayLiteral.Of(row.Text)}");

        switch (row)
        {
            case ScreenRow.Frame frame:
                Assert.False(GutterPattern.IsMatch(frame.Row), $"a frame row matches the gutter: {DisplayLiteral.Of(frame.Row)}");
                Assert.False(ScreenRow.BeginsWithGutter(frame.Row));
                break;
            case ScreenRow.Content content:
                Assert.Matches(GutterPattern, content.Text);
                Assert.True(ScreenRow.BeginsWithGutter(content.Text));
                Assert.Equal(content.Continuation ? '+' : '|', content.Text.TrimStart(' ').TrimStart("0123456789".ToCharArray())[0]);
                AssertNothingATerminalActsOn(content.Body);
                break;
        }
    }

    private static void AssertNothingATerminalActsOn(string body)
    {
        var first = true;
        foreach (var rune in body.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            var at = $"U+{rune.Value:X4} in {DisplayLiteral.Of(body)}";
            Assert.False(
                category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
                    or UnicodeCategory.ParagraphSeparator or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned
                    or UnicodeCategory.Surrogate,
                at);
            Assert.False(first && IsMark(rune), $"a row begins with a mark: {at}");
            first = false;
        }

        // No unpaired surrogate survives EnumerateRunes as itself: it enumerates as U+FFFD, which the source never held.
        Assert.DoesNotContain('\uFFFD', body);
    }
}
