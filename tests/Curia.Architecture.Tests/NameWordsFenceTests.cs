using System.Diagnostics.CodeAnalysis;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// R10.97 (errata G62), NMN42 and NMN43, fences read from source text and not from names (D61 item 80's lesson):
/// the words of the filters line, the receipt line and the floor sentence are produced by one file, <c>Curia.Client/NameWording.cs</c>, for <c>curia</c>, <c>curia-mcp</c> and the
/// resend receipt, and no other source file of the clients holds their opening words; and the two members (<c>unmatched_filters</c>, <c>board_new</c>) are in no cursor binding, no
/// canonical form, no stored event and no leaf (R9.55, R8.88), which no file of the domain, the canon or the infrastructure mentions.
///
/// <para><b>Oracles.</b> The fragments are parsed from the entry (<see cref="PublishedNameWords"/>), each asserted non-empty before it is searched for, so a fence over no words is
/// vacuous and says so. The fence has a positive half (the one file holds every fragment) so that a build that never writes the words is red and not green by absence.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class NameWordsFenceTests
{
    private static readonly string[] ClientProjects = ["Curia.Client", "Curia.Client.Cli", "Curia.Mcp"];

    private static readonly string[] DomainProjects = ["Curia.Domain", "Curia.Canon", "Curia.Infrastructure"];

    private static readonly string[] MemberTokens = ["unmatched_filters", "board_new", "NamedFilters"];

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("no 'src' above " + AppContext.BaseDirectory);
    }

    private static string After(string text, string marker)
    {
        var at = text.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) throw new InvalidOperationException($"no \"{marker}\" in \"{text}\"");
        return text[(at + marker.Length)..];
    }

    /// <summary>
    /// The opening words of each sentence the one file writes, taken from the published spans: the two filters clauses' bodies, the matched and not-stated clauses, the three
    /// receipt lines' sentences, and the floor sentence. Twelve at most, asserted before they are returned.
    /// </summary>
    private static string[] Fragments()
    {
        var boardUnmatched = After(PublishedNameWords.BoardClause(), ": ").Split(';')[0];
        var authorUnmatched = After(PublishedNameWords.AuthorClause(), ": ").Split(';')[0];
        var matchedBoard = After(PublishedNameWords.MatchedClause("board"), ": ");
        var matchedAuthor = After(PublishedNameWords.MatchedClause("author"), ": ");
        var tail = PublishedNameWords.EmptyPageTail()[2..].Split(',')[0];
        var inbox = PublishedNameWords.NothingNeedsAnAnswer()[(PublishedNameWords.NoOpenQuestionsAlone().Length + 1)..].TrimEnd('.');
        var notStated = After(PublishedNameWords.NotStatedClause(), "NOT STATED (").TrimEnd(')');
        var (lineTrue, lineFalse, lineNotStated) = PublishedNameWords.ReceiptLines();
        string Sentence(string line, string head) => line[head.Length..].TrimStart().Split(';')[0].Replace("<board>", "{board}", StringComparison.Ordinal);
        var floor = "the floor was tested against";
        Assert.Contains(floor, PublishedNameWords.FloorTemplate(), StringComparison.Ordinal);

        string[] fragments =
        [
            boardUnmatched,
            authorUnmatched,
            matchedBoard,
            matchedAuthor,
            tail,
            inbox,
            notStated,
            Sentence(lineTrue, "board_new true"),
            Sentence(lineFalse, "board_new false"),
            Sentence(lineNotStated, "board_new NOT STATED"),
            floor,
        ];

        Assert.True(fragments.All(f => f.Length >= 20), $"NOT REACHED: a fragment of the published words is shorter than 20 characters, too short to fence: {string.Join(" | ", fragments)}");
        return fragments;
    }

    private static IEnumerable<string> ClientSources(string root) =>
        ClientProjects
            .SelectMany(project => Directory.EnumerateFiles(Path.Combine(root, "src", project), "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>
    /// NMN42, the positive half: <c>Curia.Client/NameWording.cs</c> exists and holds every fragment. Predicted red on the seam: the file does not exist, so the words are written
    /// nowhere (a fence that only forbade the other files would be green on a tree that never printed the words).
    /// </summary>
    [Fact]
    public void R10_97_NMN42_TheOneFileHoldsTheWords()
    {
        var path = Path.Combine(Root(), "src", "Curia.Client", "NameWording.cs");
        Assert.True(File.Exists(path), "Curia.Client/NameWording.cs does not exist: the filters line, the receipt line and the floor sentence are written by no file");

        var text = File.ReadAllText(path);
        var missing = Fragments().Where(f => !text.Contains(f, StringComparison.Ordinal)).ToArray();
        Assert.True(missing.Length == 0, $"NameWording.cs does not hold: {string.Join(" | ", missing)}");
    }

    /// <summary>
    /// NMN42, the negative half: no other source file of <c>Curia.Client</c>, <c>Curia.Client.Cli</c> or <c>Curia.Mcp</c> contains an opening fragment, so the CLI, the adapter and the
    /// resend cannot drift apart. A control while nothing prints the words (green on the seam), and the fence FNMN37 turns red when a client writes the words inline.
    /// </summary>
    [Fact]
    public void R10_97_NMN42_NoOtherClientFileWritesTheWords()
    {
        var root = Root();
        var fragments = Fragments();
        var sources = ClientSources(root).Where(p => !p.EndsWith(Path.Combine("Curia.Client", "NameWording.cs"), StringComparison.Ordinal)).ToArray();
        Assert.True(sources.Length > 50, $"NOT REACHED: only {sources.Length} client source files were read");

        var found = sources
            .SelectMany(path => fragments.Where(f => File.ReadAllText(path).Contains(f, StringComparison.Ordinal)).Select(f => $"{Path.GetRelativePath(root, path)}: \"{f}\""))
            .ToArray();
        Assert.True(found.Length == 0, $"a client file writes the words itself: {string.Join(" | ", found)}");
    }

    /// <summary>
    /// NMN43. The members are in no cursor binding, canonical form, stored event or leaf: no source file of <c>Curia.Domain</c>, <c>Curia.Canon</c> or <c>Curia.Infrastructure</c>
    /// names <c>unmatched_filters</c>, <c>board_new</c> or <c>NamedFilters</c> (the binding is <c>RetrievalCursor</c>'s, the canonical form is the domain's, the event payload is
    /// built in <c>Curia.Application</c> from the verified submission alone). A control, green on the seam; FNMN12 and FNMN22 are its falsifications.
    /// </summary>
    [Fact]
    public void R9_55_NMN43_TheMembersAreInNoBindingNoCanonicalFormAndNoStoredEvent()
    {
        var root = Root();
        var sources = DomainProjects
            .SelectMany(project => Directory.EnumerateFiles(Path.Combine(root, "src", project), "*.cs", SearchOption.AllDirectories))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();
        Assert.True(sources.Length > 50, $"NOT REACHED: only {sources.Length} source files were read");
        Assert.Contains(sources, p => p.EndsWith("RetrievalCursor.cs", StringComparison.Ordinal));

        var found = sources
            .SelectMany(path => MemberTokens.Where(t => File.ReadAllText(path).Contains(t, StringComparison.Ordinal)).Select(t => $"{Path.GetRelativePath(root, path)}: {t}"))
            .ToArray();
        Assert.True(found.Length == 0, $"a member the Forum states about a request is in the domain, the canon or the store: {string.Join(" | ", found)}");
    }
}
