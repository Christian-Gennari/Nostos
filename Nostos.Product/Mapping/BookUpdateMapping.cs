using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Library;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Mapping;

/// <summary>
/// The single implementation of a book update's field mapping (issue #1612).
///
/// Two write entry points exist — the library command
/// (<see cref="LibraryUpdateBookRequest"/>, used by REST, Ask Nostos and the UI)
/// and the legacy REST patch shape (<see cref="UpdateBookDto"/>) — and they must
/// never disagree about what an update means. There is one field mapping, here;
/// both project onto it and call it.
///
/// What it deliberately does NOT do:
/// <list type="bullet">
/// <item>Collection membership. <c>BookCollections</c> is the only record of it
/// and resolving a set needs the database, so the library service owns that
/// write. The collection fields on <see cref="UpdateBookDto"/> are ignored here
/// and translated by the service.</item>
/// <item>Validation. Blank titles, an ISBN on an audiobook and duplicate
/// identifiers are rejected by the service before it calls this.</item>
/// </list>
///
/// The rules it does keep, because they are easy to break:
/// title/author and every metadata field are null-preserving and cleared by an
/// empty string; rating, favourite and review are the BOOK's and go on its work,
/// while <c>EditionRating</c>/<c>EditionReview</c> are this edition's own
/// exceptions; finishing sets the moment, 100% and (only when there is none) the
/// last-read time, and unfinishing clears the moment only; an identifier is
/// cleared by an empty string; and a changed paper page count recomputes what
/// the stored page means.
/// </summary>
public static class BookUpdateMapping
{
    /// <summary>
    /// The field patch carried by a library update. Receipt identity
    /// (client / idempotency key), the target book and collection membership
    /// stay outside the patch: they are the command's, not the fields'.
    /// </summary>
    public static UpdateBookDto ToPatch(this LibraryUpdateBookRequest request) =>
        new(
            Title: request.Title,
            Subtitle: request.Subtitle,
            Author: request.Author,
            Editor: request.Editor,
            Translator: request.Translator,
            Narrator: request.Narrator,
            Description: request.Description,
            Isbn: request.Isbn,
            Asin: request.Asin,
            Duration: request.Duration,
            Publisher: request.Publisher,
            PlaceOfPublication: request.PlaceOfPublication,
            PublishedDate: request.PublishedDate,
            Edition: request.Edition,
            PageCount: request.PageCount,
            Language: request.Language,
            Categories: request.Categories,
            Series: request.Series,
            VolumeNumber: request.VolumeNumber,
            Rating: request.Rating,
            IsFavorite: request.IsFavorite,
            PersonalReview: request.PersonalReview,
            FinishedAt: request.FinishedAt,
            IsFinished: request.IsFinished,
            EditionRating: request.EditionRating,
            EditionReview: request.EditionReview);

