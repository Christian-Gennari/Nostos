namespace Nostos.Backend.Services.Library.Imports;

/// <summary>
/// Receives one zipped Calibre library per owner and turns it into a staged
/// library the existing <see cref="CalibreImportService"/> can read (issue
/// #1572). It is the same preview → match → commit → undo flow as the mounted
/// folder: only the source of the bytes changes.
///
/// The session is resumable: the server owns the part size and the receipt,
/// a received part is never requested again, and the same file can be
/// re-selected after a dropped connection. The staged bytes are disposable
/// and are removed when the import finishes, when the owner cancels, or when
/// the session expires.
/// </summary>
public sealed class CalibreUploadService(
    ICalibreUploadStore store,
    ICalibreUploadLimitsProvider limits,
    ICalibreUploadOwnerProvider owner,
    TimeProvider? clock = null) : ICalibreUploadAvailability
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private CalibreStagedLibrary? _staged;

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;
    private string OwnerKey => owner.GetOwnerKey();

    public bool IsSupported => true;

    /// <summary>The staged library last read by this process, for the source's availability check.</summary>
    public CalibreStagedLibrary? CachedStaged
    {
        get
        {
            lock (_gate)
                return _staged;
        }
    }

    public CalibreUploadLimits CurrentLimits => limits.For(OwnerKey);

    // ------------------------------------------------------------------
    // Reading
    // ------------------------------------------------------------------

    /// <summary>The session this owner would resume, or null.</summary>
    public async Task<CalibreUploadStatus?> GetCurrentAsync(CancellationToken ct)
    {
        await RefreshStagedAsync(ct);
        var session = await store.FindActiveAsync(OwnerKey, ct);
        if (session is null)
            return null;

        if (session.ExpiresAtUtc <= Now)
        {
            await DiscardAsync(session, ct);
            return null;
        }

        return ToStatus(session, limits.For(OwnerKey));
    }

    public async Task<CalibreUploadStatus?> GetAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await store.FindAsync(sessionId, OwnerKey, ct);
        if (session is null)
            return null;

        if (session.ExpiresAtUtc <= Now)
        {
            await DiscardAsync(session, ct);
            throw CalibreUploadException.Error(
                CalibreUploadException.Expired,
                "This upload expired. Choose the zip again to start over.");
        }

        return ToStatus(session, limits.For(OwnerKey));
    }

    public async Task<CalibreStagedLibrary?> GetStagedLibraryAsync(CancellationToken ct)
    {
        await RefreshStagedAsync(ct);
        lock (_gate)
            return _staged;
    }

    /// <summary>True once the current owner has an extracted library ready to review.</summary>
    public bool HasStagedLibrary => CachedStaged is not null;

    // ------------------------------------------------------------------
    // Receiving
    // ------------------------------------------------------------------

    public async Task<CalibreUploadStatus> CreateOrResumeAsync(
        CalibreUploadCreateRequest request,
        CancellationToken ct)
    {
        ValidateCreate(request);
        var effective = limits.For(OwnerKey);
        if (request.TotalBytes > effective.MaxLibraryBytes)
        {
            throw CalibreUploadException.Error(
                CalibreUploadException.TooLarge,
                $"This library is {Bytes(request.TotalBytes)}; the limit is {Bytes(effective.MaxLibraryBytes)}. " +
                "Remove some formats or split the library into smaller uploads.");
        }

        var now = Now;
        var existing = await store.FindActiveAsync(OwnerKey, ct);
        if (existing is not null && existing.ExpiresAtUtc <= now)
        {
            await DiscardAsync(existing, ct);
            existing = null;
        }

        if (existing is not null)
        {
            var sameFile = existing.TotalBytes == request.TotalBytes
                && string.Equals(existing.Sha256, request.Sha256, StringComparison.OrdinalIgnoreCase);
            if (sameFile)
                return ToStatus(existing, effective);

            // One upload per owner: a different library replaces the staged or
            // partial one instead of accumulating scratch.
            await DiscardAsync(existing, ct);
        }

        CalibreUploadManifest created;
        try
        {
            created = await store.CreateAsync(OwnerKey, request, effective, now, ct);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw CalibreUploadException.Error(
                CalibreUploadException.StorageExhausted,
                "There is not enough scratch space to receive this library.");
        }

        ClearStaged();
        return ToStatus(created, effective);
    }

    public async Task<CalibreUploadPartReceipt> UploadPartAsync(
        Guid sessionId,
        int partIndex,
        long start,
        long end,
        long total,
        string sha256,
        Stream content,
        CancellationToken ct)
    {
        var session = await FindAsync(sessionId, ct);
        var effective = limits.For(OwnerKey);
        if (session.ExpiresAtUtc <= Now)
        {
            await DiscardAsync(session, ct);
            throw CalibreUploadException.Error(
                CalibreUploadException.Expired,
                "This upload expired. Choose the zip again to start over.");
        }

        return await store.ReceivePartAsync(
            session, partIndex, start, end, total, sha256, content, effective, Now, ct);
    }

    /// <summary>
    /// Verifies the whole file, safely extracts it and proves metadata.db is
    /// Calibre's. The resulting library is what the review reads.
    /// </summary>
    public async Task<CalibreUploadStatus> CompleteAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await FindAsync(sessionId, ct);
        if (session.ExpiresAtUtc <= Now)
        {
            await DiscardAsync(session, ct);
            throw CalibreUploadException.Error(
                CalibreUploadException.Expired,
                "This upload expired. Choose the zip again to start over.");
        }

        var effective = limits.For(OwnerKey);
        var staged = await store.StageAsync(session, effective, Now, ct);
        lock (_gate)
            _staged = staged;

        var updated = await store.FindAsync(sessionId, OwnerKey, ct)
            ?? throw CalibreUploadException.Error(CalibreUploadException.NotFound);
        return ToStatus(updated, effective);
    }

    public async Task DiscardAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await store.FindAsync(sessionId, OwnerKey, ct)
            ?? throw CalibreUploadException.Error(CalibreUploadException.NotFound);
        await DiscardAsync(session, ct);
    }

    /// <summary>Removes the staged library after its import has finished.</summary>
    public async Task DiscardCurrentAsync(CancellationToken ct)
    {
        var staged = await GetStagedLibraryAsync(ct);
        if (staged is not null)
        {
            var session = await store.FindAsync(staged.SessionId, OwnerKey, ct);
            if (session is not null)
                await DiscardAsync(session, ct);
        }

        ClearStaged();
    }

    /// <summary>Deletes every expired session and orphaned scratch directory.</summary>
    public async Task<int> ExpireAsync(CancellationToken ct)
    {
        var removed = await store.ExpireAsync(Now, ct);
        if (removed > 0)
            ClearStaged();

        return removed;
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private async Task<CalibreUploadManifest> FindAsync(Guid sessionId, CancellationToken ct) =>
        await store.FindAsync(sessionId, OwnerKey, ct)
        ?? throw CalibreUploadException.Error(
            CalibreUploadException.NotFound,
            "This upload is no longer available. Choose the zip again to start over.");

    private async Task DiscardAsync(CalibreUploadManifest session, CancellationToken ct)
    {
        await store.DiscardAsync(session, ct);
        lock (_gate)
        {
            if (_staged?.SessionId == session.Id)
                _staged = null;
        }
    }

    /// <summary>
    /// Loads the staged library from disk. A staged library survives a restart
    /// because it lives in scratch; the review simply asks for it again. The
    /// scan is a listing of one owner's few session directories.
    /// </summary>
    private async Task RefreshStagedAsync(CancellationToken ct)
    {
        var latest = await store.FindLatestStagedAsync(OwnerKey, ct);
        CalibreStagedLibrary? staged = null;
        if (latest is not null)
        {
            if (latest.ExpiresAtUtc <= Now)
            {
                await store.DiscardAsync(latest, ct);
            }
            else
            {
                staged = await store.ReadStagedAsync(latest, ct);
            }
        }

        lock (_gate)
            _staged = staged;
    }

    private void ClearStaged()
    {
        lock (_gate)
            _staged = null;
    }

    private static void ValidateCreate(CalibreUploadCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TotalBytes <= 0)
            throw CalibreUploadException.Error(
                CalibreUploadException.InvalidRequest, "The selected file is empty.");
        if (string.IsNullOrWhiteSpace(request.FileName))
            throw CalibreUploadException.Error(
                CalibreUploadException.InvalidRequest, "The selected file has no name.");
        if (request.Sha256 is not { Length: 64 }
            || !request.Sha256.All(Uri.IsHexDigit))
        {
            throw CalibreUploadException.Error(
                CalibreUploadException.InvalidRequest, "The file's SHA-256 is missing or malformed.");
        }
    }

    private static CalibreUploadStatus ToStatus(
        CalibreUploadManifest session,
        CalibreUploadLimits effective)
    {
        var parts = effective.PartCountFor(session.TotalBytes);
        var received = session.ReceivedParts
            .Where(index => index >= 0 && index < parts)
            .Distinct()
            .OrderBy(index => index)
            .ToList();
        var receivedBytes = received.Sum(index => (long)effective.PartLengthFor(session.TotalBytes, index));

        return new CalibreUploadStatus(
            session.Id,
            session.State,
            session.FileName,
            session.TotalBytes,
            effective.PartBytes,
            parts,
            received,
            receivedBytes,
            Utc(session.CreatedAtUtc),
            Utc(session.ExpiresAtUtc),
            effective.MaxLibraryBytes,
            effective.MaxExtractedBytes,
            effective.MaxFiles,
            session.Books,
            session.StagedBytes,
            session.FailureCode,
            session.FailureMessage);
    }

    private static DateTimeOffset Utc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private static string Bytes(long bytes) => bytes switch
    {
        >= 1024L * 1024L * 1024L => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
        >= 1024L * 1024L => $"{bytes / (1024.0 * 1024):0.#} MB",
        >= 1024L => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} bytes",
    };
}
