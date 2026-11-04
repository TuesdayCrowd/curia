using System.Globalization;
using System.Text.RegularExpressions;

namespace Curia.Tests.Shared;

/// <summary>
/// The words and rules of errata G59 (R6.61, R11.82, R11.83) as the errata publishes them, read at test time from <c>curia-whitepaper-ERRATA-AND-ADDENDUM.md</c> and never from the
/// code a probe is about. Every parse asserts its count before it returns a value (trap 78): an edit that removes or duplicates a span turns a probe red by name and does not shrink
/// what it compares. The words keep their placeholders (<c>&lt;n&gt;</c>, <c>&lt;names&gt;</c>, <c>&lt;kid&gt;</c>, ...); a probe matches the text before each placeholder to at least
/// 20 characters (trap 141) and asserts a stable phrase of each paragraph's content and not only that it was found (trap 142). Self-contained; whitespace is folded.
/// </summary>
internal static partial class PublishedAgentWords
{
    private const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";

    /// <summary>The paragraphs that carry words, and the stable phrase each must hold (trap 142: content, not only a count).</summary>
    private static readonly (string Name, string Phrase)[] WordParagraphs =
    [
        ("R11.82 words (several profiles)", "Nothing was sent, nothing was read from the Forum, and no kept act was touched"),
        ("R11.82 words (blank)", "name the profile 'curia <verb>' should act as"),
        ("R11.82 rule words", "` --agent <slug>`"),
        ("R11.83 words (retained head, signature)", "this check makes no claim about the Forum's history"),
        ("R11.83 words (retained head, key)", "copy it aside before removing it"),
        ("R11.83 words (failed, equal sizes)", "are both signed by the log's published keys"),
        ("R11.83 words (failed, a proof)", "each naming the head by role"),
        ("R11.83 words (verified, a lagging replica)", "a lagging replica rather than a rewrite"),
    ];

    /// <summary>Asserts that each of the eight word paragraphs is found once and holds its stable phrase; returns the count (8).</summary>
    internal static int AssertParagraphs()
    {
        foreach (var (name, phrase) in WordParagraphs)
        {
            if (!Paragraph(name).Contains(phrase, StringComparison.Ordinal))
                throw new InvalidOperationException($"{Errata}: \"{name}\" no longer holds \"{phrase}\"");
        }

        return WordParagraphs.Length;
    }

    /// <summary>R11.82 (a): the verbs whose agent comes from <c>--agent</c> or the one profile, in the order the requirement lists them. Asserted to be 19.</summary>
    internal static string[] IdentityVerbs
    {
        get
        {
            var text = Requirement("R11.82");
            var m = One(VerbList().Matches(text), "the verbs of R11.82 (a)");
            var verbs = Backticked(m.Groups[1].Value);
            if (verbs.Length != 19) throw new InvalidOperationException($"{Errata}: R11.82 (a) lists {verbs.Length} verbs, expected 19");
            return verbs;
        }
    }

    /// <summary>R11.82 words (several profiles), with <c>&lt;n&gt;</c>, <c>&lt;names&gt;</c> and <c>&lt;verb&gt;</c> left in.</summary>
    internal static string SeveralWords => Span("R11.82 words (several profiles)", "`error: ");

    /// <summary>R11.82 words (blank), with <c>&lt;verb&gt;</c> left in.</summary>
    internal static string BlankWords => Span("R11.82 words (blank)", "`error: ");

    /// <summary>R11.82: the clause after the 16th name: <c>, and &lt;m&gt; more; 'curia agents' lists them</c>.</summary>
    internal static string MoreWords => Between(Paragraph("R11.82 words (several profiles)"), "then `", "`.");

    /// <summary>R11.82 rule words, whole, folded.</summary>
    internal static string RuleWords => Paragraph("R11.82 rule words");

