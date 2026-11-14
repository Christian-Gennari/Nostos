using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Providers.Acquisition.Media;
using Nostos.Backend.Services.ExternalMedia;
using Nostos.Product.Storage;
using Nostos.Shared.Enums;

namespace Nostos.Backend.Services.BookUpload;

/// <summary>What became of a set of uploaded audio files.</summary>
public sealed record BookTrackUploadResult(string? Error, int Tracks = 0)
{
    public bool Ok => Error is null;
}

/// <summary>
/// Turns several uploaded audio files into the tracks of ONE audiobook.
///
/// The files are put in the order a person reads their names (numbers as
/// numbers, the rule External media uses), staged, measured, and only then
/// stored, so a file that is not audio leaves the book as it was. The caller
/// owns the <see cref="BookFileMutationGate"/> lease and the book row load.
/// </summary>
public sealed class BookTrackUpload(
    IBookRepository repo,
    IBookAssetStorage assets,
    IAudioDurationProbe durationProbe,
    IBookTrackStorage? trackStorage = null,
    IProductScratchStorage? scratchStorage = null)
{
    // A host that names no scratch root (Nostos-Cloud today) keeps the process
    // temp path this upload used before the seam existed (issue #1596).
    private readonly IProductScratchStorage _scratch = scratchStorage ?? ProductScratchStorage.TempPath;

    public async Task<BookTrackUploadResult> CommitAsync(
        BookModel book,
        IReadOnlyList<IFormFile> files,
        CancellationToken ct)
    {
        if (trackStorage is not { } tracks)
            return new("This server cannot store a book as several audio files.");
        if (book is not AudioBookModel audioBook)
            return new("Only an audiobook can be made from several audio files.");
        if (files.Count == 0)
            return new("Missing files.");
        if (files.Count > BookTrackList.MaxTracks)
            return new($"A book can hold at most {BookTrackList.MaxTracks} tracks.");

        var ordered = files
            .OrderBy(file => Path.GetFileName(file.FileName), Comparer<string>.Create(NaturalOrder.Compare))
            .ToList();
        foreach (var file in ordered)
        {
            if (!BookTrackFormats.TrackExtensions.Contains(Path.GetExtension(file.FileName)))
                return new("Several files can only be audio files (MP3, M4A or M4B).");
        }

        var staging = _scratch.CreateDirectory("nostos-tracks");
        try
        {
            var measured = new List<(string Path, BookTrack Track)>(ordered.Count);
            for (var i = 0; i < ordered.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var number = i + 1;
                var extension = Path.GetExtension(ordered[i].FileName).ToLowerInvariant();
                var staged = Path.Combine(staging, $"{number:D4}{extension}");
                await using (var target = new FileStream(
                    staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await using var source = ordered[i].OpenReadStream();
                    await source.CopyToAsync(target, ct);
                }

                var duration = durationProbe.ProbeDuration(staged);
                if (duration is null || duration.Value <= TimeSpan.Zero)
                    return new($"File {number} of {ordered.Count} is not playable audio.");

                uint crc;
                long bytes;
                await using (var stream = new FileStream(
                    staged, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
                {
                    (crc, bytes) = await Crc32.ComputeAsync(stream, ct);
                }

                var fileName = BookTrackFormats.CanonicalFileName(number, extension);
                measured.Add((staged, new BookTrack(
                    number,
                    fileName,
                    MediaTypeMap.ForBookFile(fileName),
                    (long)Math.Round(duration.Value.TotalMilliseconds),
                    bytes,
                    crc,
                    Path.GetFileNameWithoutExtension(ordered[i].FileName).Trim())));
            }

            var list = measured.Select(m => m.Track).ToList();
            if (BookTrackList.Validate(list) is { } invalid)
                return new(invalid);

            // A book holds either one primary file or a track list: what it
            // held before is replaced, as a single-file upload replaces it.
            await tracks.DeleteTracksAsync(book.Id, ct);
            await assets.DeleteBookFileAsync(book.Id, ct);
            try
            {
                foreach (var (path, track) in measured)
                    await tracks.AdoptTrackAsync(book.Id, track.Number, path, track.FileName, ct);
            }
            catch
            {
                await tracks.DeleteTracksAsync(book.Id, CancellationToken.None);
                throw;
            }

            long cursorMs = 0;
            var chapters = new List<object>(list.Count);
            foreach (var track in list)
            {
                chapters.Add(new { Title = track.Title ?? $"Section {track.Number}", StartTime = cursorMs / 1000.0 });
                cursorMs += track.DurationMs;
            }

            book.FileDetails.HasFile = true;
            book.FileDetails.FileName = null;
            book.FileDetails.TracksJson = BookTrackList.Serialize(list);
            book.FileDetails.ChaptersJson = JsonSerializer.Serialize(chapters);
            book.FileDetails.LocationsJson = null;
            var total = TimeSpan.FromMilliseconds(cursorMs);
            audioBook.Duration = $"{(int)total.TotalHours:D2}:{total.Minutes:D2}:{total.Seconds:D2}";
            if (book.Status == BookStatus.UploadPending)
            {
                book.Status = BookStatus.Ready;
                book.StatusMessage = null;
            }

            await repo.UpdateAsync(book);
            return new(null, list.Count);
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch (IOException) { }
        }
    }
}
