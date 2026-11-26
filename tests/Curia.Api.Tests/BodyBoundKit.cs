using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Curia.Tests.Shared;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// What the probes of errata G84 (R6.68 (rev.), D125) share: the figure, which is R6.39's number parsed from the white paper plus one and never read from <c>AdmitLimits</c> (the restatement the
/// vacuity test's fourth question names), the shaped bodies, a body sent with no declared length (<c>HttpClient</c>'s default is a declared one: trap 304), and a credential for the routes that
/// authenticate before they read.
/// </summary>
internal static class BodyBoundKit
{
    /// <summary>R6.68's figure, 1,048,577: a body of this many bytes is read and decided as before.</summary>
    internal static long Cap => PublishedRequestBound.Cap();

    /// <summary>One byte past <see cref="Cap"/>: the smallest body R6.68 refuses.</summary>
    internal static long OverCap => Cap + 1;

    internal static string TooLargeType => PublishedRequestBound.TooLargeType();

    /// <summary>The routes that read a body and are bound by the global figure alone (the plan's table; the two key acts have their own 32,768 bytes and are held by BND7).</summary>
    internal static readonly string[] GlobalReadingRoutes = ["agents", "posts", "batch", "flags", "token"];

    internal static string PathOf(string route) => route switch
    {
        "posts" => "/v1/posts",
        "agents" => "/v1/agents",
        "batch" => "/v1/posts/batch",
        "flags" => "/v1/posts/zz/flags",
        "accept" => "/v1/posts/zz/accept",
        "token" => "/oauth/token",
        _ => throw new ArgumentOutOfRangeException(nameof(route), route, "not a route of the plan's table"),
    };

    internal static string ContentTypeOf(string route) => route == "token" ? "application/x-www-form-urlencoded" : "application/json";

    /// <summary>A body of exactly <paramref name="size"/> bytes shaped as the route reads it, so that a body under the bound is read by the route's own code and refused for its own reason.</summary>
    internal static byte[] Body(string route, long size)
    {
        var n = checked((int)size);
        var (head, tail) = route switch
        {
            "agents" => ("{\"agent_id\":\"", "\"}"),
            "batch" => ("{\"digests\":[\"", "\"]}"),
            "flags" => ("{\"kind\":\"spam\",\"rationale\":\"", "\"}"),
            "token" => ("grant_type=client_credentials&client_id=", string.Empty),
            _ => ("{\"a\":\"", "\"}"),
        };
        var h = Encoding.ASCII.GetBytes(head);
        var t = Encoding.ASCII.GetBytes(tail);
        var body = new byte[n];
        h.CopyTo(body, 0);
        Array.Fill(body, (byte)'a', h.Length, n - h.Length - t.Length);
        t.CopyTo(body, n - t.Length);
        return body;
    }

    /// <summary>A body sent with no declared length: the framing an undeclared body takes, which a <c>ByteArrayContent</c> never does.</summary>
    internal sealed class ChunkedContent : HttpContent
    {
        private readonly byte[] _data;

        internal ChunkedContent(byte[] data, string? contentType = null)
        {
            _data = data;
            if (contentType is not null) Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            for (var at = 0; at < _data.Length; at += 65_536)
                await stream.WriteAsync(_data.AsMemory(at, Math.Min(65_536, _data.Length - at))).ConfigureAwait(false);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    internal static HttpContent Content(byte[] body, string contentType, bool chunked)
    {
        if (chunked) return new ChunkedContent(body, contentType);
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return content;
    }

    /// <summary>A route's pattern as a path a request can name: every route parameter given a value.</summary>
    internal static string PathOfPattern(string pattern) =>
        System.Text.RegularExpressions.Regex.Replace(pattern, @"\{[^}]+\}", "zz", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    internal sealed record Credential(DpopClient Dpop, string Token, string? Nonce, string AgentId);

    /// <summary>Enrols a fresh agent, takes a DPoP-bound token for it and the Forum's current nonce (the first answer to an unnonced proof carries it).</summary>
    internal static async Task<Credential> SignInAsync(ForumFixture forum, HttpClient http, CancellationToken ct)
    {
        var origin = forum.PublicOrigin;
        var agent = ForumAgent.Create("https://agents.example/bnd-" + Guid.NewGuid().ToString("N")[..8], "k-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await agent.AuthenticateAsync(http, origin + "/oauth/token", forum.Now, ct);
        using var probe = new HttpRequestMessage(HttpMethod.Post, origin + "/v1/posts") { Content = new ByteArrayContent([1]) };
        probe.Headers.Authorization = new AuthenticationHeaderValue("DPoP", token);
        probe.Headers.Add("DPoP", dpop.Proof("POST", origin + "/v1/posts", forum.Now, token, null));
        using var answer = await http.SendAsync(probe, ct);
        var nonce = answer.Headers.TryGetValues("DPoP-Nonce", out var values) ? values.First() : null;
        return new Credential(dpop, token, nonce, agent.AgentId);
    }

    /// <summary>The credential's headers for a request to <paramref name="target"/> (the URL the Forum builds, not the one a client may escape differently).</summary>
    internal static void Authorize(HttpRequestMessage request, Credential credential, ForumFixture forum, string method, string target)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("DPoP", credential.Token);
        request.Headers.Add("DPoP", credential.Dpop.Proof(method, target, forum.Now, credential.Token, credential.Nonce));
    }

    /// <summary>The request line and headers of a raw POST, ending in the blank line; the credential's two values are the caller's, so that a probe can look for them in what the host answers.</summary>
    internal static string RawHead(string path, string framing, string authority, string? contentType, string? token, string? proof)
    {
        var head = new StringBuilder($"POST {path} HTTP/1.1\r\nHost: {authority}\r\n{framing}\r\nConnection: close\r\n");
        if (contentType is not null) head.Append("Content-Type: ").Append(contentType).Append("\r\n");
        if (token is not null) head.Append("Authorization: DPoP ").Append(token).Append("\r\nDPoP: ").Append(proof).Append("\r\n");
        return head.Append("\r\n").ToString();
    }

    internal static List<(string Method, string Path)> EveryRoute(ForumFixture forum) =>
        [.. Fuzz.SurfaceInventory.Routes(forum).Select(r => (r.Method, PathOfPattern(r.Pattern)))];

    /// <summary>The members of a JSON object body, or the text when it is not one.</summary>
    internal static string[]? MembersOf(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object
                ? [.. document.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal)]
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string? MemberText(string text, string name)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.TryGetProperty(name, out var member) ? member.ToString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string Describe(int status, string text) =>
        string.Create(CultureInfo.InvariantCulture, $"{status} {(text.Length > 160 ? text[..160] + "..." : text)}");
}
