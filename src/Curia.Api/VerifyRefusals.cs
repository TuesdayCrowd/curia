using Curia.Application.Credentials;
using Curia.Application.Ingest;
using Curia.Application.Ports;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Microsoft.AspNetCore.Http;

namespace Curia.Api;

/// <summary>
/// R6.58 (errata G24): the status of a refusal VERIFY makes. A refusal of the envelope's own content is a request
/// problem and answers 400; 401 and 403 are for a credential that fails and an act the principal may not do.
/// Public, and a type of its own, so that a fact can drive it without a new <c>InternalsVisibleTo</c> (CS-5).
/// </summary>
public static class VerifyRefusals
{
    public static int StatusOf(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (string.Equals(error.Type, LogBoundKeys.LogUnreadableType, StringComparison.Ordinal))
            return StatusCodes.Status503ServiceUnavailable;

        if (string.Equals(error.Type, IngestErrors.UnstorableMemberType, StringComparison.Ordinal))
            return StatusCodes.Status422UnprocessableEntity;

        if (error.Type is AuthorKeyErrors.RetiredType or AuthorKeyErrors.CompromisedType)
            return StatusCodes.Status403Forbidden;

        // By prefix, so a content refusal added later answers 400 without an edit here.
        if (error.Type.StartsWith(ContentErrors.TypePrefix, StringComparison.Ordinal)
            && !string.Equals(error.Type, ContentErrors.AuthorMismatchType, StringComparison.Ordinal))
            return StatusCodes.Status400BadRequest;

        return StatusCodes.Status401Unauthorized;
    }
}
