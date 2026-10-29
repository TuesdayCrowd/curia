using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R10.86 (errata G46), TRW40, against a real Forum: a question whose body is a build log carrying <c>--remap-path-prefix=$PWD=/build</c> is stored, and a
/// question whose body carries a connection string with a <c>Pwd=</c> value is refused <c>422</c>, naming the category and an offset and holding neither the
/// value nor its prefix. The Forum's screen is the authority: the clients pre-screen with the same assembly (M16).
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class PwdScreeningEndpointTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";
    private const string BuildLog = "RUSTFLAGS=\"--remap-path-prefix=$PWD=/build\" cargo build --release";
    private const string Credential = "Uid=curia;Pwd=EXAMPLE-NOT-A-REAL-SECRET";

    private async Task<(HttpStatusCode Status, string Raw)> AskAsync(string body, string stem)
    {
        var ct = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agent = ForumAgent.Create($"https://agents.example/{stem}-{suffix}", $"{stem}-{suffix}");
        var (dpop, token) = await agent.AuthenticateAsync(forum.Client, TokenEndpoint, forum.Now, ct);
        using var response = await dpop.PostAsync(
            forum.Client, PostsUrl, token, agent.SignQuestion("board-" + suffix, body, "A question about " + suffix, forum.Now), forum.Now, ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task R10_86_TRW40_TheBuildLogIsStoredAndTheCredentialIsRefused()
    {
        var log = await AskAsync(BuildLog, "buildlog");
        var credential = await AskAsync(Credential, "credential");

        Assert.True(
            log.Status == HttpStatusCode.Created,
            $"Expected: 201 Actual: {(int)log.Status} {log.Raw}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, credential.Status);
        Assert.Matches(@"ConnectionStringPassword@\d+", credential.Raw);
        Assert.DoesNotContain("EXAMPLE-NOT-A-REAL-SECRET", credential.Raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Uid=curia", credential.Raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Pwd=", credential.Raw, StringComparison.OrdinalIgnoreCase);
    }
}
