using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.RegularExpressions;
using CsCheck;
using Curia.Domain.Screening;
using Curia.Tests.Shared;
using Xunit;
using static Curia.Domain.Tests.Screening.ReferenceProbe;

namespace Curia.Domain.Tests.Screening;

/// <summary>
/// R10.96 (errata G60), probes RNC1 to RNC14, RNC24 and RNC25 (RNC28 to RNC31 are in <c>MaskThenWordTests</c>): the connection-string password rules do not report a password that is, whole, a reference,
/// and report every value. Reached through <c>ContentScreener</c> and <c>SecretScanner.Scan</c> and never through the predicate (trap 160); every form is
/// read from the errata or the corpus files and none from the code under test (vacuity question 4). Written red against the seam of Task 1: a form the
/// rule must clear is reported, so its probe fails by assertion; a form that must stay refused is refused today, so its probe is a control and says so.
///
/// <para>The generated probes (RNC4, RNC5, RNC25 (a)) take CsCheck's per-run seed, which a failure prints, run on one thread because their callbacks fill a
/// shared set, and assert that each named class was reached (<c>NOT REACHED</c> otherwise: trap 131).</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ReferencePasswordTests
{
    /// <summary>The first four reports and a count of the rest, so a red text stays readable at 116 reports.</summary>
    private static string Describe(string[] fired) =>
        string.Join("; ", fired.Take(4)) + (fired.Length > 4 ? $"; and {fired.Length - 4} more" : string.Empty);

    // ---- RNC1: the exercise's bodies ------------------------------------------------------------------------------------

    private static readonly string[] ExerciseBodies =
    [
        "DATABASE_URL=\"postgres://$DB_USER:$DB_PASS@$DB_HOST/$DB_NAME\" sqlx migrate run",
        "Point the service at postgres://user:${DB_PASS}@host/app and restart it.",
    ];

    /// <summary>RNC1: the reproducer's body and the contributor's variant are not refused in any shape (the body of a question; a flag's rationale is the bare shape).</summary>
    [Fact]
    public void R10_96_RNC1_TheExercisesBodiesAreNotRefusedInAnyShape()
    {
        var found = ExerciseBodies.SelectMany(body => FlaggedIn(body).Select(f => $"{Show(body)}: {f}")).ToArray();

        Assert.True(found.Length == 0, $"expected no flag, found {Describe(found)}");
    }

    // ---- RNC2: the eighteen forms, in each shape and each view ----------------------------------------------------------

    /// <summary>The two benign rows whose prose follows the mask (R10.96, decision 3's exception): the markup view alone refuses them and the whole screener, RNC28, holds them.</summary>
    private static readonly string[] MaskThenProseRows = ["ref-keyword-masked", "ref-keyword-masked-line-break"];

    /// <summary>
    /// RNC2: each of the eighteen forms R10.96 clears, read from the errata, is held by a benign row, and is quiet in each shape (through the row) and in each
    /// view's own <c>Scan</c> (through the form itself, alone). The failure names the view. Reads no predicate. Amendment 2: the two prose rows
    /// (<c>ref-keyword-masked</c> and <c>ref-keyword-masked-line-break</c>, found by id and asserted to be exactly two) are the one class a view refuses
    /// that the original clears, so for them the original's <c>Scan</c> is asserted to clear and the per-view leg is RNC28's; every other form is swept per view.
    /// </summary>
    [Fact]
    public void R10_96_RNC2_EveryFormTheScreenClearsIsQuietInEachShapeAndEachView()
    {
        var forms = PublishedReferenceWords.Benign();
        var prose = Load("benign.jsonl").Where(r => MaskThenProseRows.Contains(r.Id, StringComparer.Ordinal)).ToArray();
        Assert.True(prose.Length == 2, $"expected exactly two mask-then-prose rows by id, found {prose.Length}");

        var failures = new List<string>();
        foreach (var row in prose)
            failures.AddRange(ScanOriginal(row.Content).Select(f => $"{Show(row.Content)} (row {row.Id}): expected the original's Scan to clear, found a flag at {f.Offset}+{f.Length}"));

        foreach (var form in forms)
        {
            var rows = RowsHolding("benign.jsonl", form);
            if (rows.Length == 0) failures.Add($"{Show(form)}: no row of benign.jsonl holds it");
            foreach (var row in rows)
                failures.AddRange(FlaggedIn(row.Content).Select(f => $"{Show(form)} (row {row.Id}): expected quiet, fired {f}"));

            if (prose.Any(r => string.Equals(r.Content, form, StringComparison.Ordinal))) continue;
            failures.AddRange(FlaggedByViews(form).Select(f => $"{Show(form)} alone: expected quiet, {f}"));
        }

        Assert.True(failures.Count == 0, $"expected quiet for {forms.Length} forms, {failures.Count} reports: {Describe([.. failures])}");
    }

    // ---- RNC3: the twelve forms that stay refused --------------------------------------------------------------------------

    /// <summary>
    /// RNC3: each of the twelve forms that stay refused, read from the errata, is flagged in each shape through its payload row, and in the bare shape the span
    /// equals an independent computation: the keyword to the end of the value, or the URI through its <c>@</c>. CONTROL on the seam (refused today).
    /// </summary>
    [Fact]
    public void R10_96_RNC3_EveryFormThatStaysRefusedIsFlaggedWithItsSpan()
    {
        var forms = PublishedReferenceWords.Refused();
        var failures = new List<string>();
        foreach (var form in forms)
        {
            var rows = RowsHolding("payloads.jsonl", form);
            if (rows.Length == 0) failures.Add($"{Show(form)}: no row of payloads.jsonl holds it");
            foreach (var row in rows)
            {
                foreach (var shape in Shapes)
                    if (Flags(shape, row.Content).Length == 0)
                        failures.Add($"{Show(form)} ({shape.Name}): expected a flag, found none");

                var (start, length) = SpanOf(row.Content, form);
                var bare = Flags(Shapes[0], row.Content);
                if (!bare.Contains((start, length)))
                    failures.Add($"{Show(form)}: expected the span {start}+{length}, found {string.Join(", ", bare.Select(f => $"{f.Offset}+{f.Length}"))}");
            }
        }

        Assert.True(failures.Count == 0, Describe([.. failures]));
    }

    /// <summary>The span R10.96 states for a refused form inside its row: the URI through its first <c>@</c>, or the keyword to the end of its value.</summary>
    private static (int Start, int Length) SpanOf(string content, string form)
    {
        var at = content.IndexOf(form, StringComparison.Ordinal);
        if (form.Contains("://", StringComparison.Ordinal))
        {
            var scheme = content.IndexOf("postgres://", StringComparison.Ordinal);
            var end = content.IndexOf('@', scheme);
            return (scheme, end + 1 - scheme);
        }

        // The keyword as the form spells it, up to its sign: `password=`, `Pwd=` or the split `pass**word=` the markup view reads as one.
        var keyword = form[..(form.IndexOf('=', StringComparison.Ordinal) + 1)];
        var start = content.IndexOf(keyword, at, StringComparison.OrdinalIgnoreCase);
        return (start, content.Length - start);
    }

    // ---- RNC4: the accepted classes, generated --------------------------------------------------------------------------

    private const string Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Lower = "abcdefghijklmnopqrstuvwxyz";
    private const string Digits = "0123456789";

    private static readonly string[] Hosts =
    [
        "postgres://u:{0}@h",
        "password={0}",
        "Password={0};",
        ";Pwd={0};",
    ];

    /// <summary>A name of R10.96's class: <c>_*[A-Z][A-Z0-9_]*</c> holding at least two characters that are not underscores (a letter, then an upper-case letter or a digit, then anything of the class). The generator is a typed statement of R10.96's name rule, not a reading of it: only its description is held to the errata, by hand (review M-4).</summary>
    internal static Gen<string> Names { get; } = Gen.Select(
        Gen.Int[0, 3],
        Gen.OneOfConst([.. Upper]),
        Gen.OneOfConst([.. Upper + Digits]),
        Gen.OneOfConst([.. Upper + Digits + "_"]).List[1, 19],
        (lead, letter, second, rest) => new string('_', lead) + letter + second + new string([.. rest]));

    private static string Sigil(int kind, string name) => kind switch
    {
        0 => "$" + name,
        1 => "${" + name + "}",
        _ => "%" + name + "%",
    };

    /// <summary>RNC4: every name of the class, in each of the three sigil forms, in each of four hosts, followed by nought to three closing characters, is not flagged; each class is reached.</summary>
    [Fact]
    public void R10_96_RNC4_ANameOfTheClassIsNotFlaggedInAnySigilFormHostOrClosing()
    {
        var reached = new HashSet<string>();
        Gen.Select(Names, Gen.Int[0, 2], Gen.Int[0, Hosts.Length - 1], Gen.OneOfConst([.. ClosingSet]).List[0, 3])
            .Sample(
                t =>
                {
                    var text = string.Format(System.Globalization.CultureInfo.InvariantCulture, Hosts[t.Item3], Sigil(t.Item2, t.Item1) + new string([.. t.Item4]));
                    lock (reached)
                    {
                        reached.Add($"sigil {t.Item2}");
                        reached.Add($"host {t.Item3}");
                        reached.Add($"closing {t.Item4.Count}");
                        if (t.Item1.StartsWith('_')) reached.Add("leading underscore");
                        if (t.Item1.Length >= 20) reached.Add("long name");
                    }

                    return Flags(Shapes[0], text).Length == 0;
                },
                iter: 300,
                threads: 1);

        AssertReached(reached, ["sigil 0", "sigil 1", "sigil 2", "host 0", "host 1", "host 2", "host 3", "closing 0", "closing 1", "closing 2", "closing 3", "leading underscore", "long name"]);
    }

    private static void AssertReached(HashSet<string> reached, string[] classes)
    {
        var missing = classes.Where(c => !reached.Contains(c)).ToArray();
        Assert.True(missing.Length == 0, $"NOT REACHED: {string.Join(", ", missing)}");
    }

    // ---- RNC5: the refused classes, generated ---------------------------------------------------------------------------

    private static readonly char[] OuterChars = ['-', '+', '#', '&', '='];

    private static string WithLowerAt(string name, int at, char lower)
    {
        var chars = name.ToCharArray();
        chars[at % chars.Length] = lower;
        return new string(chars);
    }

    private static Gen<(string Class, string Value)> Refused { get; } = Gen.OneOf(
        Gen.Select(Names, Gen.Int[0, 1000], Gen.OneOfConst([.. Lower]), Gen.Int[0, 2],
            (n, i, c, s) => ("lower-case letter in a name", Sigil(s, WithLowerAt(n, i, c)))),
        Gen.Select(Gen.Int[0, 2], Gen.OneOfConst([.. Digits]), Gen.OneOfConst([.. Upper + Digits + "_"]).List[2, 8], Gen.Int[0, 2],
            (lead, digit, rest, s) => ("leading digit", Sigil(s, new string('_', lead) + digit + new string([.. rest])))),
        Gen.Select(Gen.OneOfConst([.. Upper]).List[2, 5], Gen.OneOfConst(OuterChars), Gen.OneOfConst([.. Upper]).List[1, 4], Gen.Int[0, 2],
            (a, mid, b, s) => ("inner character outside the class", Sigil(s, new string([.. a]) + mid + new string([.. b])))),
        Gen.Select(Gen.Int[0, 3], Gen.OneOfConst([.. Upper]), Gen.Int[0, 3], Gen.Int[0, 1],
            (lead, letter, trail, form) => ("one-character name", form == 0
                ? "${" + new string('_', lead) + letter + new string('_', trail) + "}"
                : "%_" + letter + new string('_', lead) + "%")),
        Gen.Select(Names, Gen.OneOfConst([.. Lower + Digits]).List[1, 5],
            (n, x) => ("default value", "${" + n + ":-" + new string([.. x]) + "}")),
        Gen.Select(Names, Gen.Int[0, 2], Gen.OneOfConst([.. Lower, '-', '=', ':', '/']), Gen.OneOfConst([.. Lower + Digits]).List[0, 3],
            (n, s, suffix, x) => ("a reference followed by a character that is no closing one", Sigil(s, n) + suffix + new string([.. x]))),
        Gen.Select(Gen.Int[2, 6], Gen.OneOfConst([.. Lower + Digits]).List[2, 4],
            (stars, tail) => ("masks then a letter", new string('*', stars) + new string([.. tail]))),
        Gen.Select(Gen.OneOfConst([.. Lower]).List[3, 6], Gen.Int[0, 4],
            (w, kind) => ("unbalanced brackets", kind switch
            {
                0 => "<" + new string([.. w]),
                1 => "{{" + new string([.. w]) + "}",
                2 => "{{" + new string([.. w]),
                _ => "{" + new string([.. w]) + "}}",
            })),
        Gen.Select(Gen.OneOfConst([.. Lower]).List[1, 3], Gen.OneOfConst(['$', '+', '#', '&', '=']), Gen.OneOfConst([.. Lower]).List[1, 3], Gen.Bool,
            (a, mid, b, angle) => ("brackets around content outside the name class", angle
                ? "<" + new string([.. a]) + mid + new string([.. b]) + ">"
                : "{{" + new string([.. a]) + mid + new string([.. b]) + "}}")));

    private static readonly string[] RefusedClasses =
    [
        "lower-case letter in a name", "leading digit", "inner character outside the class", "one-character name", "default value",
        "a reference followed by a character that is no closing one", "masks then a letter", "unbalanced brackets", "brackets around content outside the name class",
    ];

    /// <summary>RNC5: a value of each refused class is flagged, in the keyword hosts and, where its characters allow, the URI host; each class is reached. CONTROL on the seam (everything is flagged today).</summary>
    [Fact]
    public void R10_96_RNC5_AValueOfEachRefusedClassIsFlagged()
    {
        var reached = new HashSet<string>();
        Refused.Sample(
            t =>
            {
                lock (reached) reached.Add(t.Class);
                var hosts = new List<string> { "password=" + t.Value, "Password=" + t.Value + ";" };
                if (t.Value.IndexOfAny([':', '/', '@', ' ']) < 0) hosts.Add("postgres://u:" + t.Value + "@h");
                // `default value` is a keyword form alone: the URI password class excludes a colon (finding 3).
                return hosts.All(h => Flags(Shapes[0], h).Length > 0);
            },
            iter: 500,
            threads: 1);

        AssertReached(reached, RefusedClasses);
    }

    // ---- RNC6: the second @ ---------------------------------------------------------------------------------------------

    private static readonly string[] SecondAtQuiet = ["postgres://u:$DB_PASS@h/db@v2", "https://u:$DB_PASS@h?e=a@b"];
    private static readonly string[] DriveQuiet = ["Pwd=C:\\work", "Server=db;Pwd=C:/work;"];
    private static readonly string[] DriveRefused = ["Pwd=C:" + Secret, "password=C:\\work"];

    /// <summary>RNC6: a reference before a second <c>@</c> is still a password (flagged); a reference followed by a path, query or fragment holding an <c>@</c> is a reference (quiet). Both legs in one fact.</summary>
    [Fact]
    public void R10_96_RNC6_ASecondAtAfterTheReferenceKeepsTheRefusal()
    {
        var positive = FlaggedIn("postgres://u:$DB_PASS@u2:" + Secret + "@h/db");
        var negatives = SecondAtQuiet
            .SelectMany(t => FlaggedIn(t).Select(f => $"{Show(t)}: expected quiet, found {f}")).ToArray();

        Assert.True(positive.Length > 0, "expected the second-@ form flagged, found none");
        Assert.True(negatives.Length == 0, Describe(negatives));
    }

    // ---- RNC7: the drive-letter path ------------------------------------------------------------------------------------

    /// <summary>RNC7: a <c>pwd</c> whose value is a drive-letter path is quiet; a value that begins <c>C:</c> and no separator, and <c>password=C:\work</c>, are flagged. Three legs in one fact.</summary>
    [Fact]
    public void R10_96_RNC7_AnPwdThatIsADriveLetterPathIsQuietAndNothingElseIs()
    {
        var quiet = DriveQuiet
            .SelectMany(t => FlaggedIn(t).Select(f => $"{Show(t)}: expected quiet, found {f}")).ToArray();
        var refused = DriveRefused
            .Where(t => FlaggedIn(t).Length == 0).Select(t => $"{Show(t)}: expected a flag, found none").ToArray();

        Assert.True(quiet.Length == 0 && refused.Length == 0, Describe([.. quiet, .. refused]));
    }

    // ---- RNC8: the closing set ------------------------------------------------------------------------------------------

    /// <summary>RNC8: each of the eleven closing characters after <c>$DB_PASS</c> leaves it a reference, in the original, and for <c>*</c>, <c>~</c> and the backtick (which the markup view deletes) in that view.</summary>
    [Fact]
    public void R10_96_RNC8_EveryClosingCharacterLeavesTheReferenceWhole()
    {
        var failures = new List<string>();
        foreach (var c in ClosingSet)
        {
            var text = "password=$DB_PASS" + c;
            if (ScanOriginal(text).Length > 0) failures.Add($"expected quiet for \"$DB_PASS{c}\", found flag in the original");
            if (c is '*' or '~' or '`' && ScanView(text, "unmarked-up").Length > 0)
                failures.Add($"expected quiet for \"$DB_PASS{c}\", found flag in the unmarked-up view");
        }

        Assert.True(failures.Count == 0, Describe([.. failures]));
    }

    /// <summary>
    /// RNC8, the other legs: a lower-case letter, <c>:</c>, <c>=</c>, <c>/</c> or <c>-</c> after the reference is no closing character, so the value is not whole (flagged); an
    /// upper-case letter, a digit or <c>_</c> extends the name and a quotation mark ends the value, so those stay whole (quiet). R10.96's sentence "the matched value ends where the pattern's value class ends, at a blank, a semicolon or a quotation mark, so that what follows is not read" states the quotation-mark leg (non-property (m)).
    /// </summary>
    [Fact]
    public void R10_96_RNC8_ACharacterThatIsNoClosingCharacterEndsTheReference()
    {
        var failures = new List<string>();
        foreach (var c in "a:=/-")
            if (ScanOriginal("password=$DB_PASS" + c).Length == 0) failures.Add($"expected a flag for \"$DB_PASS{c}\", found none");
        foreach (var c in "1_Z\"")
            if (ScanOriginal("password=$DB_PASS" + c).Length > 0) failures.Add($"expected quiet for \"$DB_PASS{c}\" (the name continues or the value ends), found flag");

        Assert.True(failures.Count == 0, Describe([.. failures]));
    }

    // ---- RNC9: the line-joined view reads neither rule ------------------------------------------------------------------

    /// <summary>RNC9: <c>ScanShapes</c>, which the line-joined view reads, yields no connection-string password for a reference or a value, where <c>Scan</c> yields one for the value. CONTROL.</summary>
    [Fact]
    public void R10_96_RNC9_TheLineJoinedViewStillReadsNeitherRule()
    {
        string[] texts = ["password=$DB_PASS", "password=" + Secret, "postgres://u:$DB_PASS@h", "postgres://u:" + Secret + "@h"];
        var shapes = texts.Count(t => SecretScanner.ScanShapes(t).Any(f => f.Category is RiskCategory.ConnectionStringPassword));

        Assert.Equal(0, shapes);
        Assert.True(ScanOriginal("password=" + Secret).Length == 1, "the positive leg: Scan of a value yields its flag");
    }

    // ---- RNC10: what stays refused by decision --------------------------------------------------------------------------

    private static readonly string[] LowerCaseAndDigitFirst = ["password=$db_pass", "password=$1PASS"];

    /// <summary>RNC10: <c>password=changeme</c> and <c>password=$password</c> (the known false positives, read from their file) and <c>$db_pass</c> and <c>$1PASS</c> stay flagged in each shape. CONTROL.</summary>
    [Fact]
    public void R10_96_RNC10_WhatStaysRefusedByDecisionIsFlagged()
    {
        var failures = new List<string>();
        foreach (var form in PublishedReferenceWords.FalsePositives())
        {
            if (RowsHolding("known-false-positives.jsonl", form).Length == 0) failures.Add($"{Show(form)}: no row of known-false-positives.jsonl holds it");
            foreach (var shape in Shapes)
                if (Flags(shape, form).Length == 0) failures.Add($"{Show(form)} ({shape.Name}): expected a flag, found none");
        }

        foreach (var form in LowerCaseAndDigitFirst)
            foreach (var shape in Shapes)
                if (Flags(shape, form).Length == 0) failures.Add($"{Show(form)} ({shape.Name}): expected a flag, found none");

        Assert.True(failures.Count == 0, Describe([.. failures]));
    }

    // ---- RNC11: what the rule lets through is recorded ------------------------------------------------------------------

    /// <summary>RNC11: each of the seven forms R10.96 lets through, read from the errata, is held by a row of the evasions file and is quiet in each shape. Two of the five first listed were reported on the seam and the mask followed by a word (Amendment 2) was reported by the markup view.</summary>
    [Fact]
    public void R10_96_RNC11_EveryFormTheRuleLetsThroughIsRecordedAndQuiet()
    {
        var failures = new List<string>();
        foreach (var form in PublishedReferenceWords.Evasions())
        {
            var rows = RowsHolding("known-evasions.jsonl", form);
            if (rows.Length == 0) failures.Add($"{Show(form)}: no row of known-evasions.jsonl holds it");
            foreach (var row in rows)
                failures.AddRange(FlaggedIn(row.Content).Select(f => $"{Show(form)} (row {row.Id}): expected quiet, fired {f}"));
        }

        Assert.True(failures.Count == 0, $"{failures.Count} reports: {Describe([.. failures])}");
    }

    // ---- RNC12: the version ---------------------------------------------------------------------------------------------

    /// <summary>RNC12: the built detector version equals the date R10.96 names, is later than <c>secrets/2026-10-29</c>, and is named once in the scanner's history comment.</summary>
    [Fact]
    public void R10_96_RNC12_TheDetectorVersionIsThePublishedOneAndIsNewer()
    {
        var published = PublishedReferenceWords.DetectorVersion();

        Assert.Equal(SecretScanner.Version, published);
        Assert.True(string.CompareOrdinal(published, "secrets/2026-10-29") > 0, $"expected a version later than secrets/2026-10-29, found {published}");

        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Curia.Domain", "Screening", "SecretScanner.cs"));
        var date = published["secrets/".Length..];
        var history = Regex.Matches(source, $@"^\s*/// {Regex.Escape(date)}[a-z]?:", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        Assert.True(history.Count == 1, $"expected one history line for {date}, found {history.Count}");
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("no 'src' above " + AppContext.BaseDirectory);
    }

    // ---- RNC13: linear, one engine --------------------------------------------------------------------------------------

    /// <summary>RNC13: the pattern count stays 15, every pattern is non-backtracking, and the predicate's file builds no <c>Regex</c> (no member, no local). CONTROL.</summary>
    [Fact]
    public void R10_96_RNC13_TheEngineAndThePatternCountAreUnchangedAndThePredicateBuildsNoRegex()
    {
        var methods = typeof(ContentScreener).Assembly.GetTypes()
            .Where(t => t.Namespace == "Curia.Domain.Screening")
            .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(m => m.ReturnType == typeof(Regex) && m.GetParameters().Length == 0)
            .ToArray();
        var predicate = typeof(ContentScreener).Assembly.GetType("Curia.Domain.Screening.ReferencePassword")!;
        var members = predicate.GetMembers(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        var regexMembers = members.Where(m => m is FieldInfo { FieldType: var f } && f == typeof(Regex)
            || m is MethodInfo method && (method.ReturnType == typeof(Regex)
                || method.GetMethodBody()?.LocalVariables.Any(v => v.LocalType == typeof(Regex)) == true)).ToArray();

        Assert.Equal(15, methods.Length);
        Assert.True(methods.All(m => ((Regex)m.Invoke(null, null)!).Options.HasFlag(RegexOptions.NonBacktracking)), "a screening pattern is not non-backtracking");
        Assert.True(regexMembers.Length == 0, $"ReferencePassword builds a Regex: {string.Join(", ", regexMembers.Select(m => m.Name))}");
    }

    // ---- RNC14: the cost rows -------------------------------------------------------------------------------------------

    private static readonly string[] NewCostRows = ["reference-heavy", "at-sign-in-a-long-run"];

    /// <summary>RNC14: <c>ScreeningCostTests</c>' two theories each hold the rows <c>reference-heavy</c> and <c>at-sign-in-a-long-run</c>.</summary>
    [Fact]
    public void R10_96_RNC14_TheCostTheoriesHoldTheTwoNewRows()
    {
        var theories = typeof(ScreeningCostTests).GetMethods()
            .Where(m => m.GetCustomAttributes<Xunit.TheoryAttribute>().Any() && m.GetCustomAttributes<Xunit.InlineDataAttribute>().Any())
            .ToArray();
        Assert.Equal(2, theories.Length);

        var missing = new List<string>();
        foreach (var theory in theories)
        {
            var rows = theory.GetCustomAttributes<Xunit.InlineDataAttribute>().SelectMany(a => a.Data.Select(d => d?.ToString())).ToArray();
            foreach (var wanted in NewCostRows)
                if (!rows.Contains(wanted)) missing.Add($"{theory.Name}: no row named {wanted}");
        }

        Assert.True(missing.Count == 0, Describe([.. missing]));
    }

    // ---- RNC24: the errata and the corpus agree -------------------------------------------------------------------------

    /// <summary>
    /// RNC24: each of the 18, 12, 2 and 7 forms R10.96 names is contained in a row of the file it names with the outcome and expectation the paragraph states, and the
    /// counts are asserted after extraction (the extractor does it). The forms are read from the errata, the rows from the files, and no scanner is called.
    /// </summary>
    [Fact]
    public void R10_96_RNC24_TheErrataAndTheCorpusAgreeOnEveryForm()
    {
        var lists = new (string File, string Outcome, string[] Forms, string[] Expect)[]
        {
            ("benign.jsonl", "not-flagged", PublishedReferenceWords.Benign(), []),
            ("payloads.jsonl", "flagged", PublishedReferenceWords.Refused(), [Category]),
            ("known-false-positives.jsonl", "known-false-positive", PublishedReferenceWords.FalsePositives(), [Category]),
            ("known-evasions.jsonl", "expected-to-pass", PublishedReferenceWords.Evasions(), [Category]),
        };

        var failures = new List<string>();
        foreach (var (file, outcome, forms, expect) in lists)
        {
            foreach (var form in forms)
            {
                var rows = RowsHolding(file, form);
                if (rows.Length == 0) failures.Add($"{file}: no row holds {Show(form)}");
                foreach (var row in rows)
                {
                    if (row.Outcome != outcome) failures.Add($"{file}: {row.Id} has outcome {row.Outcome}, expected {outcome}");
                    if (!row.Expect.SequenceEqual(expect)) failures.Add($"{file}: {row.Id} expects [{string.Join(", ", row.Expect)}], expected [{string.Join(", ", expect)}]");
                }
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} reports: {Describe([.. failures])}");
    }

    // ---- RNC25: the name class is closed under the markup view's deletion -----------------------------------------------

    /// <summary>
    /// RNC25 (a): every name RNC4's generator reaches, with its underscores deleted, is accepted by <c>Scan</c> of the original and by <c>Scan</c> of the
    /// <c>unmarked-up</c> view, each separately (the whole screener cannot tell them apart: a view refusing is enough). Class reach asserted. RED on the seam.
    /// </summary>
    [Fact]
    public void R10_96_RNC25a_ANameWithItsUnderscoresDeletedIsAcceptedByTheOriginalAndByTheMarkupView()
    {
        var reached = new HashSet<string>();
        Gen.Select(Names, Gen.Int[0, 2], Gen.Int[0, Hosts.Length - 1])
            .Sample(
                t =>
                {
                    var name = t.Item1.Replace("_", string.Empty, StringComparison.Ordinal);
                    var text = string.Format(System.Globalization.CultureInfo.InvariantCulture, Hosts[t.Item3], Sigil(t.Item2, name));
                    lock (reached)
                    {
                        reached.Add($"sigil {t.Item2}");
                        reached.Add($"host {t.Item3}");
                        if (t.Item1.Contains('_', StringComparison.Ordinal)) reached.Add("a name that had underscores");
                    }

                    return ScanOriginal(text).Length == 0 && ScanView(text, "unmarked-up").Length == 0;
                },
                iter: 300,
                threads: 1);

        AssertReached(reached, ["sigil 0", "sigil 1", "sigil 2", "host 0", "host 1", "host 2", "host 3", "a name that had underscores"]);
    }

    /// <summary>
    /// RNC25 (b): the four forms R10.96 says are not references (read from its sentence), and <c>&lt;_1&gt;</c> (the same class rule for a bracketed name), are flagged by
    /// <c>Scan</c> of the original, in the keyword and the URI host. CONTROL on the seam. Through <c>ScreenEnvelope</c> alone this is vacuous: the markup view refuses them
    /// whatever the original's predicate does (trap 166).
    /// </summary>
    [Fact]
    public void R10_96_RNC25b_TheFormsThatAreNotReferencesAreFlaggedByTheOriginalsScan()
    {
        var paragraph = PublishedReferenceWords.Paragraph("**R10.96**");
        var end = paragraph.IndexOf(" are not references", StringComparison.Ordinal);
        Assert.True(end > 0, "R10.96 no longer says \"are not references\"");
        var named = PublishedReferenceWords.Spans(paragraph[paragraph.LastIndexOf(": ", end, StringComparison.Ordinal)..end]);
        Assert.Equal(4, named.Length);

        var failures = new List<string>();
        foreach (var form in named.Append("<_1>"))
        {
            if (ScanOriginal("password=" + form).Length == 0) failures.Add($"password={form}: expected a flag from Scan of the original, found none");
            if (ScanOriginal("postgres://u:" + form + "@h").Length == 0) failures.Add($"postgres://u:{form}@h: expected a flag from Scan of the original, found none");
        }

        Assert.True(failures.Count == 0, Describe([.. failures]));
    }
}
