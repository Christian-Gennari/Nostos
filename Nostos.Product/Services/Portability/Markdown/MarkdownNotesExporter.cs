using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Services.Portability.Markdown;

/// <summary>
/// Notes, highlights and passages as a .zip of Markdown files (issue #1221),
/// for other tools (Obsidian, a plain folder) and as the answer to "how would
/// I leave?". The .nostos archive moves a library between Nostos installations;
/// this one is for reading and keeping elsewhere, and is never imported back.
///
/// Memory stays flat on a large library: books are read one at a time with
/// their notes, each file is written to the archive as soon as it is made, and
/// what a topic file needs of a note is kept as a short excerpt. The archive
/// is built in a temporary file that deletes itself when the download ends, so
/// a failure is an ordinary error response, never a truncated download.
/// </summary>
public interface IMarkdownNotesExporter
{
    /// <summary>
    /// Writes the archive to a temporary file and returns it positioned at its
    /// start; disposing the stream deletes the file.
    /// </summary>
    Task<Stream> ExportAsync(bool includeWritings, CancellationToken cancellationToken = default);
}

public sealed class MarkdownNotesExporter(NostosDbContext db) : IMarkdownNotesExporter
{
    public const string RootFolder = "Nostos notes";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public async Task<Stream> ExportAsync(bool includeWritings, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nostos-notes-{Guid.NewGuid():N}.zip");
        var file = new FileStream(
            path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            await using (var archive = await ZipArchive.CreateAsync(
                file, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, cancellationToken))
            {
                await WriteAsync(archive, includeWritings, cancellationToken);
            }
            file.Position = 0;
            return file;
        }
        catch
        {
            await file.DisposeAsync();
            throw;
        }
    }

    private async Task WriteAsync(ZipArchive archive, bool includeWritings, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var topics = await db.Topics.AsNoTracking()
            .OrderBy(t => t.NormalizedName).ThenBy(t => t.Id)
            .Select(t => new MarkdownTopic(t.Id, t.Topic, t.Description))
            .ToListAsync(ct);

        // Topic files take their names first: a note's [[Topic]] must resolve
        // to the topic, so no book may take a topic's name.
        var names = new MarkdownNameAllocator();
        var topicFiles = topics.ToDictionary(t => t.Id, t => names.Claim(t.Name, fallback: "Topic"));
        var topicEntries = topics.ToDictionary(t => t.Id, _ => new List<MarkdownTopicEntry>());

        var bookIdsWithNotes = (await db.Notes.AsNoTracking()
            .Select(n => n.BookId).Distinct().ToListAsync(ct)).ToHashSet();

        // Oldest first, so an older book keeps its name when a newer one shares its title.
        var books = (await db.Books.AsNoTracking()
            .OrderBy(b => b.CreatedAt).ThenBy(b => b.Id)
            .Select(b => new MarkdownBook(
                b.Id,
                b.Title,
                b.Metadata.Subtitle,
                b.Author,
                b.Metadata.Translator,
                b.Metadata.Editor,
                b is AudioBookModel ? ((AudioBookModel)b).Narrator : null,
                b.Metadata.PublishedDate,
                b.Metadata.Publisher,
                b is EBookModel ? ((EBookModel)b).Isbn : b is PhysicalBookModel ? ((PhysicalBookModel)b).Isbn : null,
                b is AudioBookModel ? ((AudioBookModel)b).Asin : null,
                b is AudioBookModel ? "audiobook" : b is PhysicalBookModel ? "physical" : "ebook",
                b.FileDetails.FileName,
                b.FileDetails.TracksJson != null,
                b is AudioBookModel ? b.FileDetails.ChaptersJson : null))
            .ToListAsync(ct))
            .Where(b => bookIdsWithNotes.Contains(b.Id))
            .ToList();

        var noteCount = 0;
        foreach (var book in books)
        {
            ct.ThrowIfCancellationRequested();
            var notes = await db.Notes.AsNoTracking()
                .Where(n => n.BookId == book.Id)
                .Select(n => new MarkdownNote(
                    n.Id,
                    n.Content,
                    n.CfiRange,
                    n.SelectedText,
                    n.CreatedAt,
                    n.Kind,
                    n.SourceAnchorKind,
                    n.SourceAnchorValue,
                    n.AnchorVerified,
                    n.PassageTitle,
                    n.PassageEnd,
                    n.NoteTopics.Select(t => t.TopicId).ToList()))
                .ToListAsync(ct);
            var ordered = MarkdownNotesRenderer.InReadingOrder(notes);
            noteCount += ordered.Count;

            var fileName = names.Claim(book.Title, MarkdownNotesRenderer.Authors(book.Author).FirstOrDefault(), fallback: "Untitled book");
            await AddAsync(archive, $"{MarkdownNotesRenderer.BooksFolder}/{fileName}.md",
                MarkdownNotesRenderer.Book(book, ordered), now, ct);

            var chapters = MarkdownNotesRenderer.AudioChapters(book.ChaptersJson);
            foreach (var note in ordered)
            {
                foreach (var topicId in note.TopicIds.Distinct())
                {
                    if (topicEntries.TryGetValue(topicId, out var entries))
                        entries.Add(MarkdownNotesRenderer.TopicEntry(fileName, book, note, chapters));
                }
            }
        }

        foreach (var topic in topics)
        {
            ct.ThrowIfCancellationRequested();
            await AddAsync(archive, $"{MarkdownNotesRenderer.TopicsFolder}/{topicFiles[topic.Id]}.md",
                MarkdownNotesRenderer.Topic(topic, topicFiles[topic.Id], topicEntries[topic.Id]), now, ct);
        }

        int? writings = includeWritings ? await WriteWritingsAsync(archive, now, ct) : null;

        await AddAsync(archive, "README.md",
            MarkdownNotesRenderer.Readme(now, books.Count, noteCount, topics.Count, writings), now, ct);
    }

