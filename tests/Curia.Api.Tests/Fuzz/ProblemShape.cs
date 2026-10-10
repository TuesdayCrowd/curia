using System.Text.Json;

namespace Curia.Api.Tests.Fuzz;

/// <summary>
/// R11.33's shape of a refusal, shared by the hand sweep (<c>RequestSurfaceTests</c>) and the fuzzer's
/// oracle (clause 2). Moved from <c>RequestSurfaceTests</c>, not changed.
/// </summary>
internal static class ProblemShape
{
    /// <summary>
    /// Null when an answer is no 4xx, or is a 4xx in the form its route owes; otherwise why it is not
    /// (Task 8's second review, I2). The token endpoint answers RFC 6749 §5.2's error object; every
    /// other route an RFC 9457 problem document, whose type need not be one of <c>curia/</c>'s.
    /// </summary>
    internal static string? NotAProblem(string path, int status, string body)
    {
        if (status < 400 || status > 499) return null;

        JsonElement root;
        try
        {
            using var json = JsonDocument.Parse(body);
            root = json.RootElement.Clone();
        }
        catch (JsonException)
        {
            return "not JSON";
        }

        if (root.ValueKind != JsonValueKind.Object) return "not a JSON object";

        if (path.Equals("/oauth/token", StringComparison.Ordinal))
        {
            return root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                ? null
                : "no string error member";
        }

        return root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString()!.Length > 0
            ? null
            : "no non-empty string type member";
    }
}
