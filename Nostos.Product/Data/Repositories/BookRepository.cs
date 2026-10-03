using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;

namespace Nostos.Backend.Data.Repositories;

public class BookRepository : IBookRepository
{
    // "The library's downloadable books" — one definition so the OPDS feed's
    // page query and its total count can never disagree about what the
    // catalogue contains.
    private static readonly Expression<Func<BookModel, bool>> BooksWithFiles = b =>
        b.FileDetails.HasFile && b.FileDetails.FileName != null;

    private readonly NostosDbContext _db;

    public BookRepository(NostosDbContext db)
    {
        _db = db;
    }

    public async Task<BookModel?> GetByIdAsync(Guid id)
    {
        return await _db.Books.FindAsync(id);
    }

    public async Task<int> CountBooksWithFilesAsync()
    {
        return await _db.Books.AsNoTracking().CountAsync(BooksWithFiles);
    }

    public async Task<List<BookModel>> GetBooksWithFilesPageAsync(int skip, int take)
    {
        return await _db
            .Books.AsNoTracking()
            .Where(BooksWithFiles)
            // Creation date is not unique (an import can create several books in
            // the same tick), and SQLite is free to return tied rows in any
            // order — which would let a paged feed repeat one book and skip
            // another. The id is the tiebreaker that makes the ordering total.
            .OrderByDescending(b => b.CreatedAt)
            .ThenBy(b => b.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync();
    }

    public async Task UpdateAsync(BookModel book)
    {
        // Entity is likely already tracked, so Update usually just means SaveChanges
        // But explicit Update call is safe.
        _db.Books.Update(book);
        await _db.SaveChangesAsync();
    }
}
