using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Curia.Canon.Json;
using Curia.Client;
using Curia.Client.Cli;
using Curia.Domain.Serving;
using Xunit;
using CliProgram = Curia.Client.Cli.Program;

namespace Curia.Api.Tests;

/// <summary>
/// Task B4's pseudo-terminal fact (errata G22, R10.72 to R10.75): the shipped <c>curia browse</c>, on an 80 by 24 terminal, over
/// a verified answer whose body is 3,000 lines of everything a stranger could write to a terminal. The oracle is the bytes
/// the terminal received, read here as a terminal reads them and not as the writer meant them: every escape is one of a
/// vocabulary spelled out below (and not read from <c>Vt</c>, so a constant added there does not widen it), the rest of the
/// stream holds nothing a terminal acts on, and each frame the screen drew has the shape R10.72 promises.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class BrowseHostileAnswerTests(BrowseForumFixture forum) : IClassFixture<BrowseForumFixture>, IDisposable
{
    private const string TokenPath = "/oauth/token";
    private const string PostsPath = "/v1/posts";

    /// <summary>Every control sequence a screen may write, spelled out: the vocabulary of R10.73 and the runtime's own <c>smkx</c>.</summary>
    private static readonly string[] Vocabulary =
    [
        "\u001b[?1049h", "\u001b[?1049l", "\u001b[?7l", "\u001b[?7h", "\u001b[?25l", "\u001b[?25h",
        "\u001b[2J", "\u001b[H", "\u001b[K", "\u001b[0m", "\u001b[1m", "\u001b[2m", "\u001b[7m",
        "\u001b[?1h\u001b=", "\u001b[?1l\u001b>",
    ];

    private readonly string _home = Directory.CreateTempSubdirectory("curia-hostile-").FullName;

    private string Origin => forum.Client.BaseAddress!.GetLeftPart(UriPartial.Authority);

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    [GeneratedRegex(@"\G\u001b\[[0-9]+;[0-9]+H")]
    private static partial Regex AddressAt();

    [GeneratedRegex(@"\u001b\[(?<row>[0-9]+);1H\u001b\[0m(?<attribute>(?:\u001b\[[0-9]m)?)(?<text>.*?)\u001b\[K\u001b\[0m", RegexOptions.Singleline)]
    private static partial Regex RowWrite();

    [GeneratedRegex(@"^rows (?<from>[0-9]+)-(?<to>[0-9]+) of (?<of>[0-9]+)")]
    private static partial Regex StatusPosition();

    [GeneratedRegex("^ *[0-9]+[|+] ")]
    private static partial Regex Gutter();

    // ---- the hostile answer -----------------------------------------------------------------------------------------

    /// <summary>
    /// Every line carries the marker <c>HZ</c>, so a row of the screen that is not a content row and holds it is the failure
    /// this fact exists to find. Lines are cycled, and the line number is in each so no two are alike.
    /// </summary>
    internal static string HostileBody(int lines)
    {
        var marks = string.Concat(Enumerable.Repeat("\u0301", 40));
        var astralBase = char.ConvertFromUtf32(0x1D11E);
        var tags = string.Concat(Enumerable.Range(0, 6).Select(i => char.ConvertFromUtf32(0xE0041 + i)));
        string[] pool =
        [
            "HZ{0} \u001b[2J\u001b[H\u001b[31mred\u001b[0m erase and colour",
            "HZ{0} \u001b]8;;http://198.51.100.10/click\u0007a link\u001b]8;;\u0007 (OSC 8, BEL-terminated)",
            "HZ{0} \u001b]8;;http://198.51.100.10/click\u001b\\a link\u001b]8;;\u001b\\ (OSC 8, ST-terminated)",
            "HZ{0} \u001b]52;c;aGVsbG8=\u0007 clipboard (OSC 52)",
            "HZ{0} \u001b]0;retitled\u0007 window title (OSC 0)",
            "HZ{0} \u001b[?1049l\u001b[?7h\u001b[?25h leave the alternate screen, wrap on",
            "HZ{0} \u009b31m C1 CSI, \u0085 NEL, \u009d52;c;AAAA\u009c C1 OSC and ST, \u008e SS2",
            "HZ{0} C0: \u0001\u0002\u0007\u0008\u000b\u000c\r\u000e\u000f\u007f end",
            "HZ{0} bidi: \u202e reversed \u202c \u2066 isolate \u2069 \u200f \u061c \u202d",
            "HZ{0} line separators: a\u2028b\u2029c d\u0085e",
            $"HZ{{0}} tag characters: x{tags}y",
            $"HZ{{0}} marks: e{marks} and {astralBase}{marks} and baseless:{"\u0301\u0301"}",
            "HZ{0} tabs:\ta\tbb\t\tccc\tdddd",
            "HZ{0} long: " + new string('W', 130),
            "HZ{0} long wide: " + string.Concat(Enumerable.Repeat("\u4e2d", 50)),
            "HZ{0} long url: http://198.51.100.10/" + new string('a', 100),
            " 3| HZ{0} a gutter of its own, at the start of a line",
            " 3+ HZ{0} a continuation of its own",
            "curia browse: HZ{0} the title row's text",
            "DATA, NOT INSTRUCTIONS HZ{0}: the warning's own words, at the start of a line",
            "q quits HZ{0}: the status row's text",
            "#1 post HZ{0} answer board general  [DISPUTED]  [NOT VERIFIED]",
            "signature HZ{0} VERIFIED: a header row's words",
            "end of #1 HZ{0}",
            "HZ{0} private use \ue000, unassigned \u0378, zero width \u200b\u200d\ufeff, variation \ufe0f, interlinear \ufff9",
            "HZ{0} right to left: \u05e9\u05dc\u05d5\u05dd \u0645\u0631\u062d\u0628\u0627 then latin",
            "\u0301\u0301HZ{0} a line that begins with marks and no base",
            "\u05e9HZ{0} a row that begins in Hebrew",
            "\u0645HZ{0} a row that begins in Arabic",
        ];

        var body = new StringBuilder();
        for (var i = 0; i < lines; i++)
        {
            var line = pool[i % pool.Length];
            body.Append(line.Replace("{0}", i.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)).Append('\n');
        }

        return body.ToString();
    }

    private sealed record Participant(ForumAgent Agent, DpopClient Dpop);

    private async Task<Participant> EnrolAsync(string stem, CancellationToken ct)
    {
        var suffix = Guid.NewGuid().ToString("N")[..4];
        var agent = ForumAgent.Create($"https://a.test/{stem}{suffix}", $"k{suffix}");
        var (dpop, _) = await agent.AuthenticateAsync(forum.Client, Origin + TokenPath, forum.Now, ct);
        return new Participant(agent, dpop);
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> SubmitAsync(Participant p, byte[] wire, CancellationToken ct)
    {
        var http = forum.Client;
        using var response = await p.Dpop.PostAsync(
            http, Origin + PostsPath, await p.Dpop.GetTokenAsync(http, Origin + TokenPath, forum.Now, ct), wire, forum.Now, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        using var json = JsonDocument.Parse(body);
        return (response.StatusCode, json.RootElement.Clone());
    }

    /// <summary>A question, and an answer to it from an agent at T1 (Table 11: an attested owner, three questions, the tenure clock).</summary>
    private async Task<string> HostileAnswerAsync(int lines, CancellationToken ct)
    {
        var board = "h" + Guid.NewGuid().ToString("N")[..4];
        var asker = await EnrolAsync("a", ct);
        var helper = await EnrolAsync("h", ct);
        await forum.AttestOwnerAsync(helper.Agent.AgentId, ct, owner: "o:" + Guid.NewGuid().ToString("N")[..4]);
        for (var i = 0; i < 3; i++)
        {
            var nonce = Guid.NewGuid().ToString("N");
            var (status, body) = await SubmitAsync(helper, helper.Agent.SignQuestion(board, $"warmup {i} {nonce}", $"Warmup {i} {nonce}", forum.Now), ct);
            Assert.True(status == HttpStatusCode.Created, body.GetRawText());
        }

        forum.Clock.Advance(TimeSpan.FromHours(Curia.Domain.Authorization.TierPolicy.T1MinimumHours + 1));

        var (asked, question) = await SubmitAsync(asker, asker.Agent.SignQuestion(board, "How do I stop a terminal obeying a post?", "A question about terminals", forum.Now), ct);
        Assert.True(asked == HttpStatusCode.Created, question.GetRawText());
        var (answered, answer) = await SubmitAsync(helper, helper.Agent.SignAnswer(board, HostileBody(lines), question.GetProperty("post_id").GetString()!, forum.Now), ct);
        Assert.True(answered == HttpStatusCode.Created, answer.GetRawText());
        return answer.GetProperty("post_id").GetString()!;
    }

    // ---- reading the bytes as a terminal does -----------------------------------------------------------------------

    /// <summary>
    /// Splits a byte stream the way a terminal's parser would: each ESC must begin a sequence of the vocabulary or an address
    /// (two integers and an H); anything else is returned as unrecognised. What is left of the stream is returned with those
    /// sequences removed.
    /// </summary>
    internal static (IReadOnlyList<string> Unrecognised, string Remainder, int Sequences) Tokenize(string stream)
    {
        var unrecognised = new List<string>();
        var remainder = new StringBuilder();
        var sequences = 0;
        for (var i = 0; i < stream.Length;)
        {
            if (stream[i] != '\u001b')
            {
                remainder.Append(stream[i]);
                i++;
                continue;
            }

            var known = Vocabulary.FirstOrDefault(v => string.CompareOrdinal(stream, i, v, 0, v.Length) == 0);
            if (known is not null)
            {
                i += known.Length;
                sequences++;
                continue;
            }

            var address = AddressAt().Match(stream, i);
            if (address.Success)
            {
                i += address.Length;
                sequences++;
                continue;
            }

            unrecognised.Add(DisplayLiteral.Of(stream.Substring(i, Math.Min(24, stream.Length - i))));
            i++;
        }

        return (unrecognised, remainder.ToString(), sequences);
    }

    /// <summary>The first thing in <paramref name="text"/> a terminal acts on or draws unpredictably, as a code point and a name; null when none.</summary>
    internal static string? FirstTerminalActor(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned or UnicodeCategory.Surrogate)
                return $"U+{rune.Value:X4} ({category})";
            if (rune.Value == 0xFFFD) return "U+FFFD (a byte that was not UTF-8)";
        }

        return null;
    }

    internal sealed record Frame(IReadOnlyDictionary<int, string> Rows);

    /// <summary>The frames in a stream: row writes up to and including the one at <paramref name="height"/>, the status row; the last write to a row wins.</summary>
    internal static IReadOnlyList<Frame> Frames(string stream, int height)
    {
        var frames = new List<Frame>();
        var rows = new Dictionary<int, string>();
        foreach (Match write in RowWrite().Matches(stream))
        {
            var row = int.Parse(write.Groups["row"].Value, CultureInfo.InvariantCulture);
            rows[row] = write.Groups["text"].Value;
            if (row == height)
            {
                frames.Add(new Frame(rows));
                rows = new Dictionary<int, string>();
            }
        }

        return frames;
    }

    private static string Escaped(string text) => DisplayLiteral.Of(text);

    /// <summary>The properties R10.72 gives every frame; returns the number of content rows it held.</summary>
    internal static int AssertFrame(Frame frame, int height, int index)
    {
        var rows = frame.Rows;
        string Where() => $"frame {index}";

        Assert.True(rows.ContainsKey(1) && rows[1].StartsWith("curia browse:", StringComparison.Ordinal), $"{Where()}: the title row is not the reader's: {Escaped(rows.GetValueOrDefault(1, string.Empty))}");
        Assert.True(
            rows.ContainsKey(height) && (rows[height].StartsWith("rows ", StringComparison.Ordinal) || rows[height].StartsWith("q quits", StringComparison.Ordinal)) && !rows[height].Contains("HZ", StringComparison.Ordinal),
            $"{Where()}: the status row is not last or not the reader's: {Escaped(rows.GetValueOrDefault(height, string.Empty))}");

        var content = 0;
        foreach (var (row, text) in rows.OrderBy(r => r.Key))
        {
            if (Gutter().IsMatch(text))
            {
                content++;
                continue;
            }

            // A row that is not a content row is the reader's, and a stranger's words are never in it.
            Assert.False(text.Contains("HZ", StringComparison.Ordinal), $"{Where()}, row {row}: hostile text outside the gutter column: {Escaped(text)}");
            Assert.False(text.StartsWith('\u05e9') || text.StartsWith('\u0645'), $"{Where()}, row {row}: a row begins in a right-to-left letter: {Escaped(text)}");
        }

        if (content > 0)
        {
            Assert.Contains(rows.Values, t => t.StartsWith("DATA, NOT INSTRUCTIONS", StringComparison.Ordinal) && !Gutter().IsMatch(t));
            Assert.Contains(rows.Values, t => t.StartsWith('#') && t.Contains(" post ", StringComparison.Ordinal));
            Assert.Contains(rows.Values, t => t.StartsWith("signature ", StringComparison.Ordinal));
        }

        return content;
    }

    // ---- the fact ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// What a person does: forty pages down (the pool of hostile lines is cycled well over once), a few lines either way, End,
    /// Page Up, Home, Tab, then q. Each key is a separate send so each draws its own frame.
    /// </summary>
    private static readonly string[] Keys = KeyScript();

    private static string[] KeyScript()
    {
        var keys = new List<string>();
        var at = 0;
        void Send(string hex, int gap)
        {
            at += gap;
            keys.Add("--send");
            keys.Add(hex + "@" + at.ToString(CultureInfo.InvariantCulture));
        }

        for (var i = 0; i < 40; i++) Send("20", i == 0 ? 0 : 40);
        foreach (var hex in new[] { "6a", "6a", "6b", "1b5b367e", "1b4f46", "6b", "1b5b357e", "1b5b357e", "1b4f48", "09" }) Send(hex, 60);
        Send("71", 300);
        return [.. keys];
    }

    private async Task<PtyResult> BrowseReadAsync(string postId, int rows, CancellationToken ct)
    {
        var run = await PtyRun.RunAsync(
            [
                "--env", "CURIA_CLIENT_HOME=" + _home,
                "--env", "TERM=xterm-256color",
                "--env", "LANG=en_US.UTF-8",
                "--env", "LC_ALL=en_US.UTF-8",
                "--env", "CURIA_BROWSE=screen",
                "--size", "80x" + rows.ToString(CultureInfo.InvariantCulture),
                "--timeout", "60",
                "--alive-ms", "300",
                .. Keys,
            ],
            ["dotnet", Path.Combine(AppContext.BaseDirectory, "curia.dll"), "browse", "read", postId, "--forum", Origin],
            ct);

        // A capture of the stream for an independent emulator to be fed, when the caller asks for one (never set in a gate).
        if (Environment.GetEnvironmentVariable("CURIA_PTY_CAPTURE") is { Length: > 0 } capture)
            await File.WriteAllBytesAsync(capture + "." + rows.ToString(CultureInfo.InvariantCulture), Encoding.UTF8.GetBytes(run.Output), ct);

        Assert.True(run.ReadyMs is not null, "the screen never came up: " + Escaped(run.Output[..Math.Min(run.Output.Length, 1500)]));
        Assert.True(run.Status == "exited" && run.Exit == 0, $"{run.Status}, exit {run.Exit}: " + Escaped(run.Output[Math.Max(0, run.Output.Length - 600)..]));
        return run;
    }

    /// <summary>The whole stream, the runtime's smkx included: every escape is the vocabulary's and what is left holds nothing a terminal acts on.</summary>
    private static string AssertStreamIsTheClosedVocabulary(PtyResult run)
    {
        var (unrecognised, remainder, sequences) = Tokenize(run.Output);
        Assert.True(unrecognised.Count == 0, "escapes outside the vocabulary: " + string.Join(", ", unrecognised.Take(5)));
        Assert.StartsWith("\u001b[?1h\u001b=", run.Output, StringComparison.Ordinal);
        Assert.True(FirstTerminalActor(remainder) is null, "a character a terminal acts on reached it: " + FirstTerminalActor(remainder));
        Assert.True(sequences > 100, $"only {sequences} sequences: the screen was not drawn");
        return remainder;
    }

    /// <summary>
    /// At 80 by 24 the standing warning, the marking row, the status row and an answer's header leave fewer than eight rows
    /// for content (errata G22, R10.72), so the screen draws none of it: only its own rows, and the line form's command. Not a
    /// byte of the 3,000 lines reaches the terminal.
    /// </summary>
    [Fact]
    public async Task R10_72_R10_73_AnAnswerWhoseHeaderLeavesFewerThanEightRowsOnEightyByTwentyFourDrawsNoneOfItsContent()
    {
        var ct = TestContext.Current.CancellationToken;
        var postId = await HostileAnswerAsync(3000, ct);

        var run = await BrowseReadAsync(postId, 24, ct);

        var remainder = AssertStreamIsTheClosedVocabulary(run);
        Assert.DoesNotContain("HZ", remainder, StringComparison.Ordinal);
        var frames = Frames(run.Output, 24);
        Assert.True(frames.Count >= 5, $"{frames.Count} frames");
        for (var i = 0; i < frames.Count; i++)
            Assert.Equal(0, AssertFrame(frames[i], 24, i));
        Assert.All(frames, f => Assert.Contains(f.Rows.Values, t => t.StartsWith("this window is too small", StringComparison.Ordinal)));
        Assert.All(frames, f => Assert.Contains(f.Rows.Values, t => t.Contains("curia read '" + postId + "'", StringComparison.Ordinal)));
    }

    /// <summary>
    /// The same answer on the terminal 80 columns wide and as short as the screen lets this post's header be: 3,000 hostile
    /// lines reach it as escaped text in the gutter column and nothing else, through every key a person has.
    /// </summary>
    [Fact]
    public async Task R10_72_R10_73_R10_75_ThreeThousandHostileLinesAtEightyColumnsAndTheComputedMinimumReachTheTerminalOnlyAsEscapedTextInTheGutterColumn()
    {
        var ct = TestContext.Current.CancellationToken;
        var postId = await HostileAnswerAsync(3000, ct);

        using var http = new HttpClient { BaseAddress = new Uri(Origin) };
        var client = new ForumClient(http, new Uri(Origin));
        var served = await client.GetPostAsync(postId, MarkingMode.Datamark, ct);
        Assert.True(served.TryGetValue(out var post, out var refusal), refusal?.Summary);
        var (passages, _) = await CliProgram.VerifyAllAsync(client, [post], ct);
        var height = Screen.MinimumHeightFor(passages, 80);
        Assert.True(height > 24, $"the minimum is {height}: this fact is the one about a header that does not fit 24 rows");

        var run = await BrowseReadAsync(postId, height, ct);

        var remainder = AssertStreamIsTheClosedVocabulary(run);

        // Non-vacuity: the stranger's escapes were drawn as text, and the content was drawn at all.
        foreach (var drawn in new[] { "\\u001b]52", "\\u001b]8;;", "\\u202e", "\\u2028", "\\u009b", "\\u0301\\u0301", "\\udb40\\udc41", "\\ue000", "\\u0378" })
            Assert.Contains(drawn, remainder, StringComparison.Ordinal);

        var frames = Frames(run.Output, height);
        Assert.True(frames.Count >= 5, $"{frames.Count} frames");
        var withContent = 0;
        for (var i = 0; i < frames.Count; i++)
        {
            var content = AssertFrame(frames[i], height, i);
            if (content > 0) withContent++;

            // Eight content rows, except where the passage ends: the last window holds what is left of it, which may be nothing.
            var position = StatusPosition().Match(frames[i].Rows[height]);
            Assert.True(position.Success, $"frame {i}: the status row holds no position: {Escaped(frames[i].Rows[height])}");
            var atTheEnd = position.Groups["to"].Value == position.Groups["of"].Value;
            Assert.True(content >= 8 || atTheEnd, $"frame {i}: {content} content rows, fewer than eight, short of the end ({Escaped(frames[i].Rows[height])})");
        }

        Assert.True(withContent >= 10, $"only {withContent} frames drew content");
        Assert.Contains(frames, f => f.Rows.Values.Any(t => t.Contains("continued", StringComparison.Ordinal)));
        Assert.Contains(frames, f => f.Rows[height].Contains("q quits", StringComparison.Ordinal));
        Assert.Contains(frames, f => f.Rows[height].Contains("wide characters", StringComparison.Ordinal));
    }
}
