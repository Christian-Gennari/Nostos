using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Notes;
using Nostos.Backend.Services.Portability.Markdown;

namespace Nostos.Backend.Services.Carrel;

/// <summary>
/// The Carrel Sync Protocol's library feed: the read-only projection of a
/// person's notes, their books, topics and a ready-to-use citation, plus the
/// rows that remember what each device was already told.
///
/// <para>Highlights, notes and passages are exported with their book, topics
/// and citation. The same reconciliation idea gives deletions: a note that is
/// gone (deleted alone, or with its book or through an import's bulk delete)
/// is reported as a tombstone instead of silently disappearing from a device
/// that cached it.</para>
///
/// <para>Text a device quotes is the text the person reads: a
/// <c>[[Topic]]</c> reference becomes its name, by the same rules as
/// <see cref="TopicReferences"/>, and text that only looks like a reference
/// (<c>[[a|b]]</c>, an unclosed <c>[[</c>) stays exactly as typed.</para>
/// </summary>
public static class CarrelLibrarySnapshotBuilder
{
    /// <summary>
    /// The page a device pulls: the books its live items name, the items changed
    /// after <paramref name="sinceValue"/>, the notes deleted since then, and
    /// the cursor to resume from.
    /// </summary>
    public static async Task<CarrelLibraryPage> PageAsync(
        NostosDbContext db,
        string ownerKey,
        long sinceValue,
        string baseUrl,
        CancellationToken ct)
    {
        var mappings = await db.CarrelLibraryItems
            .AsNoTracking()
            .Where(i => i.OwnerKey == ownerKey)
            .ToListAsync(ct);
        var changed = mappings.Where(i => i.Cursor > sinceValue).ToList();

        // Only the items this sync returns need their full text; the rest of
        // the library was compared on its version, book and topics alone.
        var wanted = changed.Where(i => !i.Deleted).Select(i => i.NoteId).ToList();
        var notes = await LoadNotesAsync(db, wanted, ct);
        var topics = await LoadTopicsAsync(db, wanted, ct);

        var items = new List<CarrelLibraryItem>(wanted.Count);
        foreach (var note in notes.Values)
        {
            items.Add(ToLibraryItem(note, topics.GetValueOrDefault(note.Id, []), baseUrl));
        }

        // Every book a live item names, so a device can refresh its shelves.
        var books = await db.Notes
            .AsNoTracking()
            .Where(n => db.CarrelLibraryItems.Any(
                i => i.OwnerKey == ownerKey && !i.Deleted && i.NoteId == n.Id))
            .Select(n => new { n.BookId, n.Book!.Title, n.Book.Author })
            .ToListAsync(ct);

        var deleted = changed.Where(i => i.Deleted).Select(i => i.NoteId.ToString("D")).ToList();
        var cursor = changed.Count == 0
            ? sinceValue
            : Math.Max(sinceValue, changed.Max(i => i.Cursor));
        return new CarrelLibraryPage(
            books
                .GroupBy(b => b.BookId)
                .Select(g => new CarrelLibraryBook(g.Key.ToString("D"), g.First().Title, g.First().Author))
                .OrderBy(b => b.Title, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            items.OrderBy(i => i.Id, StringComparer.Ordinal).ToList(),
            deleted.OrderBy(id => id, StringComparer.Ordinal).ToList(),
            cursor.ToString(CultureInfo.InvariantCulture));
    }

    private sealed record LibraryRow(
        Guid Id,
        Guid BookId,
        DateTime CreatedAt,
        DateTime? UpdatedAt,
        string Kind,
        string? PassageTitle,
        string? BookTitle,
        string? BookAuthor);

    /// <summary>
    /// Brings the exported-note rows in line with the library. A note is new or
    /// changed when its version, book headline, topics or kind differ; a note
    /// that is gone (deleted alone, with its book, or by an import's bulk
    /// delete) is a tombstone, because a deleted row cannot describe itself.
    /// </summary>
    public static async Task ReconcileAsync(
        NostosDbContext db, string ownerKey, DateTime now, CancellationToken ct)
    {
        var notes = await db.Notes
            .AsNoTracking()
            .Where(n => n.Book != null)
            .Select(n => new LibraryRow(
                n.Id,
                n.BookId,
                n.CreatedAt,
                n.UpdatedAt,
                n.Kind,
                n.PassageTitle,
                n.Book!.Title,
                n.Book.Author))
            .ToListAsync(ct);

        var topicRows = await db.NoteTopics
            .AsNoTracking()
            .Select(nt => new { nt.NoteId, Name = nt.Topic.Topic })
            .ToListAsync(ct);
        var topicsByNote = topicRows
            .GroupBy(t => t.NoteId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToList());

        var items = await db.CarrelLibraryItems
            .Where(i => i.OwnerKey == ownerKey)
            .ToListAsync(ct);
        var byNote = items.ToDictionary(i => i.NoteId);
        var touched = new List<CarrelLibraryItemModel>();

        foreach (var note in notes)
        {
            var signature = Signature(note, topicsByNote.GetValueOrDefault(note.Id, []));
            if (!byNote.TryGetValue(note.Id, out var item))
            {
                item = new CarrelLibraryItemModel
                {
                    OwnerKey = ownerKey,
                    NoteId = note.Id,
                    Signature = signature,
                    Deleted = false,
                    ModifiedAtUtc = now,
                };
                db.CarrelLibraryItems.Add(item);
                touched.Add(item);
                continue;
            }

            if (!item.Deleted && item.Signature == signature)
                continue;

            item.Signature = signature;
            item.Deleted = false;
            item.ModifiedAtUtc = now;
            touched.Add(item);
        }

        var live = notes.Select(n => n.Id).ToHashSet();
        foreach (var item in items.Where(i => !i.Deleted && !live.Contains(i.NoteId)))
        {
            item.Deleted = true;
            item.ModifiedAtUtc = now;
            touched.Add(item);
        }

        await CarrelCursors.StampLibraryAsync(db, ownerKey, touched, now, ct);
    }

    /// <summary>
    /// The version that decides whether a device is told about an item: the
    /// note's own version plus the book headline and topics, none of which
    /// moves the note's <c>UpdatedAt</c>. The text itself is not hashed again;
    /// every edit to it stamps <c>UpdatedAt</c> in the context's save pipeline.
    /// </summary>
    private static string Signature(LibraryRow note, IReadOnlyList<string> topics)
    {
        var canonical = string.Join(
            '\u001f',
            note.Id.ToString("D"),
            (note.UpdatedAt ?? note.CreatedAt).Ticks.ToString(CultureInfo.InvariantCulture),
            note.Kind ?? string.Empty,
            note.PassageTitle ?? string.Empty,
            note.BookId.ToString("D"),
            note.BookTitle ?? string.Empty,
            note.BookAuthor ?? string.Empty,
            string.Join('\u001e', topics));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private sealed record FullNote(
        Guid Id,
        Guid BookId,
        string? Content,
        string? SelectedText,
        string? CfiRange,
        DateTime CreatedAt,
        string Kind,
        string? PassageTitle,
        string? PassageEnd,
        string SourceAnchorKind,
        string? SourceAnchorValue,
        bool AnchorVerified,
        string? BookTitle,
        string? BookAuthor);

    private static async Task<Dictionary<Guid, FullNote>> LoadNotesAsync(
        NostosDbContext db, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var result = new Dictionary<Guid, FullNote>();
        foreach (var chunk in ids.Chunk(400))
        {
            var batch = chunk.ToList();
            var rows = await db.Notes
                .AsNoTracking()
                .Where(n => batch.Contains(n.Id) && n.Book != null)
                .Select(n => new FullNote(
                    n.Id,
                    n.BookId,
                    n.Content,
                    n.SelectedText,
                    n.CfiRange,
                    n.CreatedAt,
                    n.Kind,
                    n.PassageTitle,
                    n.PassageEnd,
                    n.SourceAnchorKind,
                    n.SourceAnchorValue,
                    n.AnchorVerified,
                    n.Book!.Title,
                    n.Book.Author))
                .ToListAsync(ct);
            foreach (var row in rows)
                result[row.Id] = row;
        }

        return result;
    }

    private static async Task<Dictionary<Guid, IReadOnlyList<string>>> LoadTopicsAsync(
        NostosDbContext db, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var result = new Dictionary<Guid, IReadOnlyList<string>>();
        foreach (var chunk in ids.Chunk(400))
        {
            var batch = chunk.ToList();
            var rows = await db.NoteTopics
                .AsNoTracking()
                .Where(nt => batch.Contains(nt.NoteId))
                .Select(nt => new { nt.NoteId, Name = nt.Topic.Topic })
                .ToListAsync(ct);
            foreach (var group in rows.GroupBy(r => r.NoteId))
            {
                result[group.Key] = group
                    .Select(r => r.Name)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        return result;
    }

    private static CarrelLibraryItem ToLibraryItem(
        FullNote note, IReadOnlyList<string> topics, string baseUrl)
    {
        var kind = note.Kind == NoteKinds.Passage
            ? "passage"
            : string.IsNullOrWhiteSpace(note.SelectedText) ? "note" : "highlight";
        var thought = TopicReferences.AsPlainWords(MarkdownNotesRenderer.Thought(note.Content));
        var text = kind == "note"
            ? thought ?? TopicReferences.AsPlainWords(Clean(note.Content)) ?? string.Empty
            : (note.SelectedText ?? string.Empty).Trim();

        var located = MarkdownNotesRenderer.Locate(new MarkdownNote(
            note.Id,
            note.Content ?? string.Empty,
            note.CfiRange,
            note.SelectedText,
            note.CreatedAt,
            note.Kind,
            note.SourceAnchorKind,
            note.SourceAnchorValue,
            note.AnchorVerified,
            note.PassageTitle,
            note.PassageEnd,
            [])).Words;
        var passageTitle = kind == "passage" ? Clean(note.PassageTitle) : null;
        var location = located ?? (passageTitle is null ? null : $"“{passageTitle}”");

        return new CarrelLibraryItem(
            note.Id.ToString("D"),
            kind,
            note.BookId.ToString("D"),
            text,
            kind == "note" ? null : thought,
            location,
            topics,
            new CarrelLibraryCitation(
                CitationMarkdown(note.BookAuthor, note.BookTitle, located, passageTitle),
                $"{baseUrl.TrimEnd('/')}/brain/notes/{note.Id:D}"));
    }

    /// <summary>
    /// The footnote text for one quote: <c>Author, *Title*, p. 42</c>. A passage
    /// is named rather than paged, as the Studio's house citation style does.
    /// </summary>
    public static string CitationMarkdown(
        string? author, string? title, string? location, string? passageTitle)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(author))
            parts.Add(MarkdownNotesRenderer.EscapeText(Clean(author)!));
        var cleanTitle = MarkdownNotesRenderer.EscapeText(Clean(title) ?? "Untitled");
        parts.Add($"*{cleanTitle}*");
        if (!string.IsNullOrWhiteSpace(passageTitle))
            parts.Add($"“{MarkdownNotesRenderer.EscapeText(Clean(passageTitle)!)}”");
        else if (CitationLocator(location) is { } locator)
            parts.Add(locator);
        return string.Join(", ", parts);
    }

    /// <summary><c>Page 42</c> → <c>p. 42</c>, in the Studio's house style. Anything else is kept.</summary>
    private static string? CitationLocator(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
            return null;
        var value = location.Trim();
        if (value.StartsWith("Pages ", StringComparison.Ordinal))
            return "pp. " + value["Pages ".Length..];
        if (value.StartsWith("Page ", StringComparison.Ordinal))
            return "p. " + value["Page ".Length..];
        if (value.StartsWith("At ", StringComparison.Ordinal))
            return "at " + value["At ".Length..];
        if (value.StartsWith("From ", StringComparison.Ordinal) && value.Contains(" to ", StringComparison.Ordinal))
            return "at " + value["From ".Length..].Replace(" to ", "–", StringComparison.Ordinal);
        if (value.StartsWith("In ", StringComparison.Ordinal))
            return value["In ".Length..];
        return value;
    }

    private static string? Clean(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length == 0 ? null : text;
    }
}
