using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Curia.Canon.Json;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;

namespace Curia.Client;

/// <summary>
/// An authenticated agent's write surface: obtains a DPoP-bound access token, keeps it for its
/// 300 seconds, and posts signed submissions with a fresh proof each time.
///
/// <para><b>The nonce challenge is the normal flow.</b> RFC 9449 §8 makes <c>401</c> +
/// <c>DPoP-Nonce</c> + <c>WWW-Authenticate: DPoP error="use_dpop_nonce"</c> the way a server tells
/// a client which nonce to use. This session caches the last nonce so the common case is one
/// round-trip, and still retries on the challenge, because a cached nonce goes stale on the
/// Forum's own rotation schedule and a client that could not recover would fail intermittently
/// for reasons its user could never reproduce.</para>
///
/// <para><b>The retry mints a new proof, never resends the old one.</b> <c>jti</c> is burned in a
/// replay cache on first sight, so a resent proof is refused as a replay -- which looks exactly
/// like a nonce that did not take.</para>
/// </summary>
public sealed class ForumSession
{
    /// <summary>
    /// R5.x: access tokens last 300 seconds. Declared here so the client's own refresh logic and
    /// its documentation cannot disagree about the number.
    /// </summary>
    public const int AccessTokenLifetimeSeconds = 300;

    private readonly ForumClient _client;
    private readonly EnrolledAgent _agent;
    private readonly ProfileStore _store;
    private readonly TimeProvider _clock;
    private readonly DpopSigner _signer;

    public ForumSession(ForumClient client, EnrolledAgent agent, ProfileStore store, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);

