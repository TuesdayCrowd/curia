using Curia.Domain.Primitives;

namespace Curia.Api;

/// <summary>
/// A 5xx problem document: the fault's type and title, and no detail. The detail goes to the log
/// (R5.12, R11.33; errata G17).
///
/// <para><b>Why the detail is withheld from every 5xx and not from some.</b> A server fault's detail
/// is whatever the component that failed said about itself, and the component decides that, not
/// this boundary: the vector index said Postgres's own words, <c>22000: NaN not allowed in
/// vector</c>, to an anonymous search (register D25). A rule written per adapter is a rule the next
/// adapter does not know about. At this boundary it holds for every one, and the operator, who is
/// the only party who can act on a server fault, reads the detail where operators read.</para>
/// </summary>
internal sealed partial class ServerFault(int status, Error error) : IResult
{
    /// <summary>The fault's slug, for a caller that answers one fault differently from another.</summary>
    public string Type => error.Type;

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var logger = httpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger<ServerFault>();
        Withheld(logger, status, error.Type, error.Detail ?? "(none)");

        await Results.Json(new Problem(error.Type, error.Title, null), statusCode: status)
            .ExecuteAsync(httpContext)
            .ConfigureAwait(false);
    }

    [LoggerMessage(
        EventId = 5000,
        Level = LogLevel.Error,
        Message = "Served {Status} {Type} without its detail, which was: {Detail}")]
    private static partial void Withheld(ILogger logger, int status, string type, string detail);
}
