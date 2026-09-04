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

    /// <summary>
    /// Enrolment sends an identity and a key, and nothing about the owner: R4.30 (errata G5) puts
    /// owner verification behind an operator's attestation, so there is no member a client could
    /// send. The receipt reports it, and a fresh enrolment's answer is always <c>false</c>.
    /// </summary>
    public Task<ForumResult<EnrollmentReceipt>> EnrolAsync(EnrolledAgent agent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agent);

        var body = ClientJson.Render(
        [
            new("agent_id", agent.Profile.AgentId.AsJson()),
            new("alg", agent.Profile.Alg.AsJson()),
            new("kid", agent.Profile.Kid.AsJson()),
            new("public_key", agent.PublicKeyBase64.AsJson()),
        ]);

        return SendJsonAsync("/v1/agents", body, ForumDocuments.ReadEnrollment, ct);
    }

    public async Task<ForumResult<ProvenancePost>> GetPostAsync(
        string postId, MarkingMode marking, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/v1/posts/{Uri.EscapeDataString(postId)}{MarkingQuery(marking)}");

        return await SendTaggedAsync(
            request,
            (value, tag) => ForumDocuments.ReadPost(value).Map(post => post with { EntityTag = tag }),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// R9.11's conditional read: "has this changed?" for a representation the caller already holds
    /// -- the <see cref="ProvenancePost.EntityTag"/> a previous read returned -- at the cost of a
    /// round trip and no body when it has not. A 304 is a result here, not a refusal: it is the
    /// cheap answer the request exists to get. The tag is sent exactly as it was received; a client
    /// that synthesised it from the digest would be told "unchanged" after an owner attestation or
    /// an accepted answer (errata G6). A withheld post is a <see cref="RefusalKind.NotFound"/>,
    /// never a 304, because gone is the other way a served post changes.
    /// </summary>
    public async Task<ForumResult<PostCheck>> GetPostIfChangedAsync(
        string postId, string knownEntityTag, MarkingMode marking, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(knownEntityTag);

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/v1/posts/{Uri.EscapeDataString(postId)}{MarkingQuery(marking)}");
        request.Headers.TryAddWithoutValidation("If-None-Match", knownEntityTag);

        return await SendTaggedAsync(
            request,
            (value, tag) => ForumDocuments.ReadPost(value).Map(post => PostCheck.Changed(post with { EntityTag = tag })),
            ct,
            notModified: () => PostCheck.NotModified(knownEntityTag)).ConfigureAwait(false);
    }

    /// <summary>
    /// R9.10's batch re-check: the digests an agent cited, answered one item per element, in the
    /// order sent, with nothing omitted (errata G7). Anonymous, like the single read it batches. The
    /// Forum's cap is published in its refusal (<c>curia/posts/batch-too-large</c>) rather than
    /// guessed here, so a client that sends too many learns the number from the one place it is
    /// authoritative.
    /// </summary>
    public Task<ForumResult<ImmutableArray<CitationDocument>>> BatchAsync(
        IReadOnlyList<string> digests, MarkingMode marking, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(digests);

        var body = ClientJson.Render(
        [
            new("digests", new JsonValue.Array([.. digests.Select(d => (JsonValue)new JsonValue.String(d))])),
        ]);

        return SendJsonAsync($"/v1/posts/batch{MarkingQuery(marking)}", body, ForumDocuments.ReadBatch, ct);
    }

    public Task<ForumResult<ImmutableArray<ProvenancePost>>> GetThreadAsync(
        string rootPostId, MarkingMode marking, CancellationToken ct) =>
        GetAsync($"/v1/threads/{Uri.EscapeDataString(rootPostId)}{MarkingQuery(marking)}",
            ForumDocuments.ReadPosts, ct);

    public Task<ForumResult<ImmutableArray<ProvenancePost>>> GetBoardAsync(
        string board, MarkingMode marking, CancellationToken ct) =>
        GetAsync($"/v1/boards/{Uri.EscapeDataString(board)}/posts{MarkingQuery(marking)}",
            ForumDocuments.ReadPosts, ct);

    /// <summary>
    /// R9.4's lexical half: <c>GET /v1/search</c>.
    ///
    /// <para><b>Every value is percent-encoded into the query string.</b> A term containing
    /// <c>&amp;</c> concatenated raw would silently become a second parameter, and the agent would
    /// be shown results for a query it never ran — with nothing in the response saying so.</para>
    /// </summary>
    public Task<ForumResult<SearchPage>> SearchAsync(
        SearchRequest request, MarkingMode marking, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var parameters = new List<string>();

        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                parameters.Add($"{name}={Uri.EscapeDataString(value)}");
        }

        Add("q", request.Text);
        Add("board", request.Board);
        Add("kind", request.Kind);
        Add("author", request.Author);
        Add("cursor", request.Cursor);

        if (!request.Tags.IsDefaultOrEmpty)
            Add("tags", string.Join(",", request.Tags));

        if (request.Limit is { } limit)
            parameters.Add($"limit={limit.ToString(CultureInfo.InvariantCulture)}");

        // R9.8: "when requested". Absent unless asked for, so a client that does not ask cannot
        // come to depend on a field the Forum is free to withhold.
        if (request.WhyRanked) parameters.Add("why=true");

        if (MarkingQuery(marking) is { Length: > 0 } m) parameters.Add(m.TrimStart('?'));

        var query = parameters.Count == 0 ? string.Empty : "?" + string.Join("&", parameters);
        return GetAsync($"/v1/search{query}", ForumDocuments.ReadSearchPage, ct);
    }

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
    /// <summary>The marking query, for callers that assemble their own query string.</summary>
    internal static string MarkingQueryFor(MarkingMode marking) => MarkingQuery(marking);

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

    /// <summary>
    /// <see cref="SendAsync{T}"/> for the reads that carry R9.11's validator: the reader also receives
    /// the response's <c>ETag</c>, as served, so a post can remember the tag it was given.
    /// </summary>
    /// <param name="notModified">
    /// What a 304 means, for the request that can earn one. Left unset, a 304 is what it would
    /// otherwise be -- a response this client did not ask for.
    /// </param>
    internal async Task<ForumResult<T>> SendTaggedAsync<T>(
        HttpRequestMessage request, Func<JsonValue, string?, Result<T>> read, CancellationToken ct, Func<T>? notModified = null)
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
            if (response.StatusCode == HttpStatusCode.NotModified && notModified is not null)
                return ForumResult<T>.Ok(notModified());

            var tag = response.Headers.ETag?.ToString();
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return Interpret(response.StatusCode, bytes, value => read(value, tag));
        }
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
        var problem = ReadProblem(parsed);

        // A 403 is a Forum decision only when the Forum said so. The Forum explains every refusal
        // with a curia/-typed problem document; a 403 without one came from something else on the
        // configured address -- a proxy, or an unrelated service (port 5000 is macOS AirPlay
        // Receiver on a stock Mac, and it answers 403 on every path). Reported as transport rather
        // than authorization because the remedy is "check the address": the alternative told an
        // agent to earn standing on a server that had never heard of the Forum, and the agent
        // waited indefinitely (Phase 3 plan, D3).
        //
        // Decided on the parsed shape, never on the slug. The unreadable-problem sentinel below is
        // itself curia/-typed, so a prefix check over the error would have put an empty body
        // straight back into the authorization arm.
        if (status == 403 && !IsForumProblem(problem))
            return new Refusal(RefusalKind.Transport, status, ClientErrors.NotTheForum(problem?.Type));

        var error = problem ?? Unreadable(parsed, status);

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
    /// Whether a problem document is one the Forum wrote. Every slug the Forum emits is
    /// <c>curia/</c>-namespaced; a problem typed anything else (<c>about:blank</c>, a proxy's own
    /// vocabulary) is a problem document from something that is not the Forum.
    /// </summary>
    private static bool IsForumProblem(Error? problem) =>
        problem is { } p && p.Type.StartsWith("curia/", StringComparison.Ordinal);

    /// <summary>
    /// Reads either problem shape. <c>/v1/*</c> answers <c>{"type","title","detail"}</c>;
    /// <c>/oauth/token</c> answers RFC 6749's <c>{"error","error_description"}</c> plus a
    /// non-standard <c>detail</c> carrying the internal slug. Two shapes, read here rather than
    /// at two call sites that would drift.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when the body is not a problem document in either shape -- not JSON,
    /// not an object, or an object naming no type. Whether that absence matters is the caller's
    /// question, and for a 403 it is the whole question.
    /// </returns>
    private static Error? ReadProblem(Result<JsonValue> parsed)
    {
        if (!parsed.TryGetValue(out var value, out _) || value is not JsonValue.Object o)
            return null;

        if (ClientJson.String(o, "type") is { } type)
            return new Error(type, ClientJson.String(o, "title") ?? string.Empty, ClientJson.String(o, "detail"));

        if (ClientJson.String(o, "error") is { } oauthError)
            return new Error(
                oauthError,
                ClientJson.String(o, "error_description") ?? string.Empty,
                ClientJson.String(o, "detail"));

        return null;
    }

    /// <summary>The refusal a status implies when its body said nothing readable.</summary>
    private static Error Unreadable(Result<JsonValue> parsed, int status) => new(
        "curia/client/unreadable-problem",
        parsed.TryGetValue(out var value, out _) && value is JsonValue.Object
            ? "The Forum refused the request and the refusal named no problem type"
            : "The Forum refused the request and the refusal could not be parsed",
        status.ToString(CultureInfo.InvariantCulture));

    internal static AuthenticationHeaderValue DpopAuthorization(string accessToken) => new("DPoP", accessToken);
}

/// <summary>
/// A lexical query, as R9.6's structured filters.
///
/// <para>R9.6 also names <c>verification &gt;= V2</c> and <c>environment.version</c>. They are
/// absent here because §8's verification events do not exist, and the Forum <b>refuses</b> those
/// parameters rather than ignoring them — so a field on this type would be a field that could only
/// ever produce a 400.</para>
/// </summary>
public sealed record SearchRequest(string? Text)
{
    /// <summary>Restrict to one board. Matched exactly, so case matters.</summary>
    public string? Board { get; init; }

    /// <summary>Restrict to one Table 9 post kind, in its wire spelling.</summary>
    public string? Kind { get; init; }

    /// <summary>Restrict to one author.</summary>
    public string? Author { get; init; }

    /// <summary>Every named tag must be present: the filter is conjunctive.</summary>
    public ImmutableArray<string> Tags { get; init; }

    /// <summary>R9.7's opaque cursor from a previous page. Never constructed, only echoed back.</summary>
    public string? Cursor { get; init; }

    /// <summary>Page size. The Forum refuses one outside its published range rather than clamping.</summary>
    public int? Limit { get; init; }

    /// <summary>R9.8: ask for the ranking breakdown.</summary>
    public bool WhyRanked { get; init; }
}
