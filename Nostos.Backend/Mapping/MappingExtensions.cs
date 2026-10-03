using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nostos.Backend.Data.Models;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Mapping;

public static class MappingExtensions
{
    private static readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(builder =>
        builder.AddConsole()
    );
    private static readonly ILogger _logger = _loggerFactory.CreateLogger("MappingExtensions");

    // ------------------------------
    // Read mappings (Model → DTO)
    // ------------------------------
    public static BookDto ToDto(this BookModel model)
    {
        // 1. Access FileDetails for file-related info
        string? coverUrl = model.FileDetails.CoverFileName is null
            ? null
            : $"/api/books/{model.Id}/cover";

        // Default values for polymorphic fields
        string type = "physical";
        string? isbn = null;
        string? asin = null;
        string? duration = null;
        int? pageCount = null;
        string? narrator = null;

        // Pattern Matching: Extract fields based on the specific subclass
        switch (model)
        {
            case PhysicalBookModel p:
                type = "physical";
                isbn = p.Isbn;
                pageCount = p.PageCount;
                break;
            case EBookModel e:
                type = "ebook";
                isbn = e.Isbn;
                pageCount = e.PageCount;
                break;
            case AudioBookModel a:
                type = "audiobook";
                asin = a.Asin;
                duration = a.Duration;
                narrator = a.Narrator;
                break;
        }

        // Deserialize Chapters from JSON (stored in FileDetails)
        IEnumerable<BookChapterDto>? chapters = null;
        if (!string.IsNullOrWhiteSpace(model.FileDetails.ChaptersJson))
        {
            try
            {
                chapters = JsonSerializer.Deserialize<List<BookChapterDto>>(
                    model.FileDetails.ChaptersJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                );
            }
            catch (JsonException ex)
            {
                chapters = null;
                _logger.LogWarning(
                    ex,
                    "Failed to deserialize chapters JSON for book {BookId}",
                    model.Id
                );
            }
        }

        var workBooks = model.Work?.Books?.ToList();
        if (workBooks is not null && workBooks.All(b => b.Id != model.Id))
            workBooks.Add(model);

        var otherEditions = workBooks is null
            ? null
            : workBooks
                .Where(b => b.Id != model.Id)
                .Select(ToEditionSummary)
                .ToList();

        // Use Named Arguments to prevent argument order mismatches
        return new BookDto(
            Id: model.Id,
            Title: model.Title,
            Author: model.Author, // Root Property
            // --- MAPPED FROM METADATA ---
            Subtitle: model.Metadata.Subtitle,
            Editor: model.Metadata.Editor,
            Translator: model.Metadata.Translator,
            Description: model.Metadata.Description,
            Publisher: model.Metadata.Publisher,
            PlaceOfPublication: model.Metadata.PlaceOfPublication,
            PublishedDate: model.Metadata.PublishedDate,
            Edition: model.Metadata.Edition,
            Language: model.Metadata.Language,
            Categories: model.Metadata.Categories,
            Series: model.Metadata.Series,
            VolumeNumber: model.Metadata.VolumeNumber,
            // --- POLYMORPHIC FIELDS ---
            Type: type,
            Isbn: isbn,
            Asin: asin,
            Duration: duration,
            Narrator: narrator,
            PageCount: pageCount,
            // --- ROOT FIELDS ---
            CreatedAt: model.CreatedAt,
            // Membership falls back to an empty set when the caller did not
            // Include(BookCollections) — membership lives only in the join
            // table, so there is nothing else to read.
            CollectionIds: model.BookCollections.Select(bc => bc.CollectionId).OrderBy(id => id).ToList(),
            // --- MAPPED FROM FILE DETAILS ---
            HasFile: model.FileDetails.HasFile,
            FileName: model.FileDetails.FileName,
            CoverUrl: coverUrl,
            Chapters: chapters,
            // --- MAPPED FROM PROGRESS ---
            LastLocation: model.Progress.LastLocation,
            ProgressPercent: model.Progress.ProgressPercent,
            LastReadAt: model.Progress.LastReadAt,
            Rating: model.Progress.Rating,
            IsFavorite: model.Progress.IsFavorite,
            PersonalReview: model.Progress.PersonalReview,
            FinishedAt: model.Progress.FinishedAt,
            WorkId: model.WorkId,
            EditionCount: workBooks?.Count ?? 1,
            OtherEditions: otherEditions,
            // Null for a hand-uploaded book and for any caller that did not
            // Include(Acquisition) — absence is the honest answer there.
            Source: model.Acquisition is null
                ? null
                : new BookSourceDto(
                    ProviderId: model.Acquisition.ProviderId,
                    ProviderDisplayName: model.Acquisition.ProviderDisplayName,
                    ExternalId: model.Acquisition.ExternalId,
                    SourceUrl: model.Acquisition.SourceUrl,
                    AssetFormat: model.Acquisition.AssetFormat,
                    RightsStatement: model.Acquisition.RightsStatement,
                    AcquiredAt: model.Acquisition.AcquiredAt),
            Status: model.Status,
            StatusMessage: model.StatusMessage
        );
    }

