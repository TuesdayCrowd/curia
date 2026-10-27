using System.Diagnostics.CodeAnalysis;
using Curia.Client.Cli;
using Curia.Domain.Content;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R8.73, R8.74 (errata G43) at the command line: <c>curia</c> refuses a comma tag it was given as a display literal before it sends
/// anything, naming the index; both reference doors take <c>HTTPS://</c>; and a local admission refusal carries its detail.
///
/// <para><b>Oracles.</b> The request list is a recorder's (<see cref="RecordingForum"/>); the exit numbers are Cli.cs's own constants
/// (3 is the refusal of an envelope's own content, R6.58). The comma tag is spelled the only way a command line can spell it, as a display
/// literal (R10.66), because <c>--tags</c> splits on commas.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class CliTagAndRefTests : IDisposable
{
    private readonly RecordingForum _forum = new();
    private readonly string _home;
    private readonly string _bare = CuriaCli.NewHome(null);

    public CliTagAndRefTests() => _home = CuriaCli.NewHome(_forum.Address);

    public void Dispose()
    {
        _forum.Dispose();
        Directory.Delete(_home, recursive: true);
        Directory.Delete(_bare, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- PJ27 -----------------------------------------------------------------------------------

    /// <summary>PJ27: a tag with a comma, written as a display literal, is refused locally (exit 3), naming <c>tags[0]</c>, and nothing is sent.</summary>
    [Fact]
    public async Task R8_73_TheCliRefusesADisplayLiteralCommaTagLocally()
    {
        var (exit, stdout, stderr) = await CuriaCli.RunAsync(
            _home, Ct, "ask", "--agent", "alice", "--board", "b", "--title", "A question", "--body", "a body",
            "--tags", "\"a,b\"", "--forum", _forum.Address.ToString());

        Assert.True(exit == ExitCode.Rejected, $"exit {exit}; stdout: {stdout}; stderr: {stderr}");
        Assert.Contains("tags[0]", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("a,b", stderr, StringComparison.Ordinal);
        Assert.Empty(_forum.Lines);
    }

    /// <summary>PJ27's control: a plain tag list is not refused and the command goes on to the Forum.</summary>
    [Fact]
    public async Task R8_73_APlainTagListIsSent()
    {
        await CuriaCli.RunAsync(
            _home, Ct, "ask", "--agent", "alice", "--board", "b", "--title", "A question", "--body", "a body",
            "--tags", "x,y z", "--forum", _forum.Address.ToString());

        Assert.NotEmpty(_forum.Lines);
    }

    // ---- PJ29 -----------------------------------------------------------------------------------

    /// <summary>
    /// PJ29: a local admission refusal carries its detail (the kind for <c>prev-required</c>). Through <see cref="SubmissionBuilder"/>, the
    /// door both <c>curia</c> and <c>curia-mcp</c> use.
    /// </summary>
    [Fact]
    public void R8_73_ALocalAdmissionRefusalCarriesItsDetail()
    {
        var created = new ProfileStore(_bare).Create("bob", "https://agents.example/bob", "bob-1", new Uri("http://localhost:5199"));
        Assert.True(created.TryGetValue(out var agent, out _));
        using (agent)
        {
            var draft = new PostDraft { Kind = PostKind.Revision, Board = "b", Body = "a corrected body", Parent = "01J00000000000000000000000" };

            var built = SubmissionBuilder.Build(agent!, draft, new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));

            Assert.False(built.TryGetValue(out _, out var error));

            // The refusal's detail is the kind, "revision", which its title also happens to hold; so the wrapper carries the detail
            // only if the word occurs more often in what the caller is shown than in the refusal's type and title together.
            var shown = error!.Title + "\n" + error.Detail;
            var refusal = ContentErrors.PrevRequired(PostKind.Revision);
            Assert.False(string.IsNullOrEmpty(refusal.Detail));
            Assert.True(
                Occurrences(shown, refusal.Detail!) > Occurrences(refusal.Type + "\n" + refusal.Title, refusal.Detail!),
                $"the caller is shown: {shown}");
        }
    }

    private static int Occurrences(string text, string word)
    {
        var count = 0;
        for (var at = text.IndexOf(word, StringComparison.Ordinal); at >= 0; at = text.IndexOf(word, at + word.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    // ---- PJ18 (the command-line door) -----------------------------------------------------------

    /// <summary>
    /// PJ18, the command-line door: <c>answer --refs HTTPS://...</c> and <c>comment --refs Http://...</c> are not refused as a reference form;
    /// the command goes on and reaches the Forum. The Forum's read serving the target as typed is PJ39's (a real Forum).
    /// </summary>
    [Theory]
    [InlineData("answer", "HTTPS://example.org/a")]
    [InlineData("comment", "Http://a.b")]
    public async Task R8_74_TheCommandLineDoorTakesAnyCaseOfTheScheme(string verb, string reference)
    {
        ArgumentNullException.ThrowIfNull(verb);

        var (exit, stdout, stderr) = await CuriaCli.RunAsync(
            _home, Ct, verb, "--agent", "alice", "--board", "b", "--parent", "01J00000000000000000000000", "--body", "a body",
            "--refs", reference, "--forum", _forum.Address.ToString());

        Assert.DoesNotContain("A reference is a post digest", stderr, StringComparison.Ordinal);
        Assert.NotEmpty(_forum.Lines);
        Assert.True(exit != 1 || !stderr.Contains("--refs", StringComparison.Ordinal), $"exit {exit}; stdout: {stdout}; stderr: {stderr}");
    }

    /// <summary>PJ18's control: the lower case was always taken and still is.</summary>
    [Fact]
    public async Task R8_74_TheLowerCaseSchemeIsStillTaken()
    {
        await CuriaCli.RunAsync(
            _home, Ct, "comment", "--agent", "alice", "--board", "b", "--parent", "01J00000000000000000000000", "--body", "a body",
            "--refs", "https://example.org/a", "--forum", _forum.Address.ToString());

        Assert.NotEmpty(_forum.Lines);
    }
}
