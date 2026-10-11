using System.Data;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;

namespace Nostos.Product.Alignment;

public sealed record AlignmentAnchorInput(
    Guid AudioBookId,
    Guid TextBookId,
    string AudioRevision,
    string TextRevision,
    long AudioPositionMs,
    int TextGlobalOffset,
    int SpineIndex,
    string ResourceHref,
    string? Cfi,
    int? TextOffset,
    double Score);

public interface IAlignmentAnchorStore
{
    Task<bool> SaveAsync(
        AlignmentAnchorInput anchor,
        CancellationToken ct = default);

    Task<IReadOnlyList<AlignmentAnchorModel>> GetNearbyAsync(
        Guid audioBookId,
        Guid textBookId,
        string audioRevision,
        string textRevision,
        long audioPositionMs,
        long radiusMs,
        int limit = 100,
        CancellationToken ct = default);

    Task<AlignmentAnchorModel?> AtOrBeforeTextAsync(
        Guid audioBookId,
        Guid textBookId,
        string audioRevision,
        string textRevision,
        int textGlobalOffset,
        CancellationToken ct = default);

    Task<AlignmentAnchorModel?> AtOrAfterTextAsync(
        Guid audioBookId,
        Guid textBookId,
        string audioRevision,
        string textRevision,
        int textGlobalOffset,
        CancellationToken ct = default);

    Task<AlignmentAnchorModel?> AtOrBeforeAudioAsync(
        Guid audioBookId,
        Guid textBookId,
        string audioRevision,
        string textRevision,
        long audioPositionMs,
        CancellationToken ct = default);

    Task<AlignmentAnchorModel?> AtOrAfterAudioAsync(
        Guid audioBookId,
        Guid textBookId,
        string audioRevision,
        string textRevision,
        long audioPositionMs,
        CancellationToken ct = default);
}

