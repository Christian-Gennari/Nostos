using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Library;
using Nostos.Backend.Services.Notes;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Services.ReadingList;

/// <summary>
/// Outcome of a reading-list command. Carries no HTTP type: the endpoint maps
/// <see cref="ErrorCode"/> to a status, as the note and library services do.
/// </summary>
public sealed record ReadingListResult<T>(bool Success, string? ErrorCode, string? ErrorMessage, T? Value)
{
    public static ReadingListResult<T> Ok(T value) => new(true, null, null, value);

    public static ReadingListResult<T> Fail(string errorCode, string errorMessage) =>
        new(false, errorCode, errorMessage, default);
}

/// <summary>The typed failure codes <see cref="IReadingListService"/> emits.</summary>
public static class ReadingListErrorCodes
{
    public const string EntryNotFound = "reading_list_entry_not_found";
    public const string BookNotFound = "book_not_found";
    public const string InvalidEntry = "invalid_reading_list_entry";
    public const string InvalidIsbn = "invalid_isbn";
    public const string InvalidOrder = "invalid_reading_list_order";
    public const string ListFull = "reading_list_full";
}

public interface IReadingListService
{
    /// <summary>"Next up" in shelf order, then "Someday" newest first.</summary>
    Task<IReadOnlyList<ReadingListEntryDto>> ListAsync(CancellationToken ct = default);

    Task<ReadingListResult<ReadingListEntryDto>> CreateAsync(SaveReadingListEntryDto dto, CancellationToken ct = default);

    Task<ReadingListResult<ReadingListEntryDto>> UpdateAsync(Guid id, SaveReadingListEntryDto dto, CancellationToken ct = default);

    Task<ReadingListResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Replaces the "Next up" shelf; every entry not named becomes "Someday".</summary>
    Task<ReadingListResult<IReadOnlyList<ReadingListEntryDto>>> SetNextAsync(
        IReadOnlyList<Guid>? ids, CancellationToken ct = default);

    /// <summary>
    /// The user got the book: the entry's reason moves onto <paramref name="bookId"/>
    /// as a note and the entry leaves the list.
    /// </summary>
    Task<ReadingListResult<bool>> MarkAcquiredAsync(Guid id, Guid bookId, CancellationToken ct = default);
}

public sealed class ReadingListService(NostosDbContext db, INoteService notes) : IReadingListService
{
    /// <summary>
    /// A ceiling, not a product limit: the list is returned whole, so it must
    /// not be able to grow without bound.
    /// </summary>
    public const int MaxEntries = 2000;

    public async Task<IReadOnlyList<ReadingListEntryDto>> ListAsync(CancellationToken ct = default)
    {
        var entries = await db.ReadingListEntries.AsNoTracking().ToListAsync(ct);
        if (entries.Count == 0)
            return [];

        var owned = await OwnedBookIndex.LoadAsync(db, ct);

        return entries
            .OrderBy(e => e.NextPosition is null)
            .ThenBy(e => e.NextPosition)
            .ThenByDescending(e => e.CreatedAt)
            .ThenBy(e => e.Id)
            .Select(e => ToDto(e, owned.Find(e)))
            .ToList();
    }

    public async Task<ReadingListResult<ReadingListEntryDto>> CreateAsync(
        SaveReadingListEntryDto dto, CancellationToken ct = default)
    {
        if (Validate(dto, out var fields) is { } invalid)
            return invalid;

        if (await db.ReadingListEntries.CountAsync(ct) >= MaxEntries)
            return ReadingListResult<ReadingListEntryDto>.Fail(
                ReadingListErrorCodes.ListFull,
                $"The reading list holds at most {MaxEntries} books.");

        var entry = new ReadingListEntryModel();
        fields.ApplyTo(entry);

        if (dto.ReadNext)
        {
            var last = await db.ReadingListEntries.MaxAsync(e => e.NextPosition, ct);
            entry.NextPosition = (last ?? -1) + 1;
        }

        db.ReadingListEntries.Add(entry);
        await db.SaveChangesAsync(ct);

        var owned = await OwnedBookIndex.LoadAsync(db, ct);
        return ReadingListResult<ReadingListEntryDto>.Ok(ToDto(entry, owned.Find(entry)));
    }

