using System.Globalization;
using System.Text.RegularExpressions;

namespace Curia.Tests.Shared;

/// <summary>
/// The words and figures of errata G57 (R6.60, R8.86, R10.94) as the errata publishes them, read at test time from <c>curia-whitepaper-ERRATA-AND-ADDENDUM.md</c> and
/// never from the code a probe is about: the three verdicts, the redecide and delta-too-large types with the cap, the status and the title of the cap's refusal, the
/// three submission refusals with their statuses and detail forms, the flag's contended refusal with its pass count, and the two log events with their ids and
/// messages. Each value is read from its requirement's <c>words</c> paragraph in context, and each parse asserts its count before it returns: an edit that removes or
/// duplicates a span turns a probe red by name and does not shrink what it compares (trap 78's device). Self-contained.
/// </summary>
internal static partial class PublishedLateCommitWords
{
    private const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";

    // ----- R6.60 words -----

    /// <summary>The three verdicts in the order the paragraph gives them: <c>Proceed</c>, <c>Refuse</c>, <c>Redecide</c>.</summary>
    internal static string[] Verdicts()
    {
        var m = One(VerdictsPhrase().Matches(Paragraph("R6.60 words")), "the three verdicts");
        return [m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value];
    }

    /// <summary>The type of the error a <c>Redecide</c> returns.</summary>
    internal static string RedecideType => One(RedecidePhrase().Matches(Paragraph("R6.60 words")), "the redecide type").Groups[1].Value;

    /// <summary>The cap on a delta, as published.</summary>
    internal static int DeltaCap => int.Parse(One(CapPhrase().Matches(Paragraph("R6.60 words")), "the cap").Groups[1].Value.Replace(",", string.Empty, StringComparison.Ordinal), CultureInfo.InvariantCulture);

    /// <summary>The error a delta over the cap returns: its type, its status and its title. The served body's <c>detail</c> is null.</summary>
    internal static (string Type, int Status, string Title) DeltaTooLarge()
    {
        var m = One(TooLargePhrase().Matches(Paragraph("R6.60 words")), "the delta-too-large refusal");
        return (m.Groups["type"].Value, int.Parse(m.Groups["status"].Value, CultureInfo.InvariantCulture), m.Groups["title"].Value);
    }

    // ----- R8.86 words -----

    /// <summary>The three submission refusals: type, status and the detail form (<c>post=&lt;post id&gt;</c>, <c>successor=&lt;post id&gt;</c>, and for a vote a description).</summary>
    internal static (string Type, int Status, string Detail)[] Refusals()
    {
        var text = Paragraph("R8.86 words");
        var recorded = One(RecordedPhrase().Matches(text), "already-recorded");
        var revised = One(RevisedPhrase().Matches(text), "already-revised");
        var voted = One(VotedPhrase().Matches(text), "already-voted");
        return
        [
            (recorded.Groups["type"].Value, int.Parse(recorded.Groups["status"].Value, CultureInfo.InvariantCulture), recorded.Groups["detail"].Value),
            (revised.Groups["type"].Value, int.Parse(revised.Groups["status"].Value, CultureInfo.InvariantCulture), revised.Groups["detail"].Value),
            (voted.Groups["type"].Value, int.Parse(voted.Groups["status"].Value, CultureInfo.InvariantCulture), voted.Groups["detail"].Value),
        ];
    }

    /// <summary>The event a guard refusal logs: name, id, level and message with <c>&lt;Kind&gt;</c> and <c>&lt;RefusalType&gt;</c> left in.</summary>
    internal static (string Name, int Id, string Level, string Message) RefusedEvent()
    {
        var m = One(RefusedEventPhrase().Matches(Paragraph("R8.86 words")), "the AppendGuardRefused event");
        return (m.Groups["name"].Value, int.Parse(m.Groups["id"].Value, CultureInfo.InvariantCulture), m.Groups["level"].Value, m.Groups["message"].Value);
    }

    // ----- R10.94 words -----

    /// <summary>The flag's contended refusal: type, status and title. The served body's <c>detail</c> is null.</summary>
    internal static (string Type, int Status, string Title) Contended()
    {
        var m = One(ContendedPhrase().Matches(Paragraph("R10.94 words")), "the contended refusal");
        return (m.Groups["type"].Value, int.Parse(m.Groups["status"].Value, CultureInfo.InvariantCulture), m.Groups["title"].Value);
    }

