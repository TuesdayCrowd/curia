using System.Globalization;
using System.Text.RegularExpressions;

namespace Curia.Canon.Tests.Json;

/// <summary>
/// R6.39's four frozen magnitudes, parsed out of the white paper at test time -- the same
/// arrangement <c>PublishedTable10</c> and <c>PublishedTable11</c> use in
/// <c>Curia.Domain.Tests</c>, for the same reason: spec text and code check each other,
/// neither is derived from the other, and editing one without following it in the other
/// becomes a build failure.
///
/// <para><b>Why this file exists at all.</b> Every boundary test in this assembly builds its
/// input from <see cref="Curia.Canon.Json.AdmitLimits.Default"/> -- <c>MaxDepth</c>,
/// <c>MaxDepth + 1</c>, <c>MaxMembersPerObject + 1</c>, <c>MaxStringBytes + 1</c>. That is the
/// right shape for a test of the parser's arithmetic and the wrong shape for a test of a value
/// R15.1 freezes: narrow all four constants and every one of those tests stays green, because
/// each one moved with the constant it was checking. Two agents independently did exactly that
/// and reported the suite green. So the constants get pinned here, once, against the published
/// sentence; the boundary tests then keep deriving from <c>AdmitLimits.Default</c> and are
/// transitively anchored to the text through this one check.</para>
///
/// <para><b>What is checkable here.</b> R6.39 is prose, but unlike Table 11's entry criteria it
/// is prose made entirely of magnitudes -- there is no predicate to re-implement, only four
/// numbers, four units, and two parenthetical byte glosses. Parsing that is extraction, not a
/// second reading of a rule.</para>
/// </summary>
internal static partial class PublishedAdmitLimits
{
    private const string WhitePaper = "curia-agent-forum-WHITEPAPER.md";
    private const string Marker = "**R6.39**";
    private const string Enumeration = "SHALL be exactly:";

    /// <summary>
    /// The phrase each clause is keyed by. These are the clause's own subject words, not
    /// positions in the sentence: keying by ordinal would let a reordered enumeration compare
    /// the string cap against the member cap and still pass.
    /// </summary>
    private static readonly string[] ClauseKeys =
        ["nesting depth", "per object", "submission size", "string length"];

    /// <summary>One clause of R6.39's enumeration.</summary>
    /// <param name="Text">The clause as published, whitespace-normalized.</param>
    /// <param name="Magnitude">The number inside the clause's bold span ("1,024" -> 1024).</param>
    /// <param name="Unit">The unit word inside the bold span ("containers", "members", "MiB", "KiB").</param>
    /// <param name="ByteGloss">
    /// The parenthetical byte count following the bold span, where the clause gives one.
    /// R6.39 glosses both binary-prefix magnitudes and neither count, which is the asymmetry
    /// that lets <see cref="Bytes"/> cross-check the prefix against the gloss.
    /// </param>
    internal sealed record Limit(string Text, long Magnitude, string Unit, long? ByteGloss)
    {
        /// <summary>
        /// The clause's magnitude expressed in bytes, or null where the clause does not measure
        /// bytes at all (depth counts containers; the member cap counts members).
        ///
        /// <para>Computed from the binary prefix rather than read from the gloss, deliberately.
        /// The two are then compared: a document that said "1 MiB (1,000,000 bytes)" -- the
        /// decimal/binary slip that has cost other projects real interoperability -- fails here
        /// rather than silently redefining the cap by a factor of 1.05.</para>
        /// </summary>
        public long? Bytes => Unit switch
        {
            "bytes" => Magnitude,
            "KiB" => Magnitude * 1024,
            "MiB" => Magnitude * 1024 * 1024,
            _ => null,
        };
    }

    /// <summary>The full R6.39 paragraph as published, whitespace-normalized.</summary>
    internal static string Paragraph { get; } = ReadParagraph();