    public async Task<ReadingListResult<ReadingListEntryDto>> UpdateAsync(
        Guid id, SaveReadingListEntryDto dto, CancellationToken ct = default)
    {
        if (Validate(dto, out var fields) is { } invalid)
            return invalid;

        var entry = await db.ReadingListEntries.SingleOrDefaultAsync(e => e.Id == id, ct);
        if (entry is null)
            return NotFound<ReadingListEntryDto>(id);

        fields.ApplyTo(entry);
        await db.SaveChangesAsync(ct);

        var owned = await OwnedBookIndex.LoadAsync(db, ct);
        return ReadingListResult<ReadingListEntryDto>.Ok(ToDto(entry, owned.Find(entry)));
    }

    public async Task<ReadingListResult<bool>> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entry = await db.ReadingListEntries.SingleOrDefaultAsync(e => e.Id == id, ct);
        if (entry is null)
            return NotFound<bool>(id);

        db.ReadingListEntries.Remove(entry);
        await db.SaveChangesAsync(ct);
        return ReadingListResult<bool>.Ok(true);
    }

    public async Task<ReadingListResult<IReadOnlyList<ReadingListEntryDto>>> SetNextAsync(
        IReadOnlyList<Guid>? ids, CancellationToken ct = default)
    {
        if (ids is null)
            return ReadingListResult<IReadOnlyList<ReadingListEntryDto>>.Fail(
                ReadingListErrorCodes.InvalidOrder, "Send the ids of the books to read next, in order.");

        if (ids.Distinct().Count() != ids.Count)
            return ReadingListResult<IReadOnlyList<ReadingListEntryDto>>.Fail(
                ReadingListErrorCodes.InvalidOrder, "A book can only be on the shelf once.");

        var entries = await db.ReadingListEntries.ToListAsync(ct);
        var byId = entries.ToDictionary(e => e.Id);
        if (ids.Any(id => !byId.ContainsKey(id)))
        {
            return ReadingListResult<IReadOnlyList<ReadingListEntryDto>>.Fail(
                ReadingListErrorCodes.EntryNotFound, "One of those books is no longer on the reading list.");
        }

        foreach (var entry in entries)
            entry.NextPosition = null;
        for (var slot = 0; slot < ids.Count; slot++)
            byId[ids[slot]].NextPosition = slot;

        await db.SaveChangesAsync(ct);
        return ReadingListResult<IReadOnlyList<ReadingListEntryDto>>.Ok(await ListAsync(ct));
    }

    public async Task<ReadingListResult<bool>> MarkAcquiredAsync(
        Guid id, Guid bookId, CancellationToken ct = default)
    {
        var entry = await db.ReadingListEntries.SingleOrDefaultAsync(e => e.Id == id, ct);
        if (entry is null)
            return NotFound<bool>(id);

        if (!await db.Books.AnyAsync(b => b.Id == bookId, ct))
            return ReadingListResult<bool>.Fail(
                ReadingListErrorCodes.BookNotFound, $"Book {bookId} not found.");

        // The note is written before the entry is removed: if the second step
        // fails the reason is on the book and the entry is still on the list,
        // which loses nothing.
        if (AcquiredNote(entry) is { } content)
        {
            var note = await notes.CreateAsync(bookId, new CreateNoteDto(content), ct: ct);
            if (!note.Success)
                return ReadingListResult<bool>.Fail(
                    note.ErrorCode ?? ReadingListErrorCodes.InvalidEntry,
                    note.ErrorMessage ?? "The reason could not be saved on the book.");
        }

        db.ReadingListEntries.Remove(entry);
        await db.SaveChangesAsync(ct);
        return ReadingListResult<bool>.Ok(true);
    }

    /// <summary>The note an entry leaves on its book, or null when it has nothing to say.</summary>
    internal static string? AcquiredNote(ReadingListEntryModel entry)
    {
        var why = entry.Why?.Trim();
        var from = entry.RecommendedBy?.Trim();
        if (string.IsNullOrEmpty(why) && string.IsNullOrEmpty(from))
            return null;

        var parts = new List<string>(2);
        if (!string.IsNullOrEmpty(why))
            parts.Add($"Why I wanted to read this: {why}");
        if (!string.IsNullOrEmpty(from))
            parts.Add($"Recommended by: {from}");
        return string.Join("\n\n", parts);
    }

    private static ReadingListResult<T> NotFound<T>(Guid id) =>
        ReadingListResult<T>.Fail(
            ReadingListErrorCodes.EntryNotFound, $"Reading list entry {id} not found.");

    private static ReadingListResult<ReadingListEntryDto>? Validate(
        SaveReadingListEntryDto dto, out EntryFields fields)
    {
        fields = default;

        var title = Clean(dto.Title);
        if (title is null)
            return Invalid("A book needs a title.");
        if (title.Length > ReadingListEntryModel.MaxTitleLength)
            return Invalid($"The title is limited to {ReadingListEntryModel.MaxTitleLength} characters.");

        var author = Clean(dto.Author);
        if (author?.Length > ReadingListEntryModel.MaxAuthorLength)
            return Invalid($"The author is limited to {ReadingListEntryModel.MaxAuthorLength} characters.");

        var publishedDate = Clean(dto.PublishedDate);
        if (publishedDate?.Length > ReadingListEntryModel.MaxPublishedDateLength)
            return Invalid($"The publication date is limited to {ReadingListEntryModel.MaxPublishedDateLength} characters.");

        var why = Clean(dto.Why);
        if (why?.Length > ReadingListEntryModel.MaxWhyLength)
            return Invalid($"The reason is limited to {ReadingListEntryModel.MaxWhyLength} characters.");

        var recommendedBy = Clean(dto.RecommendedBy);
        if (recommendedBy?.Length > ReadingListEntryModel.MaxRecommendedByLength)
            return Invalid($"The recommender is limited to {ReadingListEntryModel.MaxRecommendedByLength} characters.");

        string? isbn = null;
        if (Clean(dto.Isbn) is { } rawIsbn)
        {
            isbn = BookIdentityNormalizer.NormalizeIsbn(rawIsbn);
            if (isbn is null)
                return ReadingListResult<ReadingListEntryDto>.Fail(
                    ReadingListErrorCodes.InvalidIsbn, "Enter a valid ISBN-10 or ISBN-13.");
        }

        fields = new EntryFields(title, author, isbn, publishedDate, why, recommendedBy);
        return null;

        static ReadingListResult<ReadingListEntryDto> Invalid(string message) =>
            ReadingListResult<ReadingListEntryDto>.Fail(ReadingListErrorCodes.InvalidEntry, message);
    }

    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static ReadingListEntryDto ToDto(ReadingListEntryModel e, Guid? ownedBookId) =>
        new(e.Id, e.Title, e.Author, e.Isbn, e.PublishedDate, e.Why, e.RecommendedBy,
            e.NextPosition, e.CreatedAt, ownedBookId);

    private readonly record struct EntryFields(
        string Title, string? Author, string? Isbn, string? PublishedDate, string? Why, string? RecommendedBy)
    {
        public void ApplyTo(ReadingListEntryModel entry)
        {
            entry.Title = Title;
            entry.Author = Author;
            entry.Isbn = Isbn;
            entry.PublishedDate = PublishedDate;
            entry.Why = Why;
            entry.RecommendedBy = RecommendedBy;
        }
    }

    /// <summary>
    /// Which library books an entry might already be. An ISBN match is exact;
    /// otherwise the title must match and, when both sides name an author, so
    /// must the author. A hint for the reader, never a reason to refuse an entry.
    /// </summary>
    private sealed class OwnedBookIndex
    {
        private readonly Dictionary<string, Guid> _byIsbn = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<(string Author, Guid BookId)>> _byTitle = new(StringComparer.Ordinal);

        public static async Task<OwnedBookIndex> LoadAsync(NostosDbContext db, CancellationToken ct)
        {
            var books = await db.Books.AsNoTracking()
                .OrderBy(b => b.CreatedAt)
                .Select(b => new { b.Id, b.NormalizedIsbn, b.Title, b.Author })
                .ToListAsync(ct);

            var index = new OwnedBookIndex();
            foreach (var book in books)
            {
                if (book.NormalizedIsbn is { } isbn)
                    index._byIsbn.TryAdd(isbn, book.Id);

                var title = BookIdentityNormalizer.NormalizeTitle(book.Title);
                if (title.Length == 0)
                    continue;
                if (!index._byTitle.TryGetValue(title, out var sameTitle))
                    index._byTitle[title] = sameTitle = [];
                sameTitle.Add((BookIdentityNormalizer.NormalizeAuthor(book.Author), book.Id));
            }

            return index;
        }

        public Guid? Find(ReadingListEntryModel entry)
        {
            if (entry.Isbn is { } isbn && _byIsbn.TryGetValue(isbn, out var byIsbn))
                return byIsbn;

            var title = BookIdentityNormalizer.NormalizeTitle(entry.Title);
            if (title.Length == 0 || !_byTitle.TryGetValue(title, out var sameTitle))
                return null;

            var author = BookIdentityNormalizer.NormalizeAuthor(entry.Author);
            foreach (var candidate in sameTitle)
            {
                if (author.Length == 0 || candidate.Author.Length == 0 || candidate.Author == author)
                    return candidate.BookId;
            }

            return null;
        }
    }
}