    /// <summary>The flag's pass count, as published.</summary>
    internal static int Passes => int.Parse(One(PassesPhrase().Matches(Paragraph("R10.94 words")), "the pass count").Groups[1].Value, CultureInfo.InvariantCulture);

    /// <summary>The event a redecided pass logs: name, id, level and message with <c>&lt;n&gt;</c> and <c>&lt;N&gt;</c> left in.</summary>
    internal static (string Name, int Id, string Level, string Message) RedecidedEvent()
    {
        var m = One(RedecidedEventPhrase().Matches(Paragraph("R10.94 words")), "the AppendGuardRedecided event");
        return (m.Groups["name"].Value, int.Parse(m.Groups["id"].Value, CultureInfo.InvariantCulture), m.Groups["level"].Value, m.Groups["message"].Value);
    }

    // ----- text helpers -----

    /// <summary>The paragraph of the errata that begins <c>**{name}.**</c>, to its blank line, whitespace folded. Exactly one must exist.</summary>
    internal static string Paragraph(string name)
    {
        var text = ErrataText();
        var anchor = $"**{name}.**";
        var start = text.IndexOf(anchor, StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException($"{Errata} has no paragraph that begins \"{anchor}\"");
        if (text.IndexOf(anchor, start + anchor.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException($"{Errata} has more than one paragraph that begins \"{anchor}\"");

        var end = text.IndexOf("\n\n", start + anchor.Length, StringComparison.Ordinal);
        return Whitespace().Replace(text[start..(end < 0 ? text.Length : end)], " ");
    }

    private static Match One(MatchCollection matches, string what) =>
        matches.Count == 1
            ? matches[0]
            : throw new InvalidOperationException($"{Errata} yields {matches.Count} readings of {what}, expected one");

    private static string ErrataText()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, Errata)))
            dir = dir.Parent;

        return dir is null
            ? throw new InvalidOperationException($"{Errata} not found above {AppContext.BaseDirectory}")
            : File.ReadAllText(Path.Combine(dir.FullName, Errata));
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"The three verdicts are `([A-Za-z]+)`, `([A-Za-z]+)` and `([A-Za-z]+)`\.", RegexOptions.CultureInvariant)]
    private static partial Regex VerdictsPhrase();

    [GeneratedRegex(@"returns has the type `(curia/append/redecide)`", RegexOptions.CultureInvariant)]
    private static partial Regex RedecidePhrase();

    [GeneratedRegex(@"The cap is ([\d,]+) events", RegexOptions.CultureInvariant)]
    private static partial Regex CapPhrase();

    [GeneratedRegex(@"has the type `(?<type>curia/append/delta-too-large)`, the status `(?<status>\d+)`, the title `(?<title>[^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex TooLargePhrase();

    [GeneratedRegex(@"`(?<type>curia/ingest/already-recorded)`, status `(?<status>\d+)`, detail exactly `(?<detail>[^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex RecordedPhrase();

    [GeneratedRegex(@"`(?<type>curia/ingest/already-revised)`, status `(?<status>\d+)`, detail exactly `(?<detail>[^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex RevisedPhrase();

    [GeneratedRegex(@"`(?<type>curia/verification/already-voted)`, status `(?<status>\d+)`, detail (?<detail>the target digest)", RegexOptions.CultureInvariant)]
    private static partial Regex VotedPhrase();

    [GeneratedRegex(@"The log event is `(?<name>AppendGuardRefused)` \(id (?<id>\d+), (?<level>[a-z]+)\): `(?<message>[^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex RefusedEventPhrase();

    [GeneratedRegex(@"The type is `(?<type>curia/flag/contended)` and the status `(?<status>\d+)`\. The title is `(?<title>[^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex ContendedPhrase();

    [GeneratedRegex(@"The pass count is (\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex PassesPhrase();

    [GeneratedRegex(@"The log event is `(?<name>AppendGuardRedecided)` \(id (?<id>\d+), (?<level>[a-z]+)\): `(?<message>[^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex RedecidedEventPhrase();
}
