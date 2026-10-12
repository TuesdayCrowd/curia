using Curia.Domain.Primitives;

namespace Curia.Api.Issuer;

/// <summary>
/// RFC 6749 §5.2's error response, and nothing beside it: <c>error</c> and <c>error_description</c>,
/// never a <c>detail</c> (M5; spec §4.11). Every code but <c>invalid_request</c> takes a fixed
/// description, so a <c>kid</c> registered to another agent and a signature that fails read the same,
/// byte for byte (R5.12). The reason goes to the log.
///
/// <para><b>What is logged, and what is not.</b> A 4xx logs event 5001 with its code and the reason's
/// slug only. The reason's detail can hold a header value the caller chose (a proof's <c>typ</c>, an
/// <c>alg</c>, a key's <c>kty</c> or <c>crv</c>), and an anonymous caller would otherwise write control
/// characters and unbounded text into the operator's log: errata G17's class of defect, carried into
/// the log. A 5xx logs event 5000 with the reason's slug and detail, as <see cref="ServerFault"/> does,
/// since a server fault's detail is the component's own and the operator is who can act on it.</para>
///
/// <para><b>Status.</b> <c>invalid_client</c> is the one code §5.2 pairs with a 401; everything it
/// defines for this endpoint is a 400. <c>server_error</c> is §4.1.2.1's code rather than §5.2's --
/// the token endpoint has no code of its own for "the Forum could not read its own log" -- and it is
/// mapped to a 500 because that is what it is: a client that retries an identical request may well
/// succeed, which is exactly the distinction a 4xx would erase.</para>
/// </summary>
internal sealed partial class OAuthRefusal(string code, string description, Error? reason) : IResult
{
    /// <summary>The description of every refusal of the client's authentication.</summary>
    internal const string ClientNotAuthenticated = "The client could not be authenticated";

    /// <summary>The description of every refusal of the token request's DPoP proof.</summary>
    internal const string ProofNotAccepted = "The DPoP proof was not accepted";

    /// <summary>The description of every server fault.</summary>
    internal const string CouldNotIssue = "The Forum could not issue a token";

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var status = code switch
        {
            "invalid_client" => StatusCodes.Status401Unauthorized,
            "server_error" => StatusCodes.Status500InternalServerError,
            _ => StatusCodes.Status400BadRequest,
        };

        var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger<OAuthRefusal>();
        if (status >= StatusCodes.Status500InternalServerError)
            Withheld(logger, status, reason?.Type ?? string.Empty, reason?.Detail ?? "(none)");
        else
            Refused(logger, code, reason?.Type ?? string.Empty);

        await Results.Json(new { error = code, error_description = description }, statusCode: status)
            .ExecuteAsync(httpContext)
            .ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 5001, Level = LogLevel.Information, Message = "Token request refused {Code}: {Type}")]
    private static partial void Refused(ILogger logger, string code, string type);

    [LoggerMessage(EventId = 5000, Level = LogLevel.Error, Message = "Served {Status} {Type} without its detail, which was: {Detail}")]
    private static partial void Withheld(ILogger logger, int status, string type, string detail);
}
