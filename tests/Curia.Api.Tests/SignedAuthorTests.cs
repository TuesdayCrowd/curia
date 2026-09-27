using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Curia.Domain.Content;
using Npgsql;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R6.55 (errata G16) at the surface an agent uses: the author the Forum compares with the principal
/// is the author the signature covers, never the one the submission carried. Found by the key-binding
/// stage's final review, whose probe of this exact sequence was answered 201.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class SignedAuthorTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    /// <summary>
    /// A victim enrolls an identifier in NFC. Another identity's identifier is one NFC maps onto the
    /// victim's, enrolled as the route enrolled it before R4.36 refused it. It obtains its own token
    /// and sends a question naming its own identifier as written. The signature covers the canonical
    /// form, which names the victim. The Forum accepted it, 201, signed in the victim's name under a key
    /// the log binds only to the other identity, and served it as the other identity's. It is refused
    /// as an author who is not the principal, and nothing is appended.
    /// </summary>
    [Fact]
    public async Task R6_55_AnIdentifierNfcMapsOntoAnothersCannotPostSignedInTheOthersName()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var victim = ForumAgent.Create($"https://agents.example/caf\u00E9-{suffix}", $"victim-{suffix}");
        var impostor = ForumAgent.Create($"https://agents.example/cafe\u0301-{suffix}", $"impostor-{suffix}");
        var board = "board-" + suffix;

        // Non-vacuity: two identifiers, one NFC form, and it is the victim's.
        Assert.NotEqual(victim.AgentId, impostor.AgentId);
        Assert.Equal(victim.AgentId, impostor.AgentId.Normalize(NormalizationForm.FormC));

        using (var enrolled = await victim.EnrollAsync(client, ct))
            Assert.Equal(HttpStatusCode.Created, enrolled.StatusCode);
        await forum.EnrollPastTheRouteAsync(impostor.AgentId, impostor.Kid, Convert.FromBase64String(impostor.PublicKeyBase64), ct);

        // The impostor holds a token, so the refusal below is VERIFY's and not the token endpoint's.
        var dpop = DpopClient.For(impostor, impostor.AssertionKey);
        var token = await dpop.GetTokenAsync(client, TokenEndpoint, forum.Now, ct);

        var wire = impostor.Sign(
            PostKind.Question, board, "Whose post is this?", "Signed author " + suffix, parent: null, forum.Now, wireInNfc: false);
        Assert.Contains(impostor.AgentId, Encoding.UTF8.GetString(wire), StringComparison.Ordinal);

        using var posted = await dpop.PostAsync(client, PostsUrl, token, wire, forum.Now, ct);
        var type = JsonNode.Parse(await posted.Content.ReadAsStringAsync(ct))?["type"]?.GetValue<string>();

        Assert.Equal(
            "401 curia/content/author-principal-mismatch; posts on the board 0",
            $"{(int)posted.StatusCode} {type}; posts on the board {await PostsOnBoardAsync(board, ct)}");
    }

    private async Task<long> PostsOnBoardAsync(string board, CancellationToken ct)
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM events WHERE event_type = 'post.accepted' AND payload->>'board' = @board;", admin);
        command.Parameters.AddWithValue("board", board);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }
}
