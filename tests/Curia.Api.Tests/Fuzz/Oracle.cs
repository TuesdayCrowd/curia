using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Curia.Api.Tests.Fuzz;

/// <summary>
/// Spec §4.10's oracle, clauses 1–3, for one answer; clauses 4 and 5 are the run's (<see cref="FuzzRun"/>).
/// </summary>
internal static partial class Oracle
{
    /// <summary>Spec §4.10; Release; set on an Apple M3 Max (spec §2.1), which the failure message names.</summary>
    internal const int BudgetMilliseconds = 2_000;

    /// <summary>The prefix every budget failure's reason carries; the ledger may never hold one.</summary>
    internal const string OverBudget = "over budget";

    /// <summary>
    /// The refusals that say the request was stopped at its credential or its signature, so a
    /// re-signed variation answered with one reached nothing behind them (clause 5).
    /// </summary>
    internal static readonly string[] AuthenticationAndSignatureRefusals =
    [
        "curia/authn/signature-invalid",
        "curia/jws/signature-invalid",
        "curia/authn/missing-authorization",
        "curia/authn/missing-dpop-proof",
    ];

    /// <summary>
    /// Every ADMIT refusal: ADMIT is the first parser of every body it reads (R6.12), so a value it
    /// refused reached nothing behind it, whichever of its rules refused it (review of 6cbfa9f).
    /// </summary>
    internal const string AdmitPrefix = "curia/admit/";

    /// <summary>
    /// The other refusals that say a value was stopped by the first parser that read it, so it reached
    /// nothing behind that parser. Exact slugs: <c>curia/authn/malformed-jwk</c> is listed on purpose,
    /// since a key that does not parse was refused by the key's parser (review of 6cbfa9f).
    /// </summary>
    internal static readonly string[] ParserRefusals =
    [
        "curia/jws/malformed",
        "curia/authn/malformed",
        "curia/authn/malformed-jwk",
        "curia/request/unreadable",
    ];

    /// <summary>
    /// The token endpoint's refusal of its DPoP proof logged with no reason, which is today the missing
    /// proof alone: nothing was parsed, so no value got past the proof's parser. Since D29 closed the
    /// one proof validator refuses a proof's signature and its claims too, and since M5 closed no body
    /// says why: each of those refusals is classified by the slug it logged (event 5001), as
    /// <c>invalid_dpop_proof &lt;slug&gt;</c>, and never by this constant, which is compared by exact
    /// equality only.
    /// </summary>
    internal const string TokenEndpointProofRefusal = "invalid_dpop_proof";

    /// <summary>
    /// The token endpoint's refusal of a request it could not read as a token request (TokenEndpoint's
    /// form checks: not a form, an unreadable form, client_assertion_type, missing client_assertion or
    /// client_id). Logged with no reason, because nothing past the form reader was parsed. It is a parser
    /// refusal, which counts as reach (register D33, "the reach clause counts a parser refusal as
    /// reach"), as it did before M5. Compared by exact equality only.
    /// </summary>
    internal const string TokenEndpointFormRefusal = "invalid_request";

    /// <summary>
    /// The first <c>curia/&lt;area&gt;/&lt;name&gt;</c> slug in a problem type, or empty. The token
    /// endpoint's problem type is <c>error + " " + </c> the slug it logged, so the slug sits inside the
    /// string.
    /// </summary>
    internal static string Slug(string problemType)
    {
        ArgumentNullException.ThrowIfNull(problemType);
        var match = SlugPattern().Match(problemType);
        return match.Success ? match.Value : string.Empty;
    }

