using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Services.Library;

/// <summary>
/// One-time idempotent backfill of normalized book identity after the
/// AddLibraryCommandSurface migration adds the columns. Runs at startup
/// through DatabaseBootstrapService so legacy rows (created before the
/// library service existed) gain canonical identity using the exact same
/// rules as the live service. Only touches rows whose normalized values
/// differ; safe to run on every boot.
/// </summary>
public static class LibraryIdentityBackfill
{
    public static async Task BackfillAsync(NostosDbContext db, CancellationToken ct = default)
    {
        var books = await db.Books.ToListAsync(ct);
        var works = await db.Works.ToListAsync(ct);

        // Work identity is deliberately a pair rather than a title-only key:
        // the same title by different authors is a different work. Reusing
        // this map also makes the operation idempotent for several legacy
        // books that arrive in the same batch.
        var worksByIdentity = works
            .GroupBy(w => (w.NormalizedTitle, NormalizedAuthor: w.NormalizedAuthor ?? string.Empty))
            .ToDictionary(g => g.Key, g => g.First());

        // Preflight: map every claimed identifier to its book BEFORE writing
        // anything. Conflicting rows must fail startup with an actionable
        // diagnostic, never a raw unique-index exception that bricks every
        // subsequent boot.
        var claims = new Dictionary<string, BookModel>(StringComparer.Ordinal);
        var conflicts = new List<string>();
        foreach (var book in books)
        {
            if (book.NormalizedIsbn is not null)
                claims.TryAdd("isbn:" + book.NormalizedIsbn, book);
            if (book.NormalizedAsin is not null)
                claims.TryAdd("asin:" + book.NormalizedAsin, book);
        }

        foreach (var book in books)
        {
            var (nIsbn, nAsin) = ComputeNormalizedIdentity(book);
            if (nIsbn is not null &&
                claims.TryGetValue("isbn:" + nIsbn, out var isbnHolder) &&
                isbnHolder.Id != book.Id)
            {
                conflicts.Add($"ISBN {nIsbn}: \"{book.Title}\" (id {book.Id}) conflicts with \"{isbnHolder.Title}\" (id {isbnHolder.Id})");
            }
            else if (nIsbn is not null)
            {
                claims.TryAdd("isbn:" + nIsbn, book);
            }

            if (nAsin is not null &&
                claims.TryGetValue("asin:" + nAsin, out var asinHolder) &&
                asinHolder.Id != book.Id)
            {
                conflicts.Add($"ASIN {nAsin}: \"{book.Title}\" (id {book.Id}) conflicts with \"{asinHolder.Title}\" (id {asinHolder.Id})");
            }
            else if (nAsin is not null)
            {
                claims.TryAdd("asin:" + nAsin, book);
            }
        }

        if (conflicts.Count > 0)
        {
            var shown = conflicts.Take(10);
            var more = conflicts.Count > 10 ? $" (+{conflicts.Count - 10} more)" : "";
            throw new InvalidOperationException(
                "Library identity backfill aborted: duplicate identifiers would violate the unique indexes. " +
                "Repair these books before startup. " + string.Join("; ", shown) + more);
        }

        // Apply the (safe) diffs; books and any new works are tracked so a
        // single save persists the complete relationship graph.
        var dirty = false;
        foreach (var book in books)
        {
            var (nIsbn, nAsin) = ComputeNormalizedIdentity(book);
            if (book.NormalizedIsbn != nIsbn || book.NormalizedAsin != nAsin)
            {
                book.NormalizedIsbn = nIsbn;
                book.NormalizedAsin = nAsin;
                dirty = true;
            }

            // A required WorkId was introduced after legacy books already
            // existed. A zero id, or an id whose parent was deleted, is
            // repaired from the same canonical title/author identity used by
            // the live matching service.
            var existingWork = book.WorkId == Guid.Empty
                ? null
                : works.FirstOrDefault(w => w.Id == book.WorkId);
            if (existingWork is null)
            {
                var nTitle = BookIdentityNormalizer.NormalizeTitle(book.Title);
                var nAuthor = BookIdentityNormalizer.NormalizeAuthor(book.Author);
                var key = (nTitle, nAuthor);

                if (!worksByIdentity.TryGetValue(key, out var work))
                {
                    work = new WorkModel
                    {
                        Id = Guid.NewGuid(),
                        Title = book.Title,
                        Author = book.Author,
                        NormalizedTitle = nTitle,
                        NormalizedAuthor = nAuthor,
                        CreatedAt = book.CreatedAt,
                    };
                    db.Works.Add(work);
                    works.Add(work);
                    worksByIdentity[key] = work;
                }

                book.WorkId = work.Id;
                book.Work = work;
                dirty = true;
            }
        }

        // The migration uses a zero-GUID placeholder solely to make the
        // SQLite foreign-key transition safe. Once all books have real work
        // ids it must not remain as a visible orphan row.
        var placeholder = works.FirstOrDefault(w => w.Id == Guid.Empty);
        if (placeholder is not null && !books.Any(b => b.WorkId == Guid.Empty))
        {
            db.Works.Remove(placeholder);
            dirty = true;
        }

        if (dirty)
            await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Canonical normalized identity for a book, type-aware: ISBN identity
    /// only for physical/ebook, ASIN identity only for audiobooks. The
    /// library service must use this same computation everywhere.
    /// </summary>
    public static (string? NormalizedIsbn, string? NormalizedAsin) ComputeNormalizedIdentity(BookModel book)
    {
        var nIsbn = book switch
        {
            PhysicalBookModel p => BookIdentityNormalizer.NormalizeIsbn(p.Isbn),
            EBookModel e => BookIdentityNormalizer.NormalizeIsbn(e.Isbn),
            _ => null,
        };
        var nAsin = book is AudioBookModel audioBook
            ? BookIdentityNormalizer.NormalizeAsin(audioBook.Asin)
            : null;
        return (nIsbn, nAsin);
    }
}