    /// <summary>R6.39's clauses, keyed by the phrase each clause uses to name what it limits.</summary>
    internal static IReadOnlyDictionary<string, Limit> Limits { get; } = Parse();

    internal static Limit Depth => Limits["nesting depth"];
    internal static Limit MembersPerObject => Limits["per object"];
    internal static Limit SubmissionSize => Limits["submission size"];
    internal static Limit StringLength => Limits["string length"];

    private static string ReadParagraph()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, WhitePaper)))
            dir = dir.Parent;

        var path = dir is null
            ? throw new InvalidOperationException($"{WhitePaper} not found above {AppContext.BaseDirectory}")
            : Path.Combine(dir.FullName, WhitePaper);

        var text = File.ReadAllText(path);
        var start = text.IndexOf(Marker, StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidOperationException($"{WhitePaper} no longer contains '{Marker}'.");

        var end = text.IndexOf("\n\n", start, StringComparison.Ordinal);
        var paragraph = end < 0 ? text[start..] : text[start..end];

        return Whitespace().Replace(paragraph, " ").Trim();
    }

    private static Dictionary<string, Limit> Parse()
    {
        // Re-read rather than reusing the Paragraph property. Static property initializers
        // run in textual declaration order, so reading Paragraph here makes this parser
        // correct only while the two declarations stay in their current order -- and the
        // symptom of getting that wrong is a NullReferenceException inside a type
        // initializer, which reports as nine unrelated test failures. Costing one extra file
        // read to remove an ordering hazard is the right trade in a test helper.
        var paragraph = ReadParagraph();

        var listStart = paragraph.IndexOf(Enumeration, StringComparison.Ordinal);
        if (listStart < 0)
            throw new InvalidOperationException($"R6.39 no longer introduces its limits with '{Enumeration}'.");

        // The enumeration runs to the first sentence end. "R6.15's" and "UTF-8" contain no
        // period-then-space, so the first ". " after the colon is the one that closes the list.
        var body = paragraph[(listStart + Enumeration.Length)..];
        var listEnd = body.IndexOf(". ", StringComparison.Ordinal);
        if (listEnd < 0)
            throw new InvalidOperationException("R6.39's enumeration does not end in a sentence.");

        var limits = new Dictionary<string, Limit>(StringComparer.Ordinal);

        foreach (var raw in body[..listEnd].Split(';'))
        {
            var clause = raw.Trim();
            if (clause.Length == 0) continue;

            var bold = BoldMagnitude().Match(clause);
            if (!bold.Success)
                throw new InvalidOperationException($"R6.39 clause states no bold magnitude: '{clause}'");

            var magnitude = long.Parse(bold.Groups[1].Value.Replace(",", "", StringComparison.Ordinal), CultureInfo.InvariantCulture);
            var gloss = ByteGloss().Match(clause);

            var limit = new Limit(
                clause,
                magnitude,
                bold.Groups[2].Value,
                gloss.Success ? long.Parse(gloss.Groups[1].Value.Replace(",", "", StringComparison.Ordinal), CultureInfo.InvariantCulture) : null);

            var key = ClauseKeys.SingleOrDefault(k => clause.Contains(k, StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    $"R6.39 clause names no limit this test recognises: '{clause}'. "
                    + "If the wording changed, re-read R6.39 before changing this list.");

            if (!limits.TryAdd(key, limit))
                throw new InvalidOperationException($"R6.39 states '{key}' more than once.");
        }

        return limits;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>A bold span holding "&lt;number&gt; &lt;unit&gt;", e.g. "**1,024 members**".</summary>
    [GeneratedRegex(@"\*\*([\d,]+)\s+([A-Za-z]+)\*\*")]
    private static partial Regex BoldMagnitude();

    /// <summary>A parenthetical byte count, e.g. "(1,048,576 bytes)".</summary>
    [GeneratedRegex(@"\(([\d,]+)\s+bytes\)")]
    private static partial Regex ByteGloss();
}
