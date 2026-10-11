using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Services.Library;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Services.Notes.Imports;

public sealed class KoreaderNoteImportService
{
    internal const string ClientId = "koreader-import";
    private readonly NostosDbContext _db;
    private readonly INoteService _notes;

    public KoreaderNoteImportService(NostosDbContext db, INoteService notes)
    {
        _db = db;
        _notes = notes;
    }

    public async Task<KoreaderImportReport> ImportAsync(
        string metadataLua,
        CancellationToken ct = default)
    {
        var document = KoreaderMetadataParser.Parse(metadataLua);
        var match = await MatchBookAsync(document, ct);
        if (match.Status != "matched")
        {
            return new KoreaderImportReport(
                match.Status,
                null,
                null,
                document.Annotations.Count,
                0,
                0,
                document.Annotations.Count,
                match.Message);
        }

        var imported = 0;
        var duplicates = 0;
        var skipped = 0;
        var bookId = match.BookId!.Value;
        var existing = await ImportedNotesAsync(_db, bookId, ct);

        foreach (var annotation in document.Annotations)
        {
            var selectedText = Clean(annotation.Text);
            var content = Clean(annotation.Note) ?? string.Empty;
            if (selectedText is null && string.IsNullOrWhiteSpace(content))
            {
                skipped++;
                continue;
            }

            var (anchorKind, anchorValue) = Anchor(annotation);
            var fingerprint = Fingerprint(bookId, anchorKind, anchorValue, selectedText, content);

            anchorValue ??= $"annotation:{fingerprint}";

            if (existing.ContainsKey(Identity(anchorKind, anchorValue, selectedText, content)))
            {
                duplicates++;
                continue;
            }

            var earlier = EarlierShapes(annotation, selectedText, content)
                .Select(shape => Identity(shape.AnchorKind, shape.AnchorValue, shape.Text, shape.Content))
                .FirstOrDefault(existing.ContainsKey);
            if (earlier is not null)
            {
                await RepairAsync(_db, _notes, existing[earlier], anchorKind, anchorValue, selectedText, content, ct);
                existing.Remove(earlier);
                existing[Identity(anchorKind, anchorValue, selectedText, content)] = Guid.Empty;
                duplicates++;
                continue;
            }

            var result = await _notes.CaptureAsync(new CaptureNoteRequest(
                BookId: bookId,
                Content: content,
                SelectedText: selectedText,
                CaptureSource: "import",
                ProcessingMode: "verbatim",
                SourceAnchorKind: anchorKind,
                SourceAnchorValue: anchorValue,
                AnchorVerified: false,
                ClientId: ClientId,
                IdempotencyKey: fingerprint),
                ct);

            if (!result.Success)
                throw new InvalidOperationException(result.ErrorMessage ?? "KOReader note import failed.");

            existing[Identity(anchorKind, anchorValue, selectedText, content)] = result.Value!.Id;
            imported++;
        }

        return new KoreaderImportReport(
            "matched",
            match.BookId,
            match.BookTitle,
            document.Annotations.Count,
            imported,
            duplicates,
            skipped,
            null);
    }

