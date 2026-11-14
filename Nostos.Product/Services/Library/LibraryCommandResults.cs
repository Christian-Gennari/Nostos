using Nostos.Backend.Data.Models;
using Nostos.Shared.Dtos;

namespace Nostos.Backend.Services.Library;

/// <summary>
/// The command-result envelopes every library writer answers with (issue #1611).
/// Reads, the mutation executor and each focused write service used to carry
/// their own copy of these; one place means a new outcome shape (a candidate
/// list, a typed error) can never be formatted two different ways.
/// </summary>
internal static class LibraryCommandResults
{
    public static LibraryCommandResultDto Result(string reply, object? data, string version) =>
        new(reply, data, version);

    public static LibraryCommandResultDto Failure(
        string code,
        string reply,
        string version = "0",
        IReadOnlyList<LibraryCandidate>? candidates = null)
    {
        if (candidates is not null)
            return new LibraryCommandResultDto(reply,
                new LibraryConfirmationErrorDto(code, candidates), version);
        return new LibraryCommandResultDto(reply, new LibraryErrorDto(code), version);
    }

    /// <summary>One ambiguity candidate for a confirmation reply.</summary>
    public static LibraryCandidate ToCandidate(BookModel book, string reason) =>
        new(book.Id, book.Title, book.Author,
            book switch
            {
                PhysicalBookModel p => p.Isbn,
                EBookModel e => e.Isbn,
                _ => null,
            },
            book is AudioBookModel a ? a.Asin : null,
            reason);

    public static (bool DidChange, LibraryCommandResultDto Result) Change(
        LibraryCommandResultDto result) => (true, result);

    public static (bool DidChange, LibraryCommandResultDto Result) NoChange(
        LibraryCommandResultDto result) => (false, result);
}
