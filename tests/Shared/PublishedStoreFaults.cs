using System.Globalization;
using System.Text.RegularExpressions;

namespace Curia.Tests.Shared;

/// <summary>
/// The words of errata G52 (R5.23, R7.31, R8.82, R9.52, R11.71, R11.72) as the errata publishes them, read at test time from
/// <c>curia-whitepaper-ERRATA-AND-ADDENDUM.md</c> and never from the code a probe is about (the D95 PR's Task 0b): the two titles and types, the token's
/// code and description, the unclassified fault's type and title, the three client sentences, the depth,, the event name and id. The version is read by the one reader of a <c>curia-mcp</c> version (<c>PublishedReadersLastWords.Version()</c>), not here (RLW40). Each value is
/// read from its requirement's <c>words</c> paragraph in context, so a bare token another paragraph could supply is not matched, and each parse asserts
/// its count before it returns: an edit that removes a span turns a probe red by name and does not shrink what it compares. Self-contained, so every
/// test assembly that links it needs nothing else.
/// </summary>
internal static partial class PublishedStoreFaults
{
    private const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";

    // ----- the two 503 types (R8.82 words) -----

    /// <summary>The type for a read of the log that failed.</summary>
    internal static string LogUnreadableType => Pair(0).Type;

    /// <summary>The title of <see cref="LogUnreadableType"/>.</summary>
    internal static string LogUnreadableTitle => Pair(0).Title;

    /// <summary>The type for any other store, the append included.</summary>
    internal static string StoreUnavailableType => Pair(1).Type;

    /// <summary>The title of <see cref="StoreUnavailableType"/>.</summary>
    internal static string StoreUnavailableTitle => Pair(1).Title;

    /// <summary>The status of each, from "the status of each is `503`".</summary>
    internal static int UnavailableStatus => int.Parse(One(Group(StatusOfEach(), WordsParagraph("R8.82"), "R8.82's status")), CultureInfo.InvariantCulture);

    /// <summary>The log event name for a served store fault: the span after "The log event name for a served store fault is".</summary>
    internal static string EventName => One(Group(EventNamePhrase(), WordsParagraph("R8.82"), "R8.82's event name"));

    /// <summary>Its id, from "its id 5002".</summary>
    internal static int EventId => int.Parse(One(Group(EventIdPhrase(), WordsParagraph("R8.82"), "R8.82's event id")), CultureInfo.InvariantCulture);

    /// <summary>The id the existing server-fault line keeps: "keeps id 5000".</summary>
    internal static int ServerFaultEventId => int.Parse(One(Group(ServerFaultIdPhrase(), WordsParagraph("R8.82"), "R8.82's server-fault id")), CultureInfo.InvariantCulture);

    // ----- the log type again (R9.52 words) -----

    /// <summary>R9.52's type for the log: held equal to R8.82's by <see cref="LogUnreadableType"/>'s own reading.</summary>
    internal static string RouteType => One(Group(RouteTypePhrase(), WordsParagraph("R9.52"), "R9.52's type"));

    // ----- the token (R5.23 words) -----

    /// <summary>The OAuth code: the span after "The code is".</summary>
    internal static string TokenCode => One(Group(TokenCodePhrase(), WordsParagraph("R5.23"), "R5.23's code"));

    /// <summary>The token's status, from "the status is `503`".</summary>
    internal static int TokenStatus => int.Parse(One(Group(TokenStatusPhrase(), WordsParagraph("R5.23"), "R5.23's status")), CultureInfo.InvariantCulture);

    /// <summary>The fixed description: the span after "the fixed description is".</summary>
    internal static string TokenDescription => One(Group(TokenDescriptionPhrase(), WordsParagraph("R5.23"), "R5.23's description"));

    /// <summary>The Forum's own fault at the token endpoint: the code kept ("The code kept for the Forum's own fault is `server_error`").</summary>
    internal static string TokenOwnFaultCode => One(Group(OwnFaultCodePhrase(), WordsParagraph("R5.23"), "R5.23's kept code"));

    /// <summary>The description kept with it: the span after "The code kept for the Forum's own fault is `server_error`, status `500`, description".</summary>
    internal static string TokenOwnFaultDescription => One(Group(OwnFaultDescriptionPhrase(), WordsParagraph("R5.23"), "R5.23's kept description"));

    // ----- the unclassified fault (R11.71 words) -----

    /// <summary>The type for an exception nothing names.</summary>
    internal static string FaultType => One(Group(FaultTypePhrase(), WordsParagraph("R11.71"), "R11.71's fault type"));

    /// <summary>Its title.</summary>
    internal static string FaultTitle => One(Group(FaultTitlePhrase(), WordsParagraph("R11.71"), "R11.71's fault title"));

    /// <summary>Its status, from "the status `500`".</summary>
    internal static int FaultStatus => int.Parse(One(Group(FaultStatusPhrase(), WordsParagraph("R11.71"), "R11.71's fault status")), CultureInfo.InvariantCulture);

    /// <summary>The predicate's depth, from "a depth of eight".</summary>
    internal static int Depth
    {
        get
        {
            var word = One(Group(DepthPhrase(), WordsParagraph("R11.71"), "R11.71's depth"));
            return word switch
            {
                "eight" => 8,
                _ => int.TryParse(word, CultureInfo.InvariantCulture, out var n) ? n : throw new InvalidOperationException($"{Errata}'s R11.71 depth is \"{word}\""),
            };
        }
    }

    // ----- the clients' sentences (R11.72 words) -----

    /// <summary>The sentence for <c>curia/log/unreadable</c>: the span after "For `curia/log/unreadable`:".</summary>
    internal static string LogUnreadableSentence => Sentence(LogUnreadableType);

