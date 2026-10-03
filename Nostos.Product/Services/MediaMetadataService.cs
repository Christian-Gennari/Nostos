using System.Text.Json;
using ATL; // Library for Audio/Metadata extraction
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Services;

public class MediaMetadataService
{
    private readonly ILogger<MediaMetadataService> _logger;

    public MediaMetadataService(ILogger<MediaMetadataService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Extracts chapters and duration from the file and updates the BookModel.
    /// Returns true if metadata was successfully updated.
    /// </summary>
    public bool EnrichBookMetadata(BookModel book, string filePath)
    {
        if (!File.Exists(filePath))
        {
            _logger.LogWarning("File not found for metadata extraction: {FilePath}", filePath);
            return false;
        }

        try
        {
            return ApplyMetadata(book, new Track(filePath));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Metadata extraction failed for book {BookId}; exception type {ExceptionType}. Details suppressed.",
                book.Id,
                ex.GetType().Name);
            // We return false but don't throw, so the upload itself doesn't fail
            return false;
        }
    }

    /// <summary>
    /// Stream-based metadata extraction for provider-neutral storage. ATL can
    /// inspect audio directly from a stream, so remote uploads do not need a
    /// durable local file merely to discover chapters/duration.
    /// </summary>
    public bool EnrichBookMetadata(BookModel book, Stream content)
    {
        try
        {
            if (content.CanSeek)
                content.Position = 0;

            return ApplyMetadata(book, new Track(content));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Metadata extraction failed for book {BookId}; exception type {ExceptionType}. Details suppressed.",
                book.Id,
                ex.GetType().Name);
            return false;
        }
    }

    private static bool ApplyMetadata(BookModel book, Track track)
    {
        if (track.Chapters != null && track.Chapters.Count > 0)
        {
            var chapters = track
                .Chapters.Select(c => new
                {
                    Title = c.Title,
                    StartTime = c.StartTime / 1000.0,
                })
                .ToList();

            book.FileDetails.ChaptersJson = JsonSerializer.Serialize(chapters);
        }

        if (
            book is AudioBookModel audioBook
            && string.IsNullOrEmpty(audioBook.Duration)
            && track.Duration > 0
        )
        {
            var duration = TimeSpan.FromSeconds(track.Duration);
            audioBook.Duration = duration.ToString(@"hh\:mm\:ss");
        }

        return true;
    }
}
