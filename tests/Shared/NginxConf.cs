using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Curia.Tests.Shared;

/// <summary>One nginx directive: its name, its arguments (quotes removed), and its block when it has one.</summary>
internal sealed record NgxDirective(string Name, IReadOnlyList<string> Args, IReadOnlyList<NgxDirective>? Block)
{
    /// <summary>The directive's arguments joined by single spaces, for a message.</summary>
    internal string Text => $"{Name} {string.Join(' ', Args)}".TrimEnd();

    /// <summary>This directive's block members with <paramref name="name"/> (not recursive).</summary>
    internal IEnumerable<NgxDirective> Children(string name) =>
        (Block ?? []).Where(d => string.Equals(d.Name, name, StringComparison.Ordinal));
}

/// <summary>
/// The subset of nginx's configuration syntax the deployment pack's recipe uses (<c>deploy/proxy/*.conf</c>): directives ended by <c>;</c>, blocks in braces, <c>#</c> comments, single- and
/// double-quoted arguments. It does not expand <c>include</c>: a caller that wants the files together parses each and reads them as the one block they would be. Shared by the probes that
/// read the recipe against the rule it carries (DPK4) and by the in-process forwarder that behaves as the recipe says (Tier A).
/// </summary>
internal static partial class NginxConf
{
    internal static List<NgxDirective> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tokens = Tokenize(text);
        var at = 0;
        var result = ParseBlock(tokens, ref at, inBlock: false);
        return result;
    }

    /// <summary>Every directive named <paramref name="name"/>, at any depth, with the block it sits in.</summary>
    internal static IEnumerable<(NgxDirective Directive, NgxDirective? Parent)> Descend(IEnumerable<NgxDirective> directives, NgxDirective? parent = null)
    {
        foreach (var d in directives)
        {
            yield return (d, parent);
            if (d.Block is not null)
                foreach (var inner in Descend(d.Block, d))
                    yield return inner;
        }
    }

    internal static IEnumerable<NgxDirective> Find(IEnumerable<NgxDirective> directives, string name) =>
        Descend(directives).Where(x => string.Equals(x.Directive.Name, name, StringComparison.Ordinal)).Select(x => x.Directive);

    /// <summary>The <c>location</c> block whose arguments read exactly <paramref name="args"/> (for example <c>= /oauth/token</c> or <c>@curia_rate_limited</c>).</summary>
    internal static NgxDirective? Location(IEnumerable<NgxDirective> directives, string args) =>
        Find(directives, "location").FirstOrDefault(l => string.Equals(string.Join(' ', l.Args), args, StringComparison.Ordinal));

    /// <summary>nginx's size syntax (<c>1m</c>, <c>512k</c>, <c>0</c>, a plain byte count) as bytes.</summary>
    internal static long SizeBytes(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var m = SizePattern().Match(value);
        if (!m.Success) throw new FormatException($"'{value}' is not an nginx size");
        var n = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return m.Groups[2].Value.ToUpperInvariant() switch { "K" => n * 1024, "M" => n * 1024 * 1024, "G" => n * 1024 * 1024 * 1024, _ => n };
    }

    /// <summary>nginx's <c>rate=10r/s</c> or <c>rate=30r/m</c> as requests per second.</summary>
    internal static double RatePerSecond(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var m = RatePattern().Match(value);
        if (!m.Success) throw new FormatException($"'{value}' is not an nginx rate");
        var n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return m.Groups[2].Value == "r/m" ? n / 60.0 : n;
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var hasCurrent = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '#')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }

            if (c is '\'' or '"')
            {
                hasCurrent = true;
                for (i++; i < text.Length && text[i] != c; i++)
                {
                    if (text[i] == '\\' && i + 1 < text.Length) i++;
                    current.Append(text[i]);
                }

                continue;
            }

            if (char.IsWhiteSpace(c) || c is ';' or '{' or '}')
            {
                if (hasCurrent || current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    hasCurrent = false;
                }

                if (c is ';' or '{' or '}') tokens.Add(c.ToString());
                continue;
            }

            current.Append(c);
        }

        if (hasCurrent || current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    private static List<NgxDirective> ParseBlock(List<string> tokens, ref int at, bool inBlock)
    {
        var result = new List<NgxDirective>();
        var words = new List<string>();
        while (at < tokens.Count)
        {
            var t = tokens[at++];
            switch (t)
            {
                case ";":
                    if (words.Count > 0) result.Add(new NgxDirective(words[0], words.Skip(1).ToList(), null));
                    words = [];
                    break;
                case "{":
                    var block = ParseBlock(tokens, ref at, inBlock: true);
                    if (words.Count == 0) throw new FormatException("a block with no directive name");
                    result.Add(new NgxDirective(words[0], words.Skip(1).ToList(), block));
                    words = [];
                    break;
                case "}":
                    if (!inBlock) throw new FormatException("an unmatched }");
                    return result;
                default:
                    words.Add(t);
                    break;
            }
        }

        if (inBlock) throw new FormatException("a block is not closed");
        if (words.Count > 0) throw new FormatException($"'{words[0]}' is not ended by ;");
        return result;
    }

    [GeneratedRegex(@"^(\d+)([kKmMgG]?)$")]
    private static partial Regex SizePattern();

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)(r/s|r/m)$")]
    private static partial Regex RatePattern();
}
