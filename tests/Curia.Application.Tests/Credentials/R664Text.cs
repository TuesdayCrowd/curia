using System.Text.RegularExpressions;

namespace Curia.Application.Tests.Credentials;

/// <summary>
/// R6.64 as the errata publishes it (errata G68), read at test time from <c>curia-whitepaper-ERRATA-AND-ADDENDUM.md</c> and never from the code a probe is about: the paragraph, from its
/// bold anchor to the next requirement's, and the predicate names it gives (a backticked hyphenated word is a predicate; a dotted, underscored or slashed one is an event type, a member or a statement
/// <c>typ</c>). Builder B read this text and nothing else.
/// </summary>
internal static partial class R664Text
{
    private const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";

    private const string Anchor = "**R6.64** The Forum SHALL decide";

    internal static string Block()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, Errata)))
            dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException($"{Errata} not found above {AppContext.BaseDirectory}");

        var text = File.ReadAllText(Path.Combine(dir.FullName, Errata));
        var start = text.IndexOf(Anchor, StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException($"no R6.64 paragraph in the errata ({Errata} has no paragraph that begins \"{Anchor}\")");

        var end = text.IndexOf("\n**R", start + Anchor.Length, StringComparison.Ordinal);
        return text[start..(end < 0 ? text.Length : end)];
    }

    /// <summary>The predicate names R6.64 gives, as a set.</summary>
    internal static SortedSet<string> Slugs() =>
        new(Slug().Matches(Block()).Select(m => m.Groups[1].Value), StringComparer.Ordinal);

    [GeneratedRegex("`([a-z]+(?:-[a-z]+)+)`")]
    private static partial Regex Slug();
}
