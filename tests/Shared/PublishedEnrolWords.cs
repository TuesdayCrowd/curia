using System.Text.RegularExpressions;

namespace Curia.Tests.Shared;

/// <summary>
/// The words of errata G79 (R11.107) as the errata publishes them, read at test time from <c>curia-whitepaper-ERRATA-AND-ADDENDUM.md</c> and never from the code a probe is about:
/// each span of the paragraph <c>**R11.107 words.**</c>, by the name its bullet gives it. A bullet that is missing or duplicated fails by name, so an edit cannot shrink what a
/// probe compares. <c>&lt;type&gt;</c> and <c>&lt;directory&gt;</c> are left in the two spans that carry them; the probe fills them as the errata says (display literals).
/// </summary>
internal static partial class PublishedEnrolWords
{
    internal static string ProofWindow => Span("Proof, window");

    internal static string ProofNotPossessed => Span("Proof, not possessed");

    internal static string ProofRequired => Span("Proof, required");

    internal static string NonceAbsent => Span("Nonce, absent");

    internal static string Continuing => Span("Continuing");

    internal static string ProofNonceStale => Span("Proof, nonce stale");

    internal static string ProofOther => Span("Proof, other");

    internal static string NotContinuable => Span("Not continuable");

    /// <summary>
    /// The span the bullet <paramref name="name"/> publishes, between its double quotes. The paragraph is the one whose <b>line begins</b> <c>**R11.107 words.**</c>: R11.107's own paragraph names that
    /// anchor in running text ("the paragraph `**R11.107 words.**` below"), so <c>PublishedClientWords.Paragraph</c> finds two and refuses (a finding of the Task 2 run); this reads by line start.
    /// </summary>
    internal static string Span(string name)
    {
        var lines = ErrataLines();
        var start = lines.FindIndex(l => l.StartsWith("**R11.107 words.**", StringComparison.Ordinal));
        if (start < 0 || lines.FindLastIndex(l => l.StartsWith("**R11.107 words.**", StringComparison.Ordinal)) != start)
            throw new InvalidOperationException("the errata does not hold exactly one paragraph whose line begins **R11.107 words.**");

        var paragraph = new List<string>();
        for (var i = start; i < lines.Count && lines[i].Length > 0; i++) paragraph.Add(lines[i]);
        var text = Regex.Replace(string.Join(' ', paragraph), @"\s+", " ", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

        var matches = Regex.Matches(text, "`" + Regex.Escape(name) + "`: \"(?<span>[^\"]+)\"", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
        return matches.Count == 1
            ? matches[0].Groups["span"].Value
            : throw new InvalidOperationException($"the errata's R11.107 words yield {matches.Count} spans named {name}, expected one");
    }

    private static List<string> ErrataLines()
    {
        const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, Errata))) dir = dir.Parent;
        return dir is null ? throw new InvalidOperationException($"{Errata} not found above {AppContext.BaseDirectory}") : [.. File.ReadAllLines(Path.Combine(dir.FullName, Errata))];
    }
}
