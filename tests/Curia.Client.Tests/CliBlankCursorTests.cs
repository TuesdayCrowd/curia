using System.Diagnostics.CodeAnalysis;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R11.58 (errata G43): <c>curia search</c> and <c>curia inbox</c> refuse a <c>--cursor</c> that is present and empty or blank, exit 1, with
/// a message that names the flag and says nothing was sent, before a profile is loaded or a token minted; an absent <c>--cursor</c> is page
/// one. The Forum reads a blank cursor as absent (R9.38) and <c>curia_search</c> refuses it by name (R11.52); the command line passed it on.
///
/// <para><b>Oracles.</b> The request list is a recorder's (<see cref="RecordingForum"/>), a listener the command was pointed at, and not the
/// command's report of itself; the flag's name is parsed from the errata (<see cref="PublishedSmallWords.BlankCursorFlag"/>); the blank
/// spellings are built at run time. Each refusal has its control: no <c>--cursor</c> sends one request, and a real cursor is sent as typed.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class CliBlankCursorTests : IDisposable
{
    private readonly RecordingForum _forum = new();
    private readonly List<string> _homes = [];

    public void Dispose()
    {
        _forum.Dispose();
        foreach (var home in _homes) Directory.Delete(home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Home(bool enrolled)
    {
        var home = CuriaCli.NewHome(enrolled ? _forum.Address : null);
        _homes.Add(home);
        return home;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The spellings of a present, blank cursor: each is the argv of a command line. The last is a trailing bare <c>--cursor</c>, so a caller puts the spelling last.</summary>
    public static TheoryData<string> Blank() => new()
    {
        "empty",
        "space",
        "tab",
        "equals",
        "bare",
    };

    private static string[] Spell(string spelling) => spelling switch
    {
        "empty" => ["--cursor", ""],
        "space" => ["--cursor", " "],
        "tab" => ["--cursor", ((char)9).ToString()],
        "equals" => ["--cursor="],
        "bare" => ["--cursor"],
        _ => throw new ArgumentOutOfRangeException(nameof(spelling)),
    };

    // ---- PJ1 ------------------------------------------------------------------------------------

    /// <summary>PJ1: <c>search</c> refuses every blank spelling, exit 1, naming <c>--cursor</c>, and sends nothing.</summary>
    [Theory]
    [MemberData(nameof(Blank))]
    public async Task R11_58_SearchRefusesABlankCursorAndSendsNothing(string spelling)
    {
        ArgumentNullException.ThrowIfNull(spelling);
        var home = Home(enrolled: true);

        var (exit, stdout, stderr) = await CuriaCli.RunAsync(
            home, Ct, ["search", "postgres", "--board", "postgres", "--forum", _forum.Address.ToString(), .. Spell(spelling)]);

        Assert.True(exit == 1, $"exit {exit}; stdout: {stdout}; stderr: {stderr}");
        Assert.Contains(PublishedSmallWords.BlankCursorFlag(), stderr, StringComparison.Ordinal);
        Assert.Contains("nothing was sent", stderr, StringComparison.Ordinal);
        Assert.Empty(_forum.Lines);
    }

    // ---- PJ2 ------------------------------------------------------------------------------------

    /// <summary>PJ2: <c>inbox</c> refuses it first, before any profile is looked for (none enrolled) and before any token is minted (one enrolled).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task R11_58_InboxRefusesItBeforeAnyProfileOrToken(bool enrolled)
    {
        var home = Home(enrolled);

        var (exit, stdout, stderr) = await CuriaCli.RunAsync(home, Ct, "inbox", "--cursor", " ", "--forum", _forum.Address.ToString());

        Assert.True(exit == 1, $"exit {exit}; stdout: {stdout}; stderr: {stderr}");
        Assert.Contains(PublishedSmallWords.BlankCursorFlag(), stderr, StringComparison.Ordinal);
        Assert.Contains("nothing was sent", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("--agent", stderr, StringComparison.Ordinal);
        Assert.Empty(_forum.Lines);
    }

    /// <summary>PJ2's other spellings on <c>inbox</c>, enrolled: every blank spelling, no request (the token request included).</summary>
    [Theory]
    [MemberData(nameof(Blank))]
    public async Task R11_58_InboxRefusesEveryBlankSpellingAndSendsNothing(string spelling)
    {
        ArgumentNullException.ThrowIfNull(spelling);
        var home = Home(enrolled: true);

        var (exit, stdout, stderr) = await CuriaCli.RunAsync(
            home, Ct, ["inbox", "--agent", "alice", "--forum", _forum.Address.ToString(), .. Spell(spelling)]);

        Assert.True(exit == 1, $"exit {exit}; stdout: {stdout}; stderr: {stderr}");
        Assert.Contains(PublishedSmallWords.BlankCursorFlag(), stderr, StringComparison.Ordinal);
        Assert.Empty(_forum.Lines);
    }

    // ---- PJ3 ------------------------------------------------------------------------------------

    /// <summary><c>search --titles</c> takes the same path (<c>browse</c> has no cursor flag, so its refusal is not this probe's).</summary>
    [Fact]
    public async Task R11_58_SearchTitlesTakesTheSamePath()
    {
        var home = Home(enrolled: true);

        var (exit, stdout, stderr) = await CuriaCli.RunAsync(
            home, Ct, "search", "postgres", "--titles", "--cursor", "", "--forum", _forum.Address.ToString());

        Assert.True(exit == 1, $"exit {exit}; stdout: {stdout}; stderr: {stderr}");
        Assert.Contains(PublishedSmallWords.BlankCursorFlag(), stderr, StringComparison.Ordinal);
        Assert.Empty(_forum.Lines);
    }

    // ---- PJ4 (control) --------------------------------------------------------------------------

    /// <summary>
    /// PJ4, the control: with no <c>--cursor</c> a request goes out and carries none, and a real cursor is sent as typed. Green first; held by
    /// FPJ3 (a check that refuses an absent cursor) and FPJ4 (one that refuses a real one).
    /// </summary>
    [Fact]
    public async Task R11_58_AnAbsentCursorIsPageOneAndARealOneAdvances()
    {
        var home = Home(enrolled: true);

        await CuriaCli.RunAsync(home, Ct, "search", "postgres", "--forum", _forum.Address.ToString());
        var first = Assert.Single(_forum.Lines);
        Assert.StartsWith("GET /v1/search?", first, StringComparison.Ordinal);
        Assert.DoesNotContain("cursor=", first, StringComparison.Ordinal);

        await CuriaCli.RunAsync(home, Ct, "search", "postgres", "--cursor", "c3R1Yi1jdXJzb3I=", "--forum", _forum.Address.ToString());
        var advanced = _forum.Lines.Last();
        Assert.Contains("cursor=c3R1Yi1jdXJzb3I%3D", advanced, StringComparison.Ordinal);
    }
}
