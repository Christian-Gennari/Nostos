using Microsoft.AspNetCore.Http;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Endpoints;

/// <summary>
/// Maps library-domain error envelopes to HTTP Problem Details, mirroring the
/// Library ToHttp convention: invalid_* → 400, *_not_found → 404,
/// the conflict family → 409, everything else → 422.
/// </summary>
public static class LibraryHttpMapper
{
    public static IResult? MapError(LibraryCommandResultDto envelope)
    {
        var code = envelope.Data switch
        {
            LibraryErrorDto e => e.Code,
            LibraryConfirmationErrorDto c => c.Code,
            _ => null,
        };
        if (code is null)
            return null;

        var status = code switch
        {
            var c when c.Contains("_not_found", StringComparison.Ordinal) => StatusCodes.Status404NotFound,
            var c when c.StartsWith("invalid_", StringComparison.Ordinal) => StatusCodes.Status400BadRequest,
            var c when c is "identity_conflict" or "duplicate_identifier" or "confirmation_required"
                or "collection_name_conflict" or "collection_cycle" or "collection_has_children"
                or "book_in_use" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status422UnprocessableEntity,
        };

        return Results.Problem(
            statusCode: status,
            title: code,
            detail: envelope.Reply);
    }
}
