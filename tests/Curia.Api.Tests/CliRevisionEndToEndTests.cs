using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Curia.Api.Tests.Fuzz;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// D48: <c>curia revision</c>, the real command in a child process, against the real Forum on a real socket.
///
/// <para><b>Why a child process.</b> The verbs build their own <c>HttpClient</c> (<c>Program.HttpFor</c>), so
/// there is no handler to hand them; a socket and the shipped binary need no seam in production code at all.
/// <c>RevisionOwnershipTests</c> signs through <c>ForumAgent.SignRevision(..., prev)</c>, which sets <c>prev</c>
/// itself, so the Forum's rule and the client's draft were each tested and never met: every
/// <c>curia revision</c> was a 403.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class CliRevisionEndToEndTests(KestrelForumFixture forum) : IClassFixture<KestrelForumFixture>, IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("curia-cli-revision-").FullName;

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    [GeneratedRegex(@"^(?<key>posted|digest)\s+""?(?<value>[^\s""]+)", RegexOptions.Multiline)]
    private static partial Regex Receipt();

    /// <summary>Runs <c>curia &lt;args&gt;</c> from this suite's output, with the profile store under a temporary home.</summary>
    private async Task<(int Exit, string Stdout, string Stderr)> CuriaAsync(CancellationToken ct, params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "curia.dll"));
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["CURIA_CLIENT_HOME"] = _home;

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, await stdout, await stderr);
    }

    private static (string PostId, string Digest) ReadReceipt(string stdout)
    {
        var found = Receipt().Matches(stdout).ToDictionary(m => m.Groups["key"].Value, m => m.Groups["value"].Value);
        return (found["posted"], found["digest"]);
    }

    /// <summary>
    /// An agent asks, then revises its own post with the verb as shipped: the Forum answers 201 and the stored
    /// revision's <c>prev</c> is the original's digest.
    /// </summary>
    [Fact]
    public async Task D48_TheRevisionVerbRevisesTheAgentsOwnPost()
    {
        var ct = TestContext.Current.CancellationToken;

        // The CLI stamps real time on what it signs; the fixture's clock is fixed, and R6.32 refuses the future.
        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now);

        var address = forum.Client.BaseAddress!.GetLeftPart(UriPartial.Authority);
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        var enrolled = await CuriaAsync(ct, "enrol", "--agent", "quill", "--forum", address);
        Assert.True(enrolled.Exit == 0, enrolled.Stdout + enrolled.Stderr);

        var asked = await CuriaAsync(ct, "ask", "--agent", "quill", "--board", board, "--title", "Original title", "--body", "the original body", "--forum", address);
        Assert.True(asked.Exit == 0, asked.Stdout + asked.Stderr);
        var (postId, digest) = ReadReceipt(asked.Stdout);

        var revised = await CuriaAsync(ct, "revision", "--agent", "quill", "--board", board, "--parent", postId, "--body", "a corrected body", "--forum", address);
        Assert.True(revised.Exit == 0, revised.Stdout + revised.Stderr);
        var (revisionId, _) = ReadReceipt(revised.Stdout);

        using var served = await forum.Client.GetAsync($"{address}/v1/posts/{revisionId}", ct);
        var body = await served.Content.ReadAsStringAsync(ct);
        Assert.True(served.IsSuccessStatusCode, body);
        using var json = JsonDocument.Parse(body);
        using var canonical = JsonDocument.Parse(Encoding.UTF8.GetBytes(json.RootElement.GetProperty("canonical").GetString()!));

        Assert.Equal(digest, canonical.RootElement.GetProperty("prev").GetString());
    }

    /// <summary>
    /// An explicit <c>--prev</c> is checked against the parent's digest as this client computed it, not used in its
    /// place: one that names another post is a usage error and nothing is sent.
    /// </summary>
    [Fact]
    public async Task D48_APrevThatIsNotTheParentsDigestIsAUsageError()
    {
        var ct = TestContext.Current.CancellationToken;
        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now);

        var address = forum.Client.BaseAddress!.GetLeftPart(UriPartial.Authority);
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        Assert.Equal(0, (await CuriaAsync(ct, "enrol", "--agent", "sable", "--forum", address)).Exit);
        var asked = await CuriaAsync(ct, "ask", "--agent", "sable", "--board", board, "--title", "Original title", "--body", "the original body", "--forum", address);
        var (postId, digest) = ReadReceipt(asked.Stdout);

        var elsewhere = "sha256:" + new string('0', 64);
        Assert.NotEqual(digest, elsewhere);
        var revised = await CuriaAsync(ct, "revision", "--agent", "sable", "--board", board, "--parent", postId, "--prev", elsewhere, "--body", "a corrected body", "--forum", address);

        Assert.Equal(1, revised.Exit);
        Assert.DoesNotContain("posted", revised.Stdout, StringComparison.Ordinal);
    }
}
