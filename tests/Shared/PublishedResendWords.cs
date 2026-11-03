using System.Globalization;
using System.Text.RegularExpressions;

namespace Curia.Tests.Shared;

/// <summary>
/// The words and figures of errata G58 (R7.32, R10.95, R11.79, R11.80, R11.81) as the errata publishes them, read at test time from <c>curia-whitepaper-ERRATA-AND-ADDENDUM.md</c> and
/// never from the code a probe is about. Every value is read from its requirement's paragraph in context, and each parse asserts its count before it returns: an edit that removes
/// or duplicates a span turns a probe red by name and does not shrink what it compares (trap 78's device). The figures a probe needs are here and not in the code: the bound of
/// 30 seconds and the 64 kept acts (R11.79), the 5 minutes of the stop sentence (R11.80), and the cause list of R11.81 (c). Self-contained; whitespace is folded to single spaces.
/// </summary>
internal static partial class PublishedResendWords
{
    private const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";

    // ----- R11.79 -----

    /// <summary>R11.79: the seconds after which the send of a signed post or vote ends in the clients ("within 30 seconds"), as published.</summary>
    internal static int WriteBoundSeconds =>
        int.Parse(One(BoundPhrase().Matches(Requirement("R11.79")), "the write bound in seconds").Groups[1].Value, CultureInfo.InvariantCulture);

    /// <summary>R11.79: the most acts kept for one agent ("at most 64 acts"), as published.</summary>
    internal static int KeptActBound =>
        int.Parse(One(ActsBoundPhrase().Matches(Requirement("R11.79")), "the bound on kept acts").Groups[1].Value, CultureInfo.InvariantCulture);

    // ----- R11.80 words -----

    /// <summary>The four clauses of the store sentence: post, resolve, flag, other. Each is the span between its backticks.</summary>
    internal static (string Post, string Resolve, string Flag, string Other) Clauses() =>
        (Span("R11.80 clause words (post)"), Span("R11.80 clause words (resolve)"), Span("R11.80 clause words (flag)"), Span("R11.80 clause words (other)"));

    /// <summary>The act line, with <c>&lt;digest&gt;</c> left in where the printer substitutes.</summary>
    internal static string ActWords => Span("R11.80 act words");

    /// <summary>The kind words appended to the act line for a <c>revision</c> and an <c>endorse</c> only.</summary>
    internal static string KindWords => Span("R11.80 kind words", "only: `");

    /// <summary>The unheard sentence.</summary>
    internal static string UnheardWords => Span("R11.80 unheard words");

    /// <summary>The stop sentence, with <c>&lt;n&gt;</c> and <c>&lt;digest&gt;</c> left in.</summary>
    internal static string StopWords => Span("R11.80 stop words");

