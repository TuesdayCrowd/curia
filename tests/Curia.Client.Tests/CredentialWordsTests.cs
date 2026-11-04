using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Curia.Client;
using Curia.Domain.Content;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R11.84 and R10.96 (errata G60), probes RNC17 to RNC19, RNC22, RNC26 and RNC27 at the command: the real <c>curia</c> in a child process (stdout and stderr read
/// separately, the exit code, a recording Forum that counts what reaches it). The sentences are read from the errata by <see cref="PublishedReferenceWords"/>
/// and matched in full, never from <see cref="CredentialWording"/>: a copy that differed from the errata by one word is red (FRNC29). Each negative carries its
/// positive in a fact of its own, so a refusal that prints nothing for the wrong reason cannot pass for one that prints nothing for the right one.
///
/// <para>The values in these drafts are obviously fake (<c>EXAMPLE-NOT-A-REAL-SECRET</c>, the corpus's own); the references are R10.96's forms. Written red on the
/// seam, where <see cref="CredentialWording.SentenceFor"/> returns nothing and a reference is still refused.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class CredentialWordsTests : IDisposable
{
    private const string Value = "Connect with postgres://u:EXAMPLE-NOT-A-REAL-SECRET@h before you start.";
    private const string Reference = "DATABASE_URL=\"postgres://$DB_USER:$DB_PASS@$DB_HOST/$DB_NAME\" sqlx migrate run";
    private const string Token = "My token is ghp_A1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r8 and it fails.";
    private const string Both = Value + " " + Token;
    private const string CloudKey = "CI logged the key AKIAIOSFODNN7EXAMPLE and we must rotate it.";

    private readonly RecordingForum _forum = new();
    private readonly string _home;

    public CredentialWordsTests() => _home = CuriaCli.NewHome(_forum.Address);

    public void Dispose()
    {
        _forum.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Fold(string text) => Whitespace().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    private static int Count(string haystack, string needle)
    {
        var count = 0;
        for (var at = haystack.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            count++;

        return count;
    }

    private async Task<(int Exit, string Stdout, string Stderr)> AskAsync(string body)
    {
        var file = Path.Combine(_home, "body.txt");
        await File.WriteAllTextAsync(file, body, Ct);
        return await CuriaCli.RunAsync(_home, Ct, "ask", "--agent", "alice", "--board", "general", "--title", "A title", "--body-file", file, "--forum", _forum.Address.ToString());
    }

    // ---- RNC17 ----------------------------------------------------------------------------------------------------------

    /// <summary>RNC17: the client's pre-screen builds a draft whose connection string is made of variables (nothing sent, nothing refused), and refuses one that carries a value, naming the category and an offset and never the value.</summary>
    [Fact]
    public void R10_96_RNC17_ThePreScreenBuildsAReferenceAndRefusesAValue()
    {
        var root = Directory.CreateTempSubdirectory("curia-rnc17-").FullName;
        try
        {
            var created = new ProfileStore(root).Create("alice", "https://agents.example/alice", "alice-1", new Uri("http://localhost:5199"));
            Assert.True(created.TryGetValue(out var agent, out _));
            using (agent)
            {
                var draft = new PostDraft { Kind = PostKind.Question, Board = "general", Title = "A title", Body = Reference, Tags = ["jcs"] };
                var refused = SubmissionBuilder.Build(agent!, draft with { Body = Value }, new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
                Assert.False(refused.TryGetValue(out _, out var error), "the value leg: expected a refusal");
                Assert.True(ClientErrors.IsCredentialMaterial(error!), $"expected a credential refusal, found {error!.Type}");
                Assert.Contains("ConnectionStringPassword@", error.Detail, StringComparison.Ordinal);
                Assert.DoesNotContain("EXAMPLE-NOT-A-REAL-SECRET", $"{error.Title}{error.Detail}", StringComparison.Ordinal);

                var built = SubmissionBuilder.Build(agent!, draft, new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero));
                Assert.True(built.TryGetValue(out _, out var buildError), $"expected a submission, found {buildError?.Type}");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ---- RNC18 ----------------------------------------------------------------------------------------------------------

    /// <summary>RNC18: <c>curia ask</c> of a value draft exits 3; stderr holds the title, "Rotate" and then R11.84's refusal sentence exactly once, in that order; stdout is empty; nothing reaches the Forum.</summary>
    [Fact]
    public async Task R11_84_RNC18_TheCliSaysAfterTheInstructionToRotateWhatIsAcceptedInsteadOfAValue()
    {
        var (exit, stdout, stderr) = await AskAsync(Value);
        var folded = Fold(stderr);
        var sentence = Fold(PublishedReferenceWords.RefusalSentence());

        Assert.True(exit == 3, $"exit {exit}; stdout: {stdout}; stderr: {stderr}");
        Assert.Empty(stdout);
        Assert.Empty(_forum.Lines);
        var rotate = folded.IndexOf("Rotate it", StringComparison.Ordinal);
        Assert.True(rotate >= 0, $"expected the title ending 'Rotate it', stderr: {stderr}");
        var at = folded.IndexOf(sentence, StringComparison.Ordinal);
        Assert.True(at >= 0, $"expected R11.84's sentence after the instruction to rotate, found none; stderr: {stderr}");
        Assert.True(at > rotate, "expected the sentence after the instruction to rotate, found it before");
        Assert.Equal(1, Count(folded, sentence));
        Assert.DoesNotContain("EXAMPLE-NOT-A-REAL-SECRET", stderr, StringComparison.Ordinal);
    }

    // ---- RNC19 ----------------------------------------------------------------------------------------------------------

    /// <summary>RNC19, the negative leg: a token alone is refused with "Rotate" and no sentence about references (its category is not the one R11.84 names). CONTROL on the seam.</summary>
    [Fact]
    public async Task R11_84_RNC19_ATokenAloneCarriesNoSentenceAboutReferences()
    {
        var (exit, _, stderr) = await AskAsync(Token);
        var folded = Fold(stderr);

        Assert.True(exit == 3, $"exit {exit}; stderr: {stderr}");
        Assert.Contains("Rotate it", folded, StringComparison.Ordinal);
        Assert.DoesNotContain("reference to a secret", folded, StringComparison.Ordinal);
        Assert.DoesNotContain(Fold(PublishedReferenceWords.RefusalSentence()), folded, StringComparison.Ordinal);
    }

    /// <summary>RNC19, the positive leg: a text with both categories carries the sentence once.</summary>
    [Fact]
    public async Task R11_84_RNC19_ATextWithBothCategoriesCarriesTheSentenceOnce()
    {
        var (exit, _, stderr) = await AskAsync(Both);
        var folded = Fold(stderr);
        var sentence = Fold(PublishedReferenceWords.RefusalSentence());

        Assert.True(exit == 3, $"exit {exit}; stderr: {stderr}");
        Assert.True(Count(folded, sentence) == 1, $"expected R11.84's sentence for ConnectionStringPassword once, found {Count(folded, sentence)}; stderr: {stderr}");
    }

    // ---- RNC22 ----------------------------------------------------------------------------------------------------------

    /// <summary>RNC22: <c>curia help</c> carries R11.84's description sentence once and still carries the sentence about local screening.</summary>
    [Fact]
    public async Task R11_84_RNC22_TheHelpCarriesTheDescriptionSentenceOnce()
    {
        var (exit, stdout, _) = await CuriaCli.RunAsync(_home, Ct, "help");
        var folded = Fold(stdout);
        var sentence = Fold(PublishedReferenceWords.DescriptionSentence());

        Assert.Equal(0, exit);
        Assert.Contains("Content is screened locally before anything is transmitted.", folded, StringComparison.Ordinal);
        Assert.True(Count(folded, sentence) == 1, $"expected the sentence once, found {Count(folded, sentence)}");
    }

    // ---- RNC26 ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// RNC26, the CLI's legs: over four category sets (a connection-string password alone, a token alone, both, a cloud key alone) each refusal is exit 3 and holds "Rotate"
    /// (so the second type is read as the first is by <c>curia</c>), and R11.84's sentence is present exactly when the connection-string category is in the set.
    /// </summary>
    [Fact]
    public async Task R11_84_RNC26_EveryCategorySetIsRefusedAndTheSentenceIsThereExactlyForTheConnectionString()
    {
        var sentence = Fold(PublishedReferenceWords.RefusalSentence());
        var failures = new List<string>();
        foreach (var (name, body, expected) in new[] { ("ConnectionStringPassword", Value, true), ("ApiKey", Token, false), ("both", Both, true), ("CloudCredential", CloudKey, false) })
        {
            var (exit, _, stderr) = await AskAsync(body);
            var folded = Fold(stderr);
            if (exit != 3) failures.Add($"{name}: expected exit 3, was {exit}");
            if (!folded.Contains("Rotate", StringComparison.Ordinal)) failures.Add($"{name}: expected 'Rotate', found none");
            var count = Count(folded, sentence);
            if (count != (expected ? 1 : 0)) failures.Add($"{name}: expected the sentence for ConnectionStringPassword {(expected ? "once" : "not at all")}, found {count}");
        }

        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    // ---- RNC27 ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// RNC27 (ruling 17): a flag whose rationale the Forum refuses (<c>curia/flag/rationale-rejected</c>) prints the Forum's line, exit 3, and neither R11.84's sentence nor
    /// "Rotate": that refusal names the category and instructs nothing. CONTROL on the seam. Its positive is <see cref="R11_84_RNC18_TheCliSaysAfterTheInstructionToRotateWhatIsAcceptedInsteadOfAValue"/>.
    /// </summary>
    [Fact]
    public async Task R11_84_RNC27_AFlagRationaleRefusalCarriesNeitherSentenceNorRotate()
    {
        using var forum = new RationaleRefusingForum();
        var home = CuriaCli.NewHome(forum.Address);
        try
        {
            var (exit, stdout, stderr) = await CuriaCli.RunAsync(home, Ct, "flag", "01ARZ3NDEKTSV4RRFFQ69G5FAV", "--agent", "alice", "--kind", "spam", "--rationale", "the log shows a connection string", "--forum", forum.Address.ToString());

            Assert.True(exit == 3, $"exit {exit}; stdout: {stdout}; stderr: {stderr}");
            Assert.Contains("curia/flag/rationale-rejected", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("Rotate", stderr, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("reference to a secret", Fold(stderr), StringComparison.Ordinal);
            Assert.DoesNotContain(Fold(PublishedReferenceWords.RefusalSentence()), Fold(stderr), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>A loopback Forum that answers the token endpoint and every other request with the problem document of a flag whose rationale carried a credential.</summary>
    private sealed class RationaleRefusingForum : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly Task _loop;

        internal RationaleRefusingForum()
        {
            for (var attempt = 1; ; attempt++)
            {
                int port;
                using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
                {
                    probe.Start();
                    port = ((IPEndPoint)probe.LocalEndpoint).Port;
                    probe.Stop();
                }

                var listener = new HttpListener();
                var address = new Uri($"http://127.0.0.1:{port}");
                listener.Prefixes.Add(address.ToString());
                try
                {
                    listener.Start();
                }
                catch (HttpListenerException) when (attempt < 10)
                {
                    listener.Close();
                    continue;
                }

                _listener = listener;
                Address = address;
                break;
            }

            _loop = Task.Run(LoopAsync);
        }

        internal Uri Address { get; }

        private async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }

                var (status, body, type) = (context.Request.Url?.AbsolutePath ?? string.Empty) == "/oauth/token"
                    ? (200, """{"access_token":"stub-token","token_type":"DPoP","expires_in":300,"scope":"flag:create"}""", "application/json")
                    : (422, """{"type":"curia/flag/rationale-rejected","title":"The flag's rationale was rejected by ingest screening","detail":"ConnectionStringPassword@14"}""", "application/problem+json");
                var bytes = Encoding.UTF8.GetBytes(body);
                context.Response.StatusCode = status;
                context.Response.ContentType = type;
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                context.Response.Close();
            }
        }

        public void Dispose()
        {
            _listener.Close();
            try
            {
                _loop.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // The listener was closed under the loop; that is how it stops.
            }
        }
    }
}
