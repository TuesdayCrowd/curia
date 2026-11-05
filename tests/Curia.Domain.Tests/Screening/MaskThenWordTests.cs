using System.Diagnostics.CodeAnalysis;
using Xunit;
using static Curia.Domain.Tests.Screening.ReferenceProbe;

namespace Curia.Domain.Tests.Screening;

/// <summary>
/// R10.96 (errata G60, Amendment 2 of its plan), probes RNC28 to RNC31: a mask followed by a word. The <c>unmarked-up</c> view deletes a run of <c>*</c>, so
/// <c>password=**** after masking.</c> reads there as <c>password= after masking.</c> and the rule's blanks after the sign read <c>after</c> as the password;
/// the screen therefore does not report, from that one view, a keyword match whose start is, in the original, a match the original cleared as a reference
/// (decision 3). Every claim is held at the whole screener, in the corpus's three shapes (trap 169: a per-view sweep asks each view alone, so it can neither
/// show an exception placed above the views nor see one removed). Forms are the errata's or literals; nothing here asks the exception.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class MaskThenWordTests
{
    private static string Describe(string[] fired) =>
        string.Join("; ", fired.Take(40)) + (fired.Length > 40 ? $"; and {fired.Length - 40} more" : string.Empty);

    // ---- RNC28: a mask followed by a word -------------------------------------------------------------------------------

    private static readonly string[] MaskThenWord =
    [
        "The log shows password=**** after masking.",
        "The config line reads password=****\nThen the service restarts on the next deploy.",
        // Plan text "Pwd=**** and then the rest" is quiet on the unmodified tree: `and` is under the four-character floor, so the markup view never reads it as a
        // value and FRNC44 (the exception omitted for `pwd`) could not be red. `then` is four characters, so this text is red now and reaches the `pwd` alternative.
        "Server=db;Pwd=**** then the rest",
        "password=****....",
        // The open check of the fix round: the emphasis the markup view also deletes does not move the mapped start off the keyword's `p`, so this clears too.
        "**password=**** after**",
    ];

    /// <summary>
    /// RNC28: a run of four asterisks followed by a word, a line or a full stop is not refused in any shape. CONTROL leg in the same fact: the
    /// <c>unmarked-up</c> view's own <c>Scan</c> of the first, with no original beside it, still flags, so a later move of the exception into the scanner is seen.
    /// </summary>
    [Fact]
    public void R10_96_RNC28_AMaskFollowedByAWordIsNotRefusedInAnyShape()
    {
        var found = MaskThenWord.SelectMany(t => FlaggedIn(t).Select(f => $"{Show(t)}: expected quiet, {f}")).ToArray();
        var control = ScanView(MaskThenWord[0], "unmarked-up");

        // The exception is keyed to the keyword's own offset: a cleared keyword is not flagged while another keyword of the same text, read by the markup view
        // alone, is (RNC29's last case holds the second; this holds the first).
        var pair = KeepsCleared + "pass**word= " + Secret;
        found = [.. found, .. Flags(Shapes[0], pair).Where(f => f.Offset == 0).Select(f => $"{Show(pair)}: expected nothing at the cleared keyword's offset 0, found a flag at {f.Offset}+{f.Length} (bare)")];

        Assert.True(found.Length == 0, $"{found.Length} reports: {Describe(found)}");
        Assert.True(control.Length > 0, "the control leg: Scan of the unmarked-up view alone, with no original, must still flag the first form");
    }

    // ---- RNC29: the exception admits no value ---------------------------------------------------------------------------

    private const string KeepsCleared = "password=**** then\n";

    /// <summary>
    /// RNC29: the exception admits no value. In the bare shape the flag lies at the keyword's own offset (and runs to the end of the value) for each of: a mask
    /// the original refuses, a three-asterisk mask (no reference, the original has no match), a keyword the original does not read (<c>pass**word=</c>), and a
    /// text whose first keyword the original clears while its second is read by the markup view alone -- the second is still flagged at its own offset. Every
    /// shape flags. CONTROLS on the unmodified tree (everything here is refused there); red under FRNC41 (the despaced text), FRNC42, FRNC43 and FRNC45. That the first keyword is
    /// not flagged is RNC28's.
    /// </summary>
    [Fact]
    public void R10_96_RNC29_TheExceptionAdmitsNoValue()
    {
        (string Name, string Text, int Offset)[] cases =
        [
            ("a mask the original refuses", "password=****" + Secret, 0),
            ("three asterisks", "password=*** " + Secret, 0),
            ("a split keyword", "pass**word= " + Secret, 0),
            // Found by operating the screen (ruling 42, step 4): the one text a view other than `unmarked-up` refuses while the original clears it. The despaced view
            // drops a blank between two single characters, and `C` after `_` is one, so `$AB_C x y z` reads there as the value `$AB_Cxyz`. The exception is for the
            // markup view alone, so this stays refused (the old behaviour); the base64, rot13 and unconfused views cannot differ from the original at a cleared keyword.
            ("a reference the despaced view joins to single-character words", "password=$AB_C x y z", 0),
            ("a split keyword after a cleared one", KeepsCleared + "pass**word= " + Secret, KeepsCleared.Length),
        ];

        var failures = new List<string>();
        foreach (var (name, text, offset) in cases)
        {
            var bare = Flags(Shapes[0], text);
            if (!bare.Any(f => f.Offset == offset && f.Offset + f.Length == text.Length))
                failures.Add($"{name} {Show(text)}: expected a flag at {offset} to the end, found [{string.Join(", ", bare.Select(f => $"{f.Offset}+{f.Length}"))}]");
            foreach (var shape in Shapes.Skip(1))
                if (Flags(shape, text).Length == 0) failures.Add($"{name} ({shape.Name}): expected a flag, found none");
        }

        Assert.True(failures.Count == 0, $"{failures.Count} reports: {Describe([.. failures])}");
    }

    // ---- RNC30: the @ read's other stops --------------------------------------------------------------------------------

    private static readonly string[] AtQuiet =
    [
        "https://u:$DB_PASS@h#a@b",
        "postgres://u:$DB_PASS@h and mail a@b.c",
    ];

    /// <summary>RNC30: a reference whose only further <c>@</c> lies past a <c>#</c> or a blank is a reference (quiet); the positive in the same fact is the same reference before a second <c>@</c> in the authority (flagged). CONTROL; red under FRNC46.</summary>
    [Fact]
    public void R10_96_RNC30_AFragmentOrABlankEndsTheAtRead()
    {
        var quiet = AtQuiet.SelectMany(t => FlaggedIn(t).Select(f => $"{Show(t)}: expected quiet, {f}")).ToArray();
        var positive = FlaggedIn("postgres://u:$DB_PASS@u2:" + Secret + "@h/db");

        Assert.True(positive.Length > 0, "the positive leg: a second @ in the authority is still refused");
        Assert.True(quiet.Length == 0, Describe(quiet));
    }

    // ---- RNC31: the two new evasions are recorded and quiet -------------------------------------------------------------

    private static readonly string[] NewEvasions =
    [
        "password=**** " + Secret,
        "password=****\n" + Secret,
        "password=$DB_PASS\"" + Secret + "\"",
    ];

    /// <summary>
    /// RNC31: the mask followed by a word (also over a line break) and the reference followed by a quotation mark are quiet in each shape, and each is a row of
    /// <c>known-evasions.jsonl</c> whose reason names the non-property that records it, (l) or (m).
    /// </summary>
    [Fact]
    public void R10_96_RNC31_TheTwoNewEvasionsAreQuietAndRecordedWithTheirReason()
    {
        var failures = NewEvasions.SelectMany(t => FlaggedIn(t).Select(f => $"{Show(t)}: expected quiet, {f}")).ToList();

        var evasions = Load("known-evasions.jsonl");
        foreach (var (form, property) in new[] { ("password=**** " + Secret, "non-property (l)"), ("password=$DB_PASS\"" + Secret + "\"", "non-property (m)") })
        {
            var rows = evasions.Where(r => string.Equals(r.Content, form, StringComparison.Ordinal)).ToArray();
            if (rows.Length != 1) { failures.Add($"{Show(form)}: expected one row of known-evasions.jsonl, found {rows.Length}"); continue; }
            if (!rows[0].Why.Contains(property, StringComparison.Ordinal)) failures.Add($"{rows[0].Id}: expected the reason to name {property}");
        }

        Assert.True(failures.Count == 0, $"{failures.Count} reports: {Describe([.. failures])}");
    }
}
