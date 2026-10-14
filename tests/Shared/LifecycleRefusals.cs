namespace Curia.Tests.Shared;

/// <summary>
/// The two refusal problem documents a guarded Forum serves a lifecycle route (R4.38 (add.), GF9; spec sections 4.7 and
/// 4.9): type <c>curia/keys/lifecycle-not-enabled</c>, status 403, and a fixed detail naming the kind (<c>rotation</c>
/// or <c>retirement</c>) and nothing about the caller.
///
/// <para><b>Why this is one file linked into three suites.</b> <c>StubLog</c>'s guarded mode serves these in
/// <c>Curia.Client.Tests</c> and <c>Curia.Mcp.Tests</c>, where no Forum is running, and
/// <c>R4_38_TheGuardedRefusalIsTheDocumentTheClientStandInServes</c> holds them in <c>Curia.Api.Tests</c> to what the
/// guarded Forum really serves, member for member. So the stand-in is an artifact the real refusal is checked against,
/// and never a second reading of spec section 4.7.</para>
///
/// <para>It needs nothing beyond the BCL and what <c>Curia.Client</c> exposes, since <c>Curia.Client.Tests</c> and
/// <c>Curia.Mcp.Tests</c> reach nothing else.</para>
/// </summary>
internal static class LifecycleRefusals
{
    /// <summary>The problem type of both refusals.</summary>
    internal const string Type = "curia/keys/lifecycle-not-enabled";

    /// <summary>The HTTP status of both refusals.</summary>
    internal const int Status = 403;

    /// <summary>The title of both refusals.</summary>
    internal const string Title = "Key lifecycle acts are not enabled";

    /// <summary>The rotation route's refusal, as the guarded Forum serves it.</summary>
    internal static string Rotation => Document("key rotation is not enabled on this Forum");

    /// <summary>The retirement route's refusal, as the guarded Forum serves it.</summary>
    internal static string Retirement => Document("key retirement is not enabled on this Forum");

    private static string Document(string detail) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["type"] = Type,
            ["title"] = Title,
            ["detail"] = detail,
        });
}