        _client = client;
        _agent = agent;
        _store = store;
        _clock = clock;
        _signer = new DpopSigner(agent);
    }

    public Uri TokenEndpoint => _client.UrlFor("/oauth/token");

    /// <summary>
    /// A usable access token: the cached one while it lasts, a freshly minted one otherwise.
    /// </summary>
    public async Task<ForumResult<string>> AccessTokenAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var cached = _store.ReadToken(_agent.Profile.Slug);
        if (cached is not null && cached.IsUsableAt(now)) return ForumResult<string>.Ok(cached.AccessToken);

        var minted = await MintAsync(now, ct).ConfigureAwait(false);
        if (!minted.TryGetValue(out var token, out var refusal)) return ForumResult<string>.Refused(refusal);

        _store.WriteToken(
            _agent.Profile.Slug,
            new CachedToken(token, now.AddSeconds(AccessTokenLifetimeSeconds), cached?.Nonce));

        return ForumResult<string>.Ok(token);
    }

    private async Task<ForumResult<string>> MintAsync(DateTimeOffset now, CancellationToken ct)
    {
        var endpoint = TokenEndpoint;

        // grant_type is sent for RFC hygiene; this Forum's token endpoint does not read it. Scope
        // is sent explicitly rather than left blank so the token records what was asked for --
        // the Forum neither validates nor enforces scope today, and authorization is decided from
        // live tier state, so a scope string is a statement of intent and never a grant.
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = _agent.Profile.AgentId,
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            ["client_assertion"] = _signer.ClientAssertion(endpoint, now),
            ["scope"] = "question:create answer:create",
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/token") { Content = form };
        request.Headers.Add("DPoP", _signer.Proof("POST", endpoint, now));

        return await _client.SendAsync(request, ReadAccessToken, ct).ConfigureAwait(false);
    }

    private static Result<string> ReadAccessToken(JsonValue value) =>
        value is JsonValue.Object o && ClientJson.String(o, "access_token") is { } token
            ? Result<string>.Ok(token)
            : Result<string>.Fail(ClientErrors.TokenRefused("the token response carried no access_token"));

    /// <summary>
    /// Posts an already-signed submission. Takes the wire bytes rather than an envelope: the
    /// bytes are what the signature covers, and a session that could rebuild them would be a
    /// session that could change them between signing and sending.
    /// </summary>
    public Task<ForumResult<PostReceipt>> SubmitAsync(ReadOnlyMemory<byte> wire, CancellationToken ct) =>
        WriteAsync("/v1/posts", wire, ForumDocuments.ReadReceipt, ct);

    /// <summary>
    /// R10.35: raises a typed flag against a post. Named <c>FlagAsync</c> rather than
    /// <c>RaiseFlagAsync</c> because the analyzer reads a <c>Raise</c> prefix as an event invoker.
    ///
    /// <para><b>Both refusals a client can make itself are made here</b>, before a round trip. An
    /// unknown kind and a missing rationale are things <c>FlagKinds.Parse</c> and R10.35 already
    /// settle locally, and the Forum would refuse them identically -- so spending a request to be
    /// told is a request wasted, and the local refusal names the same condition slug the Forum
    /// would have.</para>
    ///
    /// <para>The flag is attributed by the DPoP-bound token, not signed: R10.37 requires a signed
    /// entry for a <i>moderation action</i>, and R10.35 requires no such thing of a flag. So there
    /// is no envelope here and nothing to canonicalize — which is also why this does not go through
    /// <c>SubmissionBuilder</c>.</para>
    /// </summary>
    public Task<ForumResult<FlagReceipt>> FlagAsync(
        string postId, string kind, string rationale, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(postId);

        if (!FlagKinds.Parse(kind).TryGetValue(out var parsed, out var kindError))
            return Task.FromResult(ForumResult<FlagReceipt>.Refused(
                new Refusal(RefusalKind.Local, 0, kindError!)));

        if (string.IsNullOrWhiteSpace(rationale))
            return Task.FromResult(ForumResult<FlagReceipt>.Refused(
                new Refusal(RefusalKind.Local, 0, ModerationErrors.RationaleRequired())));

        var body = JsonSerializer.SerializeToUtf8Bytes(new FlagBody(FlagKinds.Wire(parsed), rationale));

        // Percent-encoded, for the reason the JWKS route had to move off a path segment: an
        // identifier interpolated raw into a URL is one identifier-format change away from being a
        // path traversal, and Table 9 already types one identifier in this system as a URI.
        return WriteAsync(
            $"/v1/posts/{Uri.EscapeDataString(postId)}/flags", body, ForumDocuments.ReadFlagReceipt, ct);
    }

    /// <summary>
    /// One DPoP-bound write, including RFC 9449 §8's nonce exchange.
    ///
    /// <para>Shared by every write path rather than copied per endpoint. The nonce dance is the
    /// part a client either gets right once or fails intermittently on forever, and two copies of
    /// it is two chances to get it right — which is one more than this is worth.</para>
    /// </summary>
    private async Task<ForumResult<T>> WriteAsync<T>(
        string path,
        ReadOnlyMemory<byte> body,
        Func<JsonValue.Object, Result<T>> read,
        CancellationToken ct)
    {
        var tokenResult = await AccessTokenAsync(ct).ConfigureAwait(false);
        if (!tokenResult.TryGetValue(out var token, out var tokenRefusal))
            return ForumResult<T>.Refused(tokenRefusal);

        var url = _client.UrlFor(path);
        var cached = _store.ReadToken(_agent.Profile.Slug);

        var first = await PostOnceAsync(path, body, token, url, cached?.Nonce, read, ct).ConfigureAwait(false);

        if (first.Nonce is { } challenge)
        {
            // Cache before retrying: even if this retry fails for some other reason, the next
            // command should not have to spend a round-trip rediscovering the same nonce.
            RememberNonce(challenge);
            var retry = await PostOnceAsync(path, body, token, url, challenge, read, ct).ConfigureAwait(false);
            return retry.Result;
        }

        return first.Result;
    }

    private void RememberNonce(string nonce)
    {
        var cached = _store.ReadToken(_agent.Profile.Slug);
        if (cached is null) return;

        _store.WriteToken(_agent.Profile.Slug, cached with { Nonce = nonce });
    }

    /// <summary>
    /// One attempt. Returns the outcome, plus the nonce the Forum challenged with when it did --
    /// the challenge is not a failure to report, it is an instruction to retry.
    /// </summary>
    private async Task<(ForumResult<T> Result, string? Nonce)> PostOnceAsync<T>(
        string path,
        ReadOnlyMemory<byte> wire,
        string token,
        Uri url,
        string? nonce,
        Func<JsonValue.Object, Result<T>> read,
        CancellationToken ct)
    {
        var now = _clock.GetUtcNow();

        using var content = new ReadOnlyMemoryContent(wire);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Headers.Authorization = ForumClient.DpopAuthorization(token);
        request.Headers.Add("DPoP", _signer.Proof("POST", url, now, token, nonce));

        HttpResponseMessage response;
        try
        {
            response = await _client.Http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return (ForumResult<T>.Refused(new Refusal(
                RefusalKind.Transport, 0, ClientErrors.Transport($"{url}: {ex.Message}"))), null);
        }

        using (response)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

            var challenge = response.StatusCode == HttpStatusCode.Unauthorized
                && response.Headers.TryGetValues("DPoP-Nonce", out var values)
                    ? values.FirstOrDefault()
                    : null;

            var result = ForumClient.Interpret(
                response.StatusCode,
                bytes,
                v => v is JsonValue.Object o
                    ? read(o)
                    : Result<T>.Fail(ClientErrors.ResponseMalformed("expected a JSON object")));

            return (result, challenge);
        }
    }

    /// <summary>How long the cached token has left, for <c>whoami</c>.</summary>
    public string TokenStatus()
    {
        var cached = _store.ReadToken(_agent.Profile.Slug);
        if (cached is null) return "none cached";

        var remaining = cached.ExpiresAt - _clock.GetUtcNow();
        return remaining > TimeSpan.Zero
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"valid for {(int)remaining.TotalSeconds}s")
            : "expired";
    }
}

/// <summary>The flag request body, as R10.35's route takes it.</summary>
internal sealed record FlagBody(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("rationale")] string Rationale);
