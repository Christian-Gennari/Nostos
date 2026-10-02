using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Services.Library;

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
            var fingerprint = Fingerprint(
                match.BookId!.Value,
                anchorKind,
                anchorValue,
                selectedText,
                content);

            anchorValue ??= $"annotation:{fingerprint}";

            var alreadyImported = await _db.Notes.AsNoTracking().AnyAsync(note =>
                note.BookId == match.BookId.Value
                && note.CaptureSource == "import"
                && note.SourceAnchorKind == anchorKind
                && note.SourceAnchorValue == anchorValue
                && note.SelectedText == selectedText
                && note.Content == content,
                ct);

            if (alreadyImported)
            {
                duplicates++;
                continue;
            }

            var result = await _notes.CaptureAsync(new CaptureNoteRequest(
                BookId: match.BookId.Value,
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

        var page = Clean(annotation.PageNumber) ?? Clean(annotation.Page);
        return page is null
            ? ("koreader_unknown", null)
            : ("koreader_page", page);
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
