using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;
using CsCheck;
using Curia.Domain.Screening;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Domain.Tests.Screening;

/// <summary>
/// R10.86 (errata G46): the credential screen's <c>pwd</c> keyword is a password only after a <c>;</c>, an opening quotation mark or a line start, and not
/// when its value begins with <c>/</c>, <c>~</c>, <c>$</c> or <c>%</c>; <c>password</c> is unchanged. Probes TRW32 to TRW39, with the corpus rows they read
/// (TRW28 to TRW31 and TRW33 are the corpus files themselves, read by <c>RedTeamCorpusTests</c>). The version and every corpus form are read from the
/// errata's text; the property reads its positions and values from the ruling and not from the pattern; the legacy pattern is held in this file as a
/// literal, the only copy that survives the change.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class PwdKeywordTests
{
    private const string Secret = "EXAMPLE-NOT-A-REAL-SECRET";

    private sealed record Row(string Id, string Content, string[] Expect);

    private static Row[] Load(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "conformance", "red-team")))
            dir = dir.Parent;

        Assert.True(dir is not null, $"conformance/red-team not found above {AppContext.BaseDirectory}");

        return [.. File.ReadAllLines(Path.Combine(dir!.FullName, "conformance", "red-team", file))
            .Where(line => line.Trim().Length > 0)
            .Select(line =>
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                var expect = root.TryGetProperty("expect", out var e) ? e : root.GetProperty("would_detect");
                return new Row(
                    root.GetProperty("id").GetString()!,
                    root.GetProperty("content").GetString()!,
                    [.. expect.EnumerateArray().Select(x => x.GetString()!)]);
            })];
    }

    private static RiskFlag[] KeywordFlags(string text) =>
        [.. SecretScanner.Scan(text).Where(f => f.Category is RiskCategory.ConnectionStringPassword)];

    // ---- TRW32: the corpus holds a true positive for each alternative -------------------------------------------------

    /// <summary>
    /// TRW32 (trap 50). The red-team corpus held no true positive of the keyword rule, so "narrow it without losing one" was true of any narrowing. The rows
    /// are controls on the unmodified rule (it flags all eight); the fact is that the corpus holds a payload of each alternative at all.
    /// </summary>
    [Fact]
    public void R10_86_TRW32_TheCorpusHoldsATruePositiveOfEachAlternative()
    {
        var payloads = Load("payloads.jsonl").Where(r => r.Expect.Contains("ConnectionStringPassword")).ToArray();

        var pwd = payloads.Count(r => r.Content.Contains("pwd=", StringComparison.OrdinalIgnoreCase));
        var password = payloads.Count(r => r.Content.Contains("password=", StringComparison.OrdinalIgnoreCase));

        Assert.True(pwd >= 1, $"Expected: >= 1 payload with pwd= expecting ConnectionStringPassword. Actual: {pwd}");
        Assert.True(password >= 1, $"Expected: >= 1 payload with password= expecting ConnectionStringPassword. Actual: {password}");
    }

    // ---- TRW34: the property, from the ruling and not from the pattern ------------------------------------------------

    private static readonly char[] Alphabet = [.. "abcXYZ019-_.:,!#*+@&=EXAMPLE"];

    private static readonly (string Lead, string Position)[] Positions =
    [
        (string.Empty, string.Empty),
        ("Server=db", ";"),
        ("Server=db", "; "),
        ("Server=db", ";\t"),
        ("UID=curia", "\n"),
        ("UID=curia", "\r\n"),
        ("UID=curia", "\r"),
        ("x: ", "\""),
        ("x: ", "'"),
        ("[db]", "\n    "),
    ];

    private static readonly string[] Cases = ["pwd", "Pwd", "PWD", "pWd"];
    private static readonly string[] Assigns = ["=", " = ", "= ", " ="];
    private static readonly string[] Prefixes = ["echo ", "$", "=", "/", "-", "x", "."];

    private static Gen<string> Value(int min, int max, bool excludedFirst)
    {
        var first = excludedFirst ? Gen.OneOfConst("/~$%".ToCharArray()) : Gen.OneOfConst(Alphabet);
        return Gen.Select(first, Gen.OneOfConst(Alphabet).List[min - 1, max - 1], (f, rest) => f + new string([.. rest]));
    }

    /// <summary>TRW34 A: every qualifying position, case and spacing, with an ordinary value, is one flag whose span starts at the <c>p</c> and ends at the value's last character.</summary>
    [Fact]
    public void R10_86_TRW34A_APwdAfterAQualifyingPositionIsFlaggedFromTheKeywordToTheValuesEnd() =>
        Gen.Select(Gen.Int[0, Positions.Length - 1], Gen.OneOfConst(Cases), Gen.OneOfConst(Assigns), Value(4, 12, excludedFirst: false))
            .Sample(
                t =>
                {
                    var (lead, position) = Positions[t.Item1];
                    var text = lead + position + t.Item2 + t.Item3 + t.Item4;
                    var at = lead.Length + position.Length;
                    return KeywordFlags(text) is [{ } flag] && flag.Offset == at && flag.Offset + flag.Length == text.Length;
                },
                iter: 400);

    /// <summary>TRW34 B: the same with a first value character of <c>/ ~ $ %</c>: no flag.</summary>
    [Fact]
    public void R10_86_TRW34B_APwdWhoseValueBeginsWithAPathOrAReferenceIsNotFlagged() =>
        Gen.Select(Gen.Int[0, Positions.Length - 1], Gen.OneOfConst(Cases), Gen.OneOfConst(Assigns), Value(4, 12, excludedFirst: true))
            .Sample(
                t =>
                {
                    var (lead, position) = Positions[t.Item1];
                    return KeywordFlags(lead + position + t.Item2 + t.Item3 + t.Item4).Length == 0;
                },
                iter: 400);

    /// <summary>TRW34 C: a prefix that is no qualifying position (a command word, <c>$</c>, <c>=</c>, <c>/</c>, <c>-</c>, a letter, <c>.</c>): no flag, whatever the value.</summary>
    [Fact]
    public void R10_86_TRW34C_APwdAfterAPrefixThatIsNoPositionIsNotFlagged() =>
        Gen.Select(Gen.OneOfConst(Prefixes), Gen.OneOfConst(Cases), Gen.OneOfConst(Assigns), Value(4, 12, excludedFirst: false))
            .Sample(
                t => KeywordFlags(t.Item1 + t.Item2 + t.Item3 + t.Item4).Length == 0,
                iter: 400);

    /// <summary>TRW34 D: <c>password=</c> and any value of four or more characters, including one that begins with <c>/ ~ $ %</c>, is flagged, after any prefix.</summary>
    [Fact]
    public void R10_86_TRW34D_PasswordKeepsItsRuleWhateverItsValueOrPrefix() =>
        Gen.Select(Gen.OneOfConst(string.Empty, "echo ", "x: ", "--"), Gen.Bool, Value(4, 12, excludedFirst: false), Value(4, 12, excludedFirst: true))
            .Sample(
                t => KeywordFlags(t.Item1 + "password=" + (t.Item2 ? t.Item3 : t.Item4)).Length >= 1,
                iter: 400);

    /// <summary>TRW34 E: the floor. A value of three characters is not flagged and of four is, at a qualifying position.</summary>
    [Fact]
    public void R10_86_TRW34E_TheFloorIsFourCharacters() =>
        Gen.Select(Gen.Int[0, Positions.Length - 1], Gen.OneOfConst(Cases), Value(3, 3, excludedFirst: false), Gen.OneOfConst(Alphabet))
            .Sample(
                t =>
                {
                    var (lead, position) = Positions[t.Item1];
                    var three = KeywordFlags(lead + position + t.Item2 + "=" + t.Item3);
                    var four = KeywordFlags(lead + position + t.Item2 + "=" + t.Item3 + t.Item4);
                    return three.Length == 0 && four.Length == 1;
                },
                iter: 400);

    // ---- TRW35, TRW36, TRW37, TRW38, TRW39 ----------------------------------------------------------------------------

    /// <summary>TRW35. The built detector version is the one R10.86 publishes.</summary>
    [Fact]
    public void R10_86_TRW35_TheDetectorVersionIsThePublishedOne() =>
        Assert.True(
            PublishedAcceptBudget.DetectorVersion() == SecretScanner.Version,
            $"Expected: \"{PublishedAcceptBudget.DetectorVersion()}\" Actual: \"{SecretScanner.Version}\"");

    /// <summary>TRW36 (green first, held by FTRW46). The span is the keyword to the end of its value and never the prefix.</summary>
    [Theory]
    [InlineData("Server=db;Pwd=" + Secret + ";X=1", "Pwd=" + Secret)]
    [InlineData("UID=curia\nPWD=" + Secret, "PWD=" + Secret)]
    [InlineData("x: \"Pwd=" + Secret + ";Server=db\"", "Pwd=" + Secret)]
    public void R10_86_TRW36_TheSpanStartsAtTheKeywordAndReachesTheValuesEnd(string text, string span)
    {
        ArgumentNullException.ThrowIfNull(text);
        var flag = Assert.Single(KeywordFlags(text));

        Assert.Equal(span, text.Substring(flag.Offset, flag.Length));
    }

    /// <summary>The legacy keyword pattern, copied from <c>SecretScanner.cs</c> at <c>46c4dff1</c> before it changed (M15).</summary>
    [GeneratedRegex(
        @"\b(?:password|pwd)\s*=\s*[^\s;""']{4,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex LegacyKeywordPattern();

    private static readonly string[] BenignIds =
    [
        "shell-remap-path-prefix-pwd", "env-dump-pwd-path", "echo-pwd-assignment", "env-pwd-word-value",
        "connection-string-pwd-dollar-reference", "connection-string-pwd-percent-reference",
        "connection-string-pwd-tilde-path", "connection-string-pwd-absolute-path",
    ];

    private static readonly string[] EvasionIds =
    [
        "evade-pwd-after-a-command-word", "evade-pwd-space-separated-pairs", "evade-pwd-value-starts-with-a-dollar",
        "evade-pwd-after-nel", "evade-pwd-after-line-separator", "evade-pwd-after-paragraph-separator",
    ];

    /// <summary>
    /// TRW37 (trap 50; green first, held by FTRW54). No new benign row is quiet for the floor's reason, and no evasion is a loss that never was: the legacy
    /// pattern matches every one of them.
    /// </summary>
    [Fact]
    public void R10_86_TRW37_TheLegacyRuleFiresOnEveryNewBenignRowAndEveryNewEvasion()
    {
        var benign = Load("benign.jsonl").ToDictionary(r => r.Id, StringComparer.Ordinal);
        var evasions = Load("known-evasions.jsonl").ToDictionary(r => r.Id, StringComparer.Ordinal);

        var quiet = new List<string>();
        foreach (var id in BenignIds)
        {
            if (!benign.TryGetValue(id, out var row)) quiet.Add($"{id} (missing from benign.jsonl)");
            else if (!LegacyKeywordPattern().IsMatch(row.Content)) quiet.Add(id);
        }

        foreach (var id in EvasionIds)
        {
            if (!evasions.TryGetValue(id, out var row)) quiet.Add($"{id} (missing from known-evasions.jsonl)");
            else if (!LegacyKeywordPattern().IsMatch(row.Content)) quiet.Add(id);
        }

        Assert.Empty(quiet);
    }

    /// <summary>
    /// TRW56 (green first, held by FTRW54). The benign file is read whole: no row of it, however added, raises the keyword rule on the built scanner, and the
    /// rows read include the eighth exercise's false positive and every row that mentions <c>pwd</c> or <c>password</c> (TRW37 and the fixed lists reach only
    /// the rows someone named).
    /// </summary>
    [Fact]
    public void R10_86_TRW56_NoBenignRowOfTheFileIsFlaggedByTheKeywordRule()
    {
        var rows = Load("benign.jsonl");
        var keywordRows = rows.Where(r => r.Content.Contains("pwd", StringComparison.OrdinalIgnoreCase) || r.Content.Contains("password", StringComparison.OrdinalIgnoreCase)).ToArray();

        var flagged = rows.Where(r => KeywordFlags(r.Content).Length > 0).Select(r => r.Id).ToArray();

        Assert.Contains("shell-remap-path-prefix-pwd", keywordRows.Select(r => r.Id));
        Assert.True(BenignIds.All(id => keywordRows.Any(r => r.Id == id)), "a named benign pwd row is not among the rows read");
        Assert.True(keywordRows.Length >= BenignIds.Length, $"Expected: at least {BenignIds.Length} pwd/password rows. Actual: {keywordRows.Length} of {rows.Length} rows read");
        Assert.True(flagged.Length == 0, $"Expected: no benign row flagged by the keyword rule. Actual: {string.Join(", ", flagged)} of {rows.Length} rows read");
    }

    /// <summary>
    /// TRW38 (green first, held by FTRW53). The corpus holds every form R10.86 names: the code spans of each of its three clauses are substrings of some
    /// content of the file the clause names. The paragraph and the files are parsed here and the scanner is not called.
    /// </summary>
    [Fact]
    public void R10_86_TRW38_TheCorpusHoldsEveryFormTheErrataNames()
    {
        var paragraph = PublishedAcceptBudget.Paragraph("**R10.86**");
        var benignAt = paragraph.IndexOf("The benign corpus SHALL hold", StringComparison.Ordinal);
        var payloadAt = paragraph.IndexOf("the red-team corpus SHALL hold", StringComparison.Ordinal);
        var evasionAt = paragraph.IndexOf("the known-evasions list SHALL hold", StringComparison.Ordinal);
        var reasonAt = paragraph.IndexOf("each with its reason", StringComparison.Ordinal);
        Assert.True(0 <= benignAt && benignAt < payloadAt && payloadAt < evasionAt && evasionAt < reasonAt, "R10.86 no longer has its three corpus clauses in order");

        var clauses = new[]
        {
            ("benign.jsonl", paragraph[benignAt..payloadAt]),
            ("payloads.jsonl", paragraph[payloadAt..evasionAt]),
            ("known-evasions.jsonl", paragraph[evasionAt..reasonAt]),
        };

        var missing = new List<string>();
        var named = 0;
        foreach (var (file, clause) in clauses)
        {
            var contents = Load(file).Select(r => r.Content).ToArray();
            foreach (Match span in CodeSpan().Matches(clause))
            {
                named++;
                if (!contents.Any(c => c.Contains(span.Groups[1].Value, StringComparison.Ordinal)))
                    missing.Add($"{file}: {span.Groups[1].Value}");
            }
        }

        Assert.True(named >= 16, $"R10.86 names {named} forms, expected at least 16");
        Assert.True(missing.Count == 0, $"Expected: every named form in the corpus. Actual: {missing.Count} of {named} missing: {string.Join("; ", missing)}");
    }

    [GeneratedRegex("`([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex CodeSpan();

    /// <summary>TRW39 (green first, held by FTRW51). The line-joined view does not read the rule: <c>ScanShapes</c> yields no keyword flag where <c>Scan</c> does.</summary>
    [Theory]
    [InlineData("Uid=curia;Pwd=" + Secret)]
    [InlineData("password=" + Secret)]
    public void R10_86_TRW39_TheLineJoinedViewDoesNotReadTheRule(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var shapes = SecretScanner.ScanShapes(text).Count(f => f.Category is RiskCategory.ConnectionStringPassword);

        Assert.Equal((0, 1), (shapes, KeywordFlags(text).Length));
    }
}
