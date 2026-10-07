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
    /// The token endpoint's two refusals of its DPoP proof, src/Curia.Api/Issuer/TokenEndpoint.cs:109
    /// (no proof) and :113 (a proof whose key CompactJws or JwkParser could not read). Both are answered
    /// through OAuthError with no detail, so ProblemType reads them as the bare error code. Neither got a
    /// value past the proof's parser: one had nothing to parse and the other was refused by it, the same
    /// parse failure that answers curia/authn/malformed or malformed-jwk on other routes (review of
    /// 6cbfa9f). Compared by exact equality only, so a future <c>invalid_dpop_proof &lt;slug&gt;</c>
    /// answer is classified by its slug, not by this constant.
    /// </summary>
    internal const string TokenEndpointProofRefusal = "invalid_dpop_proof";

    /// <summary>
    /// The first <c>curia/&lt;area&gt;/&lt;name&gt;</c> slug in a problem type, or empty. The token
    /// endpoint's problem type is <c>error + " " + detail</c>, so the slug sits inside the string.
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
    /// <c>error</c> and the slug it carries in <c>detail</c>. Empty for an answer that has neither.
    /// </summary>
    internal static string ProblemType(string pattern, string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return string.Empty;
            if (string.Equals(pattern, "/oauth/token", StringComparison.Ordinal))
            {
                var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()! : string.Empty;
                var detail = root.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? " " + d.GetString() : string.Empty;
                return error + detail;
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

    /// <summary>Whether an answer shows a variation got past the credential and the signature (clause 5).</summary>
    internal static bool Reached(HttpStatusCode status, string problemType)
    {
        var code = (int)status;
        if (code is >= 200 and < 300) return true;
        // Answers unchanged from the substring match: no slug in src extends any of the four entries (review of 6cbfa9f).
        return code is >= 400 and < 500
            && problemType.Length > 0
            && !AuthenticationAndSignatureRefusals.Contains(Slug(problemType), StringComparer.Ordinal);
    }
}