    /// <summary>The sentence for <c>curia/store/unavailable</c>. It is also the sentence the write tools' descriptions and the writing verbs' helps carry.</summary>
    internal static string StoreUnavailableSentence => Sentence(StoreUnavailableType);

    /// <summary>The sentence for the token: the span after "For the token:".</summary>
    internal static string TokenSentence => One(Group(TokenSentencePhrase(), WordsParagraph("R11.72"), "R11.72's token sentence"));

    /// <summary>The sentence the descriptions of the write tools and the helps of the writing verbs carry: R11.72 says it is the second.</summary>
    internal static string RepeatSentence => StoreUnavailableSentence;

    // ----- text helpers -----

    /// <summary>The whole <c>words</c> paragraph of <paramref name="requirement"/> (for example <c>R8.82</c>), whitespace folded; throws where it is absent.</summary>
    internal static string WordsParagraph(string requirement)
    {
        var text = ErrataText();
        var anchor = $"**{requirement} words.**";
        var start = text.IndexOf(anchor, StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException($"{Errata} has no paragraph that begins \"{anchor}\"");
        var end = text.IndexOf("\n\n", start + anchor.Length, StringComparison.Ordinal);
        return Whitespace().Replace(text[start..(end < 0 ? text.Length : end)], " ");
    }

    /// <summary>The paragraph that begins with <paramref name="anchor"/>, to its blank line, whitespace folded.</summary>
    internal static string Paragraph(string anchor)
    {
        var text = ErrataText();
        var start = text.IndexOf("\n" + anchor, StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException($"{Errata} has no paragraph that begins \"{anchor}\"");
        var end = text.IndexOf("\n\n", start + 1, StringComparison.Ordinal);
        return Whitespace().Replace(text[(start + 1)..(end < 0 ? text.Length : end)], " ");
    }

    /// <summary>True where <paramref name="text"/> (whitespace folded) holds <paramref name="published"/> (whitespace folded).</summary>
    internal static bool Says(string text, string published) =>
        Whitespace().Replace(text, " ").Contains(Whitespace().Replace(published, " "), StringComparison.Ordinal);

    /// <summary>The paragraph of the errata's text, whitespace folded, for a scan that must not find a published sentence copied by hand.</summary>
    internal static string[] AllSentences() =>
        [LogUnreadableSentence, StoreUnavailableSentence, TokenSentence, TokenDescription, TokenOwnFaultDescription, LogUnreadableTitle, StoreUnavailableTitle, FaultTitle];

    private static (string Type, string Title) Pair(int index)
    {
        var matches = TypeWithTitle().Matches(WordsParagraph("R8.82"));
        if (matches.Count != 2)
            throw new InvalidOperationException($"{Errata}'s R8.82 words yield {matches.Count} readings of a type with its title, expected two");

        return (matches[index].Groups[1].Value, matches[index].Groups[2].Value);
    }

    private static string Sentence(string type)
    {
        var matches = Regex.Matches(WordsParagraph("R11.72"), @"For `" + Regex.Escape(type) + @"`: `([^`]+)`", RegexOptions.CultureInvariant);
        return matches.Count == 1
            ? matches[0].Groups[1].Value
            : throw new InvalidOperationException($"{Errata}'s R11.72 words yield {matches.Count} sentences for {type}, expected one");
    }

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

    [GeneratedRegex(@"`(curia/[a-z/-]+)` with the title `([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex TypeWithTitle();

    [GeneratedRegex(@"the status of each is `(\d+)`", RegexOptions.CultureInvariant)]
    private static partial Regex StatusOfEach();

    [GeneratedRegex(@"The log event name for a served store fault is `([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex EventNamePhrase();

    [GeneratedRegex(@"The log event name for a served store fault is `[^`]+`, its id (\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex EventIdPhrase();

    [GeneratedRegex(@"served by the existing `ServerFault` keeps id (\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex ServerFaultIdPhrase();

    [GeneratedRegex(@"The type is `(curia/log/unreadable)` and the title is as above", RegexOptions.CultureInvariant)]
    private static partial Regex RouteTypePhrase();

    [GeneratedRegex(@"The code is `([^`]+)`; the status is `\d+`", RegexOptions.CultureInvariant)]
    private static partial Regex TokenCodePhrase();

    [GeneratedRegex(@"The code is `[^`]+`; the status is `(\d+)`", RegexOptions.CultureInvariant)]
    private static partial Regex TokenStatusPhrase();

    [GeneratedRegex(@"the fixed description is `([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex TokenDescriptionPhrase();

    [GeneratedRegex(@"The code kept for the Forum's own fault is `([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex OwnFaultCodePhrase();

    [GeneratedRegex(@"The code kept for the Forum's own fault is `[^`]+`, status `\d+`, description `([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex OwnFaultDescriptionPhrase();

    [GeneratedRegex(@"The type for an unclassified exception is `([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex FaultTypePhrase();

    [GeneratedRegex(@"The type for an unclassified exception is `[^`]+`, the title `([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex FaultTitlePhrase();

    [GeneratedRegex(@"The type for an unclassified exception is `[^`]+`, the title `[^`]+`, the status `(\d+)`", RegexOptions.CultureInvariant)]
    private static partial Regex FaultStatusPhrase();

    [GeneratedRegex(@"and a depth of (\w+)", RegexOptions.CultureInvariant)]
    private static partial Regex DepthPhrase();

    [GeneratedRegex(@"For the token: `([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex TokenSentencePhrase();
}