    public static EditionSummaryDto ToEditionSummary(BookModel model)
    {
        var type = model switch
        {
            AudioBookModel => "audiobook",
            EBookModel => "ebook",
            _ => "physical",
        };

        return new EditionSummaryDto(
            Id: model.Id,
            Type: type,
            // There is no separate persisted format field. Keep the format
            // useful to clients by exposing the canonical polymorphic type.
            Format: type,
            ProgressPercent: model.Progress.ProgressPercent,
            FinishedAt: model.Progress.FinishedAt,
            LastReadAt: model.Progress.LastReadAt,
            HasFile: model.FileDetails.HasFile,
            FileName: model.FileDetails.FileName,
            Narrator: (model as AudioBookModel)?.Narrator,
            Duration: (model as AudioBookModel)?.Duration,
            Edition: model.Metadata.Edition,
            // The management UI names the book it will detach; the current
            // book's own title is NOT a valid substitute for a sibling's.
            Title: model.Title,
            Author: model.Author);
    }

    public static NoteDto ToDto(this NoteModel model) =>
        new NoteDto(
            Id: model.Id,
            BookId: model.BookId,
            Content: model.Content,
            CfiRange: model.CfiRange,
            SelectedText: model.SelectedText,
            CreatedAt: model.CreatedAt,
            BookTitle: model.Book?.Title,
            // Capture provenance (issue #260 §2, §4). These are distinct from
            // Content: a polished Content never replaces the raw capture.
            RawContent: model.RawContent,
            CaptureSource: model.CaptureSource,
            ProcessingMode: model.ProcessingMode,
            SourceAnchorKind: model.SourceAnchorKind,
            SourceAnchorValue: model.SourceAnchorValue,
            AnchorVerified: model.AnchorVerified
        );

    public static CollectionDto ToDto(this CollectionModel model) =>
        new CollectionDto(model.Id, model.Name, model.ParentId);

    public static TopicDto ToDto(this TopicModel model) =>
        new TopicDto(model.Id, model.Topic, model.NoteTopics?.Count ?? 0);

    public static WritingDto ToDto(this WritingModel model)
    {
        return new WritingDto(
            model.Id,
            model.Name,
            model.Type.ToString(), // Convert Enum to String for DTO
            model.ParentId,
            model.UpdatedAt
        );
    }

    public static WritingContentDto ToContentDto(this WritingModel model)
    {
        return new WritingContentDto(
            model.Id,
            model.Name,
            model.Content ?? string.Empty,
            model.UpdatedAt
        );
    }

