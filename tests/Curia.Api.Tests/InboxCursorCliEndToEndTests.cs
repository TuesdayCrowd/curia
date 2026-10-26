using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R9.38 (errata G37), the reader's half: <c>curia inbox --cursor garbage</c>, the shipped command run as a child process
/// against the real Forum on a real socket, exits 3 (a refusal of the request itself), says on its error stream that the
/// cursor was refused and by what type, and quotes nothing of what was sent. The live run at Task 0 had it exit 0 and print page one.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class InboxCursorCliEndToEndTests(BrowseForumFixture forum) : IClassFixture<BrowseForumFixture>, IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("curia-inbox-cursor-").FullName;

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Address => forum.Client.BaseAddress!.GetLeftPart(UriPartial.Authority);

    [GeneratedRegex(@"^posted\s+""?(?<id>[^\s""]+)", RegexOptions.Multiline)]
    private static partial Regex Posted();

    private async Task<(int Exit, string Stdout, string Stderr)> CuriaAsync(CancellationToken ct, params string[] args)
    {
        // The child signs with the real clock, as the browse facts do.
        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now);
        return await CuriaProcess.RunAsync(_home, ct, args);
    }

    [Fact]
    public async Task R9_38_CuriaInboxWithAGarbageCursorExitsThreeAndSaysWhy()
    {
        var ct = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (asker, reader, board) = ("ask-" + suffix, "read-" + suffix, "board-" + suffix);
        foreach (var slug in new[] { asker, reader })
        {
            var enrolled = await CuriaAsync(ct, "enrol", "--agent", slug, "--forum", Address);
            Assert.True(enrolled.Exit == 0, enrolled.Stdout + enrolled.Stderr);
        }

        var asked = await CuriaAsync(ct, "ask", "--agent", asker, "--board", board, "--title", "Open for the inbox", "--body", "a body", "--forum", Address);
        Assert.True(asked.Exit == 0, asked.Stdout + asked.Stderr);
        var questionId = Posted().Match(asked.Stdout).Groups["id"].Value;
        Assert.NotEmpty(questionId);

        // The control: no cursor, the question is listed.
        var control = await CuriaAsync(ct, "inbox", "--agent", reader, "--board", board, "--forum", Address);
        Assert.Equal(0, control.Exit);
        Assert.Contains(questionId, control.Stdout, StringComparison.Ordinal);

        var garbage = await CuriaAsync(ct, "inbox", "--agent", reader, "--board", board, "--cursor", "garbage", "--forum", Address);

        Assert.Equal(3, garbage.Exit);
        Assert.Contains("curia/inbox/cursor-malformed", garbage.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("garbage", garbage.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(questionId, garbage.Stdout, StringComparison.Ordinal);
    }
}
