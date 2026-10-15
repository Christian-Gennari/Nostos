using Nostos.Backend.Services.ReadingList;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Endpoints;

/// <summary>
/// The reading list: books the user wants to read and does not have yet.
/// </summary>
public static class ReadingListEndpoints
{
    public static IEndpointRouteBuilder MapReadingListEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/reading-list");

        // "Next up" in shelf order, then "Someday" newest first.
        group.MapGet(
            "/",
            async (IReadingListService list, CancellationToken ct) => Results.Ok(await list.ListAsync(ct))
        );

        group.MapPost(
            "/",
            async (SaveReadingListEntryDto dto, IReadingListService list, CancellationToken ct) =>
            {
                var result = await list.CreateAsync(dto, ct);
                return MapError(result)
                    ?? Results.Created($"/api/reading-list/{result.Value!.Id}", result.Value);
            }
        );

        // Replaces the whole "Next up" shelf. Registered before /{id} so the
        // literal segment always wins.
        group.MapPut(
            "/next",
            async (SetReadingListNextDto dto, IReadingListService list, CancellationToken ct) =>
            {
                var result = await list.SetNextAsync(dto.Ids, ct);
                return MapError(result) ?? Results.Ok(result.Value);
            }
        );

        group.MapPut(
            "/{id:guid}",
            async (Guid id, SaveReadingListEntryDto dto, IReadingListService list, CancellationToken ct) =>
            {
                var result = await list.UpdateAsync(id, dto, ct);
                return MapError(result) ?? Results.Ok(result.Value);
            }
        );

        group.MapDelete(
            "/{id:guid}",
            async (Guid id, IReadingListService list, CancellationToken ct) =>
            {
                var result = await list.DeleteAsync(id, ct);
                return MapError(result) ?? Results.NoContent();
            }
        );

        // The user got the book: the entry's reason becomes a note on it and
        // the entry leaves the list.
        group.MapPost(
            "/{id:guid}/acquired",
            async (Guid id, ReadingListAcquiredDto dto, IReadingListService list, CancellationToken ct) =>
            {
                var result = await list.MarkAcquiredAsync(id, dto.BookId, ct);
                return MapError(result) ?? Results.NoContent();
            }
        );

        return routes;
    }

    private static IResult? MapError<T>(ReadingListResult<T> result)
    {
        if (result.Success)
            return null;

        var status = result.ErrorCode switch
        {
            ReadingListErrorCodes.EntryNotFound or ReadingListErrorCodes.BookNotFound
                => StatusCodes.Status404NotFound,
            ReadingListErrorCodes.ListFull => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest,
        };

        return Results.Problem(
            statusCode: status,
            title: result.ErrorCode,
            detail: result.ErrorMessage,
            extensions: new Dictionary<string, object?> { ["code"] = result.ErrorCode });
    }
}