/// <summary>
/// Scoped EF-backed anchor storage. All queries require both exact revisions.
/// Derived writes do not advance the portable library revision.
/// </summary>
public sealed class EfAlignmentAnchorStore(
    NostosDbContext db) : IAlignmentAnchorStore
{
    public const int MaximumPerPair = 2000;
    public const long DuplicateWindowMs = 2000;

    public async Task<bool> SaveAsync(
        AlignmentAnchorInput anchor,
        CancellationToken ct = default)
    {
        if (anchor.AudioBookId == Guid.Empty ||
            anchor.TextBookId == Guid.Empty ||
            anchor.AudioBookId == anchor.TextBookId ||
            string.IsNullOrWhiteSpace(anchor.AudioRevision) ||
            string.IsNullOrWhiteSpace(anchor.TextRevision) ||
            anchor.AudioRevision.Length > 128 ||
            anchor.TextRevision.Length > 512 ||
            anchor.AudioPositionMs < 0 ||
            anchor.TextGlobalOffset < 0 ||
            anchor.SpineIndex < 0 ||
            string.IsNullOrWhiteSpace(anchor.ResourceHref) ||
            anchor.ResourceHref.Length > 2048 ||
            anchor.Cfi?.Length > 2048 ||
            anchor.TextOffset is < 0 ||
            !double.IsFinite(anchor.Score) ||
            anchor.Score is < 0 or > 1)
            return false;

        // A transaction keeps stale cleanup, upsert and retention atomic.
        await using var transaction =
            await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, ct);

        var pair = db.AlignmentAnchors.Where(a =>
            a.AudioBookId == anchor.AudioBookId &&
            a.TextBookId == anchor.TextBookId);

        // Remove revisions that cannot be used with the current files.
        await pair.Where(a =>
                a.AudioRevision != anchor.AudioRevision ||
                a.TextRevision != anchor.TextRevision)
            .ExecuteDeleteAsync(ct);

        var matching = pair.Where(a =>
            a.AudioRevision == anchor.AudioRevision &&
            a.TextRevision == anchor.TextRevision);

        var earliest = Math.Max(
            0, anchor.AudioPositionMs - DuplicateWindowMs);

        var latest = anchor.AudioPositionMs >
            long.MaxValue - DuplicateWindowMs
                ? long.MaxValue
                : anchor.AudioPositionMs + DuplicateWindowMs;

        var existing = await matching
            .Where(a =>
                a.AudioPositionMs >= earliest &&
                a.AudioPositionMs <= latest)
            .OrderBy(a =>
                a.AudioPositionMs > anchor.AudioPositionMs
                    ? a.AudioPositionMs - anchor.AudioPositionMs
                    : anchor.AudioPositionMs - a.AudioPositionMs)
            .ThenBy(a => a.Id)
            .FirstOrDefaultAsync(ct);

        var now = DateTime.UtcNow;

        if (existing is null)
        {
            db.AlignmentAnchors.Add(new AlignmentAnchorModel
            {
                AudioBookId = anchor.AudioBookId,
                TextBookId = anchor.TextBookId,
                AudioRevision = anchor.AudioRevision,
                TextRevision = anchor.TextRevision,
                AudioPositionMs = anchor.AudioPositionMs,
                TextGlobalOffset = anchor.TextGlobalOffset,
                SpineIndex = anchor.SpineIndex,
                ResourceHref = anchor.ResourceHref,
                Cfi = anchor.Cfi,
                TextOffset = anchor.TextOffset,
                Score = anchor.Score,
                CreatedAt = now,
            });
        }
        else
        {
            // The latest confident measurement replaces its nearby predecessor.
            // This also refreshes its retention timestamp.
            existing.AudioPositionMs = anchor.AudioPositionMs;
            existing.TextGlobalOffset = anchor.TextGlobalOffset;
            existing.SpineIndex = anchor.SpineIndex;
            existing.ResourceHref = anchor.ResourceHref;
            existing.Cfi = anchor.Cfi;
            existing.TextOffset = anchor.TextOffset;
            existing.Score = anchor.Score;
            existing.CreatedAt = now;
        }

        await db.SaveChangesAsync(ct);

        // Retain the most recently measured anchors, not simply the
        // anchors with the largest audiobook positions.
        var obsoleteIds = await matching
            .OrderByDescending(a => a.CreatedAt)
            .ThenByDescending(a => a.Id)
            .Skip(MaximumPerPair)
            .Select(a => a.Id)
            .ToListAsync(ct);

        if (obsoleteIds.Count > 0)
        {
            await db.AlignmentAnchors
                .Where(a => obsoleteIds.Contains(a.Id))
                .ExecuteDeleteAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<AlignmentAnchorModel>> GetNearbyAsync(
        Guid audioBookId,
        Guid textBookId,
        string audioRevision,
        string textRevision,
        long audioPositionMs,
        long radiusMs,
        int limit = 100,
        CancellationToken ct = default)
    {
        if (radiusMs < 0)
            throw new ArgumentOutOfRangeException(nameof(radiusMs));

        if (limit is < 1 or > MaximumPerPair)
            throw new ArgumentOutOfRangeException(nameof(limit));

        var low = audioPositionMs < long.MinValue + radiusMs
            ? long.MinValue
            : audioPositionMs - radiusMs;

        var high = audioPositionMs > long.MaxValue - radiusMs
            ? long.MaxValue
            : audioPositionMs + radiusMs;

        return await ForRevisions(
                audioBookId, textBookId,
                audioRevision, textRevision)
            .Where(a =>
                a.AudioPositionMs >= low &&
                a.AudioPositionMs <= high)
            .OrderBy(a => a.AudioPositionMs)
            .ThenBy(a => a.Id)
            .Take(limit)
            .ToListAsync(ct);
    }

    public Task<AlignmentAnchorModel?> AtOrBeforeTextAsync(
        Guid audioBookId,
        Guid textBookId,
        string audioRevision,
        string textRevision,
        int textGlobalOffset,
        CancellationToken ct = default) =>
        ForRevisions(
                audioBookId, textBookId,
                audioRevision, textRevision)
            .Where(a => a.TextGlobalOffset <= textGlobalOffset)
            .OrderByDescending(a => a.TextGlobalOffset)
            .ThenByDescending(a => a.Score)
            .FirstOrDefaultAsync(ct);

    public Task<AlignmentAnchorModel?> AtOrAfterTextAsync(
        Guid audioBookId,
        Guid textBookId,
        string audioRevision,
        string textRevision,
        int textGlobalOffset,
        CancellationToken ct = default) =>
        ForRevisions(
                audioBookId, textBookId,
                audioRevision, textRevision)
            .Where(a => a.TextGlobalOffset >= textGlobalOffset)
            .OrderBy(a => a.TextGlobalOffset)
            .ThenByDescending(a => a.Score)
            .FirstOrDefaultAsync(ct);

    public Task<AlignmentAnchorModel?> AtOrBeforeAudioAsync(
        Guid audioBookId,
        Guid textBookId,
        string audioRevision,
        string textRevision,
        long audioPositionMs,
        CancellationToken ct = default) =>
        ForRevisions(
                audioBookId, textBookId,
                audioRevision, textRevision)
            .Where(a => a.AudioPositionMs <= audioPositionMs)
            .OrderByDescending(a => a.AudioPositionMs)
            .ThenByDescending(a => a.Score)
            .FirstOrDefaultAsync(ct);

    public Task<AlignmentAnchorModel?> AtOrAfterAudioAsync(
        Guid audioBookId,
        Guid textBookId,
        string audioRevision,
        string textRevision,
        long audioPositionMs,
        CancellationToken ct = default) =>
        ForRevisions(
                audioBookId, textBookId,
                audioRevision, textRevision)
            .Where(a => a.AudioPositionMs >= audioPositionMs)
            .OrderBy(a => a.AudioPositionMs)
            .ThenByDescending(a => a.Score)
            .FirstOrDefaultAsync(ct);

    private IQueryable<AlignmentAnchorModel> ForRevisions(
        Guid audioBookId,
        Guid textBookId,
        string audioRevision,
        string textRevision) =>
        db.AlignmentAnchors
            .AsNoTracking()
            .Where(a =>
                a.AudioBookId == audioBookId &&
                a.TextBookId == textBookId &&
                a.AudioRevision == audioRevision &&
                a.TextRevision == textRevision);
}
