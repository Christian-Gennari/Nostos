using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Library;
using Nostos.Product.BookText;
using Nostos.Shared.Dtos;
using Nostos.Shared.Enums;

namespace Nostos.Backend.Services.ExternalMedia;

/// <summary>
/// Which candidates to import: every ready one except those left out, plus
/// any named by key. Naming is the only way a candidate that needs review
/// gets imported.
/// </summary>
public sealed record ExternalMediaImportSelection(
    bool AllReady,
    IReadOnlySet<string> Except,
    IReadOnlySet<string> Keys)
{
    public bool Includes(ExternalMediaScanItem item) =>
        Keys.Contains(item.Key)
        || (AllReady && item.Status == ExternalMediaScanItem.Ready && !Except.Contains(item.Key));
}

/// <summary>What happened to one candidate of an import.</summary>
public sealed record ExternalMediaImportOutcome(
    string Key,
    string Title,
    string Outcome,
    Guid? BookId,
    string? Message)
{
    public const string Imported = "imported";
    public const string AlreadyLinked = "already_linked";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
}

public sealed record ExternalMediaImportState(
    string Status,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    int Total,
    int Completed,
    string? Current,
    IReadOnlyList<ExternalMediaImportOutcome> Outcomes)
{
    public const string Idle = "idle";
    public const string Importing = "importing";
    public const string Done = "done";
    public const string Interrupted = "interrupted";
}

