using Nostos.Backend.Services.Ai;
using Nostos.Backend.Services.Notes;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Endpoints;

/// <summary>
/// The note post-processing surface (issue #262 §7, §8). Thin by design: every
/// route delegates to <see cref="INoteService"/> and maps its typed failure to a
/// status, exactly as <see cref="NotesEndpoints"/> does.
///
/// <list type="bullet">
/// <item><c>POST /api/notes/{id}/reprocess</c> — re-derive Content from the raw
/// transcript in a chosen mode.</item>
/// <item><c>GET /api/notes/{id}/raw</c> — read the raw transcript and the mode
/// the stored text reflects.</item>
/// <item><c>POST /api/notes/{id}/raw/restore</c> — restore Content from the raw
/// transcript.</item>
/// </list>
/// </summary>
public static class NoteProcessingEndpoints
{
    public static IEndpointRouteBuilder MapNoteProcessingEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/notes");

        group.MapPost("/{id}/reprocess", ReprocessAsync);
        group.MapGet("/{id}/raw", GetRawAsync);
        group.MapPost("/{id}/raw/restore", RestoreRawAsync);

        return routes;
    }

    private static async Task<IResult> ReprocessAsync(
        Guid id,
        ReprocessNoteDto dto,
        INoteService notes,
        CancellationToken ct)
    {
        try
        {
            var result = await notes.ReprocessAsync(id, dto.ProcessingMode, ct);
            return result.Success ? Results.Ok(result.Value) : MapFailure(result);
        }
        catch (LlmException ex)
        {
            // The note and its raw transcript are untouched; the client gets the
            // typed provider failure rather than a 500.
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: ex.Code,
                detail: ex.Message);
        }
    }

    private static async Task<IResult> GetRawAsync(
        Guid id,
        INoteService notes,
        CancellationToken ct)
    {
        var result = await notes.GetRawAsync(id, ct);
        return result.Success ? Results.Ok(result.Value) : MapFailure(result);
    }

    private static async Task<IResult> RestoreRawAsync(
        Guid id,
        INoteService notes,
        CancellationToken ct)
    {
        var result = await notes.RestoreRawAsync(id, ct);
        return result.Success ? Results.Ok(result.Value) : MapFailure(result);
    }

    private static IResult MapFailure<T>(NoteCommandResult<T> result) => result.ErrorCode switch
    {
        NoteErrorCodes.NoteNotFound => Results.NotFound(new { error = result.ErrorMessage }),
        NoteErrorCodes.NoRawTranscript => Results.Conflict(new { error = result.ErrorMessage }),
        _ => Results.BadRequest(new { error = result.ErrorMessage }),
    };
}
