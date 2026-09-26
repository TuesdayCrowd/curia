using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// What an anonymous enrollment may write, at the surface an agent uses (errata G15). An agent's
/// identifier is also the aggregate its credential events are appended under, and its identifier and
/// <c>kid</c> are carried into a public leaf. Before these refusals the route wrote whatever it was
/// sent. An identifier naming a post registered a key the log could then never record, and answered
/// 500. A noncharacter wrote a leaf the reference client refuses to read. And a U+0000, which
/// Postgres <c>text</c> cannot hold, or an algorithm the Forum does not verify, reached the database
/// and answered 500, which tells an agent to retry what can never succeed.
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

    /// <summary>
    /// <c>POST /v1/agents</c> with each field spliced into the body as JSON source text, so an escape
    /// reaches the Forum's binder exactly as written. A null <paramref name="alg"/> leaves the member out.
    /// </summary>
    private static async Task<string> EnrollRawAsync(
        HttpClient client, string agentId, string kid, string? alg, string publicKey, CancellationToken ct)
    {
        static string Literal(string source) => "\"" + source + "\"";

        var body = "{\"agent_id\":" + Literal(agentId)
            + ",\"kid\":" + Literal(kid)
            + (alg is null ? "" : ",\"alg\":" + Literal(alg))
            + ",\"public_key\":" + Literal(publicKey) + "}";
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(new Uri("/v1/agents", UriKind.Relative), content, ct);
        return await AnswerAsync(response, ct);
    }

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
}