/// <summary>
/// Turns scanned candidates into library books that reference their files in
/// place. Runs one import at a time, in the background, so the page that
/// started it can be closed.
///
/// No media is copied: an import reads tags, measures tracks by streaming
/// them once, stores a cover if the source carries one, and records
/// references. Repeating an import is harmless, because a file can be
/// referenced by one book only and an already linked candidate is reported
/// as such instead of becoming a second book.
/// </summary>
public sealed class ExternalMediaImportService(
    ExternalMediaRoot root,
    ExternalMediaScanService scans,
    IExternalMediaDescriber describer,
    IBookAssetStorage storage,
    IDbContextFactory<NostosDbContext> contexts,
    IServiceScopeFactory scopes,
    ILogger<ExternalMediaImportService> logger,
    ILibraryMaintenanceCoordinator? maintenance = null)
{
    private static readonly string[] SidecarCovers = ["cover.jpg", "cover.jpeg", "cover.png", "folder.jpg", "folder.png"];
    private const long MaxSidecarCoverBytes = 20 * 1024 * 1024;

    private readonly Lock _gate = new();
    private ExternalMediaImportState _state = new(ExternalMediaImportState.Idle, null, null, 0, 0, null, []);
    private Task _running = Task.CompletedTask;

    public ExternalMediaImportState State
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    public Task Completion
    {
        get
        {
            lock (_gate)
                return _running;
        }
    }

    public enum StartResult
    {
        Started,
        AlreadyRunning,
        NoScan,
        NothingSelected,
    }

    /// <summary>
    /// Starts importing the chosen candidates of the last scan.
    /// </summary>
    public StartResult TryStart(ExternalMediaImportSelection selection, CancellationToken stopping)
    {
        lock (_gate)
        {
            if (_state.Status == ExternalMediaImportState.Importing)
                return StartResult.AlreadyRunning;

            var scan = scans.State;
            if (scan.Status != ExternalMediaScanState.Done || scan.Result is not { } result)
                return StartResult.NoScan;

            var selected = result.Items.Where(selection.Includes).ToList();
            if (selected.Count == 0)
                return StartResult.NothingSelected;

            _state = new ExternalMediaImportState(
                ExternalMediaImportState.Importing, DateTime.UtcNow, null, selected.Count, 0, null, []);
            _running = Task.Run(() => RunAsync(selected, result, stopping), CancellationToken.None);
            return StartResult.Started;
        }
    }

    private async Task RunAsync(
        IReadOnlyList<ExternalMediaScanItem> selected,
        ExternalMediaScanResult scan,
        CancellationToken ct)
    {
        var outcomes = new List<ExternalMediaImportOutcome>(selected.Count);
        var interrupted = false;

        foreach (var item in selected)
        {
            if (ct.IsCancellationRequested)
            {
                interrupted = true;
                break;
            }

            Update(state => state with { Current = item.Title });
            ExternalMediaImportOutcome outcome;
            try
            {
                // Held per book, not for the whole import: a restore or a
                // library switch waits for the book in hand, never for hours.
                await using var operation =
                    maintenance is null ? null : await maintenance.EnterOperationAsync(ct);
                outcome = await ImportAsync(item, scan.Candidates[item.Key], ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                interrupted = true;
                break;
            }
            catch (Exception ex)
            {
                // One damaged or vanished file must not stop the rest.
                logger.LogWarning(
                    "Importing an external media candidate failed; exception type {ExceptionType}. Details suppressed.",
                    ex.GetType().Name);
                outcome = Outcome(item, ExternalMediaImportOutcome.Failed, null, "This book could not be imported.");
            }

            outcomes.Add(outcome);
            var snapshot = outcomes.ToList();
            Update(state => state with { Completed = snapshot.Count, Outcomes = snapshot });
        }

        Update(state => state with
        {
            Status = interrupted ? ExternalMediaImportState.Interrupted : ExternalMediaImportState.Done,
            FinishedAt = DateTime.UtcNow,
            Current = null,
        });
    }

    private async Task<ExternalMediaImportOutcome> ImportAsync(
        ExternalMediaScanItem item,
        ExternalMediaCandidate candidate,
        CancellationToken ct)
    {
        if (item.Status == ExternalMediaScanItem.Unusable)
            return Outcome(item, ExternalMediaImportOutcome.Skipped, null, item.Concerns.LastOrDefault());

        // The scan may be minutes old: look at the files and the references
        // as they are now, not as they were.
        var files = new List<ExternalMediaFile>(candidate.Files.Count);
        foreach (var scanned in candidate.Files)
        {
            if (root.Find(root.RootId, scanned.RelativePath) is not { } current)
                return Outcome(item, ExternalMediaImportOutcome.Failed, null, "A file is no longer in the folder. Scan again.");
            files.Add(current);
        }

        var paths = files.Select(file => file.RelativePath).ToList();
        await using (var db = await contexts.CreateDbContextAsync(ct))
        {
            var linked = await db.ExternalMediaLinks.AsNoTracking()
                .Where(link => link.RootId == root.RootId && paths.Contains(link.RelativePath))
                .Select(link => link.BookId)
                .ToListAsync(ct);
            if (linked.Count > 0)
                return Outcome(item, ExternalMediaImportOutcome.AlreadyLinked, linked[0], null);
        }

        var first = describer.Describe(root.RootId, files[0].RelativePath);
        if (!first.Readable)
            return Outcome(item, ExternalMediaImportOutcome.Failed, null, "The file could not be read.");

        var media = candidate.Layout == ExternalMediaCandidate.Tracks
            ? await MeasureTracksAsync(files, first, ct)
            : SingleFile(candidate, files[0], first);
        if (media is null)
            return Outcome(item, ExternalMediaImportOutcome.Failed, null, "A track could not be read as audio.");

        var names = ExternalMediaNaming.For(candidate, first);
        using var scope = scopes.CreateScope();
        var library = scope.ServiceProvider.GetRequiredService<ILibraryService>();

        // ForceCreate: a title that is already in the library was shown for
        // review, and importing it adds a separate book rather than merging.
        // FileUploadExpected marks the row as waiting for its file, so a
        // crash before the next step leaves a visibly incomplete book.
        var created = await library.CreateOrMatchBookAsync(
            new LibraryCreateBookRequest(
                ClientId: "external-media",
                IdempotencyKey: $"external-{Guid.NewGuid():N}",
                Type: candidate.Kind,
                Title: names.Title,
                Author: names.Author,
                Narrator: candidate.Kind == ExternalMediaCandidate.Audiobook ? first.Narrator : null,
                Duration: media.Duration,
                Language: first.Language,
                ForceCreate: true,
                FileUploadExpected: true),
            strictConfirmation: false,
            ct);
        if (created.Data is not LibraryCreateOrMatchResultDto { BookId: { } bookId })
            return Outcome(item, ExternalMediaImportOutcome.Failed, null, created.Reply);

        try
        {
            var coverFileName = await StoreCoverAsync(bookId, candidate, first, ct);
            await using var db = await contexts.CreateDbContextAsync(ct);
            var book = await db.Books.SingleAsync(b => b.Id == bookId, ct);

            book.FileDetails.HasFile = true;
            book.FileDetails.FileName = media.FileName;
            book.FileDetails.TracksJson = media.Tracks is null ? null : BookTrackList.Serialize(media.Tracks);
            book.FileDetails.ChaptersJson = media.Chapters is { Count: > 0 }
                ? JsonSerializer.Serialize(media.Chapters.Select(c => new { c.Title, StartTime = c.StartSeconds }))
                : null;
            book.FileDetails.SetCover(coverFileName);
            book.Status = BookStatus.Ready;
            book.StatusMessage = null;

            for (var i = 0; i < files.Count; i++)
            {
                db.ExternalMediaLinks.Add(new ExternalMediaLinkModel
                {
                    BookId = bookId,
                    TrackNumber = media.Tracks is null ? ExternalMediaLinkModel.PrimaryFile : i + 1,
                    RootId = root.RootId,
                    RelativePath = files[i].RelativePath,
                    Length = files[i].Length,
                    LastModifiedUtc = files[i].LastModified.UtcDateTime,
                });
            }

            // File details and references in one save: a book never claims a
            // file it has no reference to, and never the reverse.
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Only what this import created is removed: the book row and a
            // cover Nostos stored. The source files are not involved.
            await library.DeleteBookAsync(bookId, CancellationToken.None);
            await storage.DeleteBookFilesAsync(bookId, CancellationToken.None);

            if (ex is DbUpdateException)
                return Outcome(item, ExternalMediaImportOutcome.AlreadyLinked, null, null);
            throw;
        }

        if (media.FileName is not null && candidate.Kind == ExternalMediaCandidate.Ebook)
        {
            var scheduler = scope.ServiceProvider.GetService<IBookTextIngestionScheduler>();
            if (scheduler is not null)
                await scheduler.ScheduleAsync(bookId, media.FileName, ct);
        }

        return Outcome(item, ExternalMediaImportOutcome.Imported, bookId, null);
    }

    private static ImportedMedia SingleFile(
        ExternalMediaCandidate candidate,
        ExternalMediaFile file,
        ExternalMediaDescription description)
    {
        var extension = Path.GetExtension(file.RelativePath).ToLowerInvariant();
        var isAudio = candidate.Kind == ExternalMediaCandidate.Audiobook;
        return new ImportedMedia(
            FileName: $"book{extension}",
            Tracks: null,
            Chapters: isAudio ? description.Chapters : null,
            Duration: isAudio ? FormatDuration(description.DurationMs) : null);
    }

    /// <summary>
    /// Measures every track: its duration from its tags, and its size and
    /// CRC-32 by reading it through once. The checksum is what lets the
    /// book's download archive be laid out without reading the tracks again.
    /// </summary>
    private async Task<ImportedMedia?> MeasureTracksAsync(
        IReadOnlyList<ExternalMediaFile> files,
        ExternalMediaDescription first,
        CancellationToken ct)
    {
        var tracks = new List<BookTrack>(files.Count);
        var chapters = new List<ExternalMediaChapter>(files.Count);
        long cursorMs = 0;

        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var description = i == 0 ? first : describer.Describe(root.RootId, file.RelativePath);
            if (!description.Readable)
                return null;

            await using var stream = root.OpenRead(root.RootId, file.RelativePath);
            if (stream is null)
                return null;
            var (crc, bytes) = await Crc32.ComputeAsync(stream, ct);

            var number = i + 1;
            var fileName = BookTrackFormats.CanonicalFileName(
                number, Path.GetExtension(file.RelativePath).ToLowerInvariant());
            var title = description.Title
                ?? Path.GetFileNameWithoutExtension(ExternalMediaGrouper.FileNameOf(file.RelativePath));

            tracks.Add(new BookTrack(
                number, fileName, MediaTypeMap.ForBookFile(fileName), description.DurationMs, bytes, crc, title));
            chapters.Add(new ExternalMediaChapter(title, cursorMs / 1000.0));
            cursorMs += description.DurationMs;
        }

        return BookTrackList.Validate(tracks) is null
            ? new ImportedMedia(null, tracks, chapters, FormatDuration(cursorMs))
            : null;
    }

    /// <summary>
    /// Copies artwork into Nostos's own storage: a cover is Nostos-owned data
    /// like notes and progress, so removing the book later removes it without
    /// going near the source folder.
    /// </summary>
    private async Task<string?> StoreCoverAsync(
        Guid bookId,
        ExternalMediaCandidate candidate,
        ExternalMediaDescription first,
        CancellationToken ct)
    {
        try
        {
            if (candidate.Layout == ExternalMediaCandidate.Tracks && candidate.Location.Length > 0)
            {
                foreach (var name in SidecarCovers)
                {
                    var path = candidate.Location + "/" + name;
                    if (root.Find(root.RootId, path) is not { Length: > 0 and <= MaxSidecarCoverBytes })
                        continue;

                    await using var sidecar = root.OpenRead(root.RootId, path);
                    if (sidecar is null)
                        continue;

                    await storage.SaveBookCoverAsync(bookId, sidecar, name, ct);
                    return "cover" + Path.GetExtension(name);
                }
            }

            if (first.Cover is { } embedded)
            {
                await using var content = new MemoryStream(embedded.Bytes, writable: false);
                await storage.SaveBookCoverAsync(bookId, content, "cover" + embedded.Extension, ct);
                return "cover" + embedded.Extension;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            // A book without artwork is still a book.
        }

        return null;
    }

    private static string? FormatDuration(long durationMs)
    {
        if (durationMs <= 0)
            return null;

        var duration = TimeSpan.FromMilliseconds(durationMs);
        return $"{(int)duration.TotalHours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}";
    }

    private static ExternalMediaImportOutcome Outcome(
        ExternalMediaScanItem item, string outcome, Guid? bookId, string? message) =>
        new(item.Key, item.Title, outcome, bookId, message);

    private void Update(Func<ExternalMediaImportState, ExternalMediaImportState> change)
    {
        lock (_gate)
            _state = change(_state);
    }

    private sealed record ImportedMedia(
        string? FileName,
        IReadOnlyList<BookTrack>? Tracks,
        IReadOnlyList<ExternalMediaChapter>? Chapters,
        string? Duration);
}
