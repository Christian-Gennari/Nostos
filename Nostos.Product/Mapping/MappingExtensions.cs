using Nostos.Backend.Services;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Notes;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Mapping;

public static class MappingExtensions
{
    // Chapter JSON is a small, stable shape; parsing it must not allocate a new
    // options object per book.
    private static readonly JsonSerializerOptions ChaptersJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // ------------------------------
    // Read mappings (Model → DTO)
    // ------------------------------
    /// <param name="logger">
    /// Where an unreadable chapters JSON is reported. Optional because most
    /// callers are static reads without a logger in scope; a caller that has
    /// one should pass it, so the warning reaches the host's logging pipeline
    /// instead of a console logger created outside DI (issue #1612).
    /// </param>
    public static BookDto ToDto(this BookModel model, ILogger? logger = null)
    {
        // The book's rating, favourite and review live on its work. A book mapped
        // without it would silently report rating 0, so fail loudly instead:
        // the context auto-includes Work, and anything that builds a model by hand
        // must set it.
        var work = model.Work ?? throw new InvalidOperationException(
            $"Book {model.Id} was mapped without its Work loaded. The book's rating, favourite and review live on the work.");

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
                duration = BookTrackList.FormatTotal(BookTrackList.Parse(model.FileDetails.TracksJson)) ?? a.Duration;
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
                    ChaptersJsonOptions
                );
            }
            catch (JsonException ex)
            {
                chapters = null;
                logger?.LogWarning(
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
            Rating: work.Rating,
            IsFavorite: work.IsFavorite,
            PersonalReview: work.PersonalReview,
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
            StatusMessage: model.StatusMessage,
            Tracks: ToTrackDtos(model.FileDetails.TracksJson),
            CoverVersion: coverUrl is null ? null : model.FileDetails.CoverVersion,
            EditionRating: model.Progress.Rating,
            EditionReview: model.Progress.PersonalReview,
            PlaybackRate: model.Progress.PlaybackRate
        );
    }

    private static List<BookTrackDto>? ToTrackDtos(string? tracksJson)
    {
        var tracks = BookTrackList.Parse(tracksJson);
        if (tracks.Count == 0)
            return null;

        return tracks
            .Select(track => new BookTrackDto(
                track.Number,
                AudiobookManifest.TrackTitle(track),
                track.DurationMs / 1000d,
                track.Bytes,
                track.ContentType))
            .ToList();
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
            Duration: model is AudioBookModel audio
                ? BookTrackList.FormatTotal(BookTrackList.Parse(model.FileDetails.TracksJson)) ?? audio.Duration
                : null,
            Edition: model.Metadata.Edition,
            // The management UI names the book it will detach; the current
            // book's own title is NOT a valid substitute for a sibling's.
            Title: model.Title,
            Author: model.Author,
            Publisher: model.Metadata.Publisher,
            PublishedDate: model.Metadata.PublishedDate,
            Rating: model.Progress.Rating,
            PersonalReview: model.Progress.PersonalReview,
            HasPlace: model.Progress.LastLocation != null);
    }

    public static NoteDto ToDto(this NoteModel model) =>
        new NoteDto(
            Id: model.Id,
            BookId: model.BookId,
            Content: model.Content,
            CfiRange: model.CfiRange,
            // A passage travels as its opening; the viewer fetches the rest.
            SelectedText: TextLigatures.Expand(model.IsPassage() ? PassageText.Opening(model.SelectedText) : model.SelectedText),
            CreatedAt: model.CreatedAt,
            BookTitle: model.Book?.Title,
            // Capture provenance (issue #260 §2, §4). These are distinct from
            // Content: a polished Content never replaces the raw capture.
            RawContent: model.RawContent,
            CaptureSource: model.CaptureSource,
            ProcessingMode: model.ProcessingMode,
            SourceAnchorKind: model.SourceAnchorKind,
            SourceAnchorValue: model.SourceAnchorValue,
            AnchorVerified: model.AnchorVerified,
            HighlightColour: model.HighlightColour,
            Kind: model.Kind,
            PassageTitle: model.PassageTitle,
            PassageEnd: model.PassageEnd,
            PassageWordCount: model.IsPassage() ? PassageText.WordCount(model.SelectedText) : null,
            BookAuthor: model.Book?.Author,
            UpdatedAt: model.UpdatedAt ?? model.CreatedAt
        );

    public static bool IsPassage(this NoteModel model) => model.Kind == NoteKinds.Passage;

    public static PassageDto ToPassageDto(this NoteModel model) =>
        new(
            Id: model.Id,
            BookId: model.BookId,
            BookTitle: model.Book?.Title,
            Title: model.PassageTitle ?? string.Empty,
            AnchorKind: model.SourceAnchorKind,
            Start: model.SourceAnchorValue ?? string.Empty,
            End: model.PassageEnd ?? string.Empty,
            Html: model.PassageHtml,
            Text: TextLigatures.Expand(model.SelectedText),
            WordCount: PassageText.WordCount(model.SelectedText),
            Content: model.Content,
            HighlightColour: model.HighlightColour,
            CreatedAt: model.CreatedAt,
            BookAuthor: model.Book?.Author);

    public static CollectionDto ToDto(this CollectionModel model) =>
        new CollectionDto(model.Id, model.Name, model.ParentId);

    public static TopicDto ToDto(this TopicModel model) =>
        new TopicDto(model.Id, model.Topic, model.NoteTopics?.Count ?? 0, Description: model.Description);

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
        // Factory Pattern: Create the correct subclass based on the "Type" string.
        // Anything else falls back to a digital book: a paper copy is something
        // someone says they own, never the shape an unlabelled row gets.
        BookModel model = dto.Type?.ToLower() switch
        {
            "audiobook" => new AudioBookModel
            {
                Asin = dto.Asin,
                Duration = dto.Duration,
                Narrator = dto.Narrator,
            },
            "physical" => new PhysicalBookModel
            {
                Isbn = dto.Isbn,
                PageCount = dto.PageCount,
            },
            _ => new EBookModel { Isbn = dto.Isbn, PageCount = dto.PageCount },
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
        // Rating, favourite and review are the book's: the library service puts
        // them on the work this edition belongs to, not on the edition.
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
            SelectedText = TextLigatures.Expand(dto.SelectedText),
            // A captured note keeps the raw capture alongside the Content; an
            // ordinary typed note falls back to the model's text/verbatim/unknown.
            RawContent = dto.RawContent,
            CaptureSource = dto.CaptureSource,
            ProcessingMode = dto.ProcessingMode,
            SourceAnchorKind = dto.SourceAnchorKind,
            SourceAnchorValue = dto.SourceAnchorValue,
            AnchorVerified = dto.AnchorVerified,
            HighlightColour = dto.HighlightColour,
            Kind = dto.Kind,
            PassageTitle = dto.PassageTitle,
            PassageEnd = dto.PassageEnd,
            PassageHtml = dto.PassageHtml,
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

    // ------------------------------
    // Update mappings (Apply fields)
    // ------------------------------
    /// <summary>
    /// The legacy REST patch shape. Delegates to <see cref="BookUpdateMapping"/>
    /// so this and the library command can never disagree about what an update
    /// means; collection membership is not a field and the service owns it.
    /// </summary>
    public static void Apply(this BookModel model, UpdateBookDto dto) =>
        BookUpdateMapping.Apply(model, dto, DateTime.UtcNow);

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
        // Update SelectedText if provided in the DTO. A passage's text is fixed
        // when it is saved: the lists carry only its opening, and an edit that
        // sent that back must not cut the passage down to it.
        if (dto.SelectedText != null && !model.IsPassage())
        {
            model.SelectedText = TextLigatures.Expand(dto.SelectedText);
        }

        if (dto.PassageTitle != null && model.IsPassage())
        {
            model.PassageTitle = dto.PassageTitle.Trim();
        }

        // Null means "not sent": editing a note's text must not clear its pen.
        if (dto.HighlightColour != null)
        {
            model.HighlightColour = dto.HighlightColour;
        }
    }

    public static WritingSourceDto ToDto(this WritingNoteModel model) =>
        new(
            Id: model.NoteId,
            BookId: model.Note.BookId,
            BookTitle: model.Note.Book?.Title,
            Content: model.Note.Content,
            SelectedText: TextLigatures.Expand(model.Note.IsPassage()
                ? PassageText.Opening(model.Note.SelectedText)
                : model.Note.SelectedText),
            CfiRange: model.Note.CfiRange,
            CreatedAt: model.Note.CreatedAt,
            AddedAt: model.AddedAt,
            SourceAnchorKind: model.Note.SourceAnchorKind,
            SourceAnchorValue: model.Note.SourceAnchorValue,
            AnchorVerified: model.Note.AnchorVerified,
            Kind: model.Note.Kind,
            PassageTitle: model.Note.PassageTitle,
            PassageEnd: model.Note.PassageEnd,
            PassageWordCount: model.Note.IsPassage() ? PassageText.WordCount(model.Note.SelectedText) : null,
            BookAuthor: model.Note.Book?.Author
        );
}