    /// <summary>The stop wait, as published: the figure and its unit ("5 minutes").</summary>
    internal static (int Count, string Unit) StopWait
    {
        get
        {
            var m = One(StopWaitPhrase().Matches(Paragraph("R11.80 stop words")), "the stop wait");
            return (int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture), m.Groups["unit"].Value);
        }
    }

    // ----- R11.81 words -----

    /// <summary>R11.81 (a): the sentence after the refusal of a resend that does not resolve the first send.</summary>
    internal static string ResendRefusalWords => Between(Words81(), "(a) `", "` (b) ");

    /// <summary>R11.81 (b): the first line of an <c>already recorded</c> answer to a resend, with <c>&lt;post id&gt;</c> left in, and the sentence after it.</summary>
    internal static (string FirstLine, string After) AlreadyRecordedWords
    {
        get
        {
            var text = Between(Words81(), "(b) `", "` (c) ");
            var m = One(AlreadyRecordedPhrase().Matches(text), "the already-recorded lines");
            return (m.Groups["first"].Value, m.Groups["after"].Value);
        }
    }

    /// <summary>R11.81 (c): the clause a refusal says once, trimmed.</summary>
    internal static string NothingRecordedClause => Between(Words81(), "(c) `", "` (d) ").Trim();

    /// <summary>R11.81 (d): the capitalised last sentence of <c>already-revised</c>, and the clause after the single-successor <c>NOT SENT</c> text.</summary>
    internal static (string Last, string Superseded) RevisionWords
    {
        get
        {
            var text = Between(Words81(), "(d) `", "` (e) ");
            var m = One(RevisionPhrase().Matches(text), "the revision words");
            return (m.Groups["last"].Value, m.Groups["after"].Value.Trim());
        }
    }

    /// <summary>R11.81 (e): the contended flag's words, with <c>&lt;title&gt;</c> left in.</summary>
    internal static string ContendedWords => Between(Words81(), "(e) `", "` where `<title>`");

    /// <summary>
    /// R11.81 (c)'s causes, read from the requirement's own list (as amended by plan ruling 34): the refusals whose own words already say nothing was recorded or stored (the once, so the
    /// clause is not added), the classes and the type for which the clause is added, the refusals that never say it, and whether the refusal of a resend says (a) in its place.
    /// </summary>
    internal static (string[] OwnWords, string Added, string[] Never, bool ResendSaysAInstead) ClauseCauses()
    {
        var text = Requirement("R11.81");
        var start = text.IndexOf("(c) every refusal", StringComparison.Ordinal);
        var end = text.IndexOf("(d) `already-revised`", StringComparison.Ordinal);
        if (start < 0 || end < 0 || end < start) throw new InvalidOperationException($"{Errata}: R11.81 (c) is not found in order");
        var c = text[start..end];

        var own = One(OwnWordsPhrase().Matches(c), "the refusals whose own words say it");
        var added = One(AddedPhrase().Matches(c), "the refusals the clause is added to");
        var never = One(NeverPhrase().Matches(c), "the refusals that never say it");
        var resend = One(ResendInPlacePhrase().Matches(c), "the resend's refusal in place");

        return (Backticked(own.Groups[1].Value), added.Groups[1].Value, Backticked(never.Groups[1].Value), resend.Success);
    }

    /// <summary>
    /// R11.61 (rev.) words: the refusal of a first send answered <c>already-recorded</c>, with <c>&lt;post id&gt;</c> left in where the printer puts the Forum's id (errata G58, plan ruling 38).
    /// </summary>
    internal static string AlreadyRecordedFirstSendWords
    {
        get
        {
            var text = FoldedParagraph("**R11.61 (rev.) words (`already-recorded`, a first send).**");
            var m = One(FirstSendPhrase().Matches(text), "the first-send already-recorded words");
            return m.Groups[1].Value;
        }
    }

    // ----- R11.79 discard words -----

    /// <summary>R11.79 discard words, after a removal: the first line (<c>discarded &lt;digest&gt;</c>, whitespace folded) and the sentence after it, with <c>&lt;digest&gt;</c> left in.</summary>
    internal static (string First, string After) DiscardedWords
    {
        get
        {
            var parts = Between(Paragraph("R11.79 discard words"), "After a removal: `", "` When no act of the digest is held").Split("` then `");
            return parts.Length == 2 ? (parts[0], parts[1]) : throw new InvalidOperationException($"{Errata}: the removal words are not a first line and a sentence ({parts.Length} parts)");
        }
    }

    /// <summary>R11.79 discard words when no act of the digest is held.</summary>
    internal static string NotHeldWords => Between(Paragraph("R11.79 discard words"), "When no act of the digest is held: `", "` When the file cannot be removed");

    /// <summary>R11.79 discard words when the file cannot be removed, with <c>&lt;reason as a display literal&gt;</c> left in.</summary>
    internal static string RemovalFailedWords => Between(Paragraph("R11.79 discard words"), "When the file cannot be removed: `", "` A listing's last line");

    /// <summary>R11.79 discard words, a listing's last line, with <c>&lt;n&gt;</c> and the <c>&lt;digest&gt;</c> placeholders left in.</summary>
    internal static string ListingFooterWords => Between(Paragraph("R11.79 discard words"), "A listing's last line: `", "` A damaged file's line");

    /// <summary>
    /// Plan ruling 41's rule, held over a printed text: every verb of the CLI the text names (<c>curia resend</c>, <c>curia recheck</c>, <c>curia acts</c>, <c>curia acts --discard</c>) is followed by
    /// <c>(in `curia-mcp`, `curia_x`)</c> with the tool that is that verb's, and no tool name stands outside such a qualifier. Returns the problems found, none when the text holds.
    /// </summary>
    internal static List<string> VerbQualificationProblems(string text)
    {
        var problems = new List<string>();
        var qualified = 0;
        foreach (Match verb in CliVerb().Matches(text))
        {
            var tool = verb.Groups["v"].Value switch
            {
                "resend" => "curia_resend",
                "recheck" => "curia_recheck",
                "acts --discard" => "curia_discard",
                "acts" => "curia_acts",
                _ => throw new InvalidOperationException(verb.Value),
            };
            var qualifier = " (in `curia-mcp`, `" + tool + "`)";
            if (text[(verb.Index + verb.Length)..].StartsWith(qualifier, StringComparison.Ordinal)) qualified++;
            else problems.Add($"{verb.Value} is not followed by{qualifier}");
        }

        var tools = McpTool().Count(text);
        if (tools != qualified) problems.Add($"{tools} tool name(s) against {qualified} qualifier(s): a tool name stands outside a qualifier");
        return problems;
    }

    // ----- R10.95 words -----

    /// <summary>R10.95: the <c>unknown</c> line after the digest, with <c>&lt;instant&gt;</c> and <c>&lt;digest&gt;</c> left in.</summary>
    internal static string UnknownLine => Between(Paragraph("R10.95 words"), "after the digest: `", "`. The closing line");

    /// <summary>R10.95: the closing line on standard error when the batch exits 5.</summary>
    internal static string ClosingLine
    {
        get
        {
            var text = Paragraph("R10.95 words");
            var start = text.IndexOf("exits 5: `", StringComparison.Ordinal);
            if (start < 0) throw new InvalidOperationException($"{Errata}: R10.95 words has no closing line");
            var rest = text[(start + "exits 5: `".Length)..];
            return rest[..rest.LastIndexOf('`')];
        }
    }

    // ----- text helpers -----

    private static string Words81() => Paragraph("R11.81 words");

    /// <summary>
    /// The span after <c>**{name}.**</c> (after <paramref name="opening"/> where the paragraph states one), between its backticks: from the opening backtick to the closing one, which is
    /// the first backtick followed by the end of the text, by a full stop and the end or a space, or by a space and a capital letter. Backticks inside a span name a command and are
    /// followed by a lower-case word, a bracket or a colon. Whitespace folded; the span must be there.
    /// </summary>
    internal static string Span(string name, string? opening = null)
    {
        var text = Paragraph(name);
        var anchor = $"**{name}.**";
        var rest = text[(text.IndexOf(anchor, StringComparison.Ordinal) + anchor.Length)..];
        var next = rest.IndexOf("**", StringComparison.Ordinal);
        if (next >= 0) rest = rest[..next];
        rest = rest.Trim();
        if (opening is not null)
        {
            var at = rest.IndexOf(opening, StringComparison.Ordinal);
            if (at < 0) throw new InvalidOperationException($"{Errata}: \"{name}\" has no \"{opening}\"");
            rest = rest[(at + opening.Length - 1)..];
        }

        if (rest.Length < 2 || rest[0] != '`') throw new InvalidOperationException($"{Errata}: \"{name}\" is not a span between backticks");
        for (var i = 1; i < rest.Length; i++)
        {
            if (rest[i] != '`') continue;
            var after = rest[(i + 1)..];
            var closes = after.Length == 0
                || (after[0] == '.' && (after.Length == 1 || after[1] == ' '))
                || (after[0] == ' ' && after.Length > 1 && char.IsUpper(after[1]));
            if (closes) return rest[1..i];
        }

        throw new InvalidOperationException($"{Errata}: \"{name}\" has no closing backtick");
    }

    /// <summary>The paragraph that begins <c>**{name}.**</c>, to its blank line, whitespace folded. Exactly one must exist.</summary>
    internal static string Paragraph(string name) => FoldedParagraph($"**{name}.**");

    /// <summary>The requirement's own paragraph, which begins <c>**{id}** </c>. Exactly one must exist.</summary>
    internal static string Requirement(string id) => FoldedParagraph($"**{id}** ");

    private static string FoldedParagraph(string anchor)
    {
        var text = ErrataText();
        var start = text.IndexOf(anchor, StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException($"{Errata} has no paragraph that begins \"{anchor}\"");
        if (text.IndexOf(anchor, start + anchor.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException($"{Errata} has more than one paragraph that begins \"{anchor}\"");

        var end = text.IndexOf("\n\n", start + anchor.Length, StringComparison.Ordinal);
        return Whitespace().Replace(text[start..(end < 0 ? text.Length : end)], " ");
    }

    private static string Between(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException($"{Errata}: no \"{from}\" in the paragraph");
        start += from.Length;
        var end = text.IndexOf(to, start, StringComparison.Ordinal);
        if (end < 0) throw new InvalidOperationException($"{Errata}: no \"{to}\" after \"{from}\"");
        return text[start..end];
    }

    private static string[] Backticked(string text) => [.. BacktickSpan().Matches(text).Select(m => m.Groups[1].Value)];

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

    [GeneratedRegex(@"`curia (?<v>acts --discard|resend|recheck|acts)(?: [^`]*)?`", RegexOptions.CultureInvariant)]
    private static partial Regex CliVerb();

    [GeneratedRegex(@"\bcuria_(?:resend|recheck|discard|acts)\b", RegexOptions.CultureInvariant)]
    private static partial Regex McpTool();

    [GeneratedRegex(@"`([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex BacktickSpan();

    [GeneratedRegex(@"^\*\*[^*]+\*\* `([^`]+)` A resend's answer is", RegexOptions.CultureInvariant)]
    private static partial Regex FirstSendPhrase();

    [GeneratedRegex(@"not answered within (\d+) seconds", RegexOptions.CultureInvariant)]
    private static partial Regex BoundPhrase();

    [GeneratedRegex(@"SHALL keep at most (\d+) acts", RegexOptions.CultureInvariant)]
    private static partial Regex ActsBoundPhrase();

    [GeneratedRegex(@"The wait is `(?<n>\d+) (?<unit>[a-z]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex StopWaitPhrase();

    [GeneratedRegex(@"^(?<first>already recorded <post id>)` then `(?<after>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex AlreadyRecordedPhrase();

    [GeneratedRegex(@"^(?<last>Nothing was stored or spent\.)` and, after the existing remedy of the single-successor `NOT SENT` text, ` (?<after>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex RevisionPhrase();

    [GeneratedRegex(@"Where the words already in the line say it \(the Forum's title or detail, or the client's sentence for that cause: ([^)]*)\) they are the once", RegexOptions.CultureInvariant)]
    private static partial Regex OwnWordsPhrase();

    [GeneratedRegex(@"where they do not \(([^)]*)\) the clause is added", RegexOptions.CultureInvariant)]
    private static partial Regex AddedPhrase();

    [GeneratedRegex(@"a refusal that says an earlier call recorded the act \(([^)]*)\) never says it", RegexOptions.CultureInvariant)]
    private static partial Regex NeverPhrase();

    [GeneratedRegex(@"the refusal of a resend says \(a\) in its place", RegexOptions.CultureInvariant)]
    private static partial Regex ResendInPlacePhrase();
}
