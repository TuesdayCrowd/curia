using System.Net;
using System.Text;
using System.Text.Json;
using Curia.Tests.Shared;

namespace Curia.Client.Tests;

/// <summary>
/// The Forum as <c>curia enrol</c> meets it, for the enrollment probes (errata G79, R11.107): it serves <c>GET /v1/enroll/nonce</c> (a new value each call) and answers <c>POST /v1/agents</c> by
/// <b>verifying the proof it receives</b> with <see cref="EnrollProofVerifier"/>, which the test project wrote from the errata, so a client that signed the wrong URL, the wrong key or no nonce is refused
/// by name and a double that accepted any body cannot pass it. A refusal can be scripted ahead of the verification (a refusal of the moment, a <c>409</c>, a <c>503</c>), and a scripted
/// <c>proof-nonce-stale</c> ends every nonce served so far, so a retry that did not fetch another is refused again.
/// </summary>
internal sealed class EnrolForum
{
    private readonly Queue<(int Status, string Type)> _script = new();
    private readonly HashSet<string> _current = new(StringComparer.Ordinal);
    private int _issued;

    internal EnrolForum()
    {
        Verifier = new EnrollProofVerifier("http://forum.test/v1/agents", () => DateTimeOffset.UtcNow, _current.Contains);
    }

    internal EnrollProofVerifier Verifier { get; }

    /// <summary>A Forum that predates the proof: <c>GET /v1/enroll/nonce</c> is a <c>404</c>.</summary>
    internal bool NoNonceRoute { get; init; }

    internal int NonceFetches { get; private set; }

    /// <summary>The proofs the Forum received, accepted or not, in order.</summary>
    internal List<string?> Proofs { get; } = [];

    /// <summary>Answers the next <c>POST /v1/agents</c> as written, before any verification.</summary>
    internal EnrolForum Refuse(int status, string type)
    {
        _script.Enqueue((status, type));
        return this;
    }

    internal static string JtiOf(string proof)
    {
        var payload = proof.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        return JsonDocument.Parse(Convert.FromBase64String(payload)).RootElement.GetProperty("jti").GetString()!;
    }

    internal HttpResponseMessage Handle(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Get && path == "/v1/enroll/nonce")
        {
            if (NoNonceRoute) return ScriptedForum.Problem(404, "curia/request/no-route", "No route reads this request");
            NonceFetches++;
            var nonce = "epp-nonce-" + (++_issued);
            _current.Add(nonce);
            return ScriptedForum.Json(200, $$"""{"nonce":"{{nonce}}","expires_at":"2099-01-01T00:00:00.0000000+00:00"}""");
        }

        if (request.Method == HttpMethod.Post && path == "/v1/agents")
        {
            var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement;
            var proof = body.TryGetProperty("proof", out var p) ? p.GetString() : null;
            Proofs.Add(proof);

            if (_script.TryDequeue(out var scripted))
            {
                if (scripted.Type.EndsWith("proof-nonce-stale", StringComparison.Ordinal)) _current.Clear();
                return ScriptedForum.Problem(scripted.Status, scripted.Type);
            }

            var slug = Verifier.Verify(
                body.GetProperty("agent_id").GetString()!,
                body.GetProperty("alg").GetString()!,
                body.GetProperty("kid").GetString()!,
                Convert.FromBase64String(body.GetProperty("public_key").GetString()!),
                proof);
            if (slug is not null) return ScriptedForum.Problem(400, "curia/enroll/" + slug);

            return ScriptedForum.Json(201,
                $$"""{"agent_id":"{{body.GetProperty("agent_id").GetString()}}","kid":"{{body.GetProperty("kid").GetString()}}","enrolled_at":"2026-01-01T00:00:00.0000000+00:00","owner_verified":false}""");
        }

        return ScriptedForum.Problem(404, "curia/posts/not-found", "No such post");
    }
}
