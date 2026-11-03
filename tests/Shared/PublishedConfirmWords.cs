using System.Globalization;
using System.Text.RegularExpressions;

namespace Curia.Tests.Shared;

/// <summary>
/// The words and figures of errata G55 (R8.84, R11.76, R11.77) as the errata publishes them, read at test time from <c>curia-whitepaper-ERRATA-AND-ADDENDUM.md</c> and
/// never from the code a probe is about: the confirm's bound, its five outcomes, the two log events with their ids and messages, R11.76's store sentence, R11.77's
/// unheard sentence, the unheard refusal's type and title, and the title of a request that provably was not sent. The version is read by the one parser <c>PublishedReadersLastWords</c> holds, and not here. Each value is read from its requirement's
/// <c>words</c> paragraph in context, so a bare token another paragraph could supply is not matched, and each parse asserts its count before it returns: an edit that
/// removes or duplicates a span turns a probe red by name and does not shrink what it compares. Self-contained, so every test assembly that links it needs nothing else.
/// </summary>
internal static partial class PublishedConfirmWords
{
    private const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";

    // ----- R8.84 words -----

    /// <summary>R8.84 words: the confirm's bound in seconds (<c>ConfirmBound</c>), as published.</summary>
    internal static int ConfirmBoundSeconds => int.Parse(One(Group(BoundPhrase(), Paragraph("R8.84 words"), "R8.84 words' bound")), CultureInfo.InvariantCulture);

    /// <summary>The confirm's outcomes in the order the paragraph gives them: <c>found</c>, <c>absent</c>, <c>unreadable</c>, <c>bound</c>, <c>stopped</c>. The count is asserted: five.</summary>
    internal static string[] Outcomes()
    {
        var names = OutcomesPhrase().Match(Paragraph("R8.84 words"));
        if (!names.Success) throw new InvalidOperationException($"{Errata}'s R8.84 words yield no outcome list");
        var all = new[] { names.Groups[1].Value, names.Groups[2].Value, names.Groups[3].Value, names.Groups[4].Value, names.Groups[5].Value };
        return all.Length == 5 ? all : throw new InvalidOperationException($"{Errata}'s R8.84 words yield {all.Length} outcomes, expected five");
    }

    /// <summary>The event published for a confirmed post: its name, its id and its message with <c>&lt;Kind&gt;</c> and <c>&lt;FaultType&gt;</c> left in.</summary>
    internal static (string Name, int Id, string Message) ConfirmedEvent() => Event("AppendConfirmed");

    /// <summary>The event published for a post not confirmed: its name, its id and its message with <c>&lt;Kind&gt;</c>, <c>&lt;FaultType&gt;</c> and <c>&lt;Outcome&gt;</c> left in.</summary>
    internal static (string Name, int Id, string Message) NotConfirmedEvent() => Event("AppendNotConfirmed");

    private static (string Name, int Id, string Message) Event(string name)
    {
        var matches = EventPhrase().Matches(Paragraph("R8.84 words")).Where(m => m.Groups["name"].Value == name).ToArray();
        return matches.Length == 1
            ? (name, int.Parse(matches[0].Groups["id"].Value, CultureInfo.InvariantCulture), matches[0].Groups["message"].Value)
            : throw new InvalidOperationException($"{Errata}'s R8.84 words yield {matches.Length} readings of the event {name}, expected one");
    }

    // ----- R11.76 words, R11.77 words -----

    /// <summary>The store sentence for a signed post (errata G58's R11.80 clause words (post), which replaced G55's R11.76 words), whitespace folded.</summary>
    internal static string StoreSentence => PublishedResendWords.Clauses().Post;

    /// <summary>The unheard sentence (errata G58's R11.80 unheard words, which replaced G55's R11.77 words' sentence), whitespace folded.</summary>
    internal static string UnheardSentence => PublishedResendWords.UnheardWords;

    /// <summary>R11.77 words: the type and the title of the client's refusal for an unheard request, in that order.</summary>
    internal static (string Type, string Title) UnheardRefusal()
    {
        var m = UnheardRefusalPhrase().Matches(Paragraph("R11.77 words"));
        return m.Count == 1
            ? (m[0].Groups["type"].Value, m[0].Groups["title"].Value)
            : throw new InvalidOperationException($"{Errata}'s R11.77 words yield {m.Count} readings of the unheard refusal, expected one");
    }

    /// <summary>R11.77 words: the title of a request that provably was not sent, which stays as it was.</summary>
    internal static string NotSentTitle => One(Group(NotSentTitlePhrase(), Paragraph("R11.77 words"), "R11.77's not-sent title"));

    /// <summary>Every sentence and long phrase G55 introduces, which a test must not write by hand.</summary>
    internal static string[] AllPublished() => [StoreSentence, UnheardSentence, ConfirmedEvent().Message, NotConfirmedEvent().Message];

    // ----- text helpers -----

    /// <summary>The paragraph of the errata that begins <c>**{name}.**</c> (for example <c>R8.84 words</c>), to its blank line, whitespace folded. Exactly one must exist.</summary>
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

    /// <summary>True where <paramref name="text"/> (whitespace folded) holds <paramref name="published"/> (whitespace folded).</summary>
    internal static bool Says(string text, string published) =>
        Whitespace().Replace(text, " ").Contains(Whitespace().Replace(published, " "), StringComparison.Ordinal);

    /// <summary>Whitespace folded to single spaces.</summary>
    internal static string Fold(string text) => Whitespace().Replace(text, " ");

    private static MatchCollection Group(Regex pattern, string paragraph, string what)
    {
        var matches = pattern.Matches(paragraph);
        return matches.Count == 1
            ? matches
            : throw new InvalidOperationException($"{Errata} yields {matches.Count} readings of {what}, expected one");
    }

    private static string One(MatchCollection matches) => matches[0].Groups[1].Value;

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

    [GeneratedRegex(@"The figure is the confirm's bound, (\d+) seconds \(`ConfirmBound`", RegexOptions.CultureInvariant)]
    private static partial Regex BoundPhrase();

    [GeneratedRegex(@"The confirm's outcomes are `([a-z]+)`, `([a-z]+)`, `([a-z]+)`, `([a-z]+)` and `([a-z]+)`\.", RegexOptions.CultureInvariant)]
    private static partial Regex OutcomesPhrase();

    [GeneratedRegex(@"`(?<name>Append(?:Not)?Confirmed)` \(id (?<id>\d+), warning[^)]*\): `(?<message>[^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex EventPhrase();

    [GeneratedRegex(@"\*\*R11\.76 words\.\*\* The store sentence, replacing the sentence of R11\.73 words for `curia/store/unavailable`: `([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex StoreSentencePhrase();

    [GeneratedRegex(@"The unheard sentence: `([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex UnheardSentencePhrase();

    [GeneratedRegex(@"has the type `(?<type>[^`]+)` and the title `(?<title>[^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex UnheardRefusalPhrase();

    [GeneratedRegex(@"the title of a request that provably was not sent stays `([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex NotSentTitlePhrase();
}
