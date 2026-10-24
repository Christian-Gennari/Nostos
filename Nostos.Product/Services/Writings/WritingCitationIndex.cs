using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Services.Writings;

/// <summary>
/// The notes a writing cites, read from its Markdown (issue #1162).
///
/// A writing's content is the source of truth, the way a note's body is for
/// its <c>[[Topic]]</c> links (<see cref="TopicReferences"/>). A live citation
/// is a Markdown link to <c>nostos:cite/&lt;book&gt;?p=..&amp;as=..&amp;src=&lt;note&gt;</c>
/// (the Studio's <c>parseCitationHref</c>); each one whose <c>src</c> names a
/// note in this library is one use of that note. <see cref="WritingCitationModel"/>
/// rows are rebuilt from the content on every save, on import and by the
/// startup pass, and nothing else writes them.
///
/// The parser is tolerant on purpose: hand-edited, half-deleted or pasted
/// links are skipped, never an error, and the text stays exactly as written.
/// </summary>
public static partial class WritingCitationIndex
{
    public const string Scheme = "nostos:cite/";

    // The same shape the Studio's Markdown extension reads as a citation:
    // [text](nostos:cite/…), with backslash escapes inside the text.
    [GeneratedRegex(@"\[((?:\\.|[^\\\]])*)\]\((nostos:cite/[^)\s]+)\)")]
    private static partial Regex CitationLinkRegex();

    // Code is shown as written, never read as a citation.
    [GeneratedRegex(@"(?m)^[ \t]{0,3}(```|~~~)[\s\S]*?(^[ \t]{0,3}\1|\z)")]
    private static partial Regex FencedCodeRegex();

    [GeneratedRegex(@"`[^`\n]*`")]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex IdRegex();

    private const int LookupChunk = 500;

    /// <summary>One parsed citation address; null fields were absent or invalid.</summary>
    public sealed record CitationRef(string Book, int? Page, string? Src, string? As);

    /// <summary>
    /// <c>nostos:cite/&lt;book&gt;?p=42&amp;as=quote&amp;src=&lt;id&gt;</c> → its parts,
    /// or null for any other address. Mirrors the Studio's <c>parseCitationHref</c>.
    /// </summary>
    public static CitationRef? ParseHref(string? href)
    {
        var value = (href ?? string.Empty).Trim();
        if (!value.StartsWith(Scheme, StringComparison.Ordinal))
            return null;

        var rest = value[Scheme.Length..];
        var queryAt = rest.IndexOf('?');
        var path = queryAt < 0 ? rest : rest[..queryAt];
        var query = queryAt < 0 ? string.Empty : rest[(queryAt + 1)..];

        var book = CleanId(path);
        if (book is null)
            return null;

        // URLSearchParams.get: the first value of a repeated key wins. An HTML
        // round trip may have written & as &amp;.
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Replace("&amp;", "&", StringComparison.Ordinal).Split('&'))
        {
            if (pair.Length == 0)
                continue;
            var equals = pair.IndexOf('=');
            var key = Decode(equals < 0 ? pair : pair[..equals]);
            var val = Decode(equals < 0 ? string.Empty : pair[(equals + 1)..]);
            parameters.TryAdd(key, val);
        }

