using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Curia.Tests.Shared;
using Npgsql;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R4.15 and R4.28 for key rows written before the enrollment route checked a key's bytes. Before
/// that check, the route registered whatever material it was sent under <c>ES256</c> or
/// <c>EdDSA</c>. The rows cannot be removed or repaired: R4.19 forbids deleting a key, and R4.32
/// forbids changing its material. Such a row made its agent's key set and its token requests
/// answer 500 for good. A P-384 key under <c>ES256</c> did worse: it was issued a token, and its key
/// set published it as <c>crv: "P-256"</c> with 48-byte coordinates, a shape the independent
/// verifier refuses.
///
/// <para>Each row is made as those enrollments left it: an honest enrollment, whose
/// <c>agent.enrolled</c> the log keeps, and then, as the provisioning role, the key row replaced
/// under the same <c>kid</c>, agent and <c>valid_from</c> by the material under test. The key set
/// must omit what it cannot publish, and the token endpoint must answer such a key as it answers
/// any signature that does not verify.</para>
///
/// <para><b>Since errata G16 the log carries an enrollment's key (R4.34)</b>, and a replaced row is
/// not the key it carries, so for an identity enrolled since then the row is refused before any
/// signature is checked (R4.35), which <see cref="R4_35_ARowReplacedUnderAKeyTheLogBindsMintsNoToken"/>
/// holds. The rows R4.15's and R4.28's rules still decide are those of identities enrolled before
/// G16, whose log binds the <c>kid</c> alone; the token theory's two replaced rows and the key set's
/// three are made under such identities (<see cref="ForumFixture.EnrollBeforeKeyBindingAsync"/>), so
/// the verifier's rule is still what refuses the first and the renderer's what omits the second.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class StoredKeyFormTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    /// <summary>What the token endpoint answers an assertion that does not verify under the named agent's own key.</summary>
    private const string SignatureDoesNotVerify =
        "{\"error\":\"invalid_client\",\"error_description\":\"Signature does not verify\",\"detail\":\"curia/authn/signature-invalid\"}";

    private static readonly byte[] Message = Encoding.UTF8.GetBytes("stored key form");

    /// <summary>What the token endpoint answers a stored key the log does not bind to the agent named (R4.35).</summary>
    private const string NotBoundByTheLog =
        "{\"error\":\"invalid_client\",\"error_description\":\"The event log binds no such key to that agent\",\"detail\":\"curia/keys/not-bound-by-the-log\"}";

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>An identity enrolled as before errata G16: the log binds its <c>kid</c> alone, so a row replaced under it still reaches the verifier.</summary>
    private async Task<ForumAgent> EnrolledBeforeKeyBindingAsync(string name, CancellationToken ct)
    {
        var agent = ForumAgent.Create($"https://agents.example/{name}", name);
        await forum.EnrollBeforeKeyBindingAsync(agent.AgentId, agent.Kid, agent.AssertionKey.ExportSubjectPublicKeyInfo(), ct);
        return agent;
    }

    private async Task<ForumAgent> EnrolledAsync(string name, CancellationToken ct)
    {
        var agent = ForumAgent.Create($"https://agents.example/{name}", name);
        using var enrolled = await agent.EnrollAsync(forum.Client, ct);
        Assert.True(enrolled.StatusCode == HttpStatusCode.Created, $"{name} was not enrolled, so this test proves nothing: {(int)enrolled.StatusCode}");
        return agent;
    }

    /// <summary>
    /// The row for <paramref name="kid"/> replaced, as the provisioning role, by <paramref name="material"/>
    /// under <paramref name="alg"/>: same <c>kid</c>, agent and <c>valid_from</c>.
    /// </summary>
    private async Task ReplaceAsync(string kid, string alg, byte[] material, CancellationToken ct)
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(ct);
        await using var replace = new NpgsqlCommand(
            "WITH gone AS (DELETE FROM agent_keys WHERE kid = @kid RETURNING kid, agent_id, valid_from) " +
            "INSERT INTO agent_keys (kid, agent_id, alg, public_key, valid_from, valid_until) " +
            "SELECT kid, agent_id, @alg, @material, valid_from, NULL FROM gone;",
            admin);
        replace.Parameters.AddWithValue("kid", kid);
        replace.Parameters.AddWithValue("alg", alg);
        replace.Parameters.AddWithValue("material", material);
        Assert.Equal(1, await replace.ExecuteNonQueryAsync(ct));
    }

    /// <summary>
    /// The served key set as one line: the status, then for a 200 the kids served and each key's
    /// coordinate lengths in bytes (<c>-</c> for a key with no <c>y</c>); otherwise the body's first line.
    /// </summary>
    private async Task<string> KeySetAsync(string agentId, CancellationToken ct)
    {
        using var response = await forum.Client.GetAsync(new Uri($"/v1/jwks?agent={Uri.EscapeDataString(agentId)}", UriKind.Relative), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            var line = body.Split('\n')[0];
            return $"{(int)response.StatusCode} {line[..Math.Min(line.Length, 160)]}";
        }

        var keys = JsonNode.Parse(body)!["keys"]!.AsArray();
        static string Length(JsonNode? coordinate) =>
            coordinate is null ? "-" : Base64Url.DecodeFromChars(coordinate.GetValue<string>()).Length.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return $"200 kids=[{string.Join(",", keys.Select(k => k!["kid"]!.GetValue<string>()))}]"
            + $" x={string.Join(",", keys.Select(k => Length(k!["x"])))}"
            + $" y={string.Join(",", keys.Select(k => Length(k!["y"])))}";
    }

    /// <summary>
    /// R4.28: the key set publishes a stored key only in its algorithm's form. Since errata G16 the
    /// rows that reach the renderer are those of identities enrolled before it, which the log binds
    /// by <c>kid</c> alone, whatever the stored bytes (R4.35); a row the log does not bind is refused
    /// before it is rendered. So each row under test is such an identity's own, replaced as the
    /// provisioning role: 32 raw bytes and a P-384 key under <c>ES256</c>, and a P-256 key under
    /// <c>EdDSA</c>. Each key set is empty, not 404, because the store holds a row; before R4.28 each
    /// answered 500. An honest identity enrolled the same way is the control: its key is served, on
    /// P-256's 32-byte coordinates.
    /// </summary>
    [Fact]
    public async Task R4_28_AKeySetServesOnlyTheStoredKeysItCanPublish()
    {
        var ct = TestContext.Current.CancellationToken;
        var suffix = Suffix();
        var honest = await EnrolledBeforeKeyBindingAsync($"stored-honest-{suffix}", ct);
        var raw = await EnrolledBeforeKeyBindingAsync($"stored-raw-{suffix}", ct);
        var p384 = await EnrolledBeforeKeyBindingAsync($"stored-p384-{suffix}", ct);
        var spkiAsEddsa = await EnrolledBeforeKeyBindingAsync($"stored-spki-as-eddsa-{suffix}", ct);

        await ReplaceAsync(raw.Kid, "ES256", KeyMaterials.Build("32-raw-bytes", Message).Material, ct);
        await ReplaceAsync(p384.Kid, "ES256", KeyMaterials.Build("p384-spki", Message).Material, ct);
        await ReplaceAsync(spkiAsEddsa.Kid, "EdDSA", KeyMaterials.Build("p256-spki", Message).Material, ct);

        Assert.Equal(
            $"honest: 200 kids=[{honest.Kid}] x=32 y=32; raw: 200 kids=[] x= y=; p384: 200 kids=[] x= y=; spki-as-eddsa: 200 kids=[] x= y=",
            $"honest: {await KeySetAsync(honest.AgentId, ct)}; raw: {await KeySetAsync(raw.AgentId, ct)}; "
            + $"p384: {await KeySetAsync(p384.AgentId, ct)}; spki-as-eddsa: {await KeySetAsync(spkiAsEddsa.AgentId, ct)}");
    }

    /// <summary>
    /// R4.35 (errata G16), the second kind of row G16 found: another holder's valid P-256 key under the
    /// <c>kid</c> an identity enrolled since G16 bound, as a lost row's recovery registered it before
    /// R4.31 (revised). The log carries the identity's own key, and this is not it, so its holder mints
    /// no token as the identity, a question signed under it in the identity's name is refused, and the
    /// key set does not publish it, which only a comparison of the key's bytes, not its <c>kid</c>, can
    /// tell. The damage is asserted first, then each refusal by name.
    /// </summary>
    [Fact]
    public async Task R4_35_AnotherHoldersKeyUnderABoundKidSignsNothingMintsNothingAndIsNotPublished()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var suffix = Suffix();
        var victim = ForumAgent.Create($"https://agents.example/stored-victim-{suffix}", $"stored-victim-{suffix}");
        var (dpop, token) = await victim.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);

        var impostor = ForumAgent.Create(victim.AgentId, victim.Kid);
        await ReplaceAsync(victim.Kid, "ES256", impostor.AssertionKey.ExportSubjectPublicKeyInfo(), ct);

        var (tokenStatus, tokenBody) = await DpopClient.For(impostor, impostor.AssertionKey)
            .RequestTokenAsync(client, TokenEndpoint, forum.Now, victim.AgentId, ct);
        using var forged = await dpop.PostAsync(
            client, PostsUrl, token,
            impostor.SignQuestion("board-" + suffix, "Signed under another holder's key.", "Impostor " + suffix, forum.Now),
            forum.Now, ct);
        var forgedBody = await forged.Content.ReadAsStringAsync(ct);
        var keySet = await KeySetAsync(victim.AgentId, ct);

        Assert.True(
            tokenStatus != HttpStatusCode.OK && forged.StatusCode != HttpStatusCode.Created && keySet == "200 kids=[] x= y=",
            $"another holder's key under {victim.Kid} acted as {victim.AgentId}: token request {(int)tokenStatus}, question {(int)forged.StatusCode} {forgedBody}, key set {keySet}");
        Assert.Equal($"401 {NotBoundByTheLog}", $"{(int)tokenStatus} {tokenBody}");
        Assert.Equal(
            "401 curia/keys/not-bound-by-the-log",
            $"{(int)forged.StatusCode} {JsonNode.Parse(forgedBody)!["type"]!.GetValue<string>()}");
    }

    /// <summary>
    /// R4.15 at the token endpoint, for a stored key that is not a key of its algorithm. Each row names
    /// an agent enrolled under its own identifier, with its own <c>kid</c>, and must be answered
    /// byte for byte as the control is: an honest agent's own <c>kid</c> under an assertion signed by
    /// a stranger's key, which is a signature that does not verify.
    /// <list type="bullet">
    /// <item><c>es256-32-raw-bytes</c>: the row holds 32 raw bytes, under an identity enrolled before
    /// errata G16. It answered 500.</item>
    /// <item><c>es256-p384-spki</c>: the row holds a P-384 key, under an identity enrolled before errata
    /// G16, and the assertion is genuinely signed by it under <c>ES256</c>. It was issued a token.</item>
    /// <item><c>eddsa-header-over-an-es256-key</c>: an honest agent's row, and an assertion whose
    /// header says <c>EdDSA</c> over 64 zero bytes. Anyone could send it, naming any agent. It
    /// answered 500.</item>
    /// </list>
    /// </summary>
    [Theory]
    [InlineData("es256-32-raw-bytes")]
    [InlineData("es256-p384-spki")]
    [InlineData("eddsa-header-over-an-es256-key")]
    public async Task R4_15_AStoredKeyThatIsNotAKeyOfItsAlgorithmMintsNoTokenAndIsAnsweredAsABadSignatureIs(string row)
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var suffix = Suffix();

        var honest = await EnrolledAsync($"stored-control-{suffix}", ct);
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (controlStatus, controlBody) = await DpopClient.For(honest, stranger)
            .RequestTokenAsync(client, TokenEndpoint, forum.Now, honest.AgentId, ct);
        Assert.Equal($"401 {SignatureDoesNotVerify}", $"{(int)controlStatus} {controlBody}");

        var agent = row is "es256-32-raw-bytes" or "es256-p384-spki"
            ? await EnrolledBeforeKeyBindingAsync($"stored-{row}-{suffix}", ct)
            : await EnrolledAsync($"stored-{row}-{suffix}", ct);
        HttpStatusCode status;
        string body;
        switch (row)
        {
            case "es256-32-raw-bytes":
                await ReplaceAsync(agent.Kid, "ES256", KeyMaterials.Build("32-raw-bytes", Message).Material, ct);
                (status, body) = await DpopClient.For(agent, agent.AssertionKey)
                    .RequestTokenAsync(client, TokenEndpoint, forum.Now, agent.AgentId, ct);
                break;

            case "es256-p384-spki":
            {
                using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
                await ReplaceAsync(agent.Kid, "ES256", p384.ExportSubjectPublicKeyInfo(), ct);
                (status, body) = await DpopClient.For(agent, p384)
                    .RequestTokenAsync(client, TokenEndpoint, forum.Now, agent.AgentId, ct);
                break;
            }

            case "eddsa-header-over-an-es256-key":
            {
                var header = new JsonObject { ["alg"] = "EdDSA", ["kid"] = agent.Kid, ["typ"] = "JWT" };
                var claims = new JsonObject
                {
                    ["iss"] = agent.AgentId,
                    ["sub"] = agent.AgentId,
                    ["aud"] = TokenEndpoint,
                    ["iat"] = forum.Now.ToUnixTimeSeconds(),
                    ["exp"] = forum.Now.AddSeconds(60).ToUnixTimeSeconds(),
                    ["jti"] = Guid.NewGuid().ToString("N"),
                };
                var assertion =
                    Base64Url.EncodeToString(Encoding.UTF8.GetBytes(header.ToJsonString())) + "." +
                    Base64Url.EncodeToString(Encoding.UTF8.GetBytes(claims.ToJsonString())) + "." +
                    Base64Url.EncodeToString(new byte[64]);
                (status, body) = await DpopClient.For(agent, agent.AssertionKey)
                    .RequestTokenAsync(client, TokenEndpoint, forum.Now, agent.AgentId, assertion, ct);
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(row), row, "no such row");
        }

        var line = body.Split('\n')[0];
        Assert.Equal($"{(int)controlStatus} {controlBody}", $"{(int)status} {line[..Math.Min(line.Length, 200)]}");
    }

    /// <summary>
    /// R4.35 (errata G16): the same replaced rows under an identity enrolled since G16, whose log entry
    /// carries its key (R4.34). The row is not the key the log bound, so the token endpoint refuses it
    /// before any signature is checked, and says why. Before R4.35 the P-384 row was issued a token and
    /// the raw-bytes row answered as a bad signature.
    /// </summary>
    [Theory]
    [InlineData("32-raw-bytes")]
    [InlineData("p384-spki")]
    public async Task R4_35_ARowReplacedUnderAKeyTheLogBindsMintsNoToken(string material)
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var agent = await EnrolledAsync($"stored-bound-{material}-{Suffix()}", ct);

        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var replaced = material == "p384-spki" ? p384.ExportSubjectPublicKeyInfo() : KeyMaterials.Build(material, Message).Material;
        await ReplaceAsync(agent.Kid, "ES256", replaced, ct);

        var (status, body) = await DpopClient.For(agent, material == "p384-spki" ? p384 : agent.AssertionKey)
            .RequestTokenAsync(client, TokenEndpoint, forum.Now, agent.AgentId, ct);

        Assert.Equal($"401 {NotBoundByTheLog}", $"{(int)status} {body}");
    }
}
