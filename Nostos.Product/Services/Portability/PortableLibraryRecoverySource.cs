using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Services.Portability;

/// <summary>
/// One primary (book or cover) media item of a retained recovery copy as
/// discovered under the retained media root. Only the classification facts are
/// carried; the relative path never leaves the host.
/// </summary>
public sealed record PortableRecoveryMedia(
    Guid BookId,
    string Kind,
    string FileName,
    long Length,
    string Sha256);

/// <summary>
/// The expected portable relational state extracted from a retained recovery
/// database, bound to the byte length and SHA-256 of the exact serialized
/// payload that was read. <see cref="Data"/> is deliberately internal: hosts
/// consume the payload through <see cref="PortableLibraryRecoverySource"/> and
/// verify it through
/// <see cref="PortableLibraryVerifier.VerifyDatabaseAgainstExpectedAsync(NostosDbContext, PortableRecoveryPayload, CancellationToken)"/>.
/// </summary>
public sealed class PortableRecoveryPayload
{
    internal PortableRecoveryPayload(
        PortableLibraryData data,
        long dataBytes,
        string dataSha256,
        MigrationArchiveCounts counts,
        IReadOnlyList<PortableArchiveMediaEntry> primaryMedia)
    {
        Data = data;
        DataBytes = dataBytes;
        DataSha256 = dataSha256;
        Counts = counts;
        PrimaryMedia = primaryMedia;
    }

    internal PortableLibraryData Data { get; }

    public long DataBytes { get; }

    public string DataSha256 { get; }

    public MigrationArchiveCounts Counts { get; }

    /// <summary>Canonical book/cover descriptors in the archive layout.</summary>
    public IReadOnlyList<PortableArchiveMediaEntry> PrimaryMedia { get; }
}

