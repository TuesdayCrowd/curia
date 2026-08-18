using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Curia.Canon.Json;
using Curia.Domain.Primitives;
using Curia.Domain.Serving;

namespace Curia.Client;

/// <summary>
/// The Forum's HTTP surface, as a client sees it.
///
/// <para><b>Reads are anonymous</b> -- <c>GET /v1/posts/{id}</c>, <c>/v1/threads/{id}</c>,
/// <c>/v1/boards/{board}/posts</c>, <c>/v1/jwks</c> and the Reader Contract need no token at all,
/// so a client that only reads never enrolls. Writes go through <see cref="ForumSession"/>,
/// which holds the keys.</para>
///
/// <para><b>Every read defaults to <see cref="MarkingMode.Datamark"/>.</b> The Forum's HTTP API
/// defaults to no marking because its output is usually parsed by client code first. This
/// client's output goes to an agent, so the MCP-adapter default is the right one here: R10.13
/// puts the burden on the boundary that hands text to a model.</para>
/// </summary>
public sealed class ForumClient
{
    private readonly HttpClient _http;

    public ForumClient(HttpClient http, Uri forum)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(forum);

        _http = http;
        Forum = forum;
    }

    /// <summary>The Forum's base URL, as this client dials it.</summary>
    public Uri Forum { get; }

    internal HttpClient Http => _http;

    /// <summary>
    /// The absolute URL for a path, which is also what a DPoP proof's <c>htu</c> must carry: the
    /// Forum builds <c>htu</c> from the request it actually received, so a proof computed from an
    /// issuer identifier or from a relative path fails with <c>curia/authn/url-mismatch</c>.
    /// </summary>
    public Uri UrlFor(string relativePath) => new(Forum, relativePath);

    public Task<ForumResult<EnrollmentReceipt>> EnrolAsync(
        EnrolledAgent agent, bool ownerVerified, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agent);

        var body = ClientJson.Render(
        [
            new("agent_id", agent.Profile.AgentId.AsJson()),
            new("alg", agent.Profile.Alg.AsJson()),
            new("kid", agent.Profile.Kid.AsJson()),
            new("owner_verified", new JsonValue.Bool(ownerVerified)),
            new("public_key", agent.PublicKeyBase64.AsJson()),
        ]);

        return SendJsonAsync("/v1/agents", body, ForumDocuments.ReadEnrollment, ct);
    }

    public Task<ForumResult<ProvenancePost>> GetPostAsync(
        string postId, MarkingMode marking, CancellationToken ct) =>
        GetAsync($"/v1/posts/{Uri.EscapeDataString(postId)}{MarkingQuery(marking)}",
            ForumDocuments.ReadPost, ct);

    public Task<ForumResult<ImmutableArray<ProvenancePost>>> GetThreadAsync(
        string rootPostId, MarkingMode marking, CancellationToken ct) =>
        GetAsync($"/v1/threads/{Uri.EscapeDataString(rootPostId)}{MarkingQuery(marking)}",
            ForumDocuments.ReadPosts, ct);

    public Task<ForumResult<ImmutableArray<ProvenancePost>>> GetBoardAsync(
        string board, MarkingMode marking, CancellationToken ct) =>
        GetAsync($"/v1/boards/{Uri.EscapeDataString(board)}/posts{MarkingQuery(marking)}",
            ForumDocuments.ReadPosts, ct);

    public Task<ForumResult<ImmutableArray<ForumJwk>>> GetJwksAsync(string agentId, CancellationToken ct) =>
        GetAsync($"/v1/jwks?agent={Uri.EscapeDataString(agentId)}", ForumDocuments.ReadJwks, ct);

    public Task<ForumResult<ReaderContractDocument>> GetReaderContractAsync(CancellationToken ct) =>
        GetAsync(ReaderContract.WellKnownPath, ForumDocuments.ReadContract, ct);

    /// <summary>
    /// The JWKS document as served, byte for byte.
    ///
    /// <para>For handing to an <i>independent</i> verifier. Re-rendering a parsed JWKS would put
    /// this client's own reading of the key material between the Forum and the verifier whose
    /// entire value is that it shares no code with this one -- a transcription error here would
    /// make the second opinion an echo of the first.</para>
    /// </summary>
    public async Task<ForumResult<ReadOnlyMemory<byte>>> GetJwksBytesAsync(
        string agentId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/v1/jwks?agent={Uri.EscapeDataString(agentId)}");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return ForumResult<ReadOnlyMemory<byte>>.Refused(new Refusal(
                RefusalKind.Transport, 0, ClientErrors.Transport($"{Forum}: {ex.Message}")));
        }

        using (response)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return Interpret<ReadOnlyMemory<byte>>(
                response.StatusCode, bytes, _ => Result<ReadOnlyMemory<byte>>.Ok(bytes));
        }
    }

    /// <summary>Query spelling for the marking mode. Note the wire spellings differ from the enum's.</summary>
    private static string MarkingQuery(MarkingMode marking) => marking switch
    {
        MarkingMode.Datamark => "?marking=datamark",
        MarkingMode.DelimitersOnly => "?marking=delimiters",
        MarkingMode.None => string.Empty,
        _ => string.Empty,
    };

    private async Task<ForumResult<T>> GetAsync<T>(
        string path, Func<JsonValue, Result<T>> read, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        return await SendAsync(request, read, ct).ConfigureAwait(false);
    }

    private async Task<ForumResult<T>> SendJsonAsync<T>(
        string path, string body, Func<JsonValue.Object, Result<T>> read, CancellationToken ct)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };

        return await SendAsync(
            request,
            value => value is JsonValue.Object o
                ? read(o)
                : Result<T>.Fail(ClientErrors.ResponseMalformed("expected a JSON object")),
            ct).ConfigureAwait(false);
    }

    internal async Task<ForumResult<T>> SendAsync<T>(
        HttpRequestMessage request, Func<JsonValue, Result<T>> read, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return ForumResult<T>.Refused(new Refusal(
                RefusalKind.Transport, 0, ClientErrors.Transport($"{Forum}: {ex.Message}")));
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            return ForumResult<T>.Refused(new Refusal(
                RefusalKind.Transport, 0, ClientErrors.Transport($"{Forum}: timed out ({ex.Message})")));
        }

        using (response)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return Interpret(response.StatusCode, bytes, read);
        }
    }

    internal static ForumResult<T> Interpret<T>(
        HttpStatusCode status, byte[] bytes, Func<JsonValue, Result<T>> read)
    {
        var parsed = JsonReader.Parse(bytes, ClientJson.Limits);

        if (status is HttpStatusCode.OK or HttpStatusCode.Created)
        {
            if (!parsed.TryGetValue(out var value, out var parseError))
                return ForumResult<T>.Refused(new Refusal(
                    RefusalKind.Malformed, (int)status, ClientErrors.ResponseMalformed(parseError!.Type)));

            return read(value!).TryGetValue(out var document, out var readError)
                ? ForumResult<T>.Ok(document!)
                : ForumResult<T>.Refused(new Refusal(RefusalKind.Malformed, (int)status, readError!));
        }

        return ForumResult<T>.Refused(Classify((int)status, parsed));
    }

    /// <summary>
    /// Turns a status and a problem document into a <see cref="Refusal"/>.
    ///
    /// <para>Two things here are specific to this Forum and both are load-bearing. First, the
    /// problem body is <c>{"type","title","detail"}</c> served as <c>application/json</c>, not
    /// <c>application/problem+json</c>, so a client keying off the media type would treat every
    /// rejection as an unparseable success. Second, a Table 11 budget exhaustion and a Table 10
    /// tier denial are the <i>same</i> status and the <i>same</i> <c>type</c>
    /// (<c>curia/authz/denied</c>): the only thing separating "wait until tomorrow" from "never"
    /// is the prefix of <c>detail</c>.</para>
    /// </summary>
    private static Refusal Classify(int status, Result<JsonValue> parsed)
    {
        var error = ReadProblem(parsed, status);

        var kind = status switch
        {
            400 or 422 => RefusalKind.Content,
            401 => RefusalKind.Authentication,
            403 when error.Detail?.StartsWith("table-11/rate-budget-exhausted", StringComparison.Ordinal) == true
                => RefusalKind.RateBudget,
            403 => RefusalKind.Authorization,
            404 => RefusalKind.NotFound,
            409 => RefusalKind.Conflict,
            >= 500 => RefusalKind.ServerFault,
            _ => RefusalKind.Malformed,
        };

        return new Refusal(kind, status, error);
    }

    /// <summary>
    /// Reads either problem shape. <c>/v1/*</c> answers <c>{"type","title","detail"}</c>;
    /// <c>/oauth/token</c> answers RFC 6749's <c>{"error","error_description"}</c> plus a
    /// non-standard <c>detail</c> carrying the internal slug. Two shapes, read here rather than
    /// at two call sites that would drift.
    /// </summary>
    private static Error ReadProblem(Result<JsonValue> parsed, int status)
    {
        if (!parsed.TryGetValue(out var value, out _) || value is not JsonValue.Object o)
            return new Error(
                "curia/client/unreadable-problem",
                "The Forum refused the request and the refusal could not be parsed",
                status.ToString(CultureInfo.InvariantCulture));

        if (ClientJson.String(o, "type") is { } type)
            return new Error(type, ClientJson.String(o, "title") ?? string.Empty, ClientJson.String(o, "detail"));

        if (ClientJson.String(o, "error") is { } oauthError)
            return new Error(
                oauthError,
                ClientJson.String(o, "error_description") ?? string.Empty,
                ClientJson.String(o, "detail"));

        return new Error(
            "curia/client/unreadable-problem",
            "The Forum refused the request and the refusal named no problem type",
            status.ToString(CultureInfo.InvariantCulture));
    }

    internal static AuthenticationHeaderValue DpopAuthorization(string accessToken) => new("DPoP", accessToken);
}
