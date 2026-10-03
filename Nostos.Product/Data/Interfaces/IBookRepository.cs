using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Data.Interfaces;

public interface IBookRepository
{
    Task<BookModel?> GetByIdAsync(Guid id);

    /// <summary>
    /// Number of books that have an uploaded file. Used by the OPDS catalog
    /// feed to describe the whole collection without materializing it.
    /// </summary>
    Task<int> CountBooksWithFilesAsync();

    /// <summary>
    /// One page of books that have an uploaded file, newest first. Used by the
    /// OPDS catalog feed.
    ///
    /// The ordering is total (creation date descending, then id) so that
    /// consecutive pages cannot repeat or skip a book at a shared timestamp.
    /// </summary>
    Task<List<BookModel>> GetBooksWithFilesPageAsync(int skip, int take);

    Task UpdateAsync(BookModel book);
}
