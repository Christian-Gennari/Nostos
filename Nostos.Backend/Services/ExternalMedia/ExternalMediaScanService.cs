using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Services.Library;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Services.ExternalMedia;

/// <summary>A candidate as the review screen shows it.</summary>
public sealed record ExternalMediaScanItem(
    string Key,
    string Kind,
    string Layout,
    string Location,
    string Status,
    string Title,
    string? Author,
    string? Narrator,
    string? Language,
    int FileCount,
    long TotalBytes,
    IReadOnlyList<string> Concerns,
    Guid? LinkedBookId,
    IReadOnlyList<ExternalMediaLibraryMatch> LibraryMatches)
{
    /// <summary>Nothing stands in the way of importing it.</summary>
    public const string Ready = "ready";

    /// <summary>Already a book in this library; importing it again does nothing.</summary>
    public const string Linked = "linked";

    /// <summary>Importable, but only when chosen by hand.</summary>
    public const string Review = "review";

    /// <summary>Cannot be imported: unreadable, or beyond what a book can hold.</summary>
    public const string Unusable = "unusable";
}

/// <summary>A book already in the library that a candidate might duplicate.</summary>
public sealed record ExternalMediaLibraryMatch(Guid BookId, string Title, string? Author);

/// <summary>A linked book whose source file is no longer where it was.</summary>
public sealed record ExternalMediaMissingBook(Guid BookId, string Title, int MissingFiles);

public sealed record ExternalMediaScanSummary(
    int Discovered,
    int Ready,
    int AlreadyLinked,
    int NeedsReview,
    int Unusable,
    int UnsupportedFiles,
    int MissingBooks,
    int SkippedLinks,
    int UnreadableFolders,
    int UnusableNames,
    bool Truncated);

public sealed record ExternalMediaScanResult(
    DateTime FinishedAt,
    ExternalMediaScanSummary Summary,
    IReadOnlyList<ExternalMediaScanItem> Items,
    IReadOnlyList<string> UnsupportedSamples,
    IReadOnlyList<ExternalMediaMissingBook> Missing,
    // Kept for the importer, which re-reads every file it links; never serialized.
    IReadOnlyDictionary<string, ExternalMediaCandidate> Candidates);

public sealed record ExternalMediaScanState(
    string Status,
    DateTime? StartedAt,
    int Examined,
    int Total,
    string? Error,
    ExternalMediaScanResult? Result)
{
    public const string Idle = "idle";
    public const string Scanning = "scanning";
    public const string Done = "done";
    public const string Failed = "failed";
}

