using System.Globalization;
using System.Net;
using Curia.Canon.Json;
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
    public async Task<ForumResult<PostReceipt>> SubmitAsync(ReadOnlyMemory<byte> wire, CancellationToken ct)
    {
        var tokenResult = await AccessTokenAsync(ct).ConfigureAwait(false);
        if (!tokenResult.TryGetValue(out var token, out var tokenRefusal))
            return ForumResult<PostReceipt>.Refused(tokenRefusal);

        var url = _client.UrlFor("/v1/posts");
        var cached = _store.ReadToken(_agent.Profile.Slug);

        var first = await PostOnceAsync(wire, token, url, cached?.Nonce, ct).ConfigureAwait(false);

        if (first.Nonce is { } challenge)
        {
            // Cache before retrying: even if this retry fails for some other reason, the next
            // command should not have to spend a round-trip rediscovering the same nonce.
            RememberNonce(challenge);
            var retry = await PostOnceAsync(wire, token, url, challenge, ct).ConfigureAwait(false);
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
    private async Task<(ForumResult<PostReceipt> Result, string? Nonce)> PostOnceAsync(
        ReadOnlyMemory<byte> wire, string token, Uri url, string? nonce, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();

        using var content = new ReadOnlyMemoryContent(wire);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/posts") { Content = content };
        request.Headers.Authorization = ForumClient.DpopAuthorization(token);
        request.Headers.Add("DPoP", _signer.Proof("POST", url, now, token, nonce));

        HttpResponseMessage response;
        try
        {
            response = await _client.Http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            return (ForumResult<PostReceipt>.Refused(new Refusal(
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
                    ? ForumDocuments.ReadReceipt(o)
                    : Result<PostReceipt>.Fail(ClientErrors.ResponseMalformed("expected a JSON object")));

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
