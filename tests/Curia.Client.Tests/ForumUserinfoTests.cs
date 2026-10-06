using System.Diagnostics.CodeAnalysis;
using Curia.Client;
using Curia.Domain.Serving;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R10.63 (the strangers stage's final gate, third round): a password written into <c>--forum</c> or
/// <c>CURIA_FORUM</c> reaches no line a reader sees. The second round routed every site that names a
/// configured Forum through <see cref="HeadStore.Origin"/>, and held two of them by facts; the transport
/// refusals had none, and the Reader Contract's fallback still printed the userinfo. These hold the
/// refusal, and a scan of the reference readers' source for the two expressions that keep it.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ForumUserinfoTests
{
    /// <summary>The files that name a configured Forum, each of which must do so through <see cref="HeadStore.Origin"/>.</summary>
    private static readonly string[] Namers = ["Program.cs", "ForumClient.cs", "ForumWriter.cs"];

    /// <summary>
    /// A transport refusal names the Forum by its origin. The handler's own message names nothing, so
    /// whatever of the configured URL reaches the refusal came from the client.
    /// </summary>
    [Fact]
    public async Task R10_63_ATransportRefusalNamesTheForumWithoutItsUserinfo()
    {
        var forum = new Uri("http://alice:s3cretPW@127.0.0.1:9/");
        using var handler = new Unreachable();
        using var http = new HttpClient(handler) { BaseAddress = forum };
        var client = new ForumClient(http, forum);

        var board = await client.GetBoardAsync("b", MarkingMode.Datamark, TestContext.Current.CancellationToken);

        Assert.False(board.TryGetValue(out _, out var refusal));
        Assert.Equal(RefusalKind.Transport, refusal!.Kind);
        foreach (var text in new[] { refusal.Summary, refusal.Error.Type, refusal.Error.Title, refusal.Error.Detail ?? "" })
        {
            Assert.DoesNotContain("s3cret", text, StringComparison.Ordinal);
            Assert.DoesNotContain("alice", text, StringComparison.Ordinal);
        }

        Assert.Contains("127.0.0.1:9", refusal.Error.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// No file of the reference readers builds a URI from a configured Forum, or takes its authority, by
    /// an expression that keeps the userinfo: <c>new Uri(forum,</c> and <c>GetLeftPart(UriPartial.Authority)</c>.
    /// <see cref="HeadStore"/> is the one exempt file, since <see cref="HeadStore.Origin"/> is where the
    /// userinfo is dropped. The guard: the scan must find <c>HeadStore.Origin(</c> in the three files
    /// that name a configured Forum, or it read nothing.
    /// </summary>
    [Fact]
    public void R10_63_NoReaderBuildsAForumUriThatKeepsItsUserinfo()
    {
        var root = RepositoryRoot();
        string[] forbidden = ["new Uri(forum,", "GetLeftPart(UriPartial.Authority)"];
        var offenders = new List<string>();
        var origins = new HashSet<string>(StringComparer.Ordinal);

        foreach (var project in new[] { "src/Curia.Client", "src/Curia.Client.Cli", "src/Curia.Mcp" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal)) continue;

                var lines = File.ReadAllLines(file);
                if (lines.Any(l => l.Contains("HeadStore.Origin(", StringComparison.Ordinal))) origins.Add(Path.GetFileName(file));
                if (relative == "src/Curia.Client/HeadStore.cs") continue;

                for (var i = 0; i < lines.Length; i++)
                    if (forbidden.Any(f => lines[i].Contains(f, StringComparison.Ordinal)))
                        offenders.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(offenders.Count == 0, "a configured Forum's userinfo is kept by:\n" + string.Join('\n', offenders));
        Assert.True(
            Namers.All(origins.Contains),
            "the scan found HeadStore.Origin( only in " + string.Join(", ", origins) + "; it did not read the readers");
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Curia.sln"))) return directory.FullName;
        }

        throw new InvalidOperationException($"Curia.sln is not above {AppContext.BaseDirectory}");
    }

    /// <summary>A handler that refuses every request as a network fault would, naming nothing.</summary>
    private sealed class Unreachable : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("connection refused");
    }
}
