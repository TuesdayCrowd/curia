using System.Text;
using System.Text.RegularExpressions;

namespace Curia.Tests.Shared;

/// <summary>
/// The words of errata G60 as the errata publishes them, read at test time from <c>curia-whitepaper-ERRATA-AND-ADDENDUM.md</c> and never from the code a
/// fact is about: R10.96's four lists of forms (the sixteen the screen clears, the ten that stay refused, the two known false positives, the five it lets
/// through) and R11.84's two sentences. A fact that took a list from the predicate it checks would be satisfied by whatever the predicate does (vacuity
/// question 4); this is the other side of the comparison.
///
/// <para>A form or a sentence is a Markdown code span, read by the rule Markdown states: a span opened by n backticks closes at the next run of exactly n,
/// and one space at each end is dropped when both are there (so a form that holds a backtick, <c>`--password=$DB_PASS`</c>, is written in two). Each list is
/// the spans between its own marker phrase and the next, <b>asserted by content and not only by count</b>: a marker phrase must occur exactly once, the
/// count must be the one the PR is built to (16, 10, 2, 5), and a form each list is known to hold must be in it, before the list is returned (traps 141 and
/// 142).</para>
/// </summary>
internal static partial class PublishedReferenceWords
{
    private const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";

    private const string BenignMarker = "The benign corpus SHALL hold, each with a value of at least four characters,";
    private const string PayloadMarker = "the red-team corpus SHALL hold the forms that stay refused,";
    private const string FalsePositiveMarker = "the known-false-positives list SHALL hold";
    private const string EvasionMarker = "the known-evasions list SHALL hold what the rule lets through,";
    private const string EndMarker = "each with its reason";

    /// <summary>R10.96's forms the screen clears: sixteen, each a benign row.</summary>
    internal static string[] Benign() => List(BenignMarker, PayloadMarker, 16, "`--password=$DB_PASS`", "Server=db;Pwd=****;");

    /// <summary>R10.96's forms that stay refused: ten, each a payload row.</summary>
    internal static string[] Refused() => List(PayloadMarker, FalsePositiveMarker, 10, "postgres://u:EXAMPLE-NOT-A-REAL-SECRET@h", "Pwd=C:EXAMPLE-NOT-A-REAL-SECRET");

    /// <summary>R10.96's known false positives: two.</summary>
    internal static string[] FalsePositives() => List(FalsePositiveMarker, EvasionMarker, 2, "password=changeme", "password=$password");

    /// <summary>R10.96's forms the rule lets through: five.</summary>
    internal static string[] Evasions() => List(EvasionMarker, EndMarker, 5, "postgres://u:$HUNTER2EXAMPLE@h", "\"password\": \"EXAMPLE-NOT-A-REAL-SECRET\"");

    /// <summary>R11.84's sentence the refusal carries after the instruction to rotate.</summary>
    internal static string RefusalSentence() => Sentence("**R11.84 words (the refusal's sentence).**", "If the ConnectionStringPassword hit is a reference to a secret and not the secret,");

    /// <summary>R11.84's sentence a write tool's description and <c>curia help</c> carry.</summary>
    internal static string DescriptionSentence() => Sentence("**R11.84 words (the write tools' description and `curia help`).**", "A reference to a secret is not a credential:");

    /// <summary>R10.96's detector version: the code span that follows "the detector version SHALL be", which must be a dated <c>secrets/</c> name.</summary>
    internal static string DetectorVersion()
    {
        var paragraph = Paragraph("**R10.96**");
        const string marker = "the detector version SHALL be ";
        var at = paragraph.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
            throw new InvalidOperationException($"{Errata}'s R10.96 no longer says \"{marker}\"");

        var span = Spans(paragraph[(at + marker.Length)..]).FirstOrDefault() ?? string.Empty;
        var dates = DatedVersion().Matches(span);
        if (dates.Count != 1)
            throw new InvalidOperationException($"R10.96's detector version yields {dates.Count} dates: \"{span}\" is not a date");

        return dates[0].Value;
    }

    private static string[] List(string startMarker, string endMarker, int count, params string[] holds)
    {
        var paragraph = Paragraph("**R10.96**");
        var start = Once(paragraph, startMarker);
        var end = Once(paragraph, endMarker);
        if (end <= start)
            throw new InvalidOperationException($"{Errata}'s R10.96 no longer has \"{startMarker}\" before \"{endMarker}\"");

        var spans = Spans(paragraph[(start + startMarker.Length)..end]);
        if (spans.Length != count)
            throw new InvalidOperationException($"{Errata}'s R10.96 list after \"{startMarker}\" yields {spans.Length} forms, expected {count}: {string.Join(" | ", spans)}");

        foreach (var form in holds)
            if (!spans.Contains(form, StringComparer.Ordinal))
                throw new InvalidOperationException($"{Errata}'s R10.96 list after \"{startMarker}\" no longer holds {form}: {string.Join(" | ", spans)}");

        return spans;
    }

    private static string Sentence(string anchor, string startsWith)
    {
        var spans = Spans(Paragraph(anchor)[Whitespace().Replace(anchor, " ").Length..]);
        if (spans.Length != 1)
            throw new InvalidOperationException($"{Errata}'s paragraph \"{anchor}\" yields {spans.Length} code spans after its heading, expected one");

        if (!spans[0].StartsWith(startsWith, StringComparison.Ordinal))
            throw new InvalidOperationException($"{Errata}'s paragraph \"{anchor}\" yields a sentence that no longer begins \"{startsWith}\": {spans[0]}");

        return spans[0];
    }

    private static int Once(string paragraph, string marker)
    {
        var at = paragraph.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0 || paragraph.IndexOf(marker, at + 1, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException($"{Errata}'s R10.96 does not hold \"{marker}\" exactly once");

        return at;
    }

    /// <summary>The code spans of <paramref name="text"/> by Markdown's rule: opened by a run of n backticks, closed at the next run of exactly n.</summary>
    internal static string[] Spans(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var spans = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] != '`') { i++; continue; }

            var n = RunLength(text, i);
            var open = i + n;
            var j = open;
            var closed = -1;
            while (j < text.Length)
            {
                if (text[j] != '`') { j++; continue; }

                var m = RunLength(text, j);
                if (m == n) { closed = j; break; }

                j += m;
            }

            if (closed < 0) { i = open; continue; }

            var content = text[open..closed];
            if (content.Length > 1 && content[0] == ' ' && content[^1] == ' ' && content.Any(c => c != ' '))
                content = content[1..^1];

            spans.Add(content);
            i = closed + n;
        }

        return [.. spans];
    }

    private static int RunLength(string text, int at)
    {
        var n = 0;
        while (at + n < text.Length && text[at + n] == '`') n++;
        return n;
    }

    /// <summary>The paragraph that begins with <paramref name="anchor"/>, to the blank line that ends it, whitespace folded.</summary>
    internal static string Paragraph(string anchor)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, Errata)))
            dir = dir.Parent;

        if (dir is null)
            throw new InvalidOperationException($"{Errata} not found above {AppContext.BaseDirectory}");

        var text = File.ReadAllText(Path.Combine(dir.FullName, Errata));
        var start = text.IndexOf(anchor, StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidOperationException($"{Errata} has no paragraph that begins \"{anchor}\"");

        var end = text.IndexOf("\n\n", start, StringComparison.Ordinal);
        return Whitespace().Replace(text[start..(end < 0 ? text.Length : end)], " ");
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^secrets/\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex DatedVersion();
}