    // ------------------------------
    // Create mappings (Dto → Model)
    // ------------------------------
    public static BookModel ToModel(this CreateBookDto dto)
    {
        // Factory Pattern: Create the correct subclass based on the "Type" string
        BookModel model = dto.Type?.ToLower() switch
        {
            "audiobook" => new AudioBookModel
            {
                Asin = dto.Asin,
                Duration = dto.Duration,
                Narrator = dto.Narrator,
            },
            "ebook" => new EBookModel { Isbn = dto.Isbn, PageCount = dto.PageCount },
            _ => new PhysicalBookModel // Default fallback
            {
                Isbn = dto.Isbn,
                PageCount = dto.PageCount,
            },
        };

        // 1. Map Shared Root Fields
        model.Id = Guid.NewGuid();
        model.Title = dto.Title;
        model.Author = dto.Author;
        model.CreatedAt = DateTime.UtcNow;

        // 2. Map Metadata (Owned Type)
        // We can assign properties directly as the object is initialized in the constructor
        model.Metadata.Subtitle = dto.Subtitle;
        model.Metadata.Editor = dto.Editor;
        model.Metadata.Translator = dto.Translator;
        model.Metadata.Description = dto.Description;
        model.Metadata.Publisher = dto.Publisher;
        model.Metadata.PlaceOfPublication = dto.PlaceOfPublication;
        model.Metadata.PublishedDate = dto.PublishedDate;
        model.Metadata.Edition = dto.Edition;
        model.Metadata.Language = dto.Language;
        model.Metadata.Categories = dto.Categories;
        model.Metadata.Series = dto.Series;
        model.Metadata.VolumeNumber = dto.VolumeNumber;

        // 3. Map Progress (Owned Type)
        model.Progress.Rating = dto.Rating;
        model.Progress.IsFavorite = dto.IsFavorite;
        model.Progress.PersonalReview = dto.PersonalReview;
        model.Progress.FinishedAt = dto.FinishedAt;

        // 4. FileDetails
        // Defaults are fine (HasFile=false), no DTO fields for files during creation usually.

        return model;
    }

    public static NoteModel ToModel(this CreateNoteDto dto, Guid bookId) =>
        new NoteModel
        {
            Id = Guid.NewGuid(),
            BookId = bookId,
            Content = dto.Content,
            CfiRange = dto.CfiRange,
            SelectedText = dto.SelectedText,
            // A captured note keeps the raw capture alongside the Content; an
            // ordinary typed note falls back to the model's text/verbatim/unknown.
            RawContent = dto.RawContent,
            CaptureSource = dto.CaptureSource,
            ProcessingMode = dto.ProcessingMode,
            SourceAnchorKind = dto.SourceAnchorKind,
            SourceAnchorValue = dto.SourceAnchorValue,
            AnchorVerified = dto.AnchorVerified,
        };

    public static CollectionModel ToModel(this CreateCollectionDto dto) =>
        new CollectionModel
        {
            Id = Guid.NewGuid(),
            Name = dto.Name,
            ParentId = dto.ParentId,
        };

    public static TopicModel ToModel(this CreateTopicDto dto) =>
        new TopicModel { Id = Guid.NewGuid(), Topic = dto.Topic };

    // Helper: treats empty string as null (allows clearing optional fields)
    private static string? NullIfEmpty(string? value) =>
        value is not null ? (string.IsNullOrEmpty(value) ? null : value) : null;