        int? page = parameters.TryGetValue("p", out var p) && int.TryParse(p, out var n) && n > 0 ? n : null;
        var role = parameters.TryGetValue("as", out var a) && a is "quote" or "note" or "reference" or "bib" ? a : null;
        var src = parameters.TryGetValue("src", out var s) ? CleanId(s) : null;
        return new CitationRef(book, page, src, role);
    }

    /// <summary>
    /// Every live citation in <paramref name="content"/>, in order. Malformed
    /// links and links inside code are skipped.
    /// </summary>
    public static IReadOnlyList<CitationRef> Citations(string? content)
    {
        if (string.IsNullOrEmpty(content) || !content.Contains(Scheme, StringComparison.Ordinal))
            return [];

        var prose = InlineCodeRegex().Replace(FencedCodeRegex().Replace(content, string.Empty), string.Empty);
        return CitationLinkRegex().Matches(prose)
            .Select(match => ParseHref(match.Groups[2].Value))
            .OfType<CitationRef>()
            .ToList();
    }

    /// <summary>
    /// <paramref name="content"/> with every live citation replaced by the
    /// words it was last written with, so nothing of Nostos is left in it
    /// (the Markdown export of notes, issue #1221). A citation shown as code
    /// stays as written; every other character is kept.
    /// </summary>
    public static string CitationsAsWords(string? content)
    {
        var text = content ?? string.Empty;
        if (!text.Contains(Scheme, StringComparison.Ordinal))
            return text;

        var code = FencedCodeRegex().Matches(text).Cast<Match>()
            .Concat(InlineCodeRegex().Matches(text).Cast<Match>())
            .Select(match => (match.Index, End: match.Index + match.Length))
            .ToList();
        return CitationLinkRegex().Replace(text, match =>
            code.Any(span => match.Index >= span.Index && match.Index < span.End)
            || ParseHref(match.Groups[2].Value) is null
                ? match.Value
                : match.Groups[1].Value);
    }

    /// <summary>
    /// The notes <paramref name="content"/> cites, once each. A citation with
    /// no <c>src</c> (book only), a bibliography entry (<c>as=bib</c>) and a
    /// <c>src</c> that is not a note id never count.
    /// </summary>
    public static IReadOnlySet<Guid> CitedNoteIds(string? content)
    {
        var ids = new HashSet<Guid>();
        foreach (var citation in Citations(content))
        {
            if (citation.As == "bib" || citation.Src is null)
                continue;
            if (Guid.TryParse(citation.Src, out var id) && id != Guid.Empty)
                ids.Add(id);
        }
        return ids;
    }

    /// <summary>
    /// Rebuilds one writing's rows from <paramref name="content"/> and saves.
    /// Only notes that exist in this library get a row. Call it inside the
    /// transaction that saved the content, after that save, so the writing's
    /// row lock orders two concurrent saves and the last one's rows match its
    /// content.
    /// </summary>
    public static async Task SyncAsync(
        NostosDbContext db, Guid writingId, string? content, CancellationToken ct = default)
    {
        var existing = await ExistingNoteIdsAsync(db, CitedNoteIds(content), ct);

        await db.WritingCitations
            .Where(c => c.WritingId == writingId)
            .ExecuteDeleteAsync(ct);

        if (existing.Count == 0)
            return;

        db.WritingCitations.AddRange(existing.Select(noteId => new WritingCitationModel
        {
            WritingId = writingId,
            NoteId = noteId,
        }));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Adds the rows for freshly materialized writings (portable import). The
    /// caller owns <c>SaveChangesAsync</c>; ids are kept on import, so every
    /// <c>src</c> still names the same note.
    /// </summary>
    public static void AddForImport(
        NostosDbContext db,
        IReadOnlyDictionary<Guid, WritingModel> writings,
        IReadOnlyDictionary<Guid, NoteModel> notes)
    {
        foreach (var writing in writings.Values)
        {
            if (writing.Type != WritingType.Document)
                continue;
            foreach (var noteId in CitedNoteIds(writing.Content))
            {
                if (!notes.TryGetValue(noteId, out var note))
                    continue;
                db.WritingCitations.Add(new WritingCitationModel
                {
                    WritingId = writing.Id,
                    Writing = writing,
                    NoteId = noteId,
                    Note = note,
                });
            }
        }
    }

    /// <summary>
    /// Brings every writing's rows in line with its content: the backfill for
    /// libraries that existed before the index, and a self-repair afterwards.
    /// Writes only the difference, so on a library that is already right it
    /// reads and changes nothing. Returns the number of rows added or removed.
    /// </summary>
    public static async Task<int> RebuildAllAsync(NostosDbContext db, CancellationToken ct = default)
    {
        var wanted = new HashSet<(Guid WritingId, Guid NoteId)>();
        var documents = db.Writings
            .AsNoTracking()
            .Where(w => w.Type == WritingType.Document && w.Content != null && w.Content.Contains(Scheme))
            .Select(w => new { w.Id, w.Content })
            .AsAsyncEnumerable();
        await foreach (var writing in documents.WithCancellation(ct))
        {
            foreach (var noteId in CitedNoteIds(writing.Content))
                wanted.Add((writing.Id, noteId));
        }

        var notes = await ExistingNoteIdsAsync(db, wanted.Select(w => w.NoteId).ToHashSet(), ct);
        wanted.RemoveWhere(w => !notes.Contains(w.NoteId));

        var current = (await db.WritingCitations
                .AsNoTracking()
                .Select(c => new { c.WritingId, c.NoteId })
                .ToListAsync(ct))
            .Select(c => (c.WritingId, c.NoteId))
            .ToHashSet();

        var stale = current.Where(row => !wanted.Contains(row)).ToList();
        var missing = wanted.Where(row => !current.Contains(row)).ToList();
        if (stale.Count == 0 && missing.Count == 0)
            return 0;

        db.WritingCitations.RemoveRange(stale.Select(row => new WritingCitationModel
        {
            WritingId = row.WritingId,
            NoteId = row.NoteId,
        }));
        db.WritingCitations.AddRange(missing.Select(row => new WritingCitationModel
        {
            WritingId = row.WritingId,
            NoteId = row.NoteId,
        }));
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return stale.Count + missing.Count;
    }

    private static async Task<HashSet<Guid>> ExistingNoteIdsAsync(
        NostosDbContext db, IReadOnlySet<Guid> ids, CancellationToken ct)
    {
        var found = new HashSet<Guid>();
        foreach (var chunk in ids.Chunk(LookupChunk))
        {
            var batch = chunk.ToList();
            found.UnionWith(await db.Notes
                .AsNoTracking()
                .Where(n => batch.Contains(n.Id))
                .Select(n => n.Id)
                .ToListAsync(ct));
        }
        return found;
    }

    private static string? CleanId(string? value)
    {
        var id = (value ?? string.Empty).Trim();
        return IdRegex().IsMatch(id) ? id : null;
    }

    private static string Decode(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            return value;
        }
    }
}
