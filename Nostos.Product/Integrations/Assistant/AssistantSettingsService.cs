using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Ai;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Integrations.Assistant;

/// <summary>
/// The one place the assistant settings the owner chooses once are read and
/// written (issue #262 §7). Today that is exactly one thing: the post-processing
/// mode applied when the user's own words become a note.
///
/// <para>
/// The stored value is nullable on purpose: <c>NULL</c> means "never chosen",
/// which must stay distinguishable from a stored <c>verbatim</c>. Every read
/// normalises through <see cref="ThoughtProcessingModes.Normalize"/>, so
/// "nothing stored" reads back as <c>verbatim</c> without pretending a choice
/// was made.
/// </para>
/// </summary>
public interface IAssistantSettingsService
{
    /// <summary>
    /// The effective capture post-processing mode: the stored value, normalised,
    /// or <see cref="ThoughtProcessingModes.Verbatim"/> when there is no row or
    /// nothing stored. This is what the orchestrator applies to a capture.
    /// </summary>
    Task<string> GetCaptureProcessingModeAsync(CancellationToken ct = default);

    /// <summary>The full settings surface as returned to the client.</summary>
    Task<AssistantSettingsResponse> GetAsync(CancellationToken ct = default);

    /// <summary>
    /// Stores the requested mode and re-reads, so the response has the same shape
    /// as <see cref="GetAsync"/>. An unsupported value is a typed refusal that
    /// stores nothing, never an exception.
    /// </summary>
    Task<AssistantSettingsUpdateResult> UpdateAsync(
        AssistantSettingsUpdateRequest request,
        CancellationToken ct = default);
}

/// <summary>
/// The outcome of a settings update. A refusal is data
/// (<see cref="ErrorCode"/>) so the endpoint can answer with the stable problem
/// title the wire contract names, rather than a 500.
/// </summary>
public sealed record AssistantSettingsUpdateResult(
    AssistantSettingsResponse? Settings,
    string? ErrorCode)
{
    public bool Success => Settings is not null;

    public static AssistantSettingsUpdateResult Ok(AssistantSettingsResponse settings) =>
        new(settings, null);

    public static AssistantSettingsUpdateResult Invalid(string errorCode) =>
        new(null, errorCode);
}

/// <inheritdoc cref="IAssistantSettingsService" />
public sealed class AssistantSettingsService(IDbContextFactory<NostosDbContext> dbFactory)
    : IAssistantSettingsService
{
    public async Task<string> GetCaptureProcessingModeAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var stored = await LoadStoredModeAsync(db, ct);
        return ThoughtProcessingModes.Normalize(stored);
    }

    public async Task<AssistantSettingsResponse> GetAsync(CancellationToken ct = default) =>
        new(await GetCaptureProcessingModeAsync(ct));

    public async Task<AssistantSettingsUpdateResult> UpdateAsync(
        AssistantSettingsUpdateRequest request,
        CancellationToken ct = default)
    {
        // Validate with the one supported-set definition. An absent value is not
        // a supported mode: there is no "clear" in this contract, only a choice.
        if (!ThoughtProcessingModes.IsSupported(request?.CaptureProcessingMode))
        {
            return AssistantSettingsUpdateResult.Invalid(AssistantErrorCodes.InvalidProcessingMode);
        }

        var mode = ThoughtProcessingModes.Normalize(request!.CaptureProcessingMode);

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var row = await db.AssistantSettings
                .FirstOrDefaultAsync(s => s.Id == AssistantSettingsModel.SingletonId, ct);

            if (row is null)
            {
                row = new AssistantSettingsModel { Id = AssistantSettingsModel.SingletonId };
                db.AssistantSettings.Add(row);
            }

            row.CaptureProcessingMode = mode;
            row.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        // Re-read so the response reflects what is actually stored, and is the
        // same shape as GET.
        return AssistantSettingsUpdateResult.Ok(await GetAsync(ct));
    }

    private static Task<string?> LoadStoredModeAsync(NostosDbContext db, CancellationToken ct) =>
        db.AssistantSettings
            .AsNoTracking()
            .Where(s => s.Id == AssistantSettingsModel.SingletonId)
            .Select(s => s.CaptureProcessingMode)
            .FirstOrDefaultAsync(ct);
}