    // ------------------------------
    // Update mappings (Apply fields)
    // ------------------------------
    public static void Apply(this BookModel model, UpdateBookDto dto)
    {
        // 1. Apply Root Fields
        if (dto.Title != null)
            model.Title = dto.Title;
        if (dto.Author != null)
            model.Author = NullIfEmpty(dto.Author);

        // 2. Apply Metadata (Owned Type) — empty string clears the field
        if (dto.Subtitle != null)
            model.Metadata.Subtitle = NullIfEmpty(dto.Subtitle);
        if (dto.Editor != null)
            model.Metadata.Editor = NullIfEmpty(dto.Editor);
        if (dto.Translator != null)
            model.Metadata.Translator = NullIfEmpty(dto.Translator);
        if (dto.Description != null)
            model.Metadata.Description = NullIfEmpty(dto.Description);
        if (dto.Publisher != null)
            model.Metadata.Publisher = NullIfEmpty(dto.Publisher);
        if (dto.PlaceOfPublication != null)
            model.Metadata.PlaceOfPublication = NullIfEmpty(dto.PlaceOfPublication);
        if (dto.PublishedDate != null)
            model.Metadata.PublishedDate = NullIfEmpty(dto.PublishedDate);
        if (dto.Edition != null)
            model.Metadata.Edition = NullIfEmpty(dto.Edition);
        if (dto.Language != null)
            model.Metadata.Language = NullIfEmpty(dto.Language);
        if (dto.Categories != null)
            model.Metadata.Categories = NullIfEmpty(dto.Categories);
        if (dto.Series != null)
            model.Metadata.Series = NullIfEmpty(dto.Series);
        if (dto.VolumeNumber != null)
            model.Metadata.VolumeNumber = NullIfEmpty(dto.VolumeNumber);

        // 3. Apply Progress (Owned Type)
        if (dto.Rating.HasValue)
            model.Progress.Rating = dto.Rating.Value;

        if (dto.IsFavorite.HasValue)
            model.Progress.IsFavorite = dto.IsFavorite.Value;

        if (dto.PersonalReview != null)
            model.Progress.PersonalReview = NullIfEmpty(dto.PersonalReview);

        // LOGIC FIX: Handle Finished Status
        if (dto.FinishedAt != null)
        {
            model.Progress.FinishedAt = dto.FinishedAt;
        }
        else if (dto.IsFinished.HasValue)
        {
            if (dto.IsFinished.Value)
            {
                // Mark as finished (Set to Now if not already set)
                model.Progress.FinishedAt ??= DateTime.UtcNow;
                model.Progress.ProgressPercent = 100;
            }
            else
            {
                // Mark as Unread
                model.Progress.FinishedAt = null;
            }
        }

        // 4. Apply Specific Fields (Pattern Matching)
        switch (model)
        {
            case PhysicalBookModel p:
                if (dto.Isbn != null)
                    p.Isbn = dto.Isbn;
                if (dto.PageCount != null)
                    p.PageCount = dto.PageCount;
                break;
            case EBookModel e:
                if (dto.Isbn != null)
                    e.Isbn = dto.Isbn;
                if (dto.PageCount != null)
                    e.PageCount = dto.PageCount;
                break;
            case AudioBookModel a:
                if (dto.Asin != null)
                    a.Asin = dto.Asin;
                if (dto.Duration != null)
                    a.Duration = dto.Duration;
                if (dto.Narrator != null)
                    a.Narrator = dto.Narrator;
                break;
        }
    }

    public static void Apply(this CollectionModel model, UpdateCollectionDto dto)
    {
        model.Name = dto.Name;
        // Check if ParentId is part of the update
        if (dto.ParentId != model.ParentId)
        {
            model.ParentId = dto.ParentId;
        }
    }

    public static void Apply(this TopicModel model, UpdateTopicDto dto)
    {
        model.Topic = dto.Topic;
    }

    public static void Apply(this NoteModel model, UpdateNoteDto dto)
    {
        model.Content = dto.Content;
        // Update SelectedText if provided in the DTO
        if (dto.SelectedText != null)
        {
            model.SelectedText = dto.SelectedText;
        }
    }

    public static WritingSourceDto ToDto(this WritingNoteModel model) =>
        new(
            Id: model.NoteId,
            BookId: model.Note.BookId,
            BookTitle: model.Note.Book?.Title,
            Content: model.Note.Content,
            SelectedText: model.Note.SelectedText,
            CfiRange: model.Note.CfiRange,
            CreatedAt: model.Note.CreatedAt,
            AddedAt: model.AddedAt,
            SourceAnchorKind: model.Note.SourceAnchorKind,
            SourceAnchorValue: model.Note.SourceAnchorValue,
            AnchorVerified: model.Note.AnchorVerified
        );
}