    [GeneratedRegex("curia/[a-z0-9-]+/[a-z0-9-]+", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();

    /// <summary>Null when the answer passes clauses 1–3; otherwise why it does not.</summary>
    internal static string? Verdict(string pattern, HttpStatusCode status, string body, TimeSpan elapsed, bool warmUp)
    {
        var code = (int)status;
        if (code >= 500) return "a server fault";
        if (ProblemShape.NotAProblem(pattern, code, body) is { } reason) return "a refusal that is no problem document: " + reason;
        if (!warmUp && elapsed.TotalMilliseconds > BudgetMilliseconds) return Budget(elapsed);
        return null;
    }

    internal static string Budget(TimeSpan elapsed) =>
        $"{OverBudget}: {Math.Round(elapsed.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)} ms against {BudgetMilliseconds.ToString(CultureInfo.InvariantCulture)} ms (set in Release on an Apple M3 Max)";

    /// <summary>
    /// The answer's problem type: a problem document's <c>type</c>, or at the token endpoint RFC 6749's
    /// <c>error</c> and <paramref name="capturedType"/>, the slug the refusal logged (event 5001, or
    /// 5000 for a server fault), since its body no longer carries one (M5). A token refusal logged with
    /// no reason is its bare <c>error</c>. Empty for an answer that has neither.
    /// </summary>
    internal static string ProblemType(string pattern, string body, string capturedType)
    {
        ArgumentNullException.ThrowIfNull(capturedType);
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return string.Empty;
            if (string.Equals(pattern, "/oauth/token", StringComparison.Ordinal))
            {
                var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()! : string.Empty;
                return capturedType.Length > 0 ? error + " " + capturedType : error;
            }

            return root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString()! : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Whether an answer shows a value got past the first parser that read it: a 2xx, or a problem type
    /// that is not empty, not one of ADMIT's (<see cref="AdmitPrefix"/>), not one of
    /// <see cref="ParserRefusals"/>, not the credential's or the signature's
    /// (<see cref="AuthenticationAndSignatureRefusals"/>), and not the token endpoint's refusal of its
    /// DPoP proof (<see cref="TokenEndpointProofRefusal"/>, exactly). Slugs are matched exactly (the
    /// random pass's floor; reviews of 9411deb and 6cbfa9f).
    /// </summary>
    internal static bool PastFirstParser(HttpStatusCode status, string problemType)
    {
        ArgumentNullException.ThrowIfNull(problemType);
        var code = (int)status;
        if (code is >= 200 and < 300) return true;
        var slug = Slug(problemType);
        return problemType.Length > 0
            && !slug.StartsWith(AdmitPrefix, StringComparison.Ordinal)
            && !ParserRefusals.Contains(slug, StringComparer.Ordinal)
            && !AuthenticationAndSignatureRefusals.Contains(slug, StringComparer.Ordinal)
            && !string.Equals(problemType, TokenEndpointProofRefusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether an answer shows a variation reached (clause 5): a 2xx, or a 4xx refusal other than the
    /// credential and signature refusals (<see cref="AuthenticationAndSignatureRefusals"/>). A parser
    /// refusal counts, though it comes before signature verification (register D33, "Observed, not acted
    /// on (Task A5): the reach clause counts a parser refusal as reach"). A token refusal counts only by
    /// the reason it logged: one logged with none, whose problem type is its bare <c>error</c>, says
    /// nothing of what was reached, so it is not reach (M5), except a bare <c>invalid_request</c>
    /// (<see cref="TokenEndpointFormRefusal"/>), the form checks' refusal, which is a parser refusal and
    /// is reach (D33).
    /// </summary>
    internal static bool Reached(string pattern, HttpStatusCode status, string problemType)
    {
        ArgumentNullException.ThrowIfNull(problemType);
        var code = (int)status;
        if (code is >= 200 and < 300) return true;
        if (string.Equals(pattern, "/oauth/token", StringComparison.Ordinal) && !problemType.Contains(' ', StringComparison.Ordinal) && !string.Equals(problemType, TokenEndpointFormRefusal, StringComparison.Ordinal)) return false;
        // Answers unchanged from the substring match: no slug in src extends any of the four entries (review of 6cbfa9f).
        return code is >= 400 and < 500
            && problemType.Length > 0
            && !AuthenticationAndSignatureRefusals.Contains(Slug(problemType), StringComparer.Ordinal);
    }
}
