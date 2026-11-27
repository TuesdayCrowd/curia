using System.Diagnostics.CodeAnalysis;
using Curia.Api.Tests.Fuzz;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// CLW4 (errata G84, R11.111, R11.79 (rev.)): <c>curia ask</c> of a body past the Forum's bound, the real command in a child process against the real Forum on a socket. The client does not screen R6.39
/// (Task 0.9), so the act is signed and sent and the Forum answers 413 <c>curia/request/too-large</c>: exit 3 (a refusal of the content, never exit 8), R11.111's sentence read from the errata, no recipe to resend,
/// and no kept act, so a second identical call is refused the same way and takes no place of the 64.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class BodyBoundCliTests(KestrelForumFixture forum) : IClassFixture<KestrelForumFixture>, IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("curia-cli-bound-").FullName;

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Address => forum.Client.BaseAddress!.GetLeftPart(UriPartial.Authority);

    [Fact]
    public async Task R11_111_CLW4_AnActPastTheBoundIsAContentRefusalThatKeepsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now);
        Assert.Equal(0, (await CuriaProcess.RunAsync(_home, ct, "enrol", "--agent", "bound-asker", "--forum", Address)).Exit);
        var bodyFile = Path.Combine(_home, "big.txt");
        await File.WriteAllTextAsync(bodyFile, new string('a', 1_100_000), ct);
        var board = "bound-" + Guid.NewGuid().ToString("N")[..8];
        string[] Ask() => ["ask", "--agent", "bound-asker", "--board", board, "--title", "A question that is far too long", "--body-file", bodyFile, "--forum", Address, FirstAsk.Flag];

        var first = await CuriaProcess.RunAsync(_home, ct, Ask());
        var acts = await CuriaProcess.RunAsync(_home, ct, "acts", "--agent", "bound-asker");
        var second = await CuriaProcess.RunAsync(_home, ct, Ask());
        var actsAfter = await CuriaProcess.RunAsync(_home, ct, "acts", "--agent", "bound-asker");

        var said = first.Stdout + first.Stderr;
        var problems = new List<string>();
        if (first.Exit != 3) problems.Add($"first call: exit {first.Exit}, expected 3 (a refusal of the content)");
        if (!said.Contains(PublishedRequestBound.Sentence(), StringComparison.Ordinal)) problems.Add($"first call: R11.111's sentence is not on the output:{Environment.NewLine}{said}");
        if (said.Contains("curia resend", StringComparison.Ordinal) || said.Contains("sha256:", StringComparison.Ordinal)) problems.Add("first call: the output offers a resend or names a digest");
        if (acts.Stdout.Contains("sha256:", StringComparison.Ordinal)) problems.Add($"curia acts after the first call lists an act:{Environment.NewLine}{acts.Stdout}");
        if (second.Exit != 3) problems.Add($"second call: exit {second.Exit}, expected 3");
        if (actsAfter.Stdout.Contains("sha256:", StringComparison.Ordinal)) problems.Add($"curia acts after the second call lists an act:{Environment.NewLine}{actsAfter.Stdout}");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
