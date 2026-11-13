using FluentAssertions;
using Nostos.Backend.Data.Interfaces;
using Nostos.Backend.Data.Models;
using Nostos.Backend.Data.Repositories;
using Nostos.Backend.Tests.Support;
using Xunit;

namespace Nostos.Backend.Tests.Data;

public sealed class RepositoryCancellationTests : IClassFixture<SqliteTestFixture>
{
    private readonly SqliteTestFixture _fixture;

    public RepositoryCancellationTests(SqliteTestFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task BookRepository_propagates_cancellation_across_all_async_methods()
    {
        await using var db = _fixture.CreateContext();
        var repo = new BookRepository(db);
        var canceled = new CancellationToken(canceled: true);
        var book = new PhysicalBookModel { Id = Guid.NewGuid(), Title = "Test" };

        await AssertCanceledAsync(() => repo.GetByIdAsync(book.Id, canceled));
        await AssertCanceledAsync(() => repo.CountBooksWithFilesAsync(canceled));
        await AssertCanceledAsync(() => repo.GetBooksWithFilesPageAsync(0, 10, canceled));
        await AssertCanceledAsync(() => repo.UpdateAsync(book, canceled));
    }

    [Fact]
    public async Task NoteRepository_propagates_cancellation_across_all_async_methods()
    {
        await using var db = _fixture.CreateContext();
        var repo = new NoteRepository(db);
        var canceled = new CancellationToken(canceled: true);
        var bookId = Guid.NewGuid();
        var note = new NoteModel { Id = Guid.NewGuid(), BookId = bookId, Content = "Note", CreatedAt = DateTime.UtcNow };
        var receipt = new NoteCommandReceipt
        {
            ClientId = "client",
            IdempotencyKey = "key",
            Command = "capture",
            ResultJson = "{}",
            CreatedAtUtc = DateTime.UtcNow,
        };
        var browseQuery = new NoteBrowseQuery(Text: "term");

        await AssertCanceledAsync(() => repo.GetByBookIdAsync(bookId, canceled));
        await AssertCanceledAsync(() => repo.GetByIdAsync(note.Id, canceled));
        await AssertCanceledAsync(() => repo.GetByIdWithTopicsAsync(note.Id, canceled));
        await AssertCanceledAsync(() => repo.GetForReadingAsync(note.Id, canceled));
        await AssertCanceledAsync(() => repo.GetByIdWithBookAsync(note.Id, canceled));
        await AssertCanceledAsync(() => repo.AddAsync(note, canceled));
        await AssertCanceledAsync(() => repo.SaveChangesAsync(canceled));
        await repo.AddAsync(note);
        await AssertCanceledAsync(() => repo.SaveChangesAsync(canceled));
        db.Entry(note).State = Microsoft.EntityFrameworkCore.EntityState.Detached;
        await AssertCanceledAsync(() => repo.DeleteAsync(note, canceled));
        await AssertCanceledAsync(() => repo.DeleteTopicLinksAsync(note.Id, canceled));
        await AssertCanceledAsync(() => repo.SearchByTextAsync("term", 10, cancellationToken: canceled));
        await AssertCanceledAsync(() => repo.SearchByAllTermsAsync(["term"], 10, forgiving: true, cancellationToken: canceled));
        await AssertCanceledAsync(() => repo.GetReceiptAsync("client", "key", canceled));
        await AssertCanceledAsync(() => repo.AddReceiptAsync(receipt, canceled));
        await AssertCanceledAsync(() => repo.HasReceiptForNoteAsync("client", note.Id, canceled));
        await AssertCanceledAsync(() => repo.GetWithoutTopicsAsync(10, 0, canceled));
        await AssertCanceledAsync(() => repo.BrowseAsync(browseQuery, 10, 0, canceled));
        await AssertCanceledAsync(() => repo.GetBookFacetsAsync(browseQuery, canceled));
        await AssertCanceledAsync(() => repo.GetSummaryAsync(canceled));
        await AssertCanceledAsync(() => repo.CountWithoutTopicsAsync(canceled));
        await AssertCanceledAsync(() => repo.CountAsync(canceled));
        await AssertCanceledAsync(() => repo.GetBookCountsAsync(10, canceled));
    }

    [Fact]
    public async Task WritingRepository_propagates_cancellation_across_all_async_methods()
    {
        await using var db = _fixture.CreateContext();
        var repo = new WritingRepository(db);
        var canceled = new CancellationToken(canceled: true);
        var writing = new WritingModel
        {
            Id = Guid.NewGuid(),
            Name = "Essay",
            Type = WritingType.Document,
            Content = "Draft",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var noteId = Guid.NewGuid();

        await AssertCanceledAsync(() => repo.GetListAsync(canceled));
        await AssertCanceledAsync(() => repo.GetByIdAsync(writing.Id, canceled));
        await AssertCanceledAsync(() => repo.AddAsync(writing, canceled));
        await AssertCanceledAsync(() => repo.UpdateAsync(writing, canceled));
        await AssertCanceledAsync(() => repo.SaveContentAsync(writing, canceled));
        await AssertCanceledAsync(() => repo.DeleteAsync(writing, canceled));
        await AssertCanceledAsync(() => repo.GetParentIdAsync(writing.Id, canceled));
        await AssertCanceledAsync(() => repo.GetKeptNotesAsync(writing.Id, canceled));
        await AssertCanceledAsync(() => repo.AddKeptNoteAsync(writing.Id, noteId, canceled));
        await AssertCanceledAsync(() => repo.RemoveKeptNoteAsync(writing.Id, noteId, canceled));
    }

    [Fact]
    public async Task TopicRepository_propagates_cancellation_across_all_async_methods()
    {
        await using var db = _fixture.CreateContext();
        var repo = new TopicRepository(db);
        var canceled = new CancellationToken(canceled: true);
        var id = Guid.NewGuid();
        var note = new NoteModel { Id = Guid.NewGuid(), BookId = Guid.NewGuid(), Content = "[[Topic]]", CreatedAt = DateTime.UtcNow };

        await AssertCanceledAsync(() => repo.GetAllWithUsageCountAsync(canceled));
        await AssertCanceledAsync(() => repo.SearchByNoteTextAsync("term", canceled));
        await AssertCanceledAsync(() => repo.SearchByNoteTextAsync("term", null, 25, canceled));
        await AssertCanceledAsync(() => repo.GetStatsAsync(canceled));
        await AssertCanceledAsync(() => repo.GetRelatedAsync(id, canceled));
        await AssertCanceledAsync(() => repo.GetGraphAsync(canceled));
        await AssertCanceledAsync(() => repo.GetWritingUsageAsync(id, 10, canceled));
        await AssertCanceledAsync(() => repo.GetByIdWithNotesAsync(id, canceled));
        await AssertCanceledAsync(() => repo.RenameAsync(id, "Renamed", canceled));
        await AssertCanceledAsync(() => repo.MergeAsync(id, Guid.NewGuid(), canceled));
        await AssertCanceledAsync(() => repo.SetDescriptionAsync(id, "Desc", canceled));
        await AssertCanceledAsync(() => repo.DeleteAsync(id, canceled));
        await AssertCanceledAsync(() => repo.GetByNamesAsync(["Topic"], canceled));
        await AssertCanceledAsync(() => repo.ClearNoteLinksAsync(note, canceled));
        await AssertCanceledAsync(() => repo.DeleteOrphanedAsync(canceled));
        await AssertCanceledAsync(() => repo.MergeCaseVariantsAsync(canceled));
    }

    private static async Task AssertCanceledAsync(Func<Task> action)
    {
        await action.Should().ThrowAsync<OperationCanceledException>();
    }
}
