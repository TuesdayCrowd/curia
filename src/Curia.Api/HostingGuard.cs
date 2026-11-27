using Curia.Domain.Primitives;

namespace Curia.Api;

/// <summary>
/// R11.112 (errata G85): a hosted Forum is never Development. The Forum does not start when the host's environment is Development and its public origin (R11.109) names a host that is not loopback. The host's
/// Development environment serves a developer exception page that carries the credentials of the request that faulted, and a Forum whose name is not this machine's is a Forum other people send credentials to.
///
/// <para><b>What it reads.</b> Only the two values it is handed: the host's environment name (<c>builder.Environment</c>, the host's own resolution, in which <c>DOTNET_ENVIRONMENT</c> wins over
/// <c>ASPNETCORE_ENVIRONMENT</c> when both are set, and which a test host may set with <c>UseEnvironment</c>) and the origin, already in its canonical form. It reads no configuration key and no process variable
/// (DEV8 holds that in IL), so there is no setting that lifts it. The comparison is the framework's own predicate, <c>IsDevelopment()</c>: ASCII case-insensitively <c>Development</c>, so that the guard cannot
/// disagree with the page about when the page exists. <c>Staging</c> and a custom name are not refused: no page exists there.</para>
///
/// <para><b>Where it runs.</b> Inside the one registration of <see cref="PublicOrigin"/> (<c>Program.cs</c>, DEV7), so nothing can obtain an origin without passing it, and before any store or key is read (DEV5).</para>
/// </summary>
public static class HostingGuard
{
    /// <summary>The refusal's problem type; the exception's message is <see cref="Words"/> and not the type, as R11.109's is.</summary>
    internal const string ErrorType = "curia/config/development-with-public-origin";

    private const string ErrorTitle = "A Forum with a public origin does not start in Development";

    /// <summary>
    /// The refusal's words, one definition: <c>{Origin}</c> is the one hole, and the hosting runbook quotes this text with the origin filled (DEV6 reads the quotation against this constant). The environment is
    /// never echoed (the framework keeps the name as it was spelled) and the origin is quoted because its grammar admits no userinfo (R11.109 refused one before this runs).
    /// </summary>
    public const string Words =
        "The Forum is started in the Development environment and its public origin is \"{Origin}\", which is not this machine's loopback address. " +
        "In Development the host serves a developer exception page that carries a request's Authorization and DPoP headers and the access token, so a Forum reached by any other name must not run there. " +
        "The host takes its environment from DOTNET_ENVIRONMENT when that is set, and from ASPNETCORE_ENVIRONMENT when it is not: unset both, or set both to Production, and start again. " +
        "(For local work only, set CURIA_PUBLIC_ORIGIN to http://localhost[:port].) No setting lifts this (R11.112).";

    /// <summary>The origin when it may be served from the environment the host is in; otherwise the refusal.</summary>
    public static Result<PublicOrigin> Check(string environmentName, PublicOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(environmentName);
        ArgumentNullException.ThrowIfNull(origin);

        if (string.Equals(environmentName, Microsoft.Extensions.Hosting.Environments.Development, StringComparison.OrdinalIgnoreCase) && !origin.IsLoopback())
            return Result<PublicOrigin>.Fail(new Error(ErrorType, ErrorTitle, Words.Replace("{Origin}", origin.Value, StringComparison.Ordinal)));

        return Result<PublicOrigin>.Ok(origin);
    }
}