/// <summary>
/// Runs at most one scan of the external root at a time and keeps the last
/// result for the review screen and the importer.
///
/// A scan only reads: it lists the folder, reads embedded metadata and
/// compares what it found with the library. The result lives in memory; after
/// a restart the folder is simply scanned again.
/// </summary>
public sealed class ExternalMediaScanService(
    ExternalMediaRoot root,
    IExternalMediaDescriber describer,
    IDbContextFactory<NostosDbContext> contexts,
    IServiceScopeFactory scopes,
    ILogger<ExternalMediaScanService> logger)
{
    /// <summary>Upper bound on files examined in one scan.</summary>
    public const int MaxFiles = 250_000;

    private const int UnsupportedSampleCount = 20;

    private readonly Lock _gate = new();
    private ExternalMediaScanState _state = new(ExternalMediaScanState.Idle, null, 0, 0, null, null);
    private Task _running = Task.CompletedTask;

    public ExternalMediaScanState State
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    /// <summary>The task of the scan in progress, or a completed task.</summary>
    public Task Completion
    {
        get
        {
            lock (_gate)
                return _running;
        }
    }

    /// <summary>Starts a scan unless one is already running. Returns false when the folder is not mounted.</summary>
    public bool TryStart(CancellationToken stopping)
    {
        lock (_gate)
        {
            if (_state.Status == ExternalMediaScanState.Scanning)
                return true;
            if (!root.IsMounted)
                return false;

            _state = new ExternalMediaScanState(
                ExternalMediaScanState.Scanning, DateTime.UtcNow, 0, 0, null, _state.Result);
            _running = Task.Run(() => RunAsync(stopping), CancellationToken.None);
            return true;
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            var result = await ScanAsync(ct);
            lock (_gate)
                _state = _state with { Status = ExternalMediaScanState.Done, Error = null, Result = result };
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Scanning the external media folder failed; exception type {ExceptionType}. Details suppressed.",
                ex.GetType().Name);
            lock (_gate)
            {
                _state = _state with
                {
                    Status = ExternalMediaScanState.Failed,
                    Error = "The folder could not be scanned. Check that it is still mounted and readable.",
                };
            }
        }
    }

    internal async Task<ExternalMediaScanResult> ScanAsync(CancellationToken ct)
    {
        var listing = root.List(MaxFiles, ct);
        var grouping = ExternalMediaGrouper.Group(listing.Files);
        Report(0, grouping.Candidates.Count);

        Dictionary<string, Guid> linkedPaths;
        List<ExternalMediaMissingBook> missing;
        await using (var db = await contexts.CreateDbContextAsync(ct))
        {
            var links = await db.ExternalMediaLinks
                .AsNoTracking()
                .Where(link => link.RootId == root.RootId)
                .Select(link => new { link.BookId, link.RelativePath, Title = link.Book!.Title })
                .ToListAsync(ct);

            linkedPaths = links.ToDictionary(link => link.RelativePath, link => link.BookId, StringComparer.Ordinal);
            var present = listing.Files.Select(file => file.RelativePath).ToHashSet(StringComparer.Ordinal);
            missing = links
                .Where(link => !present.Contains(link.RelativePath))
                .GroupBy(link => (link.BookId, link.Title))
                .Select(group => new ExternalMediaMissingBook(group.Key.BookId, group.Key.Title, group.Count()))
                .OrderBy(book => book.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var items = new List<ExternalMediaScanItem>(grouping.Candidates.Count);
        using var scope = scopes.CreateScope();
        var library = scope.ServiceProvider.GetRequiredService<ILibraryService>();

        foreach (var candidate in grouping.Candidates)
        {
            ct.ThrowIfCancellationRequested();
            items.Add(await DescribeAsync(candidate, linkedPaths, library, ct));
            Report(items.Count, grouping.Candidates.Count);
        }

        var summary = new ExternalMediaScanSummary(
            Discovered: items.Count,
            Ready: items.Count(item => item.Status == ExternalMediaScanItem.Ready),
            AlreadyLinked: items.Count(item => item.Status == ExternalMediaScanItem.Linked),
            NeedsReview: items.Count(item => item.Status == ExternalMediaScanItem.Review),
            Unusable: items.Count(item => item.Status == ExternalMediaScanItem.Unusable),
            UnsupportedFiles: grouping.UnsupportedMedia.Count,
            MissingBooks: missing.Count,
            SkippedLinks: listing.SkippedLinks,
            UnreadableFolders: listing.UnreadableFolders,
            UnusableNames: listing.UnusableNames,
            Truncated: listing.Truncated);

        return new ExternalMediaScanResult(
            DateTime.UtcNow,
            summary,
            items,
            grouping.UnsupportedMedia.Take(UnsupportedSampleCount).ToList(),
            missing,
            grouping.Candidates.ToDictionary(candidate => candidate.Key, StringComparer.Ordinal));
    }

    private async Task<ExternalMediaScanItem> DescribeAsync(
        ExternalMediaCandidate candidate,
        Dictionary<string, Guid> linkedPaths,
        ILibraryService library,
        CancellationToken ct)
    {
        var concerns = candidate.Concerns.ToList();
        var first = candidate.Files[0];

        var linkedBooks = candidate.Files
            .Select(file => linkedPaths.TryGetValue(file.RelativePath, out var bookId) ? bookId : (Guid?)null)
            .ToList();
        var linkedCount = linkedBooks.Count(bookId => bookId is not null);
        var fullyLinked = linkedCount == candidate.Files.Count
            && linkedBooks.Distinct().Count() == 1;

        var description = describer.Describe(root.RootId, first.RelativePath);
        var names = ExternalMediaNaming.For(candidate, description);

        string status;
        var matches = Array.Empty<ExternalMediaLibraryMatch>() as IReadOnlyList<ExternalMediaLibraryMatch>;
        if (fullyLinked)
        {
            status = ExternalMediaScanItem.Linked;
        }
        else if (!description.Readable)
        {
            status = ExternalMediaScanItem.Unusable;
            concerns.Add(candidate.Kind == ExternalMediaCandidate.Audiobook
                ? "The first file could not be read as audio."
                : "The file could not be read as a book.");
        }
        else if (candidate.Blocked)
        {
            status = ExternalMediaScanItem.Unusable;
        }
        else if (linkedCount > 0)
        {
            // New files appeared in a folder that is already a book. Adding
            // tracks to an existing book would move every saved position.
            status = ExternalMediaScanItem.Unusable;
            concerns.Add("Some of these files already belong to a linked book. Remove that book from Nostos and import the folder again to pick up the new files.");
        }
        else
        {
            matches = await FindLibraryMatchesAsync(library, names.Title, names.Author, ct);
            if (matches.Count > 0)
                concerns.Add("A book with this title is already in your library. Importing adds this one as a separate book.");
            status = concerns.Count > 0 ? ExternalMediaScanItem.Review : ExternalMediaScanItem.Ready;
        }

        return new ExternalMediaScanItem(
            candidate.Key,
            candidate.Kind,
            candidate.Layout,
            candidate.Location,
            status,
            names.Title,
            names.Author,
            description.Narrator,
            description.Language,
            candidate.Files.Count,
            candidate.TotalBytes,
            concerns,
            fullyLinked ? linkedBooks[0] : null,
            matches);
    }

    private static async Task<IReadOnlyList<ExternalMediaLibraryMatch>> FindLibraryMatchesAsync(
        ILibraryService library,
        string title,
        string? author,
        CancellationToken ct)
    {
        var resolved = await library.ResolveBookAsync(
            new LibraryResolveBookRequest(Title: title, Author: author, IncludeExternalMetadata: false), ct);

        return resolved.Resolution switch
        {
            LibraryResolution.ExactMatch when resolved.MatchedBook is { } book =>
                [new ExternalMediaLibraryMatch(book.Id, book.Title, book.Author)],
            LibraryResolution.Candidates when resolved.Candidates is { } candidates =>
                candidates.Select(c => new ExternalMediaLibraryMatch(c.BookId, c.Title, c.Author)).ToList(),
            _ => [],
        };
    }

    private void Report(int examined, int total)
    {
        lock (_gate)
            _state = _state with { Examined = examined, Total = total };
    }
}

/// <summary>
/// The title and author a candidate is proposed under: its own tags first,
/// then the names of the file or folder it was found in. Never the names of
/// folders above it, which are as likely to be "Audiobooks" as an author.
/// </summary>
public static class ExternalMediaNaming
{
    public static (string Title, string? Author) For(
        ExternalMediaCandidate candidate,
        ExternalMediaDescription description)
    {
        string? title;
        if (candidate.Layout == ExternalMediaCandidate.Tracks)
        {
            // A track's own title names the track, not the book.
            title = description.Album;
            if (title is null && candidate.Location.Length > 0)
                title = ExternalMediaGrouper.FileNameOf(candidate.Location);
        }
        else
        {
            title = candidate.Kind == ExternalMediaCandidate.Audiobook
                ? description.Album ?? description.Title
                : description.Title;
            title ??= Path.GetFileNameWithoutExtension(ExternalMediaGrouper.FileNameOf(candidate.Location));
        }

        return (string.IsNullOrWhiteSpace(title) ? "Untitled" : title, description.Author);
    }
}