    /// <summary>A book's imported notes, keyed by what makes each one the same annotation.</summary>
    internal static async Task<Dictionary<string, Guid>> ImportedNotesAsync(
        NostosDbContext db,
        Guid bookId,
        CancellationToken ct)
    {
        var notes = await db.Notes
            .AsNoTracking()
            .Where(note => note.BookId == bookId && note.CaptureSource == "import")
            .Select(note => new { note.Id, note.SourceAnchorKind, note.SourceAnchorValue, note.SelectedText, note.Content })
            .ToListAsync(ct);

        var byIdentity = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var note in notes)
            byIdentity.TryAdd(Identity(note.SourceAnchorKind, note.SourceAnchorValue, note.SelectedText, note.Content), note.Id);
        return byIdentity;
    }

    internal static string Identity(string kind, string? value, string? text, string? content) =>
        string.Join('\n', kind, value ?? string.Empty, text ?? string.Empty, content ?? string.Empty);

    /// <summary>
    /// Brings a note stored in an earlier shape up to what the sidecar says.
    /// The text goes through the note service so topics follow it; the anchor
    /// is provenance the service never rewrites.
    /// </summary>
    internal static async Task RepairAsync(
        NostosDbContext db,
        INoteService notes,
        Guid noteId,
        string anchorKind,
        string anchorValue,
        string? selectedText,
        string content,
        CancellationToken ct)
    {
        var result = await notes.UpdateAsync(noteId, new UpdateNoteDto(content, selectedText), ct);
        if (!result.Success)
            throw new InvalidOperationException(result.ErrorMessage ?? "KOReader note repair failed.");

        // Tracked, not a bulk statement: SaveChanges is what advances the
        // library revision for a portable row.
        var stored = await db.Notes.FirstAsync(note => note.Id == noteId, ct);
        stored.SourceAnchorKind = anchorKind;
        stored.SourceAnchorValue = anchorValue;
        await db.SaveChangesAsync(ct);
    }

    private async Task<BookMatch> MatchBookAsync(
        KoreaderMetadataDocument document,
        CancellationToken ct)
    {
        var normalizedIsbn = BookIdentityNormalizer.NormalizeIsbn(document.Isbn);
        List<BookMatchRow> candidates;

        if (normalizedIsbn is not null)
        {
            candidates = await _db.Books
                .AsNoTracking()
                .Where(book => book.NormalizedIsbn == normalizedIsbn)
                .Select(book => new BookMatchRow(book.Id, book.Title))
                .ToListAsync(ct);
        }
        else
        {
            var title = BookIdentityNormalizer.NormalizeTitle(document.Title);
            var author = BookIdentityNormalizer.NormalizeAuthor(document.Author);

            candidates = await _db.Books
                .AsNoTracking()
                .Where(book =>
                    book.Work != null
                    && book.Work.NormalizedTitle == title
                    && (author == string.Empty || book.Work.NormalizedAuthor == author))
                .Select(book => new BookMatchRow(book.Id, book.Title))
                .ToListAsync(ct);
        }

        return candidates.Count switch
        {
            1 => new BookMatch("matched", candidates[0].Id, candidates[0].Title, null),
            0 => new BookMatch(
                "unmatched",
                null,
                null,
                $"No library book matched KOReader metadata '{document.Title ?? document.Isbn}'."),
            _ => new BookMatch(
                "ambiguous",
                null,
                null,
                $"KOReader metadata matched {candidates.Count} library books; no notes were imported."),
        };
    }

    internal static (string Kind, string? Value) Anchor(KoreaderAnnotation annotation)
    {
        if (!string.IsNullOrWhiteSpace(annotation.PositionStart)
            || !string.IsNullOrWhiteSpace(annotation.PositionEnd))
        {
            return (
                "koreader_xpointer",
                $"{annotation.PositionStart ?? string.Empty}|{annotation.PositionEnd ?? string.Empty}");
        }

        // A page bookmark has no selection, but in a reflowable book its
        // `page` is still an xpointer. `pageno` there is only a count of
        // screens at the device's font size, which nothing else can open.
        if (annotation.Page is { } pointer && pointer.StartsWith("/body/", StringComparison.Ordinal))
            return ("koreader_xpointer", $"{pointer}|");

        return PageAnchor(annotation);
    }

    private static (string Kind, string? Value) PageAnchor(KoreaderAnnotation annotation)
    {
        var page = Clean(annotation.PageNumber) ?? Clean(annotation.Page);
        return page is null
            ? ("koreader_unknown", null)
            : ("koreader_page", page);
    }

    /// <summary>
    /// Every shape an earlier version of this import could have stored the
    /// same annotation in. A re-import recognises these and corrects the note
    /// in place rather than adding a second copy beside it:
    /// a value with a line break was cut at the break and kept its Lua quoting,
    /// a page bookmark was anchored to its screen count, and a PDF highlight
    /// lost its text.
    /// </summary>
    internal static IReadOnlyList<ImportedNoteShape> EarlierShapes(
        KoreaderAnnotation annotation,
        string? selectedText,
        string content)
    {
        var current = Anchor(annotation);
        var anchors = new List<(string Kind, string? Value)> { current };
        var page = PageAnchor(annotation);
        if (page != current)
            anchors.Add(page);

        var texts = new List<string?> { selectedText };
        if (CutAtLineBreak(selectedText) is { } cutText)
            texts.Add(cutText);
        if (selectedText is not null && content.Length > 0)
            texts.Add(null);

        var contents = new List<string> { content };
        if (CutAtLineBreak(content) is { } cutContent)
            contents.Add(cutContent);

        var shapes = new List<ImportedNoteShape>();
        foreach (var anchor in anchors)
        {
            foreach (var text in texts)
            {
                foreach (var body in contents)
                {
                    if (anchor == current && text == selectedText && body == content)
                        continue;
                    shapes.Add(new ImportedNoteShape(anchor.Kind, anchor.Value, text, body));
                }
            }
        }

        return shapes;
    }

    // The line-based reader kept the first source line of a %q string as it
    // stood: opening quote, escapes unresolved, and the backslash that
    // continued it onto the next line.
    private static string? CutAtLineBreak(string? value)
    {
        var lineBreak = value?.IndexOf('\n') ?? -1;
        if (lineBreak < 0)
            return null;

        var firstLine = value![..lineBreak].Replace("\\", "\\\\").Replace("\"", "\\\"");
        return Clean($"\"{firstLine}\\");
    }

    internal static string Fingerprint(
        Guid bookId,
        string anchorKind,
        string? anchorValue,
        string? selectedText,
        string content)
    {
        var canonical = string.Join(
            "\n",
            bookId.ToString("D"),
            anchorKind,
            anchorValue ?? string.Empty,
            selectedText ?? string.Empty,
            content);

        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    internal static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record BookMatch(
        string Status,
        Guid? BookId,
        string? BookTitle,
        string? Message);

    private sealed record BookMatchRow(Guid Id, string Title);
}

internal sealed record ImportedNoteShape(
    string AnchorKind,
    string? AnchorValue,
    string? Text,
    string Content);
