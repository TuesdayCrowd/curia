using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R10.96 (errata G60), RNC16, against a real Forum and a real Postgres, in the pattern of <see cref="PwdScreeningEndpointTests"/>: a question whose body is the
/// exercise's connection string built from variables is stored (<c>201</c>), and read back as it was sent with the digest the signer computed from its own
/// bytes (invariant 1: nothing between verify and persist); a question carrying a value in the same position is refused <c>422</c>, naming the category and
/// an offset and holding neither the value nor its scheme. The Forum's screen is the authority; the clients pre-screen with the same assembly.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ReferenceScreeningEndpointTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";
    private const string ExerciseBody = "DATABASE_URL=\"postgres://$DB_USER:$DB_PASS@$DB_HOST/$DB_NAME\" sqlx migrate run";
    private const string ValueBody = "postgres://u:$ecretEXAMPLE-NOT-A-REAL-SECRET@h";

    private async Task<(HttpStatusCode Status, string Raw, byte[] Sent)> AskAsync(string body, string stem)
    {
        var ct = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agent = ForumAgent.Create($"https://agents.example/{stem}-{suffix}", $"{stem}-{suffix}");
        var (dpop, token) = await agent.AuthenticateAsync(forum.Client, TokenEndpoint, forum.Now, ct);
        var wire = agent.SignQuestion("board-" + suffix, body, "A question about " + suffix, forum.Now);
        using var response = await dpop.PostAsync(forum.Client, PostsUrl, token, wire, forum.Now, ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct), wire);
    }

    /// <summary>The digest of the envelope in a signed submission, computed here from the bytes that were sent: its canonical form, hashed.</summary>
    private static string DigestOfSent(byte[] wire)
    {
        using var document = JsonDocument.Parse(wire);
        var envelope = Encoding.UTF8.GetBytes(document.RootElement.GetProperty("envelope").GetRawText());
        Assert.True(JsonReader.Parse(envelope, AdmitLimits.Default).TryGetValue(out var value, out var parseError), parseError?.Type);
        Assert.True(CanonicalJson.CanonicalizeWithNfc(value!).TryGetValue(out var canonical, out var canonError), canonError?.Type);
        return Curia.Canon.Digests.Sha256(canonical).ToPrefixed();
    }

    [Fact]
    public async Task R10_96_RNC16_TheExercisesBodyIsStoredAndReadBackAsSentAndAValueInItsPlaceIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var stored = await AskAsync(ExerciseBody, "exercise");
        var refused = await AskAsync(ValueBody, "value");

        Assert.True(stored.Status == HttpStatusCode.Created, $"expected 201, was {(int)stored.Status} {stored.Raw}");
        using var receipt = JsonDocument.Parse(stored.Raw);
        var postId = receipt.RootElement.GetProperty("post_id").GetString()!;
        var sent = DigestOfSent(stored.Sent);
        Assert.Equal(sent, receipt.RootElement.GetProperty("digest").GetString());

        using var read = await forum.Client.GetAsync(new Uri($"/v1/posts/{postId}", UriKind.Relative), ct);
        using var served = JsonDocument.Parse(await read.Content.ReadAsStringAsync(ct));
        Assert.Equal(sent, served.RootElement.GetProperty("digest").GetString());
        using var canonical = JsonDocument.Parse(served.RootElement.GetProperty("canonical").GetString()!);
        Assert.Equal(ExerciseBody, canonical.RootElement.GetProperty("body").GetString());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.Status);
        Assert.Matches(@"ConnectionStringPassword@\d+", refused.Raw);
        Assert.DoesNotContain("EXAMPLE-NOT-A-REAL-SECRET", refused.Raw, StringComparison.Ordinal);
        Assert.DoesNotContain("postgres://", refused.Raw, StringComparison.Ordinal);
    }
}