    /// <summary>
    /// Apply <paramref name="patch"/> to <paramref name="model"/>.
    /// <paramref name="now"/> dates a finish and a page-count-driven finish, so
    /// a caller can keep one clock for the whole command.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The model was loaded without its work. The book's rating, favourite and
    /// review live there, so silently skipping them would drop what the caller
    /// asked to write.
    /// </exception>
    public static void Apply(BookModel model, UpdateBookDto patch, DateTime now)
    {
        // The context auto-includes Work; anything that builds a model by hand
        // must set it. Fail loudly rather than silently discarding the values.
        var work = model.Work ?? throw new InvalidOperationException(
            $"Book {model.Id} was updated without its Work loaded. The book's rating, favourite and review live on the work.");

        // 1. Root fields. Title is not cleared here: the service rejects a blank
        //    title before mapping.
        if (patch.Title is not null)
            model.Title = patch.Title;
        if (patch.Author is not null)
            model.Author = NullIfEmpty(patch.Author);

        // 2. Metadata (owned type) — empty string clears the field.
        if (patch.Subtitle is not null)
            model.Metadata.Subtitle = NullIfEmpty(patch.Subtitle);
        if (patch.Editor is not null)
            model.Metadata.Editor = NullIfEmpty(patch.Editor);
        if (patch.Translator is not null)
            model.Metadata.Translator = NullIfEmpty(patch.Translator);
        if (patch.Description is not null)
            model.Metadata.Description = NullIfEmpty(patch.Description);
        if (patch.Publisher is not null)
            model.Metadata.Publisher = NullIfEmpty(patch.Publisher);
        if (patch.PlaceOfPublication is not null)
            model.Metadata.PlaceOfPublication = NullIfEmpty(patch.PlaceOfPublication);
        if (patch.PublishedDate is not null)
            model.Metadata.PublishedDate = NullIfEmpty(patch.PublishedDate);
        if (patch.Edition is not null)
            model.Metadata.Edition = NullIfEmpty(patch.Edition);
        if (patch.Language is not null)
            model.Metadata.Language = NullIfEmpty(patch.Language);
        if (patch.Categories is not null)
            model.Metadata.Categories = NullIfEmpty(patch.Categories);
        if (patch.Series is not null)
            model.Metadata.Series = NullIfEmpty(patch.Series);
        if (patch.VolumeNumber is not null)
            model.Metadata.VolumeNumber = NullIfEmpty(patch.VolumeNumber);

        // 3. The book's values (rating, favourite, review) are the work's,
        //    shared by every edition; the edition's own rating and review are
        //    exceptions on the edition (0 / empty clears).
        if (patch.Rating.HasValue)
            work.Rating = Math.Clamp(patch.Rating.Value, 0, 5);
        if (patch.IsFavorite.HasValue)
            work.IsFavorite = patch.IsFavorite.Value;
        if (patch.PersonalReview is not null)
            work.PersonalReview = NullIfEmpty(patch.PersonalReview);
        if (patch.EditionRating.HasValue)
            model.Progress.Rating = Math.Clamp(patch.EditionRating.Value, 0, 5);
        if (patch.EditionReview is not null)
            model.Progress.PersonalReview = NullIfEmpty(patch.EditionReview);

        // 4. Finished status. An explicit moment wins; otherwise IsFinished
        //    finishes now (100%, and Last read only if it never moved) or clears
        //    the moment. Unfinishing leaves the percentage alone.
        if (patch.FinishedAt is not null)
        {
            model.Progress.FinishedAt = patch.FinishedAt;
        }
        else if (patch.IsFinished.HasValue)
        {
            if (patch.IsFinished.Value)
            {
                model.Progress.FinishedAt ??= now;
                model.Progress.ProgressPercent = 100;
                model.Progress.LastReadAt ??= model.Progress.FinishedAt;
            }
            else
            {
                model.Progress.FinishedAt = null;
            }
        }

        // 5. Subtype fields. Identifiers are cleared by an empty string; a
        //    changed paper page count changes what its stored page means.
        switch (model)
        {
            case PhysicalBookModel p:
                if (patch.Isbn is not null)
                    p.Isbn = NullIfEmpty(patch.Isbn);
                if (patch.PageCount.HasValue && patch.PageCount != p.PageCount)
                {
                    p.PageCount = patch.PageCount;
                    PaperPageProgress.RecomputeAfterPageCountChange(p, now);
                }
                break;
            case EBookModel e:
                if (patch.Isbn is not null)
                    e.Isbn = NullIfEmpty(patch.Isbn);
                if (patch.PageCount.HasValue)
                    e.PageCount = patch.PageCount;
                break;
            case AudioBookModel a:
                if (patch.Asin is not null)
                    a.Asin = NullIfEmpty(patch.Asin);
                if (patch.Duration is not null)
                    a.Duration = NullIfEmpty(patch.Duration);
                if (patch.Narrator is not null)
                    a.Narrator = NullIfEmpty(patch.Narrator);
                break;
        }
    }

    // Helper: treats empty string as null (allows clearing optional fields).
    private static string? NullIfEmpty(string? value) =>
        value is not null ? (string.IsNullOrEmpty(value) ? null : value) : null;
}