/// <summary>
/// Extracts the portable relational state of a migrated recovery database into
/// the same camelCase JSON payload the portable archive uses, so a restore
/// materializes the previous library through
/// <see cref="PortableLibraryRelationalRestore"/> — the identical code path a
/// normal import uses. The caller owns the database schema level (migrate a
/// working copy forward first) and the destination stream; the retained
/// original is never opened for writing.
/// </summary>
/// <remarks>
/// Media presence flags and filenames are derived from the verified retained
/// media descriptors, never from the recovery database's own file-details
/// columns, so the materialized candidate always describes media that actually
/// exists in the retained copy.
/// </remarks>
public static class PortableLibraryRecoverySource
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static async Task<PortableRecoveryPayload> WriteRelationalPayloadAsync(
        NostosDbContext db,
        Stream destination,
        IReadOnlyList<PortableRecoveryMedia> retainedMedia,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(retainedMedia);

        var data = await ReadPortableDataAsync(db, retainedMedia, ct).ConfigureAwait(false);
        var primary = BuildPrimaryMedia(retainedMedia);
        var hashing = new HashingWriteStream(destination);
        await JsonSerializer.SerializeAsync(hashing, data, JsonOptions, ct).ConfigureAwait(false);
        await hashing.FlushAsync(ct).ConfigureAwait(false);
        var counts = PortableLibraryCounts.ComputeCounts(data, primary.Count);
        return new PortableRecoveryPayload(data, hashing.BytesWritten, hashing.Sha256, counts, primary);
    }

    private static async Task<PortableLibraryData> ReadPortableDataAsync(
        NostosDbContext db,
        IReadOnlyList<PortableRecoveryMedia> retainedMedia,
        CancellationToken ct)
    {
        var booksWithFile = retainedMedia
            .Where(item => string.Equals(item.Kind, PortableArchiveFormat.BookMediaKind, StringComparison.Ordinal))
            .Select(item => item.BookId)
            .ToHashSet();
        var booksWithCover = retainedMedia
            .Where(item => string.Equals(item.Kind, PortableArchiveFormat.CoverMediaKind, StringComparison.Ordinal))
            .Select(item => item.BookId)
            .ToHashSet();

        var works = (await db.Works.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct).ConfigureAwait(false))
            .Select(x => new PortableWork(x.Id, x.Title, x.Author, x.CreatedAt))
            .ToList();

        var retainedTracks = retainedMedia
            .Where(item => string.Equals(item.Kind, PortableArchiveFormat.TrackMediaKind, StringComparison.Ordinal))
            .GroupBy(item => item.BookId)
            .ToDictionary(
                group => group.Key,
                group => group.ToDictionary(item => item.FileName, item => item.Length, StringComparer.Ordinal));

        var books = (await db.Books.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct).ConfigureAwait(false))
            .Select(x => ToPortableBook(
                x,
                booksWithFile.Contains(x.Id),
                booksWithCover.Contains(x.Id),
                RetainedTracksJson(x, retainedTracks.GetValueOrDefault(x.Id))))
            .ToList();

        var collections = (await db.Collections.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct).ConfigureAwait(false))
            .Select(x => new PortableCollection(x.Id, x.Name, x.ParentId))
            .ToList();

        var bookCollections = (await db.BookCollections.AsNoTracking()
                .OrderBy(x => x.BookId).ThenBy(x => x.CollectionId).ToListAsync(ct).ConfigureAwait(false))
            .Select(x => new PortableBookCollection(x.BookId, x.CollectionId, x.AddedAt))
            .ToList();

        var notes = (await db.Notes.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct).ConfigureAwait(false))
            .Select(x => new PortableNote(
                x.Id, x.Content, x.CfiRange, x.SelectedText, x.CreatedAt, x.BookId, x.RawContent,
                x.CaptureSource, x.ProcessingMode, x.SourceAnchorKind, x.SourceAnchorValue, x.AnchorVerified))
            .ToList();

        var topics = (await db.Topics.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct).ConfigureAwait(false))
            .Select(x => new PortableTopic(x.Id, x.Topic))
            .ToList();

        var noteTopics = (await db.NoteTopics.AsNoTracking()
                .OrderBy(x => x.NoteId).ThenBy(x => x.TopicId).ToListAsync(ct).ConfigureAwait(false))
            .Select(x => new PortableNoteTopic(x.NoteId, x.TopicId))
            .ToList();

        var writings = (await db.Writings.AsNoTracking().OrderBy(x => x.Id).ToListAsync(ct).ConfigureAwait(false))
            .Select(x => new PortableWriting(
                x.Id, x.Name, x.Type.ToString(), x.Content, x.ParentId, x.CreatedAt, x.UpdatedAt))
            .ToList();

        var acquisitions = (await db.BookAcquisitions.AsNoTracking()
                .OrderBy(x => x.Id).ToListAsync(ct).ConfigureAwait(false))
            .Select(x => new PortableBookAcquisition(
                x.Id, x.BookId, x.ProviderId, x.ProviderDisplayName, x.ExternalId, x.AssetId,
                x.AssetFormat, x.ImportedExtension, x.SourceUrl, x.RightsStatement, x.AcquiredAt))
            .ToList();

        var writingNotes = (await db.WritingNotes.AsNoTracking()
                .OrderBy(x => x.WritingId).ThenBy(x => x.NoteId).ToListAsync(ct).ConfigureAwait(false))
            .Select(x => new PortableWritingNote(x.WritingId, x.NoteId, x.AddedAt))
            .ToList();

        var noteImportBookLinks = (await db.NoteImportBookLinks.AsNoTracking()
                .OrderBy(x => x.Id).ToListAsync(ct).ConfigureAwait(false))
            .Select(x => new PortableNoteImportBookLink(x.Id, x.Source, x.SourceKey, x.BookId, x.CreatedAtUtc))
            .ToList();

        var assistant = await db.AssistantSettings.AsNoTracking().SingleOrDefaultAsync(ct).ConfigureAwait(false);

        return new PortableLibraryData(
            PortableArchiveFormat.DataVersion,
            works,
            books,
            collections,
            bookCollections,
            notes,
            topics,
            noteTopics,
            writings,
            acquisitions,
            assistant is null
                ? null
                : new PortableAssistantSettings(assistant.CaptureProcessingMode, assistant.UpdatedAtUtc),
            writingNotes,
            noteImportBookLinks);
    }

    private static List<PortableArchiveMediaEntry> BuildPrimaryMedia(
        IReadOnlyList<PortableRecoveryMedia> retainedMedia)
    {
        var primary = new List<PortableArchiveMediaEntry>();
        foreach (var item in retainedMedia)
        {
            if (!PortableArchiveFormat.IsKnownMediaKind(item.Kind)
                || PortableArchiveFormat.CanonicalMediaFileName(item.Kind, item.FileName) is not { } canonicalFileName)
            {
                continue;
            }

            primary.Add(new PortableArchiveMediaEntry(
                item.BookId,
                item.Kind,
                PortableArchiveFormat.MediaPath(item.BookId, canonicalFileName),
                item.FileName,
                string.Empty,
                item.Length,
                item.Sha256));
        }

        return primary;
    }

    /// <summary>
    /// A book's track list, kept only when the retained copy really holds every
    /// track it names at the recorded size. Like the other media flags, this
    /// describes the retained media rather than trusting the database's claim.
    /// </summary>
    private static string? RetainedTracksJson(BookModel book, Dictionary<string, long>? retained)
    {
        var tracks = BookTrackList.Parse(book.FileDetails.TracksJson);
        if (tracks.Count == 0 || retained is null || retained.Count != tracks.Count)
            return null;

        return tracks.All(track => retained.TryGetValue(track.FileName, out var length) && length == track.Bytes)
            ? book.FileDetails.TracksJson
            : null;
    }

    private static PortableBook ToPortableBook(BookModel book, bool hasBookFile, bool hasCover, string? tracksJson)
    {
        var type = book switch
        {
            PhysicalBookModel => "physical",
            EBookModel => "ebook",
            AudioBookModel => "audiobook",
            _ => throw new PortableArchiveException(
                "unsupported_book_type",
                $"Recovery book {book.Id} has an unsupported concrete type."),
        };

        var isbn = book switch
        {
            PhysicalBookModel physical => physical.Isbn,
            EBookModel ebook => ebook.Isbn,
            _ => null,
        };
        var pageCount = book switch
        {
            PhysicalBookModel physical => physical.PageCount,
            EBookModel ebook => ebook.PageCount,
            _ => null,
        };
        var audio = book as AudioBookModel;

        return new PortableBook(
            book.Id,
            book.WorkId,
            type,
            book.Status.ToString(),
            book.StatusMessage,
            book.Title,
            book.Author,
            new PortableBookMetadata(
                book.Metadata.Subtitle,
                book.Metadata.Description,
                book.Metadata.Editor,
                book.Metadata.Translator,
                book.Metadata.Publisher,
                book.Metadata.PlaceOfPublication,
                book.Metadata.PublishedDate,
                book.Metadata.Language,
                book.Metadata.Categories,
                book.Metadata.Edition,
                book.Metadata.Series,
                book.Metadata.VolumeNumber),
            new PortableReadingProgress(
                book.Progress.LastLocation,
                book.Progress.ProgressPercent,
                book.Progress.Rating,
                book.Progress.IsFavorite,
                book.Progress.PersonalReview,
                book.Progress.LastReadAt,
                book.Progress.FinishedAt),
            book.CreatedAt,
            isbn,
            pageCount,
            audio?.Asin,
            audio?.Duration,
            audio?.Narrator,
            book.FileDetails.ChaptersJson,
            hasBookFile,
            hasCover,
            tracksJson);
    }

    /// <summary>
    /// Bounded-memory write wrapper that computes the SHA-256 of everything the
    /// serializer writes without buffering the payload.
    /// </summary>
    private sealed class HashingWriteStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private string? _sha256;

        public long BytesWritten { get; private set; }

        public string Sha256 => _sha256 ?? throw new InvalidOperationException("The payload digest is not final yet.");

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            inner.Flush();
            _sha256 ??= Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
            _sha256 ??= Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _hash.AppendData(buffer, offset, count);
            inner.Write(buffer, offset, count);
            BytesWritten += count;
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            _hash.AppendData(buffer.Span);
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            BytesWritten += buffer.Length;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
