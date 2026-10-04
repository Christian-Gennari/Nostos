using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Endpoints;
using Nostos.Backend.Services.Portability.Transfers;

namespace Nostos.Backend.Services.Portability.Migration;

/// <summary>
/// Preflight for a browser transfer. Every fact that can be derived from the
/// installation is derived here: the client declares only the incoming archive
/// shape, which is validated against the contract and never trusted for
/// destination or capacity decisions.
/// </summary>
public interface IMigrationPreflightService
{
    Task<MigrationPreflightResponse> EvaluateAsync(
        MigrationPreflightRequest request,
        CancellationToken ct);
}

public sealed class SelfHostedMigrationPreflightService(
    NostosDbContext db,
    ITransferStorageCapacity capacity,
    IOptions<TransferStorageOptions> options) : IMigrationPreflightService
{
    public async Task<MigrationPreflightResponse> EvaluateAsync(
        MigrationPreflightRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.IncomingCounts);

        var destination = await ReadDestinationAsync(ct);
        var snapshot = await capacity.GetSnapshotAsync(ct);
        var evaluation = MigrationPreflightEvaluator.Evaluate(new MigrationPreflightEvaluationInput(
            request,
            destination.Status,
            destination.Counts,
            snapshot.UsableAvailableBytes,
            destination.Revision));

        if (!evaluation.IsAllowed)
            return new MigrationPreflightResponse(evaluation, null, null, options.Value.ChunkBytes);

        // Admission applies the global safety margin exactly once; the host peak
        // here is contract bytes plus one effective chunk plus the fixed job
        // overhead (see TransferCapacityMath).
        var hostPeakBytes = TransferCapacityMath.CalculateHostPeakReservationBytes(
            evaluation.RequiredStorageBytes,
            options.Value.ChunkBytes,
            options.Value);
        var admitted = await capacity.TryReserveAsync(
            hostPeakBytes,
            MigrationSessionPurpose.Import,
            options.Value.PreflightReservationTtl,
            ct);

        if (!admitted.IsAdmitted)
        {
            // The contract-bytes evaluation passed but host peak did not fit the
            // capacity actually left after the margin and outstanding
            // reservations. Return the same business rejection shape.
            var rejected = evaluation with
            {
                Decision = MigrationPreflightDecision.RejectedInsufficientStorage,
                Errors =
                [
                    .. evaluation.Errors,
                    $"Migration requires {hostPeakBytes} bytes of host peak storage, " +
                    $"but only {admitted.Snapshot.UsableAvailableBytes} bytes are available.",
                ],
            };
            return new MigrationPreflightResponse(rejected, null, null, options.Value.ChunkBytes);
        }

        return new MigrationPreflightResponse(
            evaluation,
            admitted.ReservationId,
            admitted.ExpiresAtUtc,
            options.Value.ChunkBytes);
    }

    private async Task<DestinationSnapshot> ReadDestinationAsync(CancellationToken ct)
    {
        var counts = new MigrationExistingCounts(
            Works: await db.Works.CountAsync(ct),
            Books: await db.Books.CountAsync(ct),
            Notes: await db.Notes.CountAsync(ct),
            Topics: await db.Topics.CountAsync(ct),
            NoteTopics: await db.NoteTopics.CountAsync(ct),
            Writings: await db.Writings.CountAsync(ct),
            WritingNotes: await db.WritingNotes.CountAsync(ct),
            Collections: await db.Collections.CountAsync(ct),
            BookCollections: await db.BookCollections.CountAsync(ct),
            Acquisitions: await db.BookAcquisitions.CountAsync(ct),
            NoteImportBookLinks: await db.NoteImportBookLinks.CountAsync(ct),
            AssistantSettings: await db.AssistantSettings.CountAsync(ct));

        var state = await db.LibraryStates.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == LibraryState.WellKnownId, ct);
        var status = counts.TotalRows > 0
            ? MigrationDestinationStatus.Populated
            : MigrationDestinationStatus.Empty;

        return new DestinationSnapshot(
            status,
            counts,
            MigrationDestinationRevision.Compute(state?.StateVersion ?? "0", counts));
    }

    private sealed record DestinationSnapshot(
        MigrationDestinationStatus Status,
        MigrationExistingCounts Counts,
        string Revision);
}

/// <summary>
/// Opaque destination revision bound into preflight/activation comparisons.
/// The singleton library <c>StateVersion</c> bumps on every committed library
/// mutation; the counts make the revision change even if a mutation path
/// bypassed that counter.
/// </summary>
public static class MigrationDestinationRevision
{
    public static string Compute(string stateVersion, MigrationExistingCounts counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            stateVersion,
            counts.Works,
            counts.Books,
            counts.Notes,
            counts.Topics,
            counts.NoteTopics,
            counts.Writings,
            counts.WritingNotes,
            counts.Collections,
            counts.BookCollections,
            counts.Acquisitions,
            counts.NoteImportBookLinks,
            counts.AssistantSettings,
        });
        return Convert.ToHexStringLower(SHA256.HashData(payload));
    }
}
