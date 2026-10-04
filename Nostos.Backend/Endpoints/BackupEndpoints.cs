using Nostos.Backend.Services;
using Nostos.Backend.Middleware;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Endpoints;

public static class BackupEndpoints
{
    public static IEndpointRouteBuilder MapBackupEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/backup");

        group.MapGet("/status", async (IBackupService backupService) =>
        {
            var status = await backupService.GetStatusAsync();
            return Results.Ok(status);
        });

        group.MapGet("/settings", async (IBackupService backupService) =>
        {
            var settings = await backupService.GetSettingsAsync();
            return Results.Ok(settings);
        });

        group.MapPut("/settings", async (UpdateBackupSettingsDto dto, IBackupService backupService) =>
        {
            var settings = await backupService.UpdateSettingsAsync(dto);
            return Results.Ok(settings);
        });

        group.MapPost("/trigger", async (IBackupService backupService) =>
        {
            var result = await backupService.CreateBackupAsync();
            return Results.Ok(result);
        });

        group.MapPost("/restore/{id:guid}", async (Guid id, IBackupService backupService, CancellationToken ct) =>
        {
            var result = await backupService.RestoreBackupAsync(id, ct);
            if (!result.Success)
                return Results.Json(new { error = result.Message }, statusCode: StatusCodes.Status500InternalServerError);

            return Results.Ok(result);
        }).WithMetadata(new LibraryMaintenanceControl());

        group.MapGet("/history", async (IBackupService backupService) =>
        {
            var history = await backupService.GetHistoryAsync();
            return Results.Ok(history);
        });

        group.MapDelete("/history/{id:guid}", async (Guid id, IBackupService backupService) =>
        {
            await backupService.DeleteBackupRecordAsync(id);
            return Results.NoContent();
        });

        group.MapGet("/download/{id:guid}", (Guid id, IBackupService backupService) =>
        {
            var path = backupService.GetLocalArchivePath(id);
            if (path is null)
                return Results.NotFound(new { error = "Backup archive not found." });

            return Results.File(
                path,
                "application/zip",
                $"nostos-backup-{id}.nostos"
            );
        });

        group.MapPost("/import", async (IBackupService backupService) =>
        {
            var imported = await backupService.ImportExistingBackupsAsync();
            return Results.Ok(imported);
        });

        group.MapGet("/progress", (BackupSettingsProvider settingsProvider) =>
        {
            var progress = settingsProvider.Progress;
            if (progress is null)
                return Results.Ok(new { isRunning = false, currentStep = (string?)null, percentComplete = 0, startedAt = (DateTime?)null, stepNumber = 0, totalSteps = 0 });
            return Results.Ok<object>(progress);
        });

        return routes;
    }
}
