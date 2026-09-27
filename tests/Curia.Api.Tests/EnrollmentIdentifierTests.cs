using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Curia.Tests.Shared;
using Npgsql;
using NSec.Cryptography;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// What an anonymous enrollment may write, at the surface an agent uses (errata G15). An agent's
/// identifier is also the aggregate its credential events are appended under; its identifier and
/// <c>kid</c> are carried into a public leaf; and its key is the one every verifier of its posts
/// will use. The route now checks each of them before anything is written: the identifiers' text
/// and length, the algorithm, and the key's bytes. Until these refusals it wrote whatever it was
/// sent. An identifier naming a post registered a key the log could then never record, and answered
/// 500. A noncharacter wrote a leaf the reference client refuses to read. A U+0000, which Postgres
/// <c>text</c> cannot hold, an identifier too long for the store's index, an algorithm the Forum
/// does not verify, and a missing key each answered 500, which tells an agent to retry what can
/// never succeed. And bytes that were not a key of their algorithm were registered, for good: junk
/// the key set could not render, or a P-384 key published as P-256.
///
/// <para>Each refusal is asserted by status, slug and detail together, and beside it that nothing
/// naming the request was written: no key row and no event. So a regression's first red line says
/// what the Forum answered and what it wrote. Hostile text travels as a six-character JSON escape,
/// never as the code point itself.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class EnrollmentIdentifierTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    /// <summary>U+FFFE as JSON source text: backslash, <c>u</c>, <c>FFFE</c>.</summary>
    private const string NoncharacterEscape = "\\u" + "FFFE";

    /// <summary>U+0000 as JSON source text: backslash, <c>u</c>, <c>0000</c>.</summary>
    private const string NulEscape = "\\u" + "0000";

    /// <summary><c>e</c> and U+0301 as JSON source text, which NFC composes into one character.</summary>
    private const string DecomposedEscape = "e\\u" + "0301";

    /// <summary>
    /// <c>POST /v1/agents</c> with each field spliced into the body as JSON source text, so an escape
    /// reaches the Forum's binder exactly as written. A null <paramref name="alg"/> leaves the member out.
    /// </summary>
    private static Task<string> EnrollRawAsync(
        HttpClient client, string agentId, string kid, string? alg, string publicKey, CancellationToken ct) =>
        EnrollWithKeySourceAsync(client, agentId, kid, alg, Literal(publicKey), ct);

    /// <summary>
    /// The same, with <c>public_key</c> given as JSON source text: <c>null</c> sends JSON null, and a
    /// null <paramref name="publicKeySource"/> leaves the member out.
    /// </summary>
    private static async Task<string> EnrollWithKeySourceAsync(
        HttpClient client, string agentId, string kid, string? alg, string? publicKeySource, CancellationToken ct)
    {
        var body = "{\"agent_id\":" + Literal(agentId)
            + ",\"kid\":" + Literal(kid)
            + (alg is null ? "" : ",\"alg\":" + Literal(alg))
            + (publicKeySource is null ? "" : ",\"public_key\":" + publicKeySource) + "}";
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(new Uri("/v1/agents", UriKind.Relative), content, ct);
        return await AnswerAsync(response, ct);
    }

    /// <summary><paramref name="source"/> as a JSON string literal, spliced verbatim.</summary>
    private static string Literal(string source) => "\"" + source + "\"";

    /// <summary>
    /// The status, then a problem's <c>type</c> and <c>detail</c>; <c>enrolled</c> for a success; or
    /// the first line of a body that is not JSON, such as the exception a 500 serves in Development.
    /// </summary>
    private static async Task<string> AnswerAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode) return $"{status} enrolled";

        try
        {
            var problem = JsonNode.Parse(body);
            return $"{status} {problem?["type"]?.GetValue<string>()} {problem?["detail"]?.GetValue<string>()}";
        }
        catch (JsonException)
        {
            var line = body.Split('\n')[0];
            return $"{status} {line[..Math.Min(line.Length, 160)]}";
        }
    }

    /// <summary>
    /// Key rows and events that name <paramref name="marker"/> anywhere, read as the provisioning role.
    /// A marker is a clean, unique fragment of the request, so the count holds even for a field Postgres
    /// could not have been handed, and catches a row whose text also carries the hostile character.
    /// </summary>
    private async Task<string> WrittenAsync(string marker, CancellationToken ct)
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(ct);

        await using var keys = new NpgsqlCommand(
            "SELECT count(*) FROM agent_keys WHERE kid LIKE @marker OR agent_id LIKE @marker;", admin);
        keys.Parameters.AddWithValue("marker", "%" + marker + "%");
        var keyRows = (long)(await keys.ExecuteScalarAsync(ct))!;

        await using var events = new NpgsqlCommand(
            "SELECT count(*) FROM events WHERE aggregate_id LIKE @marker OR payload::text LIKE @marker;", admin);
        events.Parameters.AddWithValue("marker", "%" + marker + "%");
        var eventRows = (long)(await events.ExecuteScalarAsync(ct))!;

        return $"key rows {keyRows}, events {eventRows}";
    }

    private async Task<long> KeyRowsForKidAsync(string kid, CancellationToken ct)
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM agent_keys WHERE kid = @kid;", admin);
        command.Parameters.AddWithValue("kid", kid);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    private async Task<long> EventsUnderAsync(string aggregateId, CancellationToken ct)
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM events WHERE aggregate_id = @aggregate;", admin);
        command.Parameters.AddWithValue("aggregate", aggregateId);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>An enrolled agent's question, so a post's identifier exists to be named.</summary>
    private async Task<string> AskedQuestionAsync(HttpClient client, string suffix, CancellationToken ct)
    {
        var asker = ForumAgent.Create($"https://agents.example/asker-{suffix}", $"asker-{suffix}");
        var (dpop, token) = await asker.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);

        using var asked = await dpop.PostAsync(
            client, PostsUrl, token,
            asker.SignQuestion("board-" + suffix, "Whose stream does a post's identifier name?", "Asker " + suffix, forum.Now),
            forum.Now, ct);
        var body = await asked.Content.ReadAsStringAsync(ct);
        Assert.True(asked.StatusCode == HttpStatusCode.Created, "the question was refused, so this test proves nothing: " + body);

        return JsonNode.Parse(body)!["post_id"]!.GetValue<string>();
    }

    /// <summary>
    /// R4.33 (errata G15), the stream clause, as the review probed it: an enrollment under an existing
    /// post's identifier. Refused 409 by name, with no key row for its <c>kid</c>, and the post's
    /// aggregate still holding its one event. Before the refusal the store registered the key, the
    /// log could not append an enrollment to a stream that already held the post, and the request
    /// answered 500 <c>contended</c>, every time it was sent. A re-send now gets the same 409.
    /// </summary>
    [Fact]
    public async Task R4_33_AnEnrollmentNamingAPostIsRefusedAndRegistersNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var suffix = Suffix();
        var postId = await AskedQuestionAsync(client, suffix, ct);
        var kid = "post-named-" + suffix;
        var impostor = ForumAgent.Create(postId, kid);

        using var first = await impostor.EnrollAsync(client, ct);
        var answer = await AnswerAsync(first, ct);
        var keyRows = await KeyRowsForKidAsync(kid, ct);
        var postEvents = await EventsUnderAsync(postId, ct);

        // One line holding the answer and what was written, printed whole when it differs: the
        // served detail is long, and a truncated diff would hide the key row a regression leaves.
        var observed = $"{answer}; key rows {keyRows}; events under the post {postEvents}";
        Assert.True(
            observed == $"409 curia/enroll/identifier-reserved agent={postId}: nothing was registered. The event log keeps this identifier for its own records; an agent needs an identifier of its own.; key rows 0; events under the post 1",
            "observed: " + observed);

        using var again = await impostor.EnrollAsync(client, ct);
        Assert.Equal(answer, await AnswerAsync(again, ct));
    }

    /// <summary>
    /// R6.15's condition, held by a writer for a string it carries into a leaf ADMIT never saw. U+FFFE
    /// in <c>agent_id</c> or in <c>kid</c> is refused 400 under ADMIT's own slug, naming the field and
    /// never echoing the value, before anything is written. It answered 201, and wrote a public leaf
    /// the reference client refuses to read.
    /// </summary>
    [Theory]
    [InlineData("agent_id")]
    [InlineData("kid")]
    public async Task R6_15_AnEnrollmentHoldingANoncharacterIsRefusedBeforeAnythingIsWritten(string field)
    {
        var ct = TestContext.Current.CancellationToken;
        var suffix = Suffix();
        var agentId = $"https://agents.example/nonchar-{suffix}" + (field == "agent_id" ? NoncharacterEscape : "");
        var kid = $"nonchar-{suffix}" + (field == "kid" ? NoncharacterEscape : "");

        var answer = await EnrollRawAsync(forum.Client, agentId, kid, "ES256", ForumAgent.Create(agentId, kid).PublicKeyBase64, ct);

        Assert.Equal(
            $"400 curia/admit/noncharacter field={field}; key rows 0, events 0",
            $"{answer}; {await WrittenAsync(suffix, ct)}");
    }

    /// <summary>
    /// R4.36 (errata G16): an agent identifier NFC would change is refused 400 by name, naming the
    /// field and never echoing the value, before anything is written. A signed envelope names its
    /// author in NFC (R6.9), so such an identifier could never author a post, and every signature
    /// naming it names another identifier, which another identity can hold. It answered 201. A
    /// <c>kid</c> is never canonicalized, and one outside NFC is still enrolled.
    /// </summary>
    [Theory]
    [InlineData("agent_id", "400 curia/enroll/identifier-not-nfc field=agent_id: nothing was registered. A signed envelope names its author in NFC (R6.9), so an identifier NFC would change could never author a post (R4.36).; key rows 0, events 0")]
    [InlineData("kid", "201 enrolled; key rows 1, events 2")]
    public async Task R4_36_AnAgentIdentifierNfcWouldChangeIsRefusedBeforeAnythingIsWritten(string field, string expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var suffix = Suffix();
        var agentId = $"https://agents.example/nfd-{suffix}" + (field == "agent_id" ? DecomposedEscape : "");
        var kid = $"nfd-{suffix}" + (field == "kid" ? DecomposedEscape : "");

        var answer = await EnrollRawAsync(forum.Client, agentId, kid, "ES256", ForumAgent.Create(agentId, kid).PublicKeyBase64, ct);

        Assert.Equal(expected, $"{answer}; {await WrittenAsync(suffix, ct)}");
    }

    /// <summary>
    /// U+0000 in <c>agent_id</c> or <c>kid</c>, written as the JSON escape ADMIT accepts
    /// (c4/vector-09), so <c>CheckString</c> accepts it too. Postgres <c>text</c> cannot hold it, so
    /// the route refuses it by name, 400, naming the field, before the event log is read or the key
    /// store asked. It answered 500 with the database's own refusal (22021).
    /// </summary>
    [Theory]
    [InlineData("agent_id")]
    [InlineData("kid")]
    public async Task AnEnrollmentHoldingUPlus0000IsRefusedByNameBeforeAnythingIsWritten(string field)
    {
        var ct = TestContext.Current.CancellationToken;
        var suffix = Suffix();
        var agentId = $"https://agents.example/nul-{suffix}" + (field == "agent_id" ? NulEscape + "x" : "");
        var kid = $"nul-{suffix}" + (field == "kid" ? NulEscape + "x" : "");

        var answer = await EnrollRawAsync(forum.Client, agentId, kid, "ES256", ForumAgent.Create(agentId, kid).PublicKeyBase64, ct);

        Assert.Equal(
            $"400 curia/enroll/nul-character field={field}; key rows 0, events 0",
            $"{answer}; {await WrittenAsync(suffix, ct)}");
    }

    /// <summary>
    /// R4.15: an agent key is EdDSA or ES256, the allow-list the Forum verifies signatures with. A
    /// missing algorithm, an empty one, one the Forum does not verify, and a correct name in the wrong
    /// case are each refused 400 by name, before anything is written. Each answered 500: a null
    /// parameter, or the database's CHECK.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("RS256")]
    [InlineData("HS256")]
    [InlineData("es256")]
    public async Task R4_15_AnEnrollmentWithoutAnAlgorithmTheForumVerifiesIsRefusedByName(string? alg)
    {
        var ct = TestContext.Current.CancellationToken;
        var suffix = Suffix();
        var agentId = $"https://agents.example/alg-{suffix}";
        var kid = $"alg-{suffix}";

        var answer = await EnrollRawAsync(forum.Client, agentId, kid, alg, ForumAgent.Create(agentId, kid).PublicKeyBase64, ct);

        var shown = string.IsNullOrEmpty(alg) ? "(none)" : alg;
        Assert.Equal(
            $"400 curia/enroll/unsupported-algorithm alg={shown}: an agent key is ES256 or EdDSA (R4.15); key rows 0, events 0",
            $"{answer}; {await WrittenAsync(suffix, ct)}");
    }

    /// <summary>
    /// <c>/v1/jwks?agent=</c> for an identifier holding U+0000. No stored key can name one, since
    /// Postgres <c>text</c> cannot hold it, so the answer is the one an agent with no keys gets: 404
    /// <c>unknown-agent</c>, echoing the identifier. Handed to Postgres, it answered an anonymous
    /// caller 500. The unknown agent's own answer is the control, so two 500s cannot agree.
    /// </summary>
    [Fact]
    public async Task AKeySetAskedForAnIdentifierHoldingUPlus0000IsAnsweredAsAnUnknownAgentIs()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var suffix = Suffix();

        using var unknown = await client.GetAsync(new Uri($"/v1/jwks?agent=nowhere-{suffix}", UriKind.Relative), ct);
        using var nul = await client.GetAsync(new Uri($"/v1/jwks?agent=nul%00{suffix}", UriKind.Relative), ct);

        Assert.Equal($"404 curia/keys/unknown-agent nowhere-{suffix}", await AnswerAsync(unknown, ct));
        Assert.Equal($"404 curia/keys/unknown-agent nul\0{suffix}", await AnswerAsync(nul, ct));
    }

    /// <summary>
    /// R4.15 against R4.28's stored forms: a key is registered only as a key of its algorithm. For
    /// <c>ES256</c>, the DER SubjectPublicKeyInfo of a key on the curve named P-256, with nothing after
    /// it; for <c>EdDSA</c>, the raw 32-byte key. Every other material is refused 400 by name, never
    /// echoing the bytes, before anything is written. The rows are the ones the verifiers are asked
    /// about (<see cref="KeyMaterials"/>). Each answered 201, and the row it left could make its key
    /// set and its token requests answer 500 for good, or, for a P-384 key, be issued a token.
    /// </summary>
    [Theory]
    [InlineData("ES256", "empty")]
    [InlineData("ES256", "three-zero-bytes")]
    [InlineData("ES256", "32-raw-bytes")]
    [InlineData("ES256", "rsa-2048-spki")]
    [InlineData("ES256", "p384-spki")]
    [InlineData("ES256", "p256-spki-and-a-trailing-byte")]
    [InlineData("ES256", "brainpoolP256r1-spki")]
    [InlineData("EdDSA", "p256-spki")]
    [InlineData("EdDSA", "31-bytes")]
    [InlineData("EdDSA", "33-bytes")]
    public async Task R4_15_AnEnrollmentWhoseKeyIsNotAKeyOfItsAlgorithmIsRefusedBeforeAnythingIsWritten(string alg, string material)
    {
        var ct = TestContext.Current.CancellationToken;
        var suffix = Suffix();
        var publicKey = Convert.ToBase64String(KeyMaterials.Build(material, Encoding.UTF8.GetBytes("unused")).Material);

        var answer = await EnrollRawAsync(forum.Client, $"https://agents.example/key-{suffix}", $"key-{suffix}", alg, publicKey, ct);

        var form = alg == "ES256"
            ? "alg=ES256: public_key is not an ES256 key, which is the base64 of a P-256 key's DER SubjectPublicKeyInfo with nothing after it (R4.15, R4.28)"
            : "alg=EdDSA: public_key is not an EdDSA key, which is the base64 of the raw 32-byte Ed25519 public key (R4.15, R4.28)";
        Assert.Equal(
            $"400 curia/enroll/invalid-key {form}; key rows 0, events 0",
            $"{answer}; {await WrittenAsync(suffix, ct)}");
    }

    /// <summary>
    /// R4.28's EdDSA form, end to end, and the positive control for the theory above: an honest
    /// Ed25519 key, sent as the base64 of its raw 32 bytes, is enrolled, and the key set publishes it
    /// as RFC 8037's octet key pair, <c>x</c> being those same 32 bytes. It is the one honest EdDSA
    /// enrollment in the suite; a check that refused every EdDSA key would pass the theory.
    /// </summary>
    [Fact]
    public async Task R4_28_AnEd25519KeyIsRegisteredAndPublishedAsTheOctetKeyPairItIs()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var suffix = Suffix();
        var agentId = $"https://agents.example/ed25519-{suffix}";
        var kid = $"ed25519-{suffix}";
        using var key = Key.Create(SignatureAlgorithm.Ed25519);
        var raw = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        var answer = await EnrollRawAsync(client, agentId, kid, "EdDSA", Convert.ToBase64String(raw), ct);

        using var served = await client.GetAsync(new Uri($"/v1/jwks?agent={Uri.EscapeDataString(agentId)}", UriKind.Relative), ct);
        var body = await served.Content.ReadAsStringAsync(ct);
        var published = served.StatusCode == HttpStatusCode.OK
            ? string.Join(
                " | ",
                JsonNode.Parse(body)!["keys"]!.AsArray().Select(jwk =>
                    $"{jwk!["kty"]} {jwk["crv"]} {jwk["alg"]} {jwk["kid"]} x={jwk["x"]}"))
            : $"{(int)served.StatusCode} {body[..Math.Min(body.Length, 160)]}";

        Assert.Equal(
            $"201 enrolled; OKP Ed25519 EdDSA {kid} x={Base64Url.EncodeToString(raw)}",
            $"{answer}; {published}");
    }

    /// <summary>
    /// A <c>public_key</c> left out, or sent as JSON null, is refused 400 by name before anything is
    /// written. Both answered 500: the route decoded a null string.
    /// </summary>
    [Theory]
    [InlineData("omitted")]
    [InlineData("null")]
    public async Task AnEnrollmentWithoutAPublicKeyIsRefusedByName(string publicKey)
    {
        var ct = TestContext.Current.CancellationToken;
        var suffix = Suffix();

        var answer = await EnrollWithKeySourceAsync(
            forum.Client, $"https://agents.example/keyless-{suffix}", $"keyless-{suffix}", "ES256",
            publicKey == "null" ? "null" : null, ct);

        Assert.Equal(
            "400 curia/enroll/invalid-key public_key is missing; key rows 0, events 0",
            $"{answer}; {await WrittenAsync(suffix, ct)}");
    }

    /// <summary>
    /// <c>agent_id</c> and <c>kid</c> are each at most 1,024 UTF-8 bytes, which keeps them well under
    /// the 2,704-byte index row Postgres can store, and keeps a key set's URL fetchable. Both sides of
    /// the bound are rows, as R6.39 asks of its own caps. The multi-byte row is at most 1,024 UTF-16
    /// code units and more than 1,024 bytes, so a cap that counted code units would admit it; its byte
    /// count is computed by hand, never by the encoder under test. Past about 2,684 bytes the route
    /// answered 500 (Postgres 54000).
    /// </summary>
    [Theory]
    [InlineData("agent_id", 1024, 0, "201")]
    [InlineData("agent_id", 1025, 0, "400")]
    [InlineData("agent_id", 2685, 0, "400")]
    [InlineData("agent_id", 40, 600, "400")]
    [InlineData("kid", 1024, 0, "201")]
    [InlineData("kid", 1025, 0, "400")]
    public async Task AnIdentifierLongerThanTheForumStoresIsRefusedByName(string field, int asciiBytes, int multiByteChars, string expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var suffix = Suffix();

        // ASCII padded to asciiBytes, then multiByteChars of U+00E9, which UTF-8 spells in two bytes.
        // The padding is random hex, not one repeated letter: Postgres compresses an index entry it
        // can, and a compressible identifier fits under the index limit that an ordinary one exceeds.
        string Long(string prefix) =>
            (prefix + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(asciiBytes)))[..asciiBytes]
            + new string((char)0xE9, multiByteChars);
        var agentId = field == "agent_id" ? Long($"https://agents.example/long-{suffix}-") : $"https://agents.example/long-{suffix}";
        var kid = field == "kid" ? Long($"long-{suffix}-") : $"long-{suffix}";
        var bytes = asciiBytes + (2 * multiByteChars);

        var answer = await EnrollRawAsync(forum.Client, agentId, kid, "ES256", ForumAgent.Create(agentId, kid).PublicKeyBase64, ct);

        Assert.Equal(
            expected == "201"
                ? "201 enrolled; key rows 1, events 2"
                : $"400 curia/enroll/identifier-too-long field={field} bytes={bytes}: at most 1024 UTF-8 bytes; key rows 0, events 0",
            $"{answer}; {await WrittenAsync(suffix, ct)}");
    }
}