    /// <summary>
    /// The Writing Studio's documents in their folders, each its Markdown with
    /// citations as their words. Contents are read one document at a time.
    /// </summary>
    private async Task<int> WriteWritingsAsync(ZipArchive archive, DateTime now, CancellationToken ct)
    {
        var items = await db.Writings.AsNoTracking()
            .OrderBy(w => w.CreatedAt).ThenBy(w => w.Id)
            .Select(w => new { w.Id, w.Name, w.Type, w.ParentId, w.CreatedAt, w.UpdatedAt })
            .ToListAsync(ct);
        var byId = items.ToDictionary(w => w.Id);
        var folders = new Dictionary<Guid, MarkdownNameAllocator>();
        var folderPaths = new Dictionary<Guid, string>();

        // Guid.Empty is the top of Writings/.
        MarkdownNameAllocator In(Guid? folder) =>
            folders.TryGetValue(folder ?? Guid.Empty, out var allocator)
                ? allocator
                : folders[folder ?? Guid.Empty] = new MarkdownNameAllocator();

        // A folder's path, made once; a broken or circular parent puts it at the top.
        string FolderPath(Guid id, HashSet<Guid> seen)
        {
            if (folderPaths.TryGetValue(id, out var known)) return known;
            var item = byId[id];
            var parent = item.ParentId is { } p && byId.TryGetValue(p, out var up) && up.Type == WritingType.Folder && seen.Add(p)
                ? FolderPath(p, seen)
                : MarkdownNotesRenderer.WritingsFolder;
            var parentKey = parent == MarkdownNotesRenderer.WritingsFolder ? null : item.ParentId;
            return folderPaths[id] = $"{parent}/{In(parentKey).Claim(item.Name, extension: string.Empty, fallback: "Folder")}";
        }

        foreach (var folder in items.Where(w => w.Type == WritingType.Folder))
            FolderPath(folder.Id, [folder.Id]);

        var count = 0;
        foreach (var document in items.Where(w => w.Type == WritingType.Document))
        {
            ct.ThrowIfCancellationRequested();
            var parentFolder = document.ParentId is { } p && folderPaths.ContainsKey(p) ? p : (Guid?)null;
            var folder = parentFolder is { } f ? folderPaths[f] : MarkdownNotesRenderer.WritingsFolder;
            var name = In(parentFolder).Claim(document.Name, fallback: "Untitled");
            var content = await db.Writings.AsNoTracking()
                .Where(w => w.Id == document.Id).Select(w => w.Content).FirstOrDefaultAsync(ct);
            await AddAsync(archive, $"{folder}/{name}.md",
                MarkdownNotesRenderer.Writing(document.Name, document.CreatedAt, document.UpdatedAt, content), now, ct);
            count++;
        }
        return count;
    }

    private static async Task AddAsync(ZipArchive archive, string path, string content, DateTime now, CancellationToken ct)
    {
        var entry = archive.CreateEntry($"{RootFolder}/{path}", CompressionLevel.Optimal);
        entry.LastWriteTime = now;
        await using var stream = await entry.OpenAsync(ct);
        await stream.WriteAsync(Utf8.GetBytes(content), ct);
    }
}
