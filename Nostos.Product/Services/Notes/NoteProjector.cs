using Nostos.Backend.Data.Models;
using Nostos.Backend.Mapping;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Services.Notes;

/// <summary>
/// Note read models: a stored note as the row the Brain shows, and as the shape
/// the note-review flow reads (issue #1619, extracted from
/// <see cref="NoteService"/>). Projection only — it never touches the database,
/// so every caller of a note read gets the same DTO for the same model.
/// </summary>
public sealed class NoteProjector
{
    private readonly NoteSnippetFormatter _snippets;

    public NoteProjector(NoteSnippetFormatter snippets)
    {
        _snippets = snippets;
    }

    public NoteSearchHitDto ToSearchHit(
        NoteModel n, string? term, IReadOnlyDictionary<Guid, int>? editions = null) => new(
        n.Id,
        n.BookId,
        n.Book?.Title,
        n.Content,
        // A passage travels as its opening; the snippet still finds the match
        // anywhere in it.
        TextLigatures.Expand(n.IsPassage() ? PassageText.Opening(n.SelectedText) : n.SelectedText),
        _snippets.Snippet(n.Content, TextLigatures.Expand(n.SelectedText), term),
        n.NoteTopics
            .Where(nc => nc.Topic != null)
            .Select(nc => nc.Topic!.Topic)
            .OrderBy(name => name)
            .ToList(),
        n.CreatedAt,
        n.CfiRange,
        n.SourceAnchorKind,
        n.SourceAnchorValue,
        n.AnchorVerified,
        n.CaptureSource,
        n.ProcessingMode,
        !string.IsNullOrWhiteSpace(n.RawContent),
        n.Kind,
        n.PassageTitle,
        n.PassageEnd,
        n.IsPassage() ? PassageText.WordCount(n.SelectedText) : null,
        n.Book?.Author,
        n.HighlightColour,
        FormatOf(n.Book),
        n.Book is not null && editions is not null && editions.TryGetValue(n.Book.WorkId, out var count) ? count : 1,
        n.Book?.WorkId,
        n.UpdatedAt ?? n.CreatedAt);

    /// <summary>The format glyph's name for an edition: audiobook, pdf, ebook or physical.</summary>
    public string? FormatOf(BookModel? book) => book switch
    {
        null => null,
        AudioBookModel => "audiobook",
        PhysicalBookModel => "physical",
        _ when book.FileDetails.FileName?.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) == true => "pdf",
        _ => "ebook",
    };

    /// <summary>What the note-review flow reads: the note, its book, and its topics.</summary>
    public NoteReviewDto ToReview(NoteModel note) => new(
        note.Id,
        note.BookId,
        note.Book?.Title,
        note.Content,
        note.SelectedText,
        note.NoteTopics
            .Where(nc => nc.Topic is not null)
            .Select(nc => nc.Topic!.Topic)
            .OrderBy(name => name)
            .ToList(),
        note.NoteTopics.Select(link => link.TopicId).ToList());
}