    /// <summary>R11.82 rule words: the four commands of the rule as published (<c>curia resend &lt;digest&gt;</c>, <c>curia acts --discard &lt;digest&gt;</c>, <c>curia budget</c>, <c>curia flags</c>), plus <c>curia acts</c>.</summary>
    internal static string[] RuleCommands
    {
        get
        {
            var rule = RuleWords;
            var all = Backticked(rule[..rule.IndexOf("; `curia recheck`", StringComparison.Ordinal)]);
            var commands = all.Where(c => c.StartsWith("curia ", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
            if (commands.Length != 5) throw new InvalidOperationException($"{Errata}: the rule words name {commands.Length} commands ({string.Join(" | ", commands)}), expected 5");
            return commands;
        }
    }

    /// <summary>R11.83 words (retained head, signature): the whole span, placeholders left in.</summary>
    internal static string SignatureWords => Span("R11.83 words (retained head, signature)");

    /// <summary>R11.83 words (retained head, key): the whole span, placeholders left in.</summary>
    internal static string KeyWords => Span("R11.83 words (retained head, key)");

    /// <summary>R11.83 words (failed, equal sizes).</summary>
    internal static string FailedEqualWords => Span("R11.83 words (failed, equal sizes)");

    /// <summary>R11.83 words (failed, a proof): the span, with <c>&lt;reason&gt;</c> left in.</summary>
    internal static string FailedProofWords => Span("R11.83 words (failed, a proof)");

    /// <summary>R11.83 words (verified, a lagging replica).</summary>
    internal static string LaggingWords => Span("R11.83 words (verified, a lagging replica)");

    /// <summary>R11.83 (failed, a proof): the reasons the paragraph publishes, as the spans between backticks after "one of:". Asserted to be 3.</summary>
    internal static string[] ProofReasons
    {
        get
        {
            var text = Paragraph("R11.83 words (failed, a proof)");
            var tail = text[(text.IndexOf("one of:", StringComparison.Ordinal) + "one of:".Length)..];
            var reasons = Backticked(tail.Split("(and the like", StringSplitOptions.None)[0]);
            if (reasons.Length != 3) throw new InvalidOperationException($"{Errata}: the failed-proof reasons are {reasons.Length}, expected 3");
            return reasons;
        }
    }

    /// <summary>
    /// The text before the first placeholder of <paramref name="words"/> (at least 20 characters, asserted), as a probe matches printed text: a slice running into a placeholder never
    /// matches (trap 141).
    /// </summary>
    internal static string BeforePlaceholder(string words)
    {
        var at = words.IndexOf('<', StringComparison.Ordinal);
        var head = at < 0 ? words : words[..at];
        if (head.Length < 20) throw new InvalidOperationException($"the words begin with a placeholder or hold under 20 characters before it: \"{head}\"");
        return head;
    }

    /// <summary>
    /// The stretches of <paramref name="words"/> between its placeholders (<c>&lt;n&gt;</c>, <c>&lt;kid&gt;</c>, ...), folded and trimmed, those of 20 characters or more (trap 141): what a probe looks for in
    /// printed text, since a stretch running into a placeholder never matches. At least one must remain.
    /// </summary>
    internal static string[] Stretches(string words)
    {
        var stretches = Regex.Split(words, @"<[a-z][a-z ]*>", RegexOptions.CultureInvariant).Select(s => Whitespace().Replace(s, " ").Trim()).Where(s => s.Length >= 20).ToArray();
        return stretches.Length > 0 ? stretches : throw new InvalidOperationException("the words hold no stretch of 20 characters between placeholders: " + words);
    }

    // ----- text helpers -----

    /// <summary>The span after <c>**{name}.**</c> between backticks, from the opening backtick (after <paramref name="opening"/> where given) to the backtick that closes it; whitespace folded.</summary>
    internal static string Span(string name, string? opening = null)
    {
        var text = Paragraph(name);
        var anchor = $"**{name}.**";
        var rest = text[(text.IndexOf(anchor, StringComparison.Ordinal) + anchor.Length)..].Trim();
        if (opening is not null)
        {
            var at = rest.IndexOf(opening, StringComparison.Ordinal);
            if (at < 0) throw new InvalidOperationException($"{Errata}: \"{name}\" has no \"{opening}\"");
            rest = rest[at..];
        }

        if (rest.Length < 2 || rest[0] != '`') throw new InvalidOperationException($"{Errata}: \"{name}\" is not a span between backticks");
        for (var i = 1; i < rest.Length; i++)
        {
            if (rest[i] != '`') continue;
            var after = rest[(i + 1)..];
            var closes = after.Length == 0
                || (after[0] == '.' && (after.Length == 1 || after[1] == ' '))
                || (after[0] == ' ' && after.Length > 1 && char.IsUpper(after[1]))
                || (after[0] == ' ' && after.Length > 1 && after[1] == '`')
                || after.StartsWith(" with `", StringComparison.Ordinal);
            if (closes) return rest[1..i];
        }

        throw new InvalidOperationException($"{Errata}: \"{name}\" has no closing backtick");
    }

    internal static string Paragraph(string name) => FoldedParagraph($"**{name}.**");

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
            : throw new InvalidOperationException($"{Errata} yields {matches.Count.ToString(CultureInfo.InvariantCulture)} readings of {what}, expected one");

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

    [GeneratedRegex(@"`([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex BacktickSpan();

    [GeneratedRegex(@"take the agent of (.+?) from `--agent`", RegexOptions.CultureInvariant)]
    private static partial Regex VerbList();
}
