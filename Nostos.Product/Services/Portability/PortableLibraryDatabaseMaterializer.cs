using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Library;
using Nostos.Shared.Enums;

namespace Nostos.Backend.Services.Portability;

/// <summary>
/// Shared relational restore for the portable payload. The immediate
/// compatibility import and the activation candidate database builder both use
/// these methods so a candidate database is materialized by exactly the same
/// code path as a normal import; behaviour is unchanged for the compatibility
/// endpoint.
/// </summary>
/// <remarks>
/// <see cref="ApplyRelationalDataAsync"/> only adds the validated portable
/// entities to the supplied context. The caller owns the transaction, the
/// <c>SaveChangesAsync</c> call and every post-apply verification.
/// </remarks>
internal static class PortableLibraryDatabaseMaterializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    // The staged relational payload was already validated by the reader; this
    // deserializes the same verified bytes for the relational restore.
    internal static async Task<PortableLibraryData> ReadRelationalDataAsync(
        IPortableImportStaging staging,
        PortableStagingId stagingId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(staging);
        await using var staged = await staging
            .OpenDataReadAsync(stagingId, cancellationToken);
        return await ReadRelationalDataAsync(staged, cancellationToken);
    }

    /// <summary>
    /// Deserializes a relational payload stream; the candidate builder calls this
    /// for the exact bytes it just hash-verified.
    /// </summary>
    internal static async Task<PortableLibraryData> ReadRelationalDataAsync(
        Stream source,
        CancellationToken cancellationToken)
    {
        try
        {
            return await JsonSerializer
                .DeserializeAsync<PortableLibraryData>(source, JsonOptions, cancellationToken)
                ?? throw new PortableArchiveException(
                    "malformed_data",
                    "Portable archive relational payload is malformed.");
        }
        catch (JsonException exception)
        {
            throw new PortableArchiveException(
                "malformed_data",
                "Portable archive relational payload is malformed.",
                exception);
        }
    }

    /// <summary>
    /// Adds the validated portable user-owned entities to <paramref name="db"/>.
    /// Reconstructible cache columns (for example epub.js locations) are left
    /// empty because they are regenerated after activation.
    /// </summary>
    internal static async Task ApplyRelationalDataAsync(
        NostosDbContext db,
        PortableLibraryData data,
        IReadOnlyList<PortablePreparedMedia> stagedMedia,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(stagedMedia);

        var mediaByKey = stagedMedia.ToDictionary(
            x => (x.Descriptor.BookId, x.Descriptor.Kind));

        var works = data.Works.ToDictionary(
            x => x.Id,
            x => new WorkModel
            {
                Id = x.Id,
                Title = x.Title,
                Author = x.Author,
                NormalizedTitle = BookIdentityNormalizer.NormalizeTitle(x.Title),
                NormalizedAuthor = BookIdentityNormalizer.NormalizeAuthor(x.Author),
                CreatedAt = x.CreatedAt,
            });
        db.Works.AddRange(works.Values);

        var collections = data.Collections.ToDictionary(
            x => x.Id,
            x => new CollectionModel
            {
                Id = x.Id,
                Name = x.Name,
                ParentId = x.ParentId,
            });
        foreach (var source in data.Collections)
        {
            if (source.ParentId is { } parentId)
                collections[source.Id].Parent = collections[parentId];
        }
        db.Collections.AddRange(collections.Values);

        var writings = data.Writings.ToDictionary(
            x => x.Id,
            x => new WritingModel
            {
                Id = x.Id,
                Name = x.Name,
                Type = Enum.Parse<WritingType>(x.Type, ignoreCase: true),
                Content = x.Content,
                ParentId = x.ParentId,
                CreatedAt = x.CreatedAt,
                UpdatedAt = x.UpdatedAt,
            });
        foreach (var source in data.Writings)
        {
            if (source.ParentId is { } parentId)
                writings[source.Id].Parent = writings[parentId];
        }
        db.Writings.AddRange(writings.Values);

        var books = new Dictionary<Guid, BookModel>();
        foreach (var source in data.Books)
        {
            var bookMedia = mediaByKey.GetValueOrDefault((
                source.Id,
                PortableArchiveFormat.BookMediaKind));
            var coverMedia = mediaByKey.GetValueOrDefault((
                source.Id,
                PortableArchiveFormat.CoverMediaKind));

            BookModel book = source.Type switch
            {
                "physical" => new PhysicalBookModel
                {
                    Isbn = source.Isbn,
                    PageCount = source.PageCount,
                },
                "ebook" => new EBookModel
                {
                    Isbn = source.Isbn,
                    PageCount = source.PageCount,
                },
                "audiobook" => new AudioBookModel
                {
                    Asin = source.Asin,
                    Duration = source.Duration,
                    Narrator = source.Narrator,
                },
                _ => throw new PortableArchiveException(
                    "unsupported_book_type",
                    $"Book {source.Id} has unsupported type '{source.Type}'."),
            };

            book.Id = source.Id;
            book.WorkId = source.WorkId;
            book.Work = works[source.WorkId];
            book.Status = Enum.Parse<BookStatus>(source.Status, ignoreCase: true);
            book.StatusMessage = source.StatusMessage;
            book.Title = source.Title;
            book.Author = source.Author;
            book.Metadata = new BookMetadata
            {
                Subtitle = source.Metadata.Subtitle,
                Description = source.Metadata.Description,
                Editor = source.Metadata.Editor,
                Translator = source.Metadata.Translator,
                Publisher = source.Metadata.Publisher,
                PlaceOfPublication = source.Metadata.PlaceOfPublication,
                PublishedDate = source.Metadata.PublishedDate,
                Language = source.Metadata.Language,
                Categories = source.Metadata.Categories,
                Edition = source.Metadata.Edition,
                Series = source.Metadata.Series,
                VolumeNumber = source.Metadata.VolumeNumber,
            };
            book.Progress = new ReadingProgress
            {
                LastLocation = source.Progress.LastLocation,
                ProgressPercent = source.Progress.ProgressPercent,
                Rating = source.Progress.Rating,
                IsFavorite = source.Progress.IsFavorite,
                PersonalReview = source.Progress.PersonalReview,
                LastReadAt = source.Progress.LastReadAt,
                FinishedAt = source.Progress.FinishedAt,
            };
            book.FileDetails = new FileInfoDetails
            {
                HasFile = bookMedia is not null,
                FileName = bookMedia?.Descriptor.FileName,
                CoverFileName = coverMedia?.Descriptor.FileName,
                // Chapter metadata is portable and retained because there is
                // no lazy server-side re-extraction path today. epub.js
                // locations are a client-generated cache and are rebuilt.
                ChaptersJson = source.ChaptersJson,
                LocationsJson = null,
            };
            book.CreatedAt = source.CreatedAt;
            book.NormalizedIsbn = source.Type is "physical" or "ebook"
                ? BookIdentityNormalizer.NormalizeIsbn(source.Isbn)
                : null;
            book.NormalizedAsin = source.Type == "audiobook"
                ? BookIdentityNormalizer.NormalizeAsin(source.Asin)
                : null;

            books.Add(book.Id, book);
        }
        db.Books.AddRange(books.Values);

        foreach (var source in data.BookCollections)
        {
            db.BookCollections.Add(new BookCollectionModel
            {
                BookId = source.BookId,
                Book = books[source.BookId],
                CollectionId = source.CollectionId,
                Collection = collections[source.CollectionId],
                AddedAt = source.AddedAt,
            });
        }

        var notes = data.Notes.ToDictionary(
            x => x.Id,
            x => new NoteModel
            {
                Id = x.Id,
                Content = x.Content,
                CfiRange = x.CfiRange,
                SelectedText = x.SelectedText,
                CreatedAt = x.CreatedAt,
                BookId = x.BookId,
                Book = books[x.BookId],
                RawContent = x.RawContent,
                CaptureSource = x.CaptureSource,
                ProcessingMode = x.ProcessingMode,
                SourceAnchorKind = x.SourceAnchorKind,
                SourceAnchorValue = x.SourceAnchorValue,
                AnchorVerified = x.AnchorVerified,
            });
        db.Notes.AddRange(notes.Values);

        var topics = data.Topics.ToDictionary(
            x => x.Id,
            x => new TopicModel
            {
                Id = x.Id,
                Topic = x.Topic,
            });
        db.Topics.AddRange(topics.Values);

        foreach (var source in data.NoteTopics)
        {
            db.NoteTopics.Add(new NoteTopicModel
            {
                NoteId = source.NoteId,
                Note = notes[source.NoteId],
                TopicId = source.TopicId,
                Topic = topics[source.TopicId],
            });
        }

        if (data.WritingNotes is not null)
        {
            foreach (var source in data.WritingNotes)
            {
                db.WritingNotes.Add(new WritingNoteModel
                {
                    WritingId = source.WritingId,
                    Writing = writings[source.WritingId],
                    NoteId = source.NoteId,
                    Note = notes[source.NoteId],
                    AddedAt = source.AddedAt,
                });
            }
        }

        if (data.NoteImportBookLinks is not null)
        {
            foreach (var source in data.NoteImportBookLinks)
            {
                db.NoteImportBookLinks.Add(new NoteImportBookLink
                {
                    Id = source.Id,
                    Source = source.Source,
                    SourceKey = source.SourceKey,
                    BookId = source.BookId,
                    Book = books[source.BookId],
                    CreatedAtUtc = source.CreatedAtUtc,
                });
            }
        }

        foreach (var source in data.BookAcquisitions)
        {
            db.BookAcquisitions.Add(new BookAcquisitionModel
            {
                Id = source.Id,
                BookId = source.BookId,
                Book = books[source.BookId],
                ProviderId = source.ProviderId,
                ProviderDisplayName = source.ProviderDisplayName,
                ExternalId = source.ExternalId,
                AssetId = source.AssetId,
                AssetFormat = source.AssetFormat,
                ImportedExtension = source.ImportedExtension,
                SourceUrl = source.SourceUrl,
                RightsStatement = source.RightsStatement,
                AcquiredAt = source.AcquiredAt,
            });
        }

        if (data.AssistantSettings is { } assistant)
        {
            var existing = await db.AssistantSettings
                .SingleOrDefaultAsync(x => x.Id == AssistantSettingsModel.SingletonId, ct);
            if (existing is null)
            {
                existing = new AssistantSettingsModel
                {
                    Id = AssistantSettingsModel.SingletonId,
                };
                db.AssistantSettings.Add(existing);
            }

            existing.CaptureProcessingMode = assistant.CaptureProcessingMode;
            existing.UpdatedAtUtc = assistant.UpdatedAtUtc;
        }
    }
}

/// <summary>
/// Narrow public seam for hosts that must materialize a hash-verified portable
/// relational payload through the exact same restore as the compatibility import
/// endpoint. The SelfHosted activation candidate builder uses this instead of a
/// friend-assembly grant, so no Nostos.Product internals are exposed.
/// </summary>
public static class PortableLibraryRelationalRestore
{
    /// <summary>
    /// Deserializes and applies one already hash-verified relational payload
    /// stream. The caller owns the transaction and the <c>SaveChangesAsync</c>
    /// call, exactly like the compatibility import path.
    /// </summary>
    public static async Task ApplyVerifiedPayloadAsync(
        NostosDbContext db,
        Stream verifiedRelationalPayload,
        IReadOnlyList<PortablePreparedMedia> stagedMedia,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(verifiedRelationalPayload);
        ArgumentNullException.ThrowIfNull(stagedMedia);

        var data = await PortableLibraryDatabaseMaterializer
            .ReadRelationalDataAsync(verifiedRelationalPayload, cancellationToken);
        await PortableLibraryDatabaseMaterializer
            .ApplyRelationalDataAsync(db, data, stagedMedia, cancellationToken);
    }
}
