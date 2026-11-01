using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// AFC27 (errata G55, R8.84): the obligation of a check is created at one site, <b>after the confirm and only for a post not found</b>. PR 2's CPF28 held the one site with a
/// lazy pattern over <c>src/Curia.Api</c> alone (its open risk 12); this scan reads all of <c>src</c> and takes the <c>catch</c> by matching braces, so a second site anywhere,
/// or a site in the branch that continues with a found post, is red.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class OweAfterConfirmTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curia.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Curia.sln not found above " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"\.Owe\(", RegexOptions.CultureInvariant)]
    private static partial Regex OweCall();

    [GeneratedRegex(@"\bcatch\s*\(\s*Exception[^)]*\)\s*\{", RegexOptions.CultureInvariant)]
    private static partial Regex CatchOfException();

    /// <summary>The source with comments and the insides of string and character literals blanked (same length), so a brace or a call named in either is not read as code.</summary>
    private static string Code(string text)
    {
        var sb = new StringBuilder(text);
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') sb[i++] = ' ';
            }
            else if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + 2;
                for (; i < end; i++) if (text[i] != '\n') sb[i] = ' ';
            }
            else if (text[i] == '"')
            {
                var verbatim = i > 0 && text[i - 1] == '@';
                i++;
                while (i < text.Length && !(text[i] == '"' && !(verbatim && i + 1 < text.Length && text[i + 1] == '"')))
                {
                    if (!verbatim && text[i] == '\\' && i + 1 < text.Length) sb[i++] = ' ';
                    else if (verbatim && text[i] == '"') sb[i++] = ' ';
                    if (i < text.Length && text[i] != '\n') sb[i] = ' ';
                    i++;
                }

                i++;
            }
            else if (text[i] == '\'' && i + 2 < text.Length)
            {
                var end = text[i + 1] == '\\' ? text.IndexOf('\'', i + 3) : i + 2;
                if (end > i && end - i <= 8)
                {
                    for (var k = i + 1; k < end; k++) sb[k] = ' ';
                    i = end;
                }
            }

            i++;
        }

        return sb.ToString();
    }

    /// <summary>The index one past the brace that closes the one opened at <paramref name="open"/>.</summary>
    private static int Closing(string code, int open)
    {
        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return i + 1;
        }

        return -1;
    }

    /// <summary>The header of every block that encloses <paramref name="at"/> inside <paramref name="from"/>..<paramref name="to"/>: the text from the previous statement end to the block's brace.</summary>
    private static IEnumerable<string> EnclosingHeaders(string code, int from, int to, int at)
    {
        for (var i = from; i < at; i++)
        {
            if (code[i] != '{') continue;
            var close = Closing(code, i);
            if (close <= at || close > to) continue;
            var start = i;
            while (start > from && code[start - 1] is not (';' or '{' or '}')) start--;
            yield return code[start..i].Trim();
        }
    }

    /// <summary>AFC27. One call of <c>Owe</c> in all of <c>src</c>, inside the <c>catch</c> of the <c>try</c> around <c>PersistAsync</c>, after the call to the confirm and not in a branch that goes on with a found post.</summary>
    [Fact]
    public void R8_84_AFC27_TheObligationIsCreatedOnceAfterTheConfirmAndOnlyForAPostNotFound()
    {
        var src = Path.Combine(Root(), "src");
        var files = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();
        var calls = files
            .Select(f => (File: Path.GetFileName(f), Count: OweCall().Count(Code(File.ReadAllText(f)))))
            .Where(x => x.Count > 0)
            .ToArray();
        var total = calls.Sum(c => c.Count);

        var endpoints = Code(File.ReadAllText(Path.Combine(src, "Curia.Api", "ForumEndpoints.cs")));
        var persist = endpoints.IndexOf("PersistSubmissionAsync(", StringComparison.Ordinal);
        var call = persist < 0 ? -1 : endpoints.IndexOf("PersistAsync(", persist, StringComparison.Ordinal);
        var catchAt = call < 0 ? Match.Empty : CatchOfException().Match(endpoints, call);
        var open = catchAt.Success ? catchAt.Index + catchAt.Length - 1 : -1;
        var close = open < 0 ? -1 : Closing(endpoints, open);
        var block = open < 0 || close < 0 ? string.Empty : endpoints[open..close];

        var confirmAt = block.IndexOf("AppendConfirmation.ConfirmAsync(", StringComparison.Ordinal);
        var oweAt = OweCall().Match(block);
        var after = confirmAt >= 0 && oweAt.Success && oweAt.Index > confirmAt;
        var inFound = oweAt.Success && EnclosingHeaders(endpoints, open, close, open + oweAt.Index).Any(h => h.Contains("Found", StringComparison.Ordinal));

        var expected = "1 call site of Owe in src, inside the catch around PersistAsync, 1 call after the confirm call, none in the branch that continues with a found post";
        var actual = $"{total} call site{(total == 1 ? string.Empty : "s")} of Owe in src" +
            (total > 1 ? " (" + string.Join(", ", calls.Select(c => $"{c.File} x{c.Count}")) + ")" : string.Empty) +
            (block.Length == 0 ? ", no catch around PersistAsync was found" : oweAt.Success ? ", inside the catch around PersistAsync" : ", none inside the catch around PersistAsync") +
            (after ? ", 1 call after the confirm call" : ", 0 call sites after the confirm call") +
            (inFound ? ", one in the branch that continues with a found post" : ", none in the branch that continues with a found post");
        if (total == 1 && block.Length > 0 && oweAt.Success && after && !inFound) actual = expected;
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }
}
