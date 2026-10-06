using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Curia.Api.Tests.Fuzz;

/// <summary>
/// Spec §4.10's oracle, clauses 1–3, for one answer; clauses 4 and 5 are the run's (<see cref="FuzzRun"/>).
/// </summary>
internal static class Oracle
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

    /// <summary>Whether an answer shows a variation got past the credential and the signature (clause 5).</summary>
    internal static bool Reached(HttpStatusCode status, string problemType)
    {
        var code = (int)status;
        if (code is >= 200 and < 300) return true;
        return code is >= 400 and < 500
            && problemType.Length > 0
            && !AuthenticationAndSignatureRefusals.Any(r => problemType.Contains(r, StringComparison.Ordinal));
    }
}
